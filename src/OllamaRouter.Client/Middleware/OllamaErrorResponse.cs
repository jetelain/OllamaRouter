using System.Text.Json.Serialization;

namespace OllamaRouter.Middleware;

/// <summary>
/// Shared Ollama-protocol error payload (<c>{"error": "..."}</c>) returned by the router's
/// proxy and management endpoints on failure. The optional <see cref="Message"/> field
/// (task #146) covers the Setup API's <c>{"success": false, "message": "..."}</c> failure
/// bodies: it is deserialized client-side for human-readable error surfacing and omitted
/// from serialization when null (the daemon wire format stays byte-identical).
/// </summary>
public sealed record OllamaErrorResponse(
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);

/// <summary>
/// Diagnostics payload returned as <c>503 Service Unavailable</c> when all enabled
/// fleet targets are offline/unknown and Cloud Overflow is not available for the
/// request. The <c>error</c> field mirrors <see cref="OllamaErrorResponse"/> so clients
/// that parse <c>error</c> keep working.
/// </summary>
public sealed record AllOfflineDiagnostics(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("targets")] IReadOnlyList<OfflineTargetInfo> Targets,
    [property: JsonPropertyName("cloud_enabled")] bool CloudEnabled);

/// <summary>
/// Per-target diagnostics for the all-offline payload: the raw (non-TTL) liveness
/// state, lowercased, plus the most recent probe error (null for Unknown).
/// </summary>
public sealed record OfflineTargetInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("last_error")] string? LastError);
