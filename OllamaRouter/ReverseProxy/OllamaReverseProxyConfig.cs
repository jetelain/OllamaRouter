using OllamaRouter.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace OllamaRouter.ReverseProxy;

/// <summary>
/// Builds the YARP configuration by code: one route/cluster per effective fleet target
/// (<see cref="OllamaRouterOptions.GetEffectiveTargets"/>), selected via the <c>X-Ollama-Target</c>
/// header set by <see cref="Middleware.OllamaRoutingMiddleware"/>. A legacy configuration
/// (LocalUrl/RemoteUrl, no Targets) synthesizes exactly the "Local" and "Remote" targets, so the
/// generated routes/clusters are byte-identical to the previous two-tier configuration.
/// No YARP route is generated for the Cloud pseudo-target (it is relayed through the local
/// instance by the middleware).
/// </summary>
public static class OllamaReverseProxyConfig
{
    /// <summary>
    /// The INTERNAL YARP cluster selector header. Set by the routing middleware to steer
    /// route selection / the dispatch forwarder, and NEVER honored from inbound clients:
    /// <see cref="Middleware.OllamaRoutingMiddleware"/> strips any client-supplied value on
    /// arrival so a client cannot force YARP onto a different cluster than the router's
    /// decision.
    /// </summary>
    public const string TargetHeader = "X-Ollama-Target";

    /// <summary>
    /// The PUBLIC client-facing header. Inbound: a request may carry it to PIN a specific
    /// fleet target for the request (honored when the target is configured, enabled, online
    /// and — for inference endpoints — capable of the requested model; otherwise the
    /// middleware falls back to the normal scoring pipeline). Outbound: the middleware echoes
    /// the finally-served target in the response as the first of the <c>X-OllamaRouter-*</c>
    /// telemetry headers.
    /// </summary>
    public const string PublicTargetHeader = "X-OllamaRouter-Target";

    /// <summary>Telemetry response header: the model name dispatched to the target.</summary>
    public const string TelemetryModelHeader = "X-OllamaRouter-Model";

    /// <summary>Telemetry response header: the estimated prompt token count for the request.</summary>
    public const string TelemetryEstimatedTokensHeader = "X-OllamaRouter-Estimated-Tokens";

    /// <summary>
    /// Telemetry response header: the effective <c>num_ctx</c> the router ASSIGNED to the
    /// request (dynamic context sizing injected/clamped a bucket, including any client-value
    /// clamping). Absent when no sizing decision rewrote the request.
    /// </summary>
    public const string TelemetryContextSizeHeader = "X-OllamaRouter-Context-Size";

    /// <summary>
    /// Telemetry response header: the Auto-Shrink lease mode of the request (retained /
    /// fresh-expanded / shrunk / baseline) when the Auto-Shrink Manager participates.
    /// </summary>
    public const string TelemetryContextModeHeader = "X-OllamaRouter-Context-Mode";

    public const string LocalTarget = "Local";
    public const string RemoteTarget = "Remote";

    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Build(OllamaRouterOptions options)
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        foreach (var target in options.GetEffectiveTargets())
        {
            // The header match value is the target name exactly as configured (the route
            // selector is case-sensitive), while the ids embed the name so each target gets
            // a distinct route/cluster/destination.
            routes.Add(BuildRoute($"{target.Name}Route", $"{target.Name}Cluster", target.Name!));
            clusters.Add(BuildCluster($"{target.Name}Cluster", $"{target.Name}Ollama", target.Url!));
        }

        return (routes, clusters);
    }

    private static RouteConfig BuildRoute(string routeId, string clusterId, string targetHeaderValue) => new()
    {
        RouteId = routeId,
        ClusterId = clusterId,
        Match = new RouteMatch
        {
            Path = "/{**catch-all}",
            Headers =
            [
                new RouteHeader
                {
                    Name = TargetHeader,
                    Values = [targetHeaderValue]
                }
            ]
        }
    };

    // Ollama can take a long time to respond (model cold start, long generations),
    // so the default 100s YARP activity timeout is extended to avoid the proxy
    // cancelling the request while the response body is still being streamed.
    private static readonly TimeSpan ActivityTimeout = TimeSpan.FromMinutes(15);

    private static ClusterConfig BuildCluster(string clusterId, string destinationId, string address) => new()
    {
        ClusterId = clusterId,
        HttpRequest = new ForwarderRequestConfig
        {
            ActivityTimeout = ActivityTimeout
        },
        Destinations = new Dictionary<string, DestinationConfig>
        {
            [destinationId] = new() { Address = address }
        }
    };
}
