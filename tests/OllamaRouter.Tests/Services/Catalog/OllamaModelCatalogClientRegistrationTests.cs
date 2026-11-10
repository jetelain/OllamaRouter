using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OllamaRouter.Extensions;
using OllamaRouter.Options;
using OllamaRouter.Services.Catalog;

namespace OllamaRouter.Tests.Services.Catalog;

/// <summary>
/// DI registration assertions for the task #75 preload load client split: the dedicated
/// "PreloadLoad" named client carries the configured <see cref="OllamaRouterOptions.PreloadLoadTimeoutSeconds"/>
/// (default 20 s, read LIVE from the IOptionsMonitor at registration-delegate time), while the
/// typed <see cref="IOllamaModelCatalogClient"/> catalog client keeps its untouched 4 s bound —
/// so non-load catalog calls (tags/show/ps, catalog seeding) never slow down on a stalled peer.
/// </summary>
public class OllamaModelCatalogClientRegistrationTests
{
    [Fact]
    public void OptionsOverload_CustomPreloadLoadTimeout_NamedClientExposesIt_CatalogClientStaysFourSeconds()
    {
        var services = new ServiceCollection();
        var options = new OllamaRouterOptions { PreloadLoadTimeoutSeconds = 42 };

        OllamaRouterServiceCollectionExtensions.AddOllamaRouter(services, options, stateDirectory: null, enableTui: false);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var preloadClient = factory.CreateClient(OllamaModelCatalogClient.PreloadLoadClientName);
        Assert.Equal(TimeSpan.FromSeconds(42), preloadClient.Timeout);

        var catalogClient = Assert.IsType<OllamaModelCatalogClient>(provider.GetRequiredService<IOllamaModelCatalogClient>());
        Assert.Equal(TimeSpan.FromSeconds(4), catalogClient.CatalogHttp.Timeout);
    }

    [Fact]
    public void OptionsOverload_DefaultOptions_PreloadClientIsTwentySeconds()
    {
        var services = new ServiceCollection();

        OllamaRouterServiceCollectionExtensions.AddOllamaRouter(services, new OllamaRouterOptions(), stateDirectory: null, enableTui: false);

        using var provider = services.BuildServiceProvider();
        var preloadClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(OllamaModelCatalogClient.PreloadLoadClientName);

        Assert.Equal(TimeSpan.FromSeconds(20), preloadClient.Timeout);
    }

    [Fact]
    public void OptionsOverload_NonPositivePreloadLoadTimeout_DefensiveFallbackToTwentySeconds()
    {
        // The options overload does not run Normalize() (production normalizes on config bind
        // via OllamaRouterOptionsNormalizer): the registration delegate's defensive > 0 guard
        // must still yield the 20 s default for a non-positive option value.
        var services = new ServiceCollection();

        OllamaRouterServiceCollectionExtensions.AddOllamaRouter(
            services, new OllamaRouterOptions { PreloadLoadTimeoutSeconds = 0 }, stateDirectory: null, enableTui: false);

        using var provider = services.BuildServiceProvider();
        var preloadClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(OllamaModelCatalogClient.PreloadLoadClientName);

        Assert.Equal(TimeSpan.FromSeconds(20), preloadClient.Timeout);
    }

    [Fact]
    public void ConfigOverload_BindsPreloadLoadTimeoutSeconds_IntoPreloadClient()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OllamaRouter:PreloadLoadTimeoutSeconds"] = "60"
        }).Build();

        OllamaRouterServiceCollectionExtensions.AddOllamaRouter(services, configuration, stateDirectory: null, enableTui: false);

        using var provider = services.BuildServiceProvider();
        var preloadClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(OllamaModelCatalogClient.PreloadLoadClientName);

        Assert.Equal(TimeSpan.FromSeconds(60), preloadClient.Timeout);
    }

    [Fact]
    public async Task FullGraph_ResolvedCatalogClient_RoutesPreloadModelAsyncThroughTheNamedPreloadLoadClient()
    {
        // AC2/AC5 production-wiring lock-in: the IOllamaModelCatalogClient resolved from the FULL
        // AddOllamaRouter graph (typed registration, root-provider construction) must actually
        // receive the IHttpClientFactory and route PreloadModelAsync through the "PreloadLoad"
        // named client — the fallback to the injected 4 s catalog client must NEVER engage in
        // production (it only exists for direct unit-test construction).
        var services = new ServiceCollection();
        OllamaRouterServiceCollectionExtensions.AddOllamaRouter(services, new OllamaRouterOptions(), stateDirectory: null, enableTui: false);

        // A later primary-handler registration for the same named client wins, so this observes
        // the load request in the full graph without touching the network.
        var preloadHandler = new RecordingHandler();
        services.AddHttpClient(OllamaModelCatalogClient.PreloadLoadClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => preloadHandler);

        using var provider = services.BuildServiceProvider();
        var catalogClient = Assert.IsType<OllamaModelCatalogClient>(provider.GetRequiredService<IOllamaModelCatalogClient>());
        Assert.Equal(TimeSpan.FromSeconds(4), catalogClient.CatalogHttp.Timeout); // untouched catalog bound

        var outcome = await catalogClient.PreloadModelAsync("http://127.0.0.1:11435", "big-model:latest", 900, 8192);

        Assert.True(outcome.Accepted);
        Assert.Equal("http://127.0.0.1:11435/api/generate", preloadHandler.LastRequest?.RequestUri?.ToString());
    }

    /// <summary>Minimal request-recording handler answering with a successful empty generate response.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
