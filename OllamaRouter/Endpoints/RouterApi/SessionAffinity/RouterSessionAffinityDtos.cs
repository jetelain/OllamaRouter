using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.SessionAffinity;

/// <summary>
/// Response of <c>POST /api/router/sessions/affinity/release</c> (task #86):
/// <see cref="SessionId"/> echoes the trimmed
/// <c>X-OllamaRouter-Session-Id</c> request header exactly as sent by the client;
/// <see cref="Released"/> is true iff a LIVE session-affinity lease was found and removed —
/// false for unknown session IDs, TTL-expired (not-yet-swept) leases, and already-released
/// sessions (the endpoint is idempotent: it is ALWAYS 200, except 400 on a missing/blank
/// header).
/// </summary>
public sealed record RouterSessionAffinityReleaseResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("released")] bool Released);
