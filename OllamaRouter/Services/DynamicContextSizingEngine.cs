using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Sizing mode of one (target, model, request) triple (task #26, two-bucket MoE ladder):
/// <list type="bullet">
/// <item><see cref="Baseline"/> - MoE: the request fits the node's pre-configured context
/// (the "optimal" baseline bucket); the injected value is the baseline (clamped to the
/// extensible limit when the operator cap is below it).</item>
/// <item><see cref="Expanded"/> - MoE: the request exceeds the baseline; the single
/// extensible bucket (the model's native context limit, optionally capped by
/// <see cref="ModelThresholds.MaxContextTokens"/>) is injected.</item>
/// <item><see cref="Clamped"/> - dense: the client's static <c>num_ctx</c> exceeded the
/// node's pre-configured context and was clamped DOWN to it (one-time KV reallocation;
/// never raised, never bucketed, never leased).</item>
/// <item><see cref="PassThrough"/> - dense: the payload is untouched (no client
/// <c>num_ctx</c>, client value ≤ pre-configured, or clamping disabled by
/// <see cref="DynamicContextOptions.DenseClampEnabled"/>) — <see cref="ContextSizingDecision.InjectedNumCtx"/>
/// carries the 0 sentinel meaning "no rewrite".</item>
/// </list>
/// </summary>
public enum ContextSizingMode
{
    Baseline,
    Expanded,
    Clamped,
    PassThrough
}

/// <summary>
/// The chosen bucket and sizing mode for one (target, model, request) triple (task #26).
/// <see cref="InjectedNumCtx"/> is the value to write into the outgoing payload's
/// <c>options.num_ctx</c> (0 for <see cref="ContextSizingMode.PassThrough"/>: the payload is
/// left untouched); <see cref="EffectiveCeiling"/> is the effective capacity of the target for
/// this request (MoE: the extensible limit <c>min(native ?? 128k, MaxContextTokens ?? ∞)</c>;
/// dense: the pre-configured <c>CMax</c>); <see cref="BaselineBucket"/> is the two-bucket
/// baseline (the node's pre-configured context, also the Auto-Shrink shrink target);
/// <see cref="ClientRequestedClamped"/> is true when the client's static <c>num_ctx</c> was
/// rewritten downward (MoE requirement clamp or dense clamp-only).
/// </summary>
public sealed record ContextSizingDecision(
    string ProfileName,
    int InjectedNumCtx,
    int EffectiveCeiling,
    bool ClientRequestedClamped,
    ContextSizingMode Mode,
    int BaselineBucket);

/// <summary>
/// Stateless dynamic context sizing engine (Phase 2, items 3-4 of the Multi-Target
/// Zero-Config Routing Plan; MoE-only two-bucket ladder after task #26): MoE requests pick
/// between EXACTLY two buckets — the node's pre-configured context (the "optimal" baseline
/// bucket) and the model's native context limit (the single "extensible" bucket, optionally
/// capped by <c>Models:{model}:MaxContextTokens</c>) — while dense requests get clamp-only
/// pass-through (a dense KV refill costs 300 s+, so dense payloads are never expanded or
/// bucketed; an oversized static client <c>num_ctx</c> is merely clamped down to the
/// pre-configured value). Sizing is a pure function of (options, capability, request facts) -
/// lease/cooldown hysteresis state is maintained by the Auto-Shrink Manager
/// (<c>IContextAutoShrinkManager</c>, Phase 3 item 2, MoE only), which consumes this
/// engine's decision per request.
/// </summary>
public static class DynamicContextSizingEngine
{
    /// <summary>
    /// Minimum effective VRAM (in MB) of a server-class target (≥ 32 GB). After the
    /// profile-based bucketing was retired (task #26) this constant is the single source of
    /// truth ONLY for the routing decision service's <c>GetTargetClass</c> / hardware
    /// role-slot rules (Stage-2 scoring). It is NOT a capacity/ceiling source: context
    /// capacities derive from the node's pre-configured num_ctx, the model's native GGUF
    /// context length, or the assumed 128k fallback (tasks #25/#26).
    /// </summary>
    public const int ServerClassVramThresholdMB = 32_768;

    /// <summary>
    /// The node's pre-configured Modelfile <c>num_ctx</c> from a derived capability: presence
    /// is detected through the CDefault vs CMax distinction (task #25's
    /// <c>DeriveCapability</c> sets <c>CDefault = ModelfileNumCtx ?? 4096</c> while
    /// <c>CMax = ModelfileNumCtx ?? native ?? 128k</c>, so a pre-configured num_ctx is the
    /// only case where CDefault is not the Ollama 4096 default). Returns null when the
    /// Modelfile sets no explicit num_ctx.
    /// </summary>
    internal static int? ModelfileNumCtx(TargetModelCapability capability)
    {
        return capability.CDefault > ModelCapabilityEngine.OllamaDefaultContext ? capability.CDefault : null;
    }

    /// <summary>
    /// The model's effective native context length for capacity math: the discovered native
    /// GGUF context limit, or — when a Modelfile pre-configures an explicit num_ctx ABOVE the
    /// native length (e.g. an explicit 131,072 pin on a 32k-native model) — that larger
    /// pre-configured value (the operator declared a larger capacity than the GGUF default).
    /// Returns null when neither is known.
    /// </summary>
    internal static int? EffectiveNativeContext(TargetModelCapability capability)
    {
        var native = capability.NativeContextLimit;
        var modelfile = ModelfileNumCtx(capability);
        if (native is null)
        {
            return modelfile;
        }

        if (modelfile is { } pin && pin > native.Value)
        {
            return pin;
        }

        return native;
    }

    /// <summary>
    /// The two-bucket baseline (the "optimal" bucket): the node's pre-configured Modelfile
    /// num_ctx when present, else the model's effective native context limit, else the
    /// assumed 128k fallback (human-approved correction: the fallback is 128k, NOT the
    /// Ollama 4,096 default).
    /// </summary>
    internal static int BaselineBucket(TargetModelCapability capability)
    {
        return ModelfileNumCtx(capability)
            ?? EffectiveNativeContext(capability)
            ?? ModelCapabilityEngine.FallbackContextWindow;
    }

    /// <summary>
    /// Pure bucket selection over the two-bucket MoE ladder (task #26). The baseline bucket
    /// is the node's pre-configured context (the Modelfile num_ctx when present, else the
    /// model's effective native context limit, else the assumed 128k fallback); the single
    /// extensible bucket is <c>min(native ?? 128k, maxContextTokensOverride ?? ∞)</c>.
    /// The requirement is <c>min(estimatedTokens + contextFitMarginTokens, clientNumCtx ?? ∞)</c>
    /// (the client's static value is an upper-bound preference that can only LOWER the
    /// requirement); it is clamped to the extensible limit. Requirement ≤ baseline selects the
    /// baseline bucket (injected = <c>min(baseline, extensibleMax)</c> — a Modelfile pin ABOVE
    /// the operator cap injects the cap and still reports <see cref="ContextSizingMode.Baseline"/>);
    /// otherwise the extensible bucket is injected. No intermediate bucket, no step-grid
    /// snapping.
    /// </summary>
    public static (ContextSizingMode Mode, int Injected, int Baseline, int ExtensibleMax) SelectBucket(
        TargetModelCapability capability,
        long estimatedTokens,
        int? clientNumCtx,
        int contextFitMarginTokens,
        int? maxContextTokensOverride)
    {
        var baseline = BaselineBucket(capability);
        var extensibleMax = ModelCapabilityEngine.GetMoeExtensibleCapacity(capability, maxContextTokensOverride);

        var requirement = (long)estimatedTokens + Math.Max(0, contextFitMarginTokens);
        if (clientNumCtx is { } client && client > 0)
        {
            requirement = Math.Min(requirement, client);
        }

        requirement = Math.Min(requirement, extensibleMax);

        if (requirement <= baseline)
        {
            // Baseline bucket: inject the baseline, clamped to the extensible limit (covers
            // a Modelfile pin ABOVE the operator cap: inject the cap, report Baseline).
            var injected = Math.Min(baseline, extensibleMax);
            return (ContextSizingMode.Baseline, injected, baseline, extensibleMax);
        }

        // Expanded bucket: the single extensible limit (the final clamp is inherent — the
        // requirement was already clamped to it).
        return (ContextSizingMode.Expanded, extensibleMax, baseline, extensibleMax);
    }

    /// <summary>
    /// Computes the full sizing decision for one (target, model, request) triple (task #26,
    /// MoE-only dynamic sizing):
    /// <list type="bullet">
    /// <item><b>MoE</b> — two-bucket ladder: requirement
    /// <c>min(estimated + ContextFitMarginTokens, clientNumCtx ?? ∞)</c> ≤ baseline
    /// (Modelfile num_ctx ?? native ?? 128k) → <see cref="ContextSizingMode.Baseline"/> with
    /// injected <c>min(baseline, extensibleMax)</c>; otherwise
    /// <see cref="ContextSizingMode.Expanded"/> with injected
    /// <c>extensibleMax = min(native ?? 128k, MaxContextTokens ?? ∞)</c> (which doubles as
    /// the Stage-1 MoE qualification capacity).</item>
    /// <item><b>Dense</b> — clamp-only: a client <c>num_ctx</c> ABOVE the pre-configured
    /// baseline is clamped down to it (<see cref="ContextSizingMode.Clamped"/>, injected =
    /// baseline); otherwise the payload is untouched
    /// (<see cref="ContextSizingMode.PassThrough"/>, injected = 0 sentinel). Dense values are
    /// never raised, never bucketed, never leased (a dense KV refill costs 300 s+).</item>
    /// </list>
    /// The retired profile table is NOT consulted (profiles remain bindable but are
    /// ignored). <paramref name="maxContextTokensOverride"/> is the model's
    /// <see cref="ModelThresholds.MaxContextTokens"/> (applied to MoE only).
    /// </summary>
    public static ContextSizingDecision Decide(
        DynamicContextOptions dynamicOptions,
        TargetModelCapability capability,
        long estimatedTokens,
        int? clientNumCtx,
        int contextFitMarginTokens,
        int? maxContextTokensOverride)
    {
        if (capability.Architecture == ModelArchitectureKind.Moe)
        {
            var (mode, injected, baseline, extensibleMax) = SelectBucket(
                capability,
                estimatedTokens,
                clientNumCtx,
                contextFitMarginTokens,
                maxContextTokensOverride);

            var clamped = clientNumCtx is { } requestedValue && requestedValue > injected;
            return new ContextSizingDecision(
                DynamicContextOptions.MoeProfileName,
                injected,
                extensibleMax,
                clamped,
                mode,
                baseline);
        }

        // Dense: clamp-only pass-through (no profile consulted, no lease ever).
        var denseBaseline = BaselineBucket(capability);

        if (dynamicOptions.DenseClampEnabled &&
            clientNumCtx is { } denseClientValue &&
            denseClientValue > denseBaseline)
        {
            return new ContextSizingDecision(
                DynamicContextOptions.MoeProfileName,
                denseBaseline,
                capability.CMax,
                true,
                ContextSizingMode.Clamped,
                denseBaseline);
        }

        return new ContextSizingDecision(
            DynamicContextOptions.MoeProfileName,
            0, // Sentinel: "no rewrite" — the payload is left untouched.
            capability.CMax,
            false,
            ContextSizingMode.PassThrough,
            denseBaseline);
    }

    /// <summary>
    /// Stage-1 hardware suitability hard filter over a derived capability: a
    /// <see cref="ModelArchitectureKind.LargeDense"/> model (≥ 14 B) is INCAPABLE on targets
    /// with effective <c>MaxVramMB &lt; 12000</c> (hard veto, already encoded in
    /// <see cref="TargetModelCapability.HardwareCapable"/>); Dense ≤ 27 B is allowed on 16 GB
    /// up to its pre-configured capacity; all models up to the native limit are allowed on
    /// 96 GB. The veto ignores activity state entirely - the capability carries no notion of
    /// "loaded/idle", so a LargeDense model is excluded from a small target EVEN WHEN IDLE.
    /// Used by the fleet qualification stage; a thin, well-documented wrapper for pure,
    /// unit-testable behavior.
    /// </summary>
    public static bool IsHardwareSuitable(TargetModelCapability capability)
    {
        return capability.HardwareCapable;
    }
}
