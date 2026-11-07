using OllamaRouter.Middleware;
using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Preload;

/// <summary>
/// Optional request body of <c>POST /api/router/models/{*modelName}/preload</c> (task #44):
/// the preload retention in seconds (<c>-1</c> = indefinitely, positive = seconds,
/// null/absent = the selected target's effective class default: cloud/server-class = -1,
/// desktop = 900), the context intent in tokens (null/absent = the model's
/// recommendedContextWindow soft intent), and the explicit target pin (null/absent =
/// auto-select; the pin overrides SELECTION, not the safety gates). The same parameters
/// are also accepted as query string values; body values win per field when both are
/// present (the vram-reclaim pattern).
/// </summary>
public sealed record RouterModelPreloadRequest(
    [property: JsonPropertyName("keepAliveSeconds")] int? KeepAliveSeconds = null,
    [property: JsonPropertyName("contextIntentTokens")] int? ContextIntentTokens = null,
    [property: JsonPropertyName("target")] string? Target = null);

/// <summary>
/// One candidate row of the preload response: every effective fleet target (declaration
/// order; the Cloud pseudo-target is never a candidate) with its eligibility, the ordered
/// skip reason of the first failing gate (omitted when eligible), the live in-flight
/// count, the resident state, the effective context ceiling (MoE extensible capacity /
/// dense CMax; the Ollama default sentinel when undiscoverable), the server/cloud-class
/// flag and the busy flag (<c>inFlightRequests > 0</c> — the v1 busy definition).
/// </summary>
public sealed record RouterModelPreloadCandidate(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("eligible")] bool Eligible,
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("isResident")] bool IsResident,
    [property: JsonPropertyName("contextCeiling")] int ContextCeiling,
    [property: JsonPropertyName("serverClass")] bool ServerClass,
    [property: JsonPropertyName("busy")] bool Busy,
    [property: JsonPropertyName("skipReason")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SkipReason = null);

/// <summary>
/// Response of <c>POST /api/router/models/{*modelName}/preload</c> and
/// <c>GET /api/router/models/{*modelName}/preload/preview</c> (task #44): the selected
/// target, the effective <c>keep_alive</c> (seconds; -1 = indefinitely) and the exact
/// <c>num_ctx</c> actually applied (<c>min(intent, selected ceiling)</c>), the active context
/// intent (request value or recommendedContextWindow soft fallback), the load acceptance
/// and bounded /api/ps residency confirmation flags plus the load-call wall-clock (all
/// null on the side-effect-free preview), the full candidate evaluation and the
/// human-readable selection reason. This shape is only emitted for 200 — the 404/503
/// shapes use the shared <see cref="OllamaErrorResponse"/>.
/// </summary>
public sealed record RouterModelPreloadResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("selectedTarget")] string? SelectedTarget,
    [property: JsonPropertyName("keepAliveSeconds")] int AppliedKeepAliveSeconds,
    [property: JsonPropertyName("numCtx")] int? AppliedNumCtx,
    [property: JsonPropertyName("contextIntentTokens")] int? ContextIntentTokens,
    [property: JsonPropertyName("loadAccepted")] bool? LoadAccepted,
    [property: JsonPropertyName("residencyConfirmed")] bool? ResidencyConfirmed,
    [property: JsonPropertyName("preloadDurationMs")] long? PreloadDurationMs,
    [property: JsonPropertyName("candidates")] IReadOnlyList<RouterModelPreloadCandidate> Candidates,
    [property: JsonPropertyName("reason")] string? Reason);
