using OllamaRouter.Endpoints.RouterApi.Config;
using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Targets;

/// <summary>
/// Request body of <c>PUT /api/router/targets/{name}</c> (task #19): partial target
/// update — null/absent = no change (all four null → 400); <c>hardwareHint: ""</c> clears
/// the hint; <c>url</c> must be an absolute http(s) URI (trailing / stripped);
/// <c>maxParallel</c> must be &gt;= 1.
/// </summary>
public sealed record UpdateTargetRequest(
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("enabled")] bool? Enabled = null,
    [property: JsonPropertyName("hardwareHint")] string? HardwareHint = null,
    [property: JsonPropertyName("maxParallel")] int? MaxParallel = null);

/// <summary>
/// Response of <c>PUT /api/router/targets/{name}</c> (task #19): success flag + message,
/// and on success the updated target (omitted on failure).
/// </summary>
public sealed record UpdateTargetResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("target")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouterTargetConfigDto? Target = null);

/// <summary>
/// Request body of <c>POST /api/router/targets</c> (task #19): the target identity
/// (<c>name</c> ≤ 64 chars, no / \ :, not "Cloud"; <c>url</c> absolute http(s)), the
/// optional hardware hint (empty → null), the parallel budget (default 1) and
/// <c>testConnectionFirst</c> (default true: an immediate liveness probe runs before
/// persisting; probe failure → 422 with a retry hint).
/// </summary>
public sealed record AddTargetRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("maxParallel")] int MaxParallel = 1,
    [property: JsonPropertyName("testConnectionFirst")] bool TestConnectionFirst = true,
    [property: JsonPropertyName("hardwareHint")] string? HardwareHint = null);

/// <summary>
/// Response of <c>POST /api/router/targets</c> (task #19): 201 with the created target +
/// the probe/reporting data (<c>liveness</c> = the TargetLivenessState name
/// "Unknown"/"Online"/"Offline", <c>latencyMs</c> = the last probe latency when available,
/// <c>modelsDiscovered</c> = the seeded catalog count); 400/409/422/500 reuse the SAME
/// shape with <c>success: false</c>, <c>target</c> omitted and <c>error</c> set.
/// </summary>
public sealed record AddTargetResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("connectionTested")] bool ConnectionTested,
    [property: JsonPropertyName("liveness")] string Liveness,
    [property: JsonPropertyName("modelsDiscovered")] int ModelsDiscovered,
    [property: JsonPropertyName("target")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouterTargetConfigDto? Target = null,
    [property: JsonPropertyName("latencyMs")] long? LatencyMs = null,
    [property: JsonPropertyName("error")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

/// <summary>
/// Response of <c>DELETE /api/router/targets/{name}</c> (task #19): success flag +
/// message ("target not found: {name}" on 404).
/// </summary>
public sealed record DeleteTargetResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message);

// ------------------------------------------------------------------
//  DTOs: API key management (task #50)
// ------------------------------------------------------------------
