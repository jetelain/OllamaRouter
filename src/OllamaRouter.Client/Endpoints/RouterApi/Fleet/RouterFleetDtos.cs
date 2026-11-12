using System.Text.Json.Serialization;
using OllamaRouter.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Fleet;

public sealed record RouterFleetResponse(
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterTargetItem> Targets);

public sealed record RouterTargetItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("liveness")] string Liveness,
    [property: JsonPropertyName("latencyMs")] long? LatencyMs,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("hardware")] RouterTargetHardware Hardware,
    [property: JsonPropertyName("execution")] RouterTargetExecution Execution,
    // Task #45 (residency visibility): the CONFIGURED (router-owned) effective preload keep-alive
    // policy and its source discriminator; null (→ omitted from the JSON) for the Cloud
    // pseudo-target, which has no OllamaTargetOptions and never hosts models.
    [property: JsonPropertyName("preload")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouterTargetPreload? Preload = null,
    // Task #95 (failure-streak observability): the circuit-breaker read model of the target's
    // consecutive-failure streak (task #94 service): the live streak count, the wire circuit
    // state ("Closed"/"Cooldown"/"HalfOpen" — "Cooldown" = the breaker's Open state: tripped
    // + cooldown window active) and the remaining cooldown seconds (present ONLY while in
    // "Cooldown"; omitted for "Closed"/"HalfOpen" and for hosts without the streak service).
    [property: JsonPropertyName("failureStreak")] int FailureStreak = 0,
    [property: JsonPropertyName("circuitState")] string CircuitState = "Closed",
    [property: JsonPropertyName("cooldownRemainingSeconds")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? CooldownRemainingSeconds = null);

/// <summary>
/// The CONFIGURED (router-owned) effective preload keep-alive policy of a fleet target
/// (task #45): the effective <c>keepAliveSeconds</c> (what the router WOULD apply on a
/// <c>POST /api/router/models/{*modelName}/preload</c> load; <c>-1</c> = resident indefinitely,
/// serialized as the literal <c>-1</c>) plus the <c>keepAliveSource</c> discriminator naming
/// which rule produced it (<c>"explicit"</c> per-node <c>KeepAliveSeconds</c> override,
/// <c>"class"</c> cloud/server = -1 or desktop = 900 convention, <c>"default"</c> router-level
/// <c>PreloadDefaultKeepAliveSeconds</c> fallback). Resolved by the shared
/// <c>OllamaRouter.Services.Preload.PreloadKeepAlivePolicy.ResolvePreloadKeepAlive</c> — the same
/// source of truth as the preload load path, so the two never drift. Deliberately DISTINCT from
/// the observed daemon value (<c>execution.activeKeepAliveSeconds</c>).
/// </summary>
public sealed record RouterTargetPreload(
    [property: JsonPropertyName("keepAliveSeconds")] int KeepAliveSeconds,
    [property: JsonPropertyName("keepAliveSource")] string KeepAliveSource);

public sealed record RouterTargetHardware(
    [property: JsonPropertyName("gpuVendor")] string GpuVendor,
    [property: JsonPropertyName("devices")] IReadOnlyList<string> Devices,
    [property: JsonPropertyName("maxVramMB")] int? MaxVramMB,
    [property: JsonPropertyName("maxParallel")] int MaxParallel,
    [property: JsonPropertyName("speedFactor")] double? SpeedFactor,
    [property: JsonPropertyName("isUnifiedMemory")] bool IsUnifiedMemory = false,
    [property: JsonPropertyName("systemRamMB")] long? SystemRamMB = null,
    [property: JsonPropertyName("memoryType")] string? MemoryType = null,
    [property: JsonPropertyName("bandwidthGBs")] double? BandwidthGBs = null,
    [property: JsonPropertyName("gpuName")] string? GpuName = null);

public sealed record RouterTargetExecution(
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("activeModel")] string? ActiveModel,
    [property: JsonPropertyName("contextAllocation")] string ContextAllocation,
    [property: JsonPropertyName("activeSessions")] int ActiveSessions = 0,
    // Task #45 (residency visibility): the FULL list of model names currently resident on this
    // node per the in-memory ITargetExecutionTracker snapshot (tracker order); ALWAYS serialized
    // as an array (empty array when idle, never null — the converter writes null as []; [] for
    // Cloud). activeModel remains the FIRST loaded model (residentModels[0] == activeModel
    // whenever non-empty).
    [property: JsonPropertyName("residentModels")][property: JsonConverter(typeof(ResidentModelsJsonConverter))] IReadOnlyList<string>? ResidentModels = null,
    // Best-effort daemon-observed keep_alive (seconds) of the FIRST resident model from the
    // existing /api/ps refresh (-1 = indefinitely); null (→ omitted) when absent. Deliberately
    // distinct from the configured policy (preload.keepAliveSeconds): observed ≠ configured.
    [property: JsonPropertyName("activeKeepAliveSeconds")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ActiveKeepAliveSeconds = null);
