using OllamaRouter.Options;
using OllamaRouter.ReverseProxy;

namespace OllamaRouter.Tests.ReverseProxy;

public class OllamaReverseProxyConfigTests
{
    [Fact]
    public void Build_CreatesOneRouteAndClusterPerTarget()
    {
        var options = new OllamaRouterOptions
        {
            LocalUrl = "http://127.0.0.1:11435",
            RemoteUrl = "http://aiserver.local:11434"
        };

        var (routes, clusters) = OllamaReverseProxyConfig.Build(options);

        Assert.Equal(2, routes.Count);
        Assert.Equal(2, clusters.Count);
    }

    [Fact]
    public void Build_RoutesMatchOnTargetHeader()
    {
        var options = new OllamaRouterOptions
        {
            LocalUrl = "http://127.0.0.1:11435",
            RemoteUrl = "http://aiserver.local:11434"
        };

        var (routes, _) = OllamaReverseProxyConfig.Build(options);

        foreach (var route in routes)
        {
            var header = Assert.Single(route.Match.Headers!);
            Assert.Equal(OllamaReverseProxyConfig.TargetHeader, header.Name);
        }

        Assert.Contains(routes, r => r.Match.Headers!.Single().Values!.Contains(OllamaReverseProxyConfig.LocalTarget));
        Assert.Contains(routes, r => r.Match.Headers!.Single().Values!.Contains(OllamaReverseProxyConfig.RemoteTarget));
    }

    [Fact]
    public void Build_ClustersUseConfiguredAddresses()
    {
        var options = new OllamaRouterOptions
        {
            LocalUrl = "http://127.0.0.1:11435",
            RemoteUrl = "http://aiserver.local:11434"
        };

        var (_, clusters) = OllamaReverseProxyConfig.Build(options);

        var localCluster = Assert.Single(clusters, c => c.Destinations!.Values.Any(d => d.Address == options.LocalUrl));
        var remoteCluster = Assert.Single(clusters, c => c.Destinations!.Values.Any(d => d.Address == options.RemoteUrl));
        Assert.NotEqual(localCluster.ClusterId, remoteCluster.ClusterId);
    }
}
