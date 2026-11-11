using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Info;

/// <summary>
/// Response payload for <c>GET /api/router/info</c>: the router version, the static
/// <c>isRouter</c> marker, the fleet summary and the active feature flags.
/// </summary>
public sealed record RouterInfoResponse(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("isRouter")] bool IsRouter,
    [property: JsonPropertyName("fleet")] RouterFleetSummary Fleet,
    [property: JsonPropertyName("features")] RouterFeatures Features);

public sealed record RouterFleetSummary(
    [property: JsonPropertyName("totalTargets")] int TotalTargets,
    [property: JsonPropertyName("onlineTargets")] int OnlineTargets,
    [property: JsonPropertyName("cloudEnabled")] bool CloudEnabled);

public sealed record RouterFeatures(
    [property: JsonPropertyName("dynamicContext")] bool DynamicContext,
    [property: JsonPropertyName("autoShrink")] bool AutoShrink,
    [property: JsonPropertyName("failover")] bool Failover,
    [property: JsonPropertyName("antiThrashingAffinity")] bool AntiThrashingAffinity);
