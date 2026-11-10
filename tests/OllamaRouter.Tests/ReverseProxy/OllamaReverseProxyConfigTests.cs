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

    [Fact]
    public void Build_ThreeTargetFleet_ProducesOneRouteAndClusterPerTarget()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "desktop", Url = "http://127.0.0.1:11435" },
                new OllamaTargetOptions { Name = "laptop", Url = "http://192.168.1.30:11434" },
                new OllamaTargetOptions { Name = "server1", Url = "http://aiserver.local:11434" }
            ]
        };

        var (routes, clusters) = OllamaReverseProxyConfig.Build(options);

        Assert.Equal(3, routes.Count);
        Assert.Equal(3, clusters.Count);

        foreach (var target in options.GetEffectiveTargets())
        {
            var route = Assert.Single(routes, r =>
                r.Match.Headers is [{ Name: OllamaReverseProxyConfig.TargetHeader, Values: var values }]
                && values!.SequenceEqual([target.Name!], StringComparer.Ordinal));

            // Route/cluster ids embed the target name; each route selects its own cluster.
            var cluster = Assert.Single(clusters, c => c.ClusterId == route.ClusterId
                && c.ClusterId == $"{target.Name}Cluster");

            // The cluster forwards to exactly the target URL.
            var destination = Assert.Single(cluster.Destinations!.Values);
            Assert.Equal(target.Url, destination.Address);
        }

        // Cluster ids are unique.
        Assert.Equal(3, clusters.Select(c => c.ClusterId).Distinct().Count());
        // No YARP route is generated for the Cloud pseudo-target.
        Assert.DoesNotContain(routes, r => r.Match.Headers![0].Values!.Contains("Cloud", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_EmptyConfiguration_ProducesNoRoutesOrClusters()
    {
        var options = new OllamaRouterOptions();

        var (routes, clusters) = OllamaReverseProxyConfig.Build(options);

        Assert.Empty(routes);
        Assert.Empty(clusters);
    }
}
