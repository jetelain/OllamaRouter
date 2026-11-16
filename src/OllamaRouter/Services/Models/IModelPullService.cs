using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Options;

namespace OllamaRouter.Services.Models;

/// <summary>
/// Pre-stream target resolution outcome of the model pull (task #125): a resolved
/// <see cref="Target"/> (ready to pull) OR a <see cref="StatusCode"/> + <see cref="Error"/>
/// pair the endpoint surfaces as a plain <c>OllamaErrorResponse</c> BEFORE any stream starts
/// (400 non-Ollama type, 404 unknown target / no targets configured, 409 disabled or
/// offline).
/// </summary>
public sealed record ModelPullTargetResolution(
    OllamaTargetOptions? Target,
    int? StatusCode = null,
    string? Error = null);

/// <summary>
/// The model-pull service of <c>POST /api/router/models/pull</c> (task #125): resolves the
/// pull target LIVE from <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c> (runtime-added
/// targets work — the whole point of the dedicated endpoint over the YARP
/// <c>POST /api/pull</c> passthrough, whose route/cluster tables are the STARTUP snapshot),
/// validates up front, then drives the target daemon's <c>POST /api/pull</c> (streaming,
/// progress lines forwarded) + — when a context alias is requested — the daemon's
/// <c>POST /api/create</c> (the alias reuses the base blobs; it only writes a new Modelfile
/// blob pinning <c>num_ctx</c>), and finally best-effort refreshes the target's
/// <c>/api/tags</c> catalog cache so the cartography matrix shows the model + alias
/// immediately. Cancellation (the request's <c>HttpContext.RequestAborted</c>) aborts the
/// daemon call and the stream simply ends — no terminal event, no phantom success.
/// </summary>
public interface IModelPullService
{
    /// <summary>
    /// Pre-stream target validation (never streams): null <paramref name="targetName"/> →
    /// the primary-target fallback with the exact pinned-class semantics ("Local" when
    /// present+enabled, else first enabled effective target, else first effective target);
    /// unknown name → 404; <c>Type != "ollama"</c> → 400 (pull is Ollama-protocol only);
    /// disabled → 409; offline (fresh liveness probe via
    /// <see cref="OllamaRouter.Services.Monitoring.ITargetLivenessService.EnsureKnownAsync"/>)
    /// → 409.
    /// </summary>
    ValueTask<ModelPullTargetResolution> ResolveAsync(string? targetName, CancellationToken cancellationToken = default);

    /// <summary>
    /// The streaming pull: <c>started</c> → (presence short-circuit <c>present</c> OR
    /// daemon <c>progress</c>* lines) → when <paramref name="aliasContext"/> is set:
    /// <c>alias_started</c> → <c>alias_created</c> → terminal <c>success</c> (or a terminal
    /// <c>error</c> event with <c>stage "pull"</c> / <c>"alias"</c> on failure). A model
    /// already present on the target skips the daemon pull entirely (re-runs are fast; a
    /// stale 30 s cache costs at most one daemon no-op pull). Cancellation ends the stream
    /// silently (the <c>OperationCanceledException</c> propagates to the caller).
    /// </summary>
    /// <param name="model">The validated <c>name[:tag]</c> model reference.</param>
    /// <param name="target">The resolved + validated target (Ollama type, enabled, online).</param>
    /// <param name="aliasContext">The alias context (tokens) — null disables the alias step.</param>
    /// <param name="alias">The precomputed alias name (from
    /// <c>ModelPullNaming.TryComputeAliasName</c>; non-null iff <paramref name="aliasContext"/> is set).</param>
    IAsyncEnumerable<OllamaRouterModelPullEvent> PullAsync(
        string model,
        OllamaTargetOptions target,
        int? aliasContext,
        string? alias,
        CancellationToken cancellationToken = default);
}
