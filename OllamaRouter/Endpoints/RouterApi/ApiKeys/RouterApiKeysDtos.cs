using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.ApiKeys;

/// <summary>
/// The apiSecurity section of <c>GET /api/router/config</c> (task #50): the EFFECTIVE
/// (live-bound) API-key security settings — the enforcement-scope switch and the default-
/// key auto-rotation interval. No key material is ever part of the response.
/// </summary>
public sealed record ApiSecurityConfigDto(
    [property: JsonPropertyName("requireKeyForAllEndpoints")] bool RequireKeyForAllEndpoints,
    [property: JsonPropertyName("defaultKeyRotationHours")] int DefaultKeyRotationHours);

/// <summary>
/// One masked row of <c>GET /api/router/api-keys</c> (task #50): identity + timestamps
/// ONLY — the response NEVER contains the hash or the raw key value.
/// </summary>
public sealed record ApiKeyInfoResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Response of <c>GET /api/router/api-keys</c> (task #50): the masked user key list
/// (empty array when no user keys exist).
/// </summary>
public sealed record ApiKeysListResponse(
    [property: JsonPropertyName("keys")] IReadOnlyList<ApiKeyInfoResponse> Keys);

/// <summary>
/// Request body of <c>POST /api/router/api-keys</c> (task #50): <c>name</c> is required
/// (≤ 64 chars, no whitespace / slash / colon / control characters, case-insensitively
/// unique); <c>validFor</c> is an OPTIONAL integer NUMBER OF HOURS (e.g. 168 = 7 days;
/// omitted/null = no expiry; &lt; 1 → 400).
/// </summary>
public sealed record ApiKeyCreateRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("validFor")] int? ValidFor = null);

/// <summary>
/// 201 response of <c>POST /api/router/api-keys</c> (task #50): the raw base64url key
/// (<c>key</c>) is returned EXACTLY ONCE — only its SHA-256 hash is persisted; <c>createdAt</c>
/// / <c>expiresAt</c> are ISO 8601 UTC timestamps (expiresAt null = no expiry).
/// </summary>
public sealed record ApiKeyCreatedResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Response of <c>DELETE /api/router/api-keys/{name}</c> (task #50): success flag; on 404
/// <c>error</c> carries "API key not found: {name}" (omitted on success).
/// </summary>
public sealed record ApiKeyDeletedResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

/// <summary>
/// 200 response of <c>POST /api/router/api-keys/default/rotate</c> (task #50): the NEW raw
/// default key (returned once; also logged at INFO) and its <c>generatedAt</c> (ISO 8601
/// UTC); the previous default key stopped working immediately (no grace period).
/// </summary>
public sealed record ApiKeyRotatedResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt);
