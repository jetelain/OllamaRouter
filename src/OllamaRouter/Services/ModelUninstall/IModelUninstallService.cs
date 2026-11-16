namespace OllamaRouter.Services.ModelUninstall;

/// <summary>
/// The per-target result of a model uninstall (task #126): the CANONICAL configured
/// target name (echoed even when the request used a different casing), whether the
/// daemon's <c>POST /api/delete</c> succeeded, and the fail-soft error text (the daemon's
/// 404 "model not found" / an unreachable daemon) when it did not.
/// </summary>
public sealed record ModelUninstallTargetOutcome(string Target, bool Success, string? Error = null);

/// <summary>
/// The full result of a model uninstall request (task #126): <see cref="Error"/> is set on
/// the 404 shapes ("model not found: {model}" / "target not found: {name}") — both validated
/// BEFORE any daemon call — and <see cref="Targets"/> carries the per-target execution rows
/// (request order; a failing target never aborts the remaining ones).
/// </summary>
public sealed record ModelUninstallResult(
    string Model,
    IReadOnlyList<ModelUninstallTargetOutcome> Targets,
    string? Error = null);

/// <summary>
/// Uninstalls (deletes) a model from one or more fleet targets (task #126, report item #7):
/// the destructive counterpart of the model pre-load (task #44). Validation is a pure
/// fleet-state check (shared <c>RouterApiSharedHelpers</c> fleet-name enumeration + the
/// effective-target roster) BEFORE any daemon is touched; execution is ONE fail-soft
/// <c>POST {target}/api/delete</c> per selected target (in request order, duplicates
/// collapsed case-insensitively) via the raw-HttpClient catalog client — no routing
/// middleware, no lease mutation. After a successful delete the model disappears from the
/// daemon's /api/tags and /api/ps, so the digest-keyed capability cache and the 2 s
/// /api/ps execution tracker invalidate naturally (no explicit state invalidation needed).
/// </summary>
public interface IModelUninstallService
{
    /// <summary>
    /// Deletes <paramref name="model"/> from every name in <paramref name="targetNames"/>
    /// (request order; names matched case-insensitively against the effective targets).
    /// Returns a result with <c>Error</c> set (404 shape) for an unknown fleet model or an
    /// unknown target name — with ZERO daemon delete calls issued — or per-target fail-soft
    /// rows on execution (a single target's daemon error never aborts the rest).
    /// </summary>
    Task<ModelUninstallResult?> UninstallAsync(
        string model, IReadOnlyList<string> targetNames, CancellationToken cancellationToken = default);
}
