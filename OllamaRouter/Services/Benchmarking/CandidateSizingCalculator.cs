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
/// Pure, static, no-I/O sizing calculator for benchmark candidates (task #28). Mirrors the
/// <see cref="HardwareMetrics"/> style: deterministic math, no DI.
/// <para>
/// Formulas (pinned by tests):
/// <list type="bullet">
/// <item>weights = totalParams × bitsPerWeight / 8 / 2^20 (MB = MiB)</item>
/// <item>KV cache = contextTokens × layers × kvHeads × headDim × 2 (K and V) × bytesPerElement / 2^20
/// (GQA: only the KV heads are counted, not the full attention-head count)</item>
/// <item>totalRequired = weights + KV</item>
/// </list>
/// </para>
/// <para>
/// Pruning rule (Dense, known profile): prune only when <see cref="TotalRequiredMB"/> is
/// STRICTLY greater than 0.92 × TotalFastMemoryMB (a candidate exactly at the limit Fits).
/// <see cref="MemoryFitStatus.PrunedWeights"/> when the weights alone exceed the limit,
/// <see cref="MemoryFitStatus.PrunedKvCache"/> when only the sum exceeds it.
/// </para>
/// <para>
/// MoE rule: decode is active-parameter-bound, so the fast-memory test is
/// activeWeights + KV ≤ 0.92 × fast. The full weights additionally must be resident in some
/// tier: on non-unified nodes, weights + KV ≤ 0.92 × (fast + SystemRamMB), otherwise
/// <see cref="MemoryFitStatus.PrunedWeights"/> ("full MoE weights do not fit in fast+system
/// memory"); unified nodes apply the same 0.92 rule on fast for the full sum.
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
    /// Active weights size in MiB: MoE models decode on the active experts only
    /// (activeParams); Dense models have active == total.
    /// </summary>
    public static double ComputeActiveWeightsMB(CandidateModel model)
    {
        var activeParams = string.Equals(model.Architecture, "MoE", StringComparison.OrdinalIgnoreCase)
            ? model.ActiveParams ?? model.TotalParams
            : model.TotalParams;
        return ComputeWeightsMB(activeParams, model.BitsPerWeight);
    }

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
    /// Evaluates the memory fit of one candidate at one context size on one node profile.
    /// See the type-level documentation for the exact formulas and boundary semantics
    /// (prune only when STRICTLY greater than the 92% fast-memory limit).
    /// </summary>
    public static CandidateSizingEstimate Evaluate(CandidateModel model, int contextTokens, NodeHardwareProfile? profile)
    {
        var weightsMB = ComputeWeightsMB(model.TotalParams, model.BitsPerWeight);
        var activeWeightsMB = ComputeActiveWeightsMB(model);
        var kvCacheMB = ComputeKvCacheMB(
            contextTokens, model.Layers, model.KvHeads, model.HeadDim,
            CandidateModelCatalogOptions.KvCacheBytesPerElement);
        var totalRequiredMB = weightsMB + kvCacheMB;

        var isMoE = string.Equals(model.Architecture, "MoE", StringComparison.OrdinalIgnoreCase);

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

        if (!isMoE)
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

        // MoE: decode is active-parameter-bound — the active weights + KV must fit fast memory.
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
