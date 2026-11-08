using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.ContextSizing;

namespace OllamaRouter.Services.Catalog;

/// <summary>
/// Coarse model architecture classes used by the zero-config routing rules.
/// </summary>
public enum ModelArchitectureKind
{
    /// <summary>
    /// Mixture-of-Experts model (only a subset of experts is active per token).
    /// </summary>
    Moe,

    /// <summary>
    /// Dense model below the <see cref="ModelCapabilityEngine.LargeDenseParameterThreshold"/>
    /// parameter count.
    /// </summary>
    SmallDense,

    /// <summary>
    /// Dense model at or above the <see cref="ModelCapabilityEngine.LargeDenseParameterThreshold"/>
    /// parameter count (all parameters active per token).
    /// </summary>
    LargeDense
}

/// <summary>
/// Records WHY a <see cref="ModelArchitectureKind"/> classification happened, for logs and
/// tests (e.g. to prove the permissive MoE fallback or an explicit configuration pin).
/// </summary>
public enum ArchitectureDetectionSource
{
    /// <summary>
    /// An explicit "Dense"/"Moe" pin from the model's <c>Architecture</c> configuration override.
    /// </summary>
    Pinned,

    /// <summary>
    /// Detected via a <c>*.expert_count &gt; 0</c> entry in the /api/show <c>model_info</c>.
    /// </summary>
    MoeExpertCount,

    /// <summary>
    /// Detected via the /api/show <c>details.family</c> containing "moe".
    /// </summary>
    MoeFamily,

    /// <summary>
    /// No MoE signal was available at all (<c>model_info</c> absent, e.g. an older Ollama
    /// version): permissively treated as MoE so an unknown model never triggers a false
    /// LargeDense veto.
    /// </summary>
    PermissiveFallback,

    /// <summary>
    /// Classified by parameter count (below/above the 14B threshold).
    /// </summary>
    DenseParameterCount
}

/// <summary>
/// Per-target derived capability for one (target, model) pair: the architecture
/// classification plus the baseline context (<see cref="CDefault"/>), the maximum pre-configured
/// context ceiling (<see cref="CMax"/>) and the model's native GGUF context limit
/// (<see cref="NativeContextLimit"/>) for requests routed to that target.
/// </summary>
public sealed record TargetModelCapability(
    string Target,
    ModelArchitectureKind Architecture,
    ArchitectureDetectionSource Source,
    int CDefault,
    int CMax,
    bool HardwareCapable,
    int? AvailableVramForKVCacheMB,
    int? NativeContextLimit = null);

/// <summary>
/// Discovers model capabilities per target by inspecting the target's /api/tags entry and
/// POST /api/show, caches the parsed <see cref="ModelMetadata"/> keyed by target + model +
/// catalog digest, and derives the architecture classification and the per-target context
/// ceilings (C_default / C_max) through the capacity hierarchy:
/// the node's pre-configured Modelfile num_ctx → the model's native GGUF context length
/// (model_info "*.context_length") → the assumed 128k fallback
/// (<see cref="FallbackContextWindow").
/// </summary>
public sealed class ModelCapabilityEngine(
    IOllamaModelCatalogClient catalogClient,
    IMemoryCache memoryCache,
    ILogger<ModelCapabilityEngine> logger,
    IModelCatalogCacheService? keyRegistry = null,
    IFleetHardwareService? fleetHardware = null,
    IOptionsMonitor<OllamaRouterOptions>? routerOptions = null)
{
    /// <summary>
    /// Default Ollama context window when the Modelfile omits an explicit num_ctx.
    /// </summary>
    public const int OllamaDefaultContext = 4096;


    /// <summary>
    /// Assumed context window (128,000 tokens ≈ 131,072) used as the LAST-resort CMax when a
    /// node's Modelfile pre-configures no num_ctx AND the model's native GGUF context length
    /// is undiscoverable. Replaces the retired hardware-derived ceilings as a capacity source.
    /// </summary>
    public const int FallbackContextWindow = 131_072;

    /// <summary>
    /// Targets with fewer megabytes of VRAM than this are "small" (8gb laptop-class) targets.
    /// </summary>
    public const int SmallTargetVramThresholdMB = 12_000;

    /// <summary>
    /// Parameter count at/above which a dense model is classified as
    /// <see cref="ModelArchitectureKind.LargeDense"/>.
    /// </summary>
    public const long LargeDenseParameterThreshold = 14_000_000_000;

    /// <summary>
    /// Exact human-readable veto reason exposed by the model capability API for
    /// <see cref="ModelArchitectureKind.LargeDense"/> models on small-class (&lt; 12 GB) targets.
    /// </summary>
    public const string LargeDenseVetoReason = "Dense model >= 14B exceeds 8GB hardware limit";

    /// <summary>
    /// Wire name for REST DTOs: "Moe" for MoE, "Dense" for Small/LargeDense.
    /// </summary>
    public static string ArchitectureWireName(ModelArchitectureKind kind) =>
        kind == ModelArchitectureKind.Moe ? "Moe" : "Dense";

    private static readonly TimeSpan CapabilityCacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fetches (or returns the cached) <see cref="ModelMetadata"/> for one model on one
    /// target: the /api/tags entry (weight size) plus the POST /api/show response. The result
    /// is cached under the target's catalog digest for 30 s with single-flight semantics per
    /// key. A null/empty <paramref name="targetUrl"/> or an empty digest returns degraded
    /// ("unknown") metadata without any network call, and network/parse failures degrade to
    /// the same metadata so classification falls back to the permissive MoE path.
    /// </summary>
    public async Task<ModelMetadata> GetMetadataAsync(string? targetUrl, string modelName, string catalogDigest, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetUrl) || string.IsNullOrEmpty(modelName) || string.IsNullOrEmpty(catalogDigest))
        {
            return new ModelMetadata(modelName, 0, null, 0, null, null, false, null, null, null);
        }

        // Non-Ollama targets have no /api/tags or /api/show: synthesize the metadata from the
        // target's roster + family heuristics (network-free, always live so Setup-API roster
        // edits are visible immediately — consistent with task #61's live roster resolution).
        if (routerOptions is not null && TryResolveNonOllamaTarget(targetUrl, out var nonOllamaTarget))
        {
            return SynthesizeNonOllamaMetadata(nonOllamaTarget, modelName);
        }

        var cacheKey = BuildCacheKey(targetUrl, catalogDigest, modelName);
        var factory = async (ICacheEntry entry) =>
        {
            entry.AbsoluteExpirationRelativeToNow = CapabilityCacheDuration;

            var (tags, _) = await catalogClient.GetTagsWithDigestAsync(targetUrl, ct);
            var show = await catalogClient.GetShowAsync(targetUrl, modelName, ct);
            var parsed = ModelMetadataParser.ParseModelMetadata(modelName, tags, show);

            logger.LogDebug(
                "Discovered capabilities for {Model} on {Target} (digest {Digest}): arch signals expertCount={ExpertCount}, family={Family}, parameterCount={ParameterCount}, numCtx={NumCtx}.",
                modelName, targetUrl, catalogDigest, parsed.ExpertCount, parsed.ModelFamily, parsed.ParameterCount, parsed.ModelfileNumCtx);

            RegisterCacheKey(targetUrl, catalogDigest, cacheKey);
            return parsed;
        };

        return (ModelMetadata?)await memoryCache.GetOrCreateAsync(cacheKey, factory) ??
               new ModelMetadata(modelName, 0, null, 0, null, null, false, null, null, null);
    }

    /// <summary>
    /// Synthesizes the <see cref="ModelMetadata"/> of a model hosted on a NON-OLLAMA target
    /// from configuration only (task #62): the declared roster entry's
    /// <see cref="TargetModelConfig.ContextLength"/> / <see cref="TargetModelConfig.Quantization"/>
    /// hints plus the <see cref="ModelFamilyHeuristics"/> table. NO network and NO cache —
    /// the pure in-memory walk runs on EVERY call so a Setup-API roster edit is visible
    /// immediately. An unmatched model name degrades to the "unknown" shape
    /// (<c>HasModelInfo</c> false) so classification takes the permissive MoE path
    /// (decision D1: no veto can ever fire for an unknown remote model).
    /// </summary>
    public ModelMetadata SynthesizeNonOllamaMetadata(OllamaTargetOptions target, string modelName)
    {
        var rosterEntry = target?.Models is null
            ? null
            : target.Models.FirstOrDefault(m =>
                !string.IsNullOrWhiteSpace(m.Name) &&
                string.Equals(m.Name!.Trim(), modelName, StringComparison.OrdinalIgnoreCase));
        var match = ModelFamilyHeuristics.TryMatch(modelName);

        long? contextLength = rosterEntry is { ContextLength: > 0 }
            ? (long?)rosterEntry.ContextLength
            : match?.ContextLength is { } heuristicContext and > 0
                ? (long?)heuristicContext
                : null;

        return new ModelMetadata(
            ModelName: modelName,
            WeightSizeBytes: 0,
            ParameterCount: match?.ParameterCount,
            ExpertCount: match?.ExpertCount ?? 0,
            ModelFamily: match?.Family,
            ModelfileNumCtx: null,
            HasModelInfo: match is not null,
            ParametersRaw: null,
            NativeContextLength: contextLength,
            QuantizationLevel: rosterEntry?.Quantization);
    }

    /// <summary>
    /// Resolves the effective non-Ollama target whose URL matches <paramref name="targetUrl"/>
    /// (case-insensitive, trailing-slash tolerant — the same match as the synthetic-roster
    /// lookup in <c>ModelCatalogCacheService</c>). Returns false for Ollama targets,
    /// unresolvable URLs and hosts without the optional options reference.
    /// </summary>
    private bool TryResolveNonOllamaTarget(string targetUrl, out OllamaTargetOptions target)
    {
        target = null!;
        if (routerOptions is null || string.IsNullOrWhiteSpace(targetUrl))
        {
            return false;
        }

        foreach (var candidate in routerOptions.CurrentValue.GetEffectiveTargets())
        {
            if (candidate.Type == TargetType.Ollama || string.IsNullOrWhiteSpace(candidate.Url))
            {
                continue;
            }

            if (string.Equals(candidate.Url.TrimEnd('/'), targetUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                target = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Classifies the model architecture of <paramref name="metadata"/> using the documented
    /// fallback chain: explicit pin → expert_count → family containing "moe" → permissive MoE
    /// fallback (no model_info) → dense parameter count. Unknown pin values are ignored.
    /// </summary>
    public (ModelArchitectureKind Architecture, ArchitectureDetectionSource Source) ClassifyArchitecture(ModelMetadata metadata, string? pinnedArchitecture)
    {
        if (string.Equals(pinnedArchitecture, "Moe", StringComparison.OrdinalIgnoreCase))
        {
            return (ModelArchitectureKind.Moe, ArchitectureDetectionSource.Pinned);
        }

        if (string.Equals(pinnedArchitecture, "Dense", StringComparison.OrdinalIgnoreCase))
        {
            // The pin wins over ALL detection: apply the SmallDense/LargeDense rules from the
            // parameter count (unknown count ⇒ LargeDense).
            var (architecture, source) = DenseByParamCount(metadata);
            return (architecture, ArchitectureDetectionSource.Pinned);
        }

        if (!string.IsNullOrWhiteSpace(pinnedArchitecture))
        {
            logger.LogDebug(
                "Ignoring unknown Architecture pin '{Pin}'; proceeding with auto-detection.", pinnedArchitecture);
        }

        if (metadata.ExpertCount > 0)
        {
            return (ModelArchitectureKind.Moe, ArchitectureDetectionSource.MoeExpertCount);
        }

        if (metadata.ModelFamily is { } family && family.Contains("moe", StringComparison.Ordinal))
        {
            return (ModelArchitectureKind.Moe, ArchitectureDetectionSource.MoeFamily);
        }

        if (!metadata.HasModelInfo)
        {
            // Older Ollama versions (or failures): no MoE signal at all. Permissively treat
            // the model as MoE so unknown models are never hard-vetoed as LargeDense.
            return (ModelArchitectureKind.Moe, ArchitectureDetectionSource.PermissiveFallback);
        }

        return DenseByParamCount(metadata);
    }

    /// <summary>
    /// Derives the per-target capability (architecture, C_default, C_max, hardware
    /// capability flag, remaining VRAM for the KV cache) from the discovered metadata and
    /// the target's effective VRAM budget.
    /// </summary>
    public TargetModelCapability DeriveCapability(ModelMetadata metadata, string targetName, OllamaTargetOptions target, string? pinnedArchitecture)
    {
        var (architecture, source) = ClassifyArchitecture(metadata, pinnedArchitecture);

        var maxVram = target.GetEffectiveMaxVramMB(fleetHardware);
        var weightMB = (int)(metadata.WeightSizeBytes / 1_048_576);
        var isNonOllama = target.Type != TargetType.Ollama;

        // Stateless remote endpoints never consume LOCAL VRAM (task #62, decision D1): the
        // remaining-VRAM figure only makes sense for Ollama targets that would load the
        // weights — expose null for non-Ollama targets (Ollama behavior unchanged).
        var availableMB = (isNonOllama || maxVram is null) ? null : (int?)(maxVram.Value - weightMB);

        // Capacity hierarchy (task #25, shared with SynthesizedShowBuilder): the node's
        // pre-configured Modelfile num_ctx wins outright; otherwise the model's native context
        // length (roster hint / family heuristic for non-Ollama targets); otherwise the
        // assumed 128k fallback (hardware-derived ceilings are no longer capacity sources).
        var cMax = ResolveContextCeiling(metadata);
        var nativeContextLimit = AsContextInt(metadata.NativeContextLength);

        // 8gb / &lt;12000 MB hard veto for LargeDense only, and for OLLAMA targets ONLY
        // (decision D1: stateless remote endpoints never consume local VRAM, so a
        // synthesized LargeDense classification never hard-disqualifies a cloud-shaped target —
        // it is reported for cartography/monitoring only). MoE and SmallDense are NEVER
        // VRAM-vetoed (MoE is capable on all targets; see the zero-config routing plan §2.1).
        var hardwareCapable = isNonOllama ||
            !(architecture == ModelArchitectureKind.LargeDense &&
              (maxVram is null || maxVram < SmallTargetVramThresholdMB));

        return new TargetModelCapability(
            Target: targetName,
            Architecture: architecture,
            Source: source,
            CDefault: metadata.ModelfileNumCtx ?? OllamaDefaultContext,
            CMax: cMax,
            HardwareCapable: hardwareCapable,
            AvailableVramForKVCacheMB: availableMB,
            NativeContextLimit: nativeContextLimit);
    }

    /// <summary>
    /// Request-time clamping helper: computes whether a target with capacity
    /// <paramref name="cMax"/> is qualified for a request of <paramref name="estimatedTokens"/>
    /// plus the additive reasoning-fit margin, and the <c>num_ctx</c> value to inject:
    /// min(client-requested num_ctx ?? effective required, cMax). Qualification uses
    /// <c>estimatedTokens + contextFitMarginTokens</c> (clamped to the client's requested
    /// num_ctx when smaller — an upper-bound preference that can only LOWER the requirement);
    /// the client value never disqualifies by itself.
    /// </summary>
    public static (bool Qualified, int InjectedNumCtx) EvaluateClientContext(
        int estimatedTokens,
        int? clientRequestedNumCtx,
        int cMax,
        int contextFitMarginTokens = 8192)
    {
        var effectiveRequired = (long)estimatedTokens + Math.Max(0, contextFitMarginTokens);
        if (clientRequestedNumCtx.HasValue)
        {
            effectiveRequired = Math.Min(effectiveRequired, clientRequestedNumCtx.Value);
        }

        var qualified = effectiveRequired <= cMax;
        var injected = (int)Math.Min(clientRequestedNumCtx ?? effectiveRequired, cMax);
        return (qualified, injected);
    }

        /// <summary>
        /// Pure MoE extensible-capacity helper (task #26, Option A): the capacity a MoE target
        /// QUALIFIES at in Stage 1 and the sizing engine's single extensible (max) bucket:
        /// <c>min(max(baseline, native GGUF context limit ?? 128k assumed fallback),
        /// MaxContextTokens ?? ∞)</c>, where the baseline is the node's pre-configured context
        /// (Modelfile num_ctx ?? native ?? 128k, see
        /// <see cref="DynamicContextSizingEngine.BaselineBucket"/>). The baseline floor keeps a
        /// Modelfile-pinned node from being capped below its own pre-configured pin when the
        /// GGUF native length is undiscoverable. A null or non-positive
        /// <paramref name="maxContextTokensOverride"/> means "no operator cap". This does NOT
        /// modify the capability record's <see cref="TargetModelCapability.CMax"/> (which stays
        /// the pre-configured hierarchy value); the MoE-vs-dense split lives in the
        /// qualification/sizing comparison, not in the capability.
        /// </summary>
        public static int GetMoeExtensibleCapacity(TargetModelCapability capability, int? maxContextTokensOverride)
        {
            var capacity = Math.Max(DynamicContextSizingEngine.BaselineBucket(capability),
                capability.NativeContextLimit ?? FallbackContextWindow);
            if (maxContextTokensOverride is { } cap and > 0 && cap < capacity)
            {
                capacity = cap;
            }

            return capacity;
        }

    private (ModelArchitectureKind, ArchitectureDetectionSource) DenseByParamCount(ModelMetadata metadata)
    {
        if (!metadata.ParameterCount.HasValue)
        {
            // Known-dense shape but no count: conservative.
            return (ModelArchitectureKind.LargeDense, ArchitectureDetectionSource.DenseParameterCount);
        }

        var kind = metadata.ParameterCount.Value >= LargeDenseParameterThreshold
            ? ModelArchitectureKind.LargeDense
            : ModelArchitectureKind.SmallDense;

        return (kind, ArchitectureDetectionSource.DenseParameterCount);
    }

    /// <summary>
    /// The CMax capacity hierarchy (task #25 / task #62): the Modelfile num_ctx → the native
    /// context length → the assumed 128k fallback (<see cref="FallbackContextWindow"/>).
    /// Shared by <see cref="DeriveCapability"/> and <see cref="SynthesizedShowBuilder"/>
    /// (internal + InternalsVisibleTo) so the two consumers can never diverge.
    /// </summary>
    internal static int ResolveContextCeiling(ModelMetadata metadata) =>
        metadata.ModelfileNumCtx ?? AsContextInt(metadata.NativeContextLength) ?? FallbackContextWindow;

    private static int? AsContextInt(long? value)
    {
        if (value is null || value <= 0 || value > int.MaxValue)
        {
            return null;
        }

        return (int)value!;
    }

    private static string BuildCacheKey(string targetUrl, string catalogDigest, string modelName) =>
        $"{ModelCatalogCacheService.CapabilityKeyPrefix}{targetUrl}:{catalogDigest}:{modelName}";

    private void RegisterCacheKey(string targetUrl, string catalogDigest, string cacheKey)
    {
        keyRegistry?.RegisterCapabilityCacheKeys(targetUrl, [cacheKey], catalogDigest);
    }
}
