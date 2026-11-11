using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Info;

/// <summary>
/// Response payload for <c>GET /api/router/info</c>: the router version, the static
/// <c>isRouter</c> marker, the fleet summary, the active feature flags and the daemon process
/// telemetry (task #99: <c>processId</c> / <c>startedAtUtc</c> / <c>uptimeSeconds</c> — additive;
/// older servers simply omit the three fields).
/// </summary>
public sealed record RouterInfoResponse(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("isRouter")] bool IsRouter,
    [property: JsonPropertyName("fleet")] RouterFleetSummary Fleet,
    [property: JsonPropertyName("features")] RouterFeatures Features,
    [property: JsonPropertyName("processId")] int ProcessId = default,
    [property: JsonPropertyName("startedAtUtc")] DateTimeOffset StartedAtUtc = default,
    [property: JsonPropertyName("uptimeSeconds")] double UptimeSeconds = 0.0);

/// <summary>
/// Response payload for <c>POST /api/router/admin/shutdown</c> (202 Accepted, task #99): the
/// graceful host stop has been accepted and the router process id of the stopping daemon.
/// </summary>
public sealed record RouterShutdownResponse(
    [property: JsonPropertyName("stopping")] bool Stopping,
    [property: JsonPropertyName("processId")] int ProcessId);

public sealed record RouterFleetSummary(
    [property: JsonPropertyName("totalTargets")] int TotalTargets,
    [property: JsonPropertyName("onlineTargets")] int OnlineTargets,
    [property: JsonPropertyName("cloudEnabled")] bool CloudEnabled);

public sealed record RouterFeatures(
    [property: JsonPropertyName("dynamicContext")] bool DynamicContext,
    [property: JsonPropertyName("autoShrink")] bool AutoShrink,
    [property: JsonPropertyName("failover")] bool Failover,
    [property: JsonPropertyName("antiThrashingAffinity")] bool AntiThrashingAffinity);
