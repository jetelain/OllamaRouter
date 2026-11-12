using OllamaRouter.Endpoints.RouterApi.Config;
using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Targets;

/// <summary>
/// Request body of <c>PUT /api/router/targets/{name}</c> (task #19): partial target
/// update — null/absent = no change (all fields null → 400); <c>hardwareHint: ""</c> clears
/// the hint; <c>url</c> must be an absolute http(s) URI (trailing / stripped);
/// <c>maxParallel</c> must be &gt;= 1. Task #61 adds <c>type</c> (case-insensitive
/// <c>ollama|openai|osnative</c>), <c>apiKey</c> (null/absent = no change, <c>""</c> clears),
/// <c>provider</c> (<c>""</c> clears) and <c>models</c> (null/absent = no change, a provided list
/// — possibly empty — replaces the roster). Task #84 adds <c>powerWatts</c> (number|null;
/// null/absent = no change, negative → 400).
/// </summary>
public sealed record UpdateTargetRequest(
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("enabled")] bool? Enabled = null,
    [property: JsonPropertyName("hardwareHint")] string? HardwareHint = null,
    [property: JsonPropertyName("maxParallel")] int? MaxParallel = null,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("apiKey")] string? ApiKey = null,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("models")] IReadOnlyList<TargetModelDto>? Models = null,
    // Task #84: node power-draw override (watts); null/absent = NO CHANGE (STJ cannot
    // distinguish an explicit JSON null from an absent key; clearing a previously set
    // override is done by editing config/config.json, which removes the key when the
    // in-memory value is null).
    [property: JsonPropertyName("powerWatts")] double? PowerWatts = null);

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
/// persisting; probe failure → 422 with a retry hint). Task #61 adds the protocol family
/// <c>type</c> (case-insensitive <c>ollama|openai|osnative</c>, default <c>ollama</c>), the
/// upstream provider <c>apiKey</c> (required-for-behavior only on <c>openai</c>; provided on a
/// non-<c>openai</c> target → 400), the informational <c>provider</c> label and the declared
/// <c>models</c> roster (blank names → 400; <c>osnative</c> is accepted — config-storable,
/// unroutable until tasks #63/#64).
/// </summary>
public sealed record AddTargetRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("maxParallel")] int MaxParallel = 1,
    [property: JsonPropertyName("testConnectionFirst")] bool TestConnectionFirst = true,
    [property: JsonPropertyName("hardwareHint")] string? HardwareHint = null,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("apiKey")] string? ApiKey = null,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("models")] IReadOnlyList<TargetModelDto>? Models = null,
    // Task #84: node power-draw override (watts; null = unset ⇒ the resolution chain
    // Pricing:DefaultNodePowerWatts → 150 W applies).
    [property: JsonPropertyName("powerWatts")] double? PowerWatts = null);

/// <summary>
/// One declared model of a target's synthetic catalog (task #61) in the Setup API wire
/// format: the model <c>name</c> (required, non-blank) plus the optional context-length and
/// quantization hints (consumed by task #62). Mapped 1:1 to
/// <see cref="Options.TargetModelConfig"/>.
/// </summary>
public sealed record TargetModelDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("contextLength")] int? ContextLength = null,
    [property: JsonPropertyName("quantization")] string? Quantization = null);

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
//  DTOs: failure-streak reset (task #95)
// ------------------------------------------------------------------

/// <summary>
/// Response of <c>POST /api/router/targets/{name}/reset-streak</c> (task #95): 200 with
/// <c>success: true</c> + the reset target's name, its new streak (<c>0</c>) and circuit
/// state ("Closed" — the target is immediately routable on the next routing decision);
/// 404 (unknown target name) or 500 (streak service not registered) reuse the same shape
/// with <c>success: false</c> and <c>error</c> set (<c>streak</c>/<c>status</c> omitted).
/// </summary>
public sealed record ResetTargetStreakResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("target")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target = null,
    [property: JsonPropertyName("streak")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Streak = null,
    [property: JsonPropertyName("status")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status = null,
    [property: JsonPropertyName("error")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

/// <summary>
/// One target row of <c>POST /api/router/fleet/reset-streaks</c> (task #95): the target
/// name, the streak count read BEFORE the reset and whether the reset changed anything
/// (<c>previousStreak &gt; 0</c>).
/// </summary>
public sealed record ResetFleetStreaksTarget(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("previousStreak")] int PreviousStreak,
    [property: JsonPropertyName("reset")] bool Reset);

/// <summary>
/// Response of <c>POST /api/router/fleet/reset-streaks</c> (task #95): 200 with
/// <c>success: true</c>, the count of targets whose streak was above zero before the reset
/// (<c>resetCount</c>) and the per-target rows for EVERY effective target (configuration
/// order) plus the Cloud pseudo-target when cloud overflow is enabled; 500 (streak service
/// not registered) reuses the same shape with <c>success: false</c>, <c>error</c> set and
/// <c>targets</c> omitted.
/// </summary>
public sealed record ResetFleetStreaksResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("resetCount")] int ResetCount = 0,
    // Null (→ omitted) on the 500 error shape; the 200 shape ALWAYS carries the list
    // (possibly empty).
    [property: JsonPropertyName("targets")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ResetFleetStreaksTarget>? Targets = null,
    [property: JsonPropertyName("error")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

// ------------------------------------------------------------------
//  DTOs: API key management (task #50)
// ------------------------------------------------------------------
