using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// The chosen (profile, bucket, effective ceiling) for one (target, model, request) triple.
/// <see cref="InjectedNumCtx"/> is the value to write into the outgoing payload's
/// <c>options.num_ctx</c>; <see cref="EffectiveCeiling"/> is the effective ceiling
/// (min(profile max, Modelfile num_ctx, hardware CMax)); <see cref="ClientRequestedClamped"/>
/// is true when the client's static <c>num_ctx</c> exceeded the injected value (it was an
/// upper-bound preference, never a disqualifier).
/// </summary>
public sealed record ContextSizingDecision(
    string ProfileName,
    int InjectedNumCtx,
    int EffectiveCeiling,
    bool ClientRequestedClamped);

/// <summary>
/// Stateless dynamic context sizing engine (Phase 2, items 3-4 of the Multi-Target
/// Zero-Config Routing Plan): maps an architecture + target hardware pair to a bucket
/// profile, selects the quantized elastic context bucket for a request, and evaluates the
/// Stage-1 hardware suitability hard filter. Sizing is a pure function of (options,
/// capability, request facts) - lease/cooldown hysteresis state is maintained by the
/// Auto-Shrink Manager (<c>IContextAutoShrinkManager</c>, Phase 3 item 2), which consumes
/// this engine's decision per request.
/// </summary>
public static class DynamicContextSizingEngine
{
    /// <summary>
    /// Minimum effective VRAM (in MB) of a server-class target (≥ 32 GB): dense models at or
    /// above this budget use the <see cref="DynamicContextOptions.DenseServer96gbProfileName"/>
    /// profile (the "96gb" in its name is a historical artifact; 32 GB targets select it
    /// too, and operators may override the profile via <c>OllamaRouter:DynamicContext:Profiles</c>).
    /// This constant is the single source of truth for the server-class boundary used for
    /// PROFILE BUCKETING (<see cref="SelectProfile"/>) and the routing decision service's
    /// <c>GetTargetClass</c> / hardware role-slot rules. It is NOT a capacity/ceiling source:
    /// context capacities (C_max) derive from the node's pre-configured num_ctx, the model's
    /// native GGUF context length, or the assumed 128k fallback (task #25).
    /// </summary>
    public const int ServerClassVramThresholdMB = 32_768;

    /// <summary>
    /// Selects the bucket profile name for a (architecture, target VRAM) pair:
    /// MoE -&gt; <c>Moe</c> (any VRAM class); LargeDense -&gt; 96 GB-class (≥
    /// <see cref="ServerClassVramThresholdMB"/> MB) ? <c>DenseServer96gb</c> :
    /// <c>DenseDesktop16gb</c>; SmallDense -&gt; <c>SmallDense</c>. Unknown VRAM
    /// (<c>null</c>) for dense models falls back to the conservative
    /// <c>DenseDesktop16gb</c> profile.
    /// </summary>
    public static string SelectProfile(ModelArchitectureKind architecture, int? maxVramMB)
    {
        return architecture switch
        {
            ModelArchitectureKind.Moe => DynamicContextOptions.MoeProfileName,
            ModelArchitectureKind.LargeDense => (maxVramMB is not null && maxVramMB >= ServerClassVramThresholdMB)
                ? DynamicContextOptions.DenseServer96gbProfileName
                : DynamicContextOptions.DenseDesktop16gbProfileName,
            _ => DynamicContextOptions.SmallDenseProfileName
        };
    }

    /// <summary>
    /// Pure bucket selection over one profile. The injected value is the smallest profile
    /// bucket that is ≥ min(max(rawEstimatedTokens, clientRequestedNumCtx ?? 0), ceiling)
    /// (the approved "LargestAffordableBucket, capped at Ceiling" formula),
    /// with a final clamp to <paramref name="ceiling"/>. The client's static
    /// <c>num_ctx</c> is an upper-bound preference and NEVER a disqualifier: a large static
    /// value (e.g. 131072 from Continue/Zed) is clamped down to the target's hardware-safe
    /// bucket, while a small static value (e.g. 8192) can never truncate a larger prompt
    /// (the estimate wins and selects a bigger bucket). A prompt larger than the ceiling is
    /// clamped to the ceiling rather than rejected.
    /// </summary>
    /// <param name="dynamicOptions">The <c>DynamicContext</c> options section (profiles + step grid).</param>
    /// <param name="profileName">Profile to bucket over (see <see cref="SelectProfile"/>).</param>
    /// <param name="rawEstimatedTokens">Overhead-corrected estimated prompt token count.</param>
    /// <param name="clientRequestedNumCtx">The client's static <c>options.num_ctx</c>, if any.</param>
    /// <param name="ceiling">
    /// Effective ceiling in tokens (min(profile max, explicit Modelfile num_ctx when set,
    /// hardware CMax)). The injected value never exceeds it.
    /// </param>
    public static int SelectBucket(
        DynamicContextOptions dynamicOptions,
        string profileName,
        long rawEstimatedTokens,
        int? clientRequestedNumCtx,
        int ceiling)
    {
        var profile = dynamicOptions.GetProfile(profileName);
        var step = dynamicOptions.BucketStepTokens > 0
            ? dynamicOptions.BucketStepTokens
            : DynamicContextOptions.DefaultBucketStepTokens;

        var max = Math.Max(1, profile.MaxContext);
        var baseline = profile.DefaultContext > 0
            ? profile.DefaultContext
            : Math.Min(ModelCapabilityEngine.OllamaDefaultContext, max);

        // Assemble the ascending bucket ladder: baseline, (intermediate), max. The
        // intermediate is either the profile's explicit (hand-tuned) value or - for custom
        // profiles with no explicit value - synthesized on the BucketStepTokens grid as
        // snapUpToStep(ceil((baseline + max) / 2), step), clamped ≤ max.
        var buckets = new List<int>(3)
        {
            baseline
        };

        if (profile.IntermediateContext is { } explicitIntermediate && explicitIntermediate > baseline && explicitIntermediate < max)
        {
            buckets.Add(explicitIntermediate);
        }
        else if (profile.IntermediateContext is null && max > baseline)
        {
            var midpoint = (int)Math.Ceiling((baseline + (double)max) / 2.0);
            var snapped = SnapUpToStep(midpoint, step);
            if (snapped > baseline && snapped <= max)
            {
                buckets.Add(snapped);
            }
        }

        if (max > baseline)
        {
            buckets.Add(max);
        }

        // The client value is an upper-bound preference: the requirement is
        // max(rawEstimatedTokens, clientRequestedNumCtx ?? 0), capped at the effective
        // ceiling (clamping, never rejection — a client value above the ceiling never
        // disqualifies the target).
        var requirement = Math.Max(rawEstimatedTokens, clientRequestedNumCtx ?? 0);
        requirement = Math.Min(requirement, Math.Max(0, ceiling));

        // Smallest bucket ≥ requirement (requirement ≤ baseline ⇒ baseline bucket; a
        // requirement above the max bucket takes the max bucket).
        int selected = max;
        foreach (var bucket in buckets)
        {
            if (bucket >= requirement)
            {
                selected = bucket;
                break;
            }
        }

        // Final ceiling clamp (covers a Modelfile num_ctx BELOW the selected bucket, and an
        // explicit Modelfile pin that caps CMax at an off-bucket value such as 221,184).
        return (int)Math.Min(selected, Math.Max(0, ceiling));
    }

    /// <summary>
    /// Computes the full sizing decision for one (target, model, request) triple:
    /// <c>EffectiveCeiling = min(profile MaxContext, ceiling)</c> where
    /// <paramref name="ceiling"/> is the target's <c>CMax</c> (which already honors the
    /// hierarchy: explicit Modelfile num_ctx &gt; hardware-derived safe ceiling), followed by
    /// bucket selection.
    /// </summary>
    public static ContextSizingDecision Decide(
        DynamicContextOptions dynamicOptions,
        ModelArchitectureKind architecture,
        int? maxVramMB,
        int ceiling,
        long estimatedTokens,
        int? clientRequestedNumCtx)
    {
        var profileName = SelectProfile(architecture, maxVramMB);
        var profile = dynamicOptions.GetProfile(profileName);
        var effectiveCeiling = Math.Min(profile.MaxContext, Math.Max(0, ceiling));
        var injected = SelectBucket(dynamicOptions, profileName, estimatedTokens, clientRequestedNumCtx, effectiveCeiling);
        var clamped = clientRequestedNumCtx is { } clientValue && clientValue > injected;

        return new ContextSizingDecision(profileName, injected, effectiveCeiling, clamped);
    }

    /// <summary>
    /// Stage-1 hardware suitability hard filter over a derived capability: a
    /// <see cref="ModelArchitectureKind.LargeDense"/> model (≥ 14 B) is INCAPABLE on targets
    /// with effective <c>MaxVramMB &lt; 12000</c> (hard veto, already encoded in
    /// <see cref="TargetModelCapability.HardwareCapable"/>); Dense ≤ 27 B is allowed on 16 GB
    /// up to the 49,152 ceiling; all models up to 256k are allowed on 96 GB. The veto ignores
    /// activity state entirely - the capability carries no notion of "loaded/idle", so a
    /// LargeDense model is excluded from a small target EVEN WHEN IDLE. Used by the fleet
    /// qualification stage; a thin, well-documented wrapper for pure, unit-testable behavior.
    /// </summary>
    public static bool IsHardwareSuitable(TargetModelCapability capability)
    {
        return capability.HardwareCapable;
    }

    private static int SnapUpToStep(int value, int step)
    {
        if (value <= 0)
        {
            return step;
        }

        return (int)(((long)value + step - 1) / step * step);
    }
}
