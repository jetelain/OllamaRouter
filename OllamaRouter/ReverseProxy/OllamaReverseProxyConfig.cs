using OllamaRouter.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace OllamaRouter.ReverseProxy;

/// <summary>
/// Builds the YARP configuration by code: one route/cluster for the local Ollama instance and
/// one for the remote instance, selected via the <c>X-Ollama-Target</c> header set by
/// <see cref="Middleware.OllamaRoutingMiddleware"/>. Only the local and remote addresses remain
/// driven by configuration (<see cref="OllamaRouterOptions"/>).
/// </summary>
public static class OllamaReverseProxyConfig
{
    public const string TargetHeader = "X-Ollama-Target";
    public const string LocalTarget = "Local";
    public const string RemoteTarget = "Remote";

    private const string LocalClusterId = "localCluster";
    private const string RemoteClusterId = "remoteCluster";

    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Build(OllamaRouterOptions options)
    {
        var routes = new[]
        {
            BuildRoute("localRoute", LocalClusterId, LocalTarget),
            BuildRoute("remoteRoute", RemoteClusterId, RemoteTarget)
        };

        var clusters = new[]
        {
            BuildCluster(LocalClusterId, "localOllama", options.LocalUrl),
            BuildCluster(RemoteClusterId, "remoteOllama", options.RemoteUrl)
        };

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
