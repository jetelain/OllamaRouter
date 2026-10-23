using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Endpoints;
using OllamaRouter.Options;
using OllamaRouter.Services;
using OllamaRouter.Tests.Services;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// End-to-end (mini WebApplication + real FleetAggregationService + fake daemons on
/// loopback) tests for the unified fleet endpoints: union semantics, the 500 ms per-target
/// budget with stale-while-revalidate fallback, and the <c>router_target</c> annotation of
/// the merged /api/ps payload.
/// </summary>
public class AggregatedEndpointsTests
{
    public const string DesktopAuthority = "127.0.0.1:21435";
    public const string ServerAuthority = "127.0.0.1:21436";
    public const string LaptopAuthority = "127.0.0.1:21437";

    private static async Task<(WebApplication App, HttpClient Client, FakeOllamaHttpHandler Handler, Mock<ITargetLivenessService> Liveness)>
        CreateTestAppAsync(int? perTargetTimeoutMs = null, Dictionary<string, ModelThresholds>? models = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var handler = new FakeOllamaHttpHandler();
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        var catalogClient = new OllamaModelCatalogClient(httpClient);
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new ModelCatalogCacheService(catalogClient, memoryCache, NullLogger<ModelCatalogCacheService>.Instance);

        var liveness = new Mock<ITargetLivenessService>();
        liveness
            .Setup(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddMinutes(1));
        liveness.Setup(t => t.GetFleetTargets()).Returns(new[]
        {
            new RoutingTarget("Desktop"),
            new RoutingTarget("Server"),
            new RoutingTarget("Laptop")
        });
        liveness.Setup(t => t.GetEntries()).Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
        {
            [new RoutingTarget("Desktop")] = new(new RoutingTarget("Desktop"), TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
            [new RoutingTarget("Server")] = new(new RoutingTarget("Server"), TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
            [new RoutingTarget("Laptop")] = new(new RoutingTarget("Laptop"), TargetLivenessState.Online, null, DateTimeOffset.UtcNow)
        });

        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);

        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Desktop", Url = $"http://{DesktopAuthority}" },
                new OllamaTargetOptions { Name = "Server", Url = $"http://{ServerAuthority}" },
                new OllamaTargetOptions { Name = "Laptop", Url = $"http://{LaptopAuthority}" }
            ],
            Models = models ?? new()
        };
        if (perTargetTimeoutMs is not null)
        {
            options.Aggregation.PerTargetTimeoutMs = perTargetTimeoutMs.Value;
        }

        builder.Services.AddSingleton<MemoryCache>(memoryCache);
        builder.Services.AddSingleton<IMemoryCache>(memoryCache);
        builder.Services.AddSingleton<IOllamaModelCatalogClient>(catalogClient);
        builder.Services.AddSingleton<IModelCatalogCacheService>(cache);
        builder.Services.AddSingleton(liveness.Object);
        builder.Services.AddSingleton(availability.Object);
        builder.Services.AddSingleton(MsOptions.Create(options));
        builder.Services.AddSingleton<IFleetAggregationService, FleetAggregationService>();

        var app = builder.Build();
        app.MapOllamaRouterEndpoints();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client, handler, liveness);
    }

    private static string TagsBody(params string[] names)
    {
        var arr = new JsonArray();
        foreach (var name in names)
        {
            arr.Add(new JsonObject { ["name"] = name, ["model"] = name, ["size"] = 1 });
        }

        return new JsonObject { ["models"] = arr }.ToJsonString();
    }

    [Fact]
    public async Task GetApiTags_TwoOnlineOneAsleep_ReturnsUnionPlusStaleCatalog_WithoutWaitingForSlowTarget()
    {
        var (app, client, handler, liveness) = await CreateTestAppAsync();
        try
        {
            handler.ForAuthority(DesktopAuthority, new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses = { ["GET /api/tags"] = TagsBody("llama3", "shared") }
            });
            handler.ForAuthority(ServerAuthority, new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses = { ["GET /api/tags"] = TagsBody("qwen3", "shared") }
            });
            handler.ForAuthority(LaptopAuthority, new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses = { ["GET /api/tags"] = TagsBody("gemma3") }
            });

            // Warm the Laptop's stale slot while the whole fleet is online and fast.
            await (await client.GetAsync("/api/tags")).Content.ReadAsStringAsync();

            // Laptop goes asleep: it is offline AND its live fetch would miss the budget.
            liveness.Setup(t => t.IsOnline(new RoutingTarget("Laptop"))).Returns(false);
            liveness.Setup(t => t.GetEntries()).Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [new RoutingTarget("Desktop")] = new(new RoutingTarget("Desktop"), TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
                [new RoutingTarget("Server")] = new(new RoutingTarget("Server"), TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
                [new RoutingTarget("Laptop")] = new(new RoutingTarget("Laptop"), TargetLivenessState.Offline, "asleep", DateTimeOffset.UtcNow)
            });
            handler.Behavior(LaptopAuthority).Latency = TimeSpan.FromSeconds(5);

            var sw = Stopwatch.StartNew();
            var response = await client.GetAsync("/api/tags");
            var body = await response.Content.ReadAsStringAsync();
            sw.Stop();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // AC1: never blocked beyond the 500 ms per-target budget + margin (hard gate);
            // on this fast loopback path the response is well under 100 ms as well.
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1500),
                $"GET /api/tags took {sw.Elapsed.TotalMilliseconds:F0} ms; an asleep target must not hang the IDE.");

            // Union of the online targets + the stale Laptop catalog; "shared" deduped once.
            Assert.Contains("llama3", body);
            Assert.Contains("qwen3", body);
            Assert.Contains("gemma3", body);
            var modelsArray = JsonNode.Parse(body)!.AsObject()["models"]!.AsArray();
            Assert.Equal(1, modelsArray.Count(m => m?["name"]?.GetValue<string>() == "shared"));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetApiTags_CloudSynthesis_StillAppended()
    {
        var models = new Dictionary<string, ModelThresholds>
        {
            ["llama3"] = new() { CloudModel = "deepseek-v3.1:671b-cloud" }
        };
        var (app, client, handler, _) = await CreateTestAppAsync(models: models);
        try
        {
            handler.ForAuthority(DesktopAuthority, new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses = { ["GET /api/tags"] = TagsBody("llama3") }
            });
            handler.ForAuthority(ServerAuthority, new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses = { ["GET /api/tags"] = TagsBody("qwen3") }
            });

            var response = await client.GetAsync("/api/tags");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();

            var modelsArray = JsonNode.Parse(body)!.AsObject()["models"]!.AsArray();
            var cloud = modelsArray.SingleOrDefault(m => m?["name"]?.GetValue<string>() == "deepseek-v3.1:671b-cloud");
            Assert.NotNull(cloud);
            Assert.Equal("cloud", cloud!["details"]?["family"]?.GetValue<string>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetApiPs_MergedPayload_CarriesPerTargetVramAndRouterTarget()
    {
        var (app, client, handler, _) = await CreateTestAppAsync();
        try
        {
            handler.ForAuthority(DesktopAuthority, new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses =
                {
                    ["GET /api/ps"] = """{"models":[{"name":"llama3","pid":10,"size_vram":1000}]}""",
                    ["GET /api/tags"] = TagsBody("llama3")
                }
            });
            handler.ForAuthority(ServerAuthority, new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses =
                {
                    ["GET /api/ps"] = """{"models":[{"name":"llama3","pid":90,"size_vram":4000},{"name":"qwen3","pid":91,"size_vram":2000}]}""",
                    ["GET /api/tags"] = TagsBody("qwen3")
                }
            });
            handler.ForAuthority(LaptopAuthority, new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses = { ["GET /api/ps"] = """{"models":[]}""", ["GET /api/tags"] = TagsBody("gemma3") }
            });

            var response = await client.GetAsync("/api/ps");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();

            var modelsArray = JsonNode.Parse(body)!.AsObject()["models"]!.AsArray();
            Assert.Equal(3, modelsArray.Count);

            var llamaDesktop = modelsArray.Single(m => m?["pid"]?.GetValue<long>() == 10);
            Assert.Equal("Desktop", llamaDesktop["router_target"]?.GetValue<string>());
            Assert.Equal(1000, llamaDesktop["size_vram"]?.GetValue<long>());

            var llamaServer = modelsArray.Single(m => m?["pid"]?.GetValue<long>() == 90);
            Assert.Equal("Server", llamaServer["router_target"]?.GetValue<string>());
            Assert.Equal(4000, llamaServer["size_vram"]?.GetValue<long>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetV1Models_UnionInOpenAiShape_CloudEntryPreserved()
    {
        var models = new Dictionary<string, ModelThresholds>
        {
            ["llama3"] = new() { CloudModel = "glm-5.3-flash:cloud" }
        };
        var (app, client, handler, _) = await CreateTestAppAsync(models: models);
        try
        {
            handler.ForAuthority(DesktopAuthority, new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses =
                {
                    ["GET /v1/models"] = """{"object":"list","data":[{"id":"llama3","object":"model","created":1,"owned_by":"desktop"}]}""",
                    ["GET /api/tags"] = TagsBody("llama3")
                }
            });
            handler.ForAuthority(ServerAuthority, new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses =
                {
                    ["GET /v1/models"] = """{"object":"list","data":[{"id":"qwen3","object":"model","created":2,"owned_by":"server"}]}""",
                    ["GET /api/tags"] = TagsBody("qwen3")
                }
            });
            handler.ForAuthority(LaptopAuthority, new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses =
                {
                    ["GET /v1/models"] = """{"object":"list","data":[]}""",
                    ["GET /api/tags"] = TagsBody("gemma3")
                }
            });

            var response = await client.GetAsync("/v1/models");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();

            var root = JsonNode.Parse(body)!.AsObject();
            Assert.Equal("list", root["object"]?.GetValue<string>());
            var dataArray = root["data"]!.AsArray();
            var ids = dataArray.Select(m => m?["id"]?.GetValue<string>()).ToList();
            Assert.Equal(new[] { "llama3", "qwen3", "glm-5.3-flash:cloud" }, ids);

            // Pristine OpenAI shape: no invented fields on /v1/models entries.
            Assert.All(dataArray, m => Assert.Null(m?["router_target"]));
            Assert.All(dataArray, m => Assert.Null(m?["__router_source_target"]));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
