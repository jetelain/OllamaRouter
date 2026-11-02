namespace OllamaRouter.Services;

/// <summary>
/// One preload candidate evaluation: a single effective fleet target (declaration order; the
/// Cloud pseudo-target is never a candidate) with its eligibility for the requested model +
/// context intent and the ordered skip reason of the FIRST failing safety gate when not
/// eligible ("not routable (disabled/offline)" → "model not present in target catalog" →
/// "capability undiscoverable" → LargeDense veto / "hardware incapable" → "context ceiling
/// {ceiling} below intent {intent}"), the live in-flight count, the resident state (same
/// exact-or-<see cref="RoutingDecisionService.NormalizeModelName(string)"/> name rule as the
/// model-detail matrix), the target's effective context ceiling (MoE extensible capacity for
/// MoE models, dense CMax otherwise; the Ollama default sentinel when undiscoverable), the
/// server/cloud-class flag and the busy flag (<c>inFlightRequests &gt; 0</c>).
/// </summary>
public sealed record PreloadCandidateEvaluation(
    string Target,
    bool Eligible,
    string? SkipReason,
    int InFlightRequests,
    bool IsResident,
    int ContextCeiling,
    bool ServerClass,
    bool Busy);

/// <summary>
/// Full result of a model preload selection (and, for the POST path, execution): the resolved
/// model name, the selected target (null when nothing qualified → 503 mapping), the effective
/// keep-alive (seconds; -1 = indefinitely) and the exact <c>num_ctx</c> applied
/// (<c>min(intent, selected ceiling)</c>), the active context intent (request value or the
/// model's recommendedContextWindow soft fallback), the load acceptance / residency
/// confirmation flags (null on the side-effect-free preview and on the 404/503 shapes), the
/// load-call wall-clock, the full candidate evaluation (every effective target in declaration
/// order with skip reasons), the human-readable selection reason, and the 404 error string
/// ("model not found" / "target not found: {name}") when set.
/// </summary>
public sealed record ModelPreloadResult(
    string Model,
    string? SelectedTarget,
    int AppliedKeepAliveSeconds,
    int? AppliedNumCtx,
    int? ContextIntentTokens,
    bool? LoadAccepted,
    bool? ResidencyConfirmed,
    long? PreloadDurationMs,
    IReadOnlyList<PreloadCandidateEvaluation> Candidates,
    string? Reason,
    string? Error);

/// <summary>
/// Router-owned model pre-load: selects WHICH fleet target should host a model
/// (reusing the existing capability/discovery machinery — no new scoring pipeline) and issues
/// ONE bounded <c>keep_alive</c>-aware load call (<see cref="IOllamaModelCatalogClient.PreloadModelAsync"/>)
/// directly to that target's daemon (never through the routing middleware / YARP), followed by
/// a bounded <c>/api/ps</c> residency confirmation. The API is stateless and idempotent.
/// Selection: enabled + liveness-online + model present + hardware-capable + context-ceiling
/// qualified (intent + fit margin ≤ ceiling for an explicit intent), ranked idle-first (a busy
/// node is bypassed in favor of an idle capable node), server/cloud-class first for large
/// intents (≥ <see cref="ModelPreloadService.LargeIntentThresholdTokens"/>), then lowest
/// in-flight, then declaration order. The Cloud pseudo-target is never a candidate.
/// </summary>
public interface IModelPreloadService
{
    /// <summary>
    /// Full preload: selection + bounded load call + bounded residency confirmation. Returns a
    /// result carrying <see cref="ModelPreloadResult.Error"/> (→ 404 mapping) when the model or
    /// the pinned target is unknown, <see cref="ModelPreloadResult.SelectedTarget"/> null (→
    /// 503 mapping) when no candidate qualified, or the selected target plus the load/
    /// residency flags otherwise. Never throws on daemon failures (fail-soft flags).
    /// </summary>
    /// <param name="modelName">The requested model name (fleet-catalog resolution applies, including the tagless-prefix fallback).</param>
    /// <param name="keepAliveSeconds">Explicit retention (-1 = indefinitely, &gt; 0 = seconds); null = the selected target's effective class default.</param>
    /// <param name="contextIntentTokens">Explicit context intent in tokens; null = the model's recommendedContextWindow soft intent.</param>
    /// <param name="pinnedTarget">Explicit target pin (case-insensitive; overrides selection, not the safety gates); null = auto-select.</param>
    Task<ModelPreloadResult?> PreloadAsync(
        string modelName,
        int? keepAliveSeconds,
        int? contextIntentTokens,
        string? pinnedTarget,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Side-effect-free preview: the EXACT same selection as <see cref="PreloadAsync"/> but with
    /// ZERO daemon calls (no <c>/api/generate</c>, no <c>/api/ps</c> load/confirmation — only the
    /// catalog /api/tags + /api/show discovery the capability path itself performs);
    /// <see cref="ModelPreloadResult.LoadAccepted"/> / <see cref="ModelPreloadResult.ResidencyConfirmed"/>
    /// / <see cref="ModelPreloadResult.PreloadDurationMs"/> are null.
    /// </summary>
    Task<ModelPreloadResult?> PreviewAsync(
        string modelName,
        int? keepAliveSeconds,
        int? contextIntentTokens,
        string? pinnedTarget,
        CancellationToken cancellationToken = default);
}
