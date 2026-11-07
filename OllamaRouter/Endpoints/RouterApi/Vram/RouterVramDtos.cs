using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Vram;

/// <summary>
/// Optional request body of <c>POST /api/router/vram/reclaim</c> / <c>POST /api/router/fleet/unload</c>:
/// the target name to unload (null/absent = all effective targets) and whether to
/// administratively disable the processed target(s) (default false). The same parameters are
/// also accepted as query string values; body values win when both are present.
/// </summary>
public sealed record RouterVramReclaimRequest(
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("disableTarget")] bool DisableTarget = false);

/// <summary>
/// One row of the <c>targets</c> array of the fleet VRAM reclaim response: the target's
/// canonical configured name, the models unloaded on it (empty when the daemon was
/// unreachable or had no running models) and whether it was administratively disabled.
/// </summary>
public sealed record RouterVramReclaimTargetResult(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("unloadedModels")] IReadOnlyList<string> UnloadedModels,
    [property: JsonPropertyName("disabled")] bool Disabled);

/// <summary>
/// Response payload of <c>POST /api/router/vram/reclaim</c> / <c>POST /api/router/fleet/unload</c>:
/// whether the operation executed against a valid target set (individual unreachable daemons
/// are fail-soft), the total number of unloaded models across the fleet, and the per-target
/// results in configured fleet order. <see cref="Message"/> is omitted (null) on success and
/// set to "no targets configured" when the effective target list is empty.
/// </summary>
public sealed record RouterVramReclaimResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("totalUnloaded")] int TotalUnloaded,
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterVramReclaimTargetResult> Targets,
    [property: JsonPropertyName("message")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);
