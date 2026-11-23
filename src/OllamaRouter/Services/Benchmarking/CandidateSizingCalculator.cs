using System.Globalization;
using System.Text.Json.Serialization;
using OllamaRouter.Options;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Services.Benchmarking;

/// <summary>
/// Outcome of the pre-flight memory residency calculation for one (candidate, context) pair:
/// <see cref="Fits"/> (resident within the fast-memory budget), <see cref="PrunedWeights"/> /
/// <see cref="PrunedKvCache"/> (pruned — no network pull may happen for this candidate) and
/// <see cref="Assumed"/> (unknown hardware profile — fail-open, no pruning; the benchmark
/// proceeds and an OOM at runtime is the empirical safety net).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemoryFitStatus>))]
public enum MemoryFitStatus
{
    Fits = 0,
    PrunedWeights,
    PrunedKvCache,
    Assumed
}

/// <summary>
/// The full memory-residency estimate for one (candidate, context) pair. All sizes are in
/// mebibytes (MiB, 2^20) — never 10^6 — so the math stays exact on powers of two.
/// </summary>
public sealed record CandidateSizingEstimate(
    [property: JsonPropertyName("weightsMB")] double WeightsMB,
    [property: JsonPropertyName("activeWeightsMB")] double ActiveWeightsMB,
    [property: JsonPropertyName("kvCacheMB")] double KvCacheMB,
    [property: JsonPropertyName("totalRequiredMB")] double TotalRequiredMB,
    [property: JsonPropertyName("fastMemoryMB")] long? FastMemoryMB,
    [property: JsonPropertyName("status")] MemoryFitStatus Status,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>
/// Pure, static, no-I/O sizing calculator for benchmark candidates (task #28, reworked for
/// hybrid/UD models in task #166). Mirrors the <see cref="HardwareMetrics"/> style:
/// deterministic math, no DI.
/// <para>
/// Formulas (pinned by tests; MB = MiB, 2^20):
/// <list type="bullet">
/// <item>weights = <c>EstimatedWeightsMB</c> when set (exact known resident footprint of the
/// mixed-precision/UD GGUF + bundled mmproj, where it differs from the parametric size),
/// otherwise totalParams × bitsPerWeight / 8 / 2^20 — see <see cref="ComputeEffectiveWeightsMB"/></item>
/// <item>activeWeights = (activeParams ?? totalParams) × bitsPerWeight / 8 / 2^20 — MoE models
/// decode on the active experts only; Dense models have active == total (parametric; the
/// mmproj/UD delta is intentionally NOT applied to the decode buffer)</item>
/// <item>KV cache = contextTokens × <c>KvBytesPerToken</c> / 2^20 when set (calibrated
/// absolute bytes/token — K+V, all layers, KV dtype included; the only correct path for
/// sliding-window / hybrid SSM+attention architectures whose KV does not grow on every
/// layer), otherwise the parametric fallback contextTokens × layers × kvHeads × headDim ×
/// 2 (K and V) × <see cref="CandidateModelCatalogOptions.KvCacheBytesPerElement"/> (f16 = 2) / 2^20
/// — see <see cref="ComputeEffectiveKvCacheMB"/>. Those are the ONLY two KV math paths, so
/// the computation stays deterministic and transparent.</item>
/// <item>totalRequired = weights + KV</item>
/// </list>
/// </para>
/// <para>
/// Strict (dense/expert) pruning rule, applied when <c>AllowCpuOffload == false</c> (all
/// Dense/Expert models, Ornith's hybrid frontier, and any legacy/synthetic model — the
/// architecture string is NO LONGER the gate; the explicit flag is): prune only when a size
/// is STRICTLY greater than 0.92 × TotalFastMemoryMB (a candidate exactly at the limit
/// Fits). <see cref="MemoryFitStatus.PrunedWeights"/> when the weights alone exceed the
/// limit, <see cref="MemoryFitStatus.PrunedKvCache"/> when only the sum exceeds it. The
/// conservative 92% rule is deliberately kept (2026-11-22 product decision) — e.g.
/// Qwen3.8-27B:iq3 prunes at 48k on a dedicated 16 GiB node even though empirical reports
/// put the boundary at ~48–64k.
/// </para>
/// <para>
/// CPU-offload (MoE) rule, applied when <c>AllowCpuOffload == true</c> (the lightweight
/// MoE models: Flint:*, Qwen3.6-35B-A3B:*): decode is active-parameter-bound, so the
/// fast-memory test is activeWeights + KV ≤ 0.92 × fast (pruned as
/// <see cref="MemoryFitStatus.PrunedKvCache"/> unless the active buffer ALONE exceeds the
/// limit → <see cref="MemoryFitStatus.PrunedWeights"/>). The full weights additionally must
/// be resident in some tier: on non-unified nodes, weights + KV ≤ 0.92 × (fast +
/// SystemRamMB), otherwise <see cref="MemoryFitStatus.PrunedWeights"/> ("full MoE weights
/// do not fit in fast+system memory"); unified nodes apply the same 0.92 rule on fast for
/// the full sum.
/// </para>
/// <para>
/// Unknown profile (null or TotalFastMemoryMB ≤ 0): <see cref="MemoryFitStatus.Assumed"/>
/// with all MB fields computed and NO pruning (fail-open, decision #4). Pruned candidates
/// must never trigger a POST /api/pull.
/// </para>
/// </summary>
public static class CandidateSizingCalculator
{
    /// <summary>
    /// Full weights size in MiB: totalParams × bitsPerWeight / 8 / 2^20.
    /// </summary>
    public static double ComputeWeightsMB(long totalParams, int bitsPerWeight) =>
        totalParams * (double)bitsPerWeight / 8.0 / 1_048_576.0;

    /// <summary>
    /// The effective full-weights resident footprint in MiB: the curated model's known
    /// <see cref="CandidateModel.EstimatedWeightsMB"/> when set (UD mixed-precision GGUF +
    /// bundled mmproj — differs from the parametric size), otherwise the parametric
    /// <see cref="ComputeWeightsMB(long, int)"/> formula.
    /// </summary>
    public static double ComputeEffectiveWeightsMB(CandidateModel model) =>
        model.EstimatedWeightsMB ?? ComputeWeightsMB(model.TotalParams, model.BitsPerWeight);

    /// <summary>
    /// Active weights size in MiB: MoE models decode on the active experts only
    /// (activeParams); Dense models have active == total (activeParams null).
    /// Purely parametric — it never uses <see cref="CandidateModel.EstimatedWeightsMB"/>.
    /// </summary>
    public static double ComputeActiveWeightsMB(CandidateModel model) =>
        ComputeWeightsMB(model.ActiveParams ?? model.TotalParams, model.BitsPerWeight);

    /// <summary>
    /// KV cache size in MiB for the given context: context × layers × kvHeads × headDim ×
    /// 2 (K and V buffers) × bytesPerElement (f16 = 2 by default) / 2^20.
    /// </summary>
    public static double ComputeKvCacheMB(
        int contextTokens,
        int layers,
        int kvHeads,
        int headDim,
        int bytesPerElement = CandidateModelCatalogOptions.KvCacheBytesPerElement) =>
        contextTokens * (double)layers * kvHeads * headDim * 2.0 * bytesPerElement / 1_048_576.0;

    /// <summary>
    /// The effective KV-cache size in MiB for one model at one context: the calibrated
    /// <see cref="CandidateModel.KvBytesPerToken"/> (context × bytes/token / 2^20) when set —
    /// required for sliding-window / hybrid SSM+attention architectures whose KV only grows
    /// on the full-attention layers — otherwise the parametric
    /// <see cref="ComputeKvCacheMB(int, int, int, int, int)"/> fallback.
    /// </summary>
    public static double ComputeEffectiveKvCacheMB(CandidateModel model, int contextTokens) =>
        model.KvBytesPerToken is { } kvBytesPerToken
            ? contextTokens * kvBytesPerToken / 1_048_576.0
            : ComputeKvCacheMB(
                contextTokens, model.Layers, model.KvHeads, model.HeadDim,
                CandidateModelCatalogOptions.KvCacheBytesPerElement);

    /// <summary>
    /// Evaluates the memory fit of one candidate at one context size on one node profile.
    /// See the type-level documentation for the exact formulas and boundary semantics
    /// (prune only when STRICTLY greater than the 92% fast-memory limit).
    /// </summary>
    public static CandidateSizingEstimate Evaluate(CandidateModel model, int contextTokens, NodeHardwareProfile? profile)
    {
        var weightsMB = ComputeEffectiveWeightsMB(model);
        var activeWeightsMB = ComputeActiveWeightsMB(model);
        var kvCacheMB = ComputeEffectiveKvCacheMB(model, contextTokens);
        var totalRequiredMB = weightsMB + kvCacheMB;

        // Fail-open: no usable profile → no pruning, the benchmark proceeds and an OOM at
        // runtime is the empirical safety net (decision #4). All sizes are still computed.
        if (profile is null || profile.TotalFastMemoryMB <= 0)
        {
            return new CandidateSizingEstimate(
                weightsMB, activeWeightsMB, kvCacheMB, totalRequiredMB,
                FastMemoryMB: null,
                Status: MemoryFitStatus.Assumed,
                Reason: null);
        }

        var limitMB = CandidateModelCatalogOptions.FastMemoryOccupancyLimit * profile.TotalFastMemoryMB;

        if (!model.AllowCpuOffload)
        {
            // Dense: weights + KV must fit 100% within the fast-memory budget.
            if (weightsMB > limitMB)
            {
                return new CandidateSizingEstimate(
                    weightsMB, activeWeightsMB, kvCacheMB, totalRequiredMB,
                    profile.TotalFastMemoryMB,
                    MemoryFitStatus.PrunedWeights,
                    $"weights {FormatMB(weightsMB)} MB alone exceed the fast-memory limit " +
                    $"{FormatMB(limitMB)} MB (92% of {profile.TotalFastMemoryMB} MB)");
            }

            if (totalRequiredMB > limitMB)
            {
                return new CandidateSizingEstimate(
                    weightsMB, activeWeightsMB, kvCacheMB, totalRequiredMB,
                    profile.TotalFastMemoryMB,
                    MemoryFitStatus.PrunedKvCache,
                    $"weights + KV cache {FormatMB(totalRequiredMB)} MB exceed the fast-memory limit " +
                    $"{FormatMB(limitMB)} MB (92% of {profile.TotalFastMemoryMB} MB); " +
                    "the KV cache would spill to system RAM");
            }

            return new CandidateSizingEstimate(
                weightsMB, activeWeightsMB, kvCacheMB, totalRequiredMB,
                profile.TotalFastMemoryMB, MemoryFitStatus.Fits, null);
        }

        // CPU-offload-eligible MoE: decode is active-parameter-bound — the active weights +
        // KV must fit fast memory.
        var decodeRequiredMB = activeWeightsMB + kvCacheMB;
        if (decodeRequiredMB > limitMB)
        {
            // The KV cache is the marginal consumer at large contexts (the active buffer is
            // fixed): attribute the prune to the KV cache unless the active buffer alone
            // already exceeds the limit.
            var status = activeWeightsMB > limitMB
                ? MemoryFitStatus.PrunedWeights
                : MemoryFitStatus.PrunedKvCache;
            return new CandidateSizingEstimate(
                weightsMB, activeWeightsMB, kvCacheMB, totalRequiredMB,
                profile.TotalFastMemoryMB,
                status,
                $"active MoE weights + KV cache {FormatMB(decodeRequiredMB)} MB exceed the " +
                $"fast-memory limit {FormatMB(limitMB)} MB (92% of {profile.TotalFastMemoryMB} MB)");
        }

        // Full MoE weights must still be resident in SOME tier:
        // unified → the fast tier is the only tier (same 0.92 rule on fast for the full sum);
        // non-unified → fast + system RAM together.
        var residencyMB = profile.IsUnifiedMemory
            ? profile.TotalFastMemoryMB
            : profile.TotalFastMemoryMB + profile.SystemRamMB;
        if (totalRequiredMB > CandidateModelCatalogOptions.FastMemoryOccupancyLimit * residencyMB)
        {
            return new CandidateSizingEstimate(
                weightsMB, activeWeightsMB, kvCacheMB, totalRequiredMB,
                profile.TotalFastMemoryMB,
                MemoryFitStatus.PrunedWeights,
                $"full MoE weights do not fit in fast+system memory: {FormatMB(totalRequiredMB)} MB " +
                $"exceed the limit {FormatMB(CandidateModelCatalogOptions.FastMemoryOccupancyLimit * residencyMB)} MB " +
                $"(92% of {residencyMB} MB)");
        }

        return new CandidateSizingEstimate(
            weightsMB, activeWeightsMB, kvCacheMB, totalRequiredMB,
            profile.TotalFastMemoryMB, MemoryFitStatus.Fits, null);
    }

    /// <summary>
    /// Formats a MiB value with invariant culture ("13900" / "1.6") so the reason strings are
    /// locale-stable (InvariantGlobalization is on, but explicit is better).
    /// </summary>
    private static string FormatMB(double mb) =>
        mb.ToString("0.#", CultureInfo.InvariantCulture);
}
