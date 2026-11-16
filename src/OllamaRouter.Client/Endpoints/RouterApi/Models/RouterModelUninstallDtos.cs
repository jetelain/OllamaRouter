using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Models;

/// <summary>
/// Request body of <c>POST /api/router/models/uninstall</c> (task #126): the model to
/// delete and the non-empty target list to delete it from. Body-only contract (no query
/// fallback — the target list is inherently multi-valued); an absent/empty/malformed body,
/// a blank <c>model</c> or an empty/blank <c>targets</c> list → 400.
/// </summary>
public sealed record RouterModelUninstallRequest(
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("targets")] IReadOnlyList<string>? Targets = null);

/// <summary>
/// One per-target row of the uninstall response: the CANONICAL configured target name,
/// whether the daemon's <c>DELETE /api/delete</c> succeeded and the fail-soft error text
/// (the daemon's 404 "model not found" / an unreachable daemon) when it did not. Partial
/// failures are still a 200 — the failing rows simply carry <c>success:false</c>.
/// </summary>
public sealed record RouterModelUninstallTargetResult(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

/// <summary>
/// 200 response of <c>POST /api/router/models/uninstall</c>: the model name echoed back
/// plus the per-target execution rows IN REQUEST ORDER. 400/404 (unknown model or target
/// name — checked before ANY daemon call) use the shared <c>OllamaErrorResponse</c> instead.
/// </summary>
public sealed record RouterModelUninstallResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterModelUninstallTargetResult> Targets,
    [property: JsonPropertyName("message")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);
