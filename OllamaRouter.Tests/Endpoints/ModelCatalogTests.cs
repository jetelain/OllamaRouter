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

public class ModelCatalogTests
{
    private static async Task<(WebApplication App, HttpClient Client, FakeOllamaHttpHandler Handler,
        Mock<ITargetLivenessService> Liveness, ModelCatalogCacheService Cache)> CreateTestAppAsync(
        Dictionary<string, ModelThresholds>? models = null)
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
        liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>(), It.IsAny<TimeSpan>())).Returns(true);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddMinutes(1));
        liveness
            .Setup(t => t.GetFleetTargets())
            .Returns(new[] { RoutingTarget.Local, RoutingTarget.Remote });
        liveness
            .Setup(t => t.GetEntries())
            .Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [RoutingTarget.Local] = new(RoutingTarget.Local, TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
                [RoutingTarget.Remote] = new(RoutingTarget.Remote, TargetLivenessState.Online, null, DateTimeOffset.UtcNow)
            });

        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);

        var routerOptions = new OllamaRouterOptions
        {
            LocalUrl = "http://127.0.0.1:11435",
            RemoteUrl = "http://192.168.1.25:11434",
            Models = models ?? new()
        };

        builder.Services.AddSingleton<MemoryCache>(memoryCache);
        builder.Services.AddSingleton<IMemoryCache>(memoryCache);
        builder.Services.AddSingleton<IOllamaModelCatalogClient>(catalogClient);
        builder.Services.AddSingleton<IModelCatalogCacheService>(cache);
        builder.Services.AddSingleton(liveness.Object);
        builder.Services.AddSingleton(availability.Object);
        builder.Services.AddSingleton(MsOptions.Create(routerOptions));
        builder.Services.AddSingleton<IFleetAggregationService, FleetAggregationService>();

        var app = builder.Build();
        app.MapOllamaRouterEndpoints();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client, handler, liveness, cache);
    }

    private static string TagsBody(params string[] names)
    {
        var arr = new JsonArray();
        foreach (var name in names)
        {
            arr.Add(new JsonObject { ["name"] = name, ["model"] = name, ["size"] = 42 });
        }

        return new JsonObject { ["models"] = arr }.ToJsonString();
    }

    [Fact]
    public async Task GetApiTags_ReturnsUnionOfFleetTargets_AndAppendsCloudModel()
    {
        var models = new Dictionary<string, ModelThresholds>
        {
            ["qwen2.5-coder:32b"] = new()
            {
                CloudModel = "deepseek-v3.1:671b-cloud"
            }
        };

        var (app, client, handler, _, _) = await CreateTestAppAsync(models);
        try
        {
            handler.ForAuthority("127.0.0.1:11435", new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses = { ["GET /api/tags"] = TagsBody("qwen2.5-coder:32b", "llama3") }
            });
            handler.ForAuthority("192.168.1.25:11434", new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses = { ["GET /api/tags"] = TagsBody("llama3", "qwen3") }
            });

            var response = await client.GetAsync("/api/tags");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.NotNull(json);

            var modelsArray = json["models"]?.AsArray();
            Assert.NotNull(modelsArray);

            var names = modelsArray.Select(m => m?["name"]?.GetValue<string>()).ToList();
            // Union in effective-target (Local first) order; "llama3" deduped to Local's entry.
            Assert.Equal(new[] { "qwen2.5-coder:32b", "llama3", "qwen3", "deepseek-v3.1:671b-cloud" }, names);

            // Pristine Ollama shape: no invented fields on /api/tags entries.
            Assert.All(modelsArray, m => Assert.Null(m?["router_target"]));
            Assert.All(modelsArray, m => Assert.Null(m?["__router_source_target"]));

            var cloudEntry = modelsArray.Single(m => m?["name"]?.GetValue<string>() == "deepseek-v3.1:671b-cloud");
            Assert.Equal(0, cloudEntry["size"]?.GetValue<long>());
            Assert.Equal("cloud", cloudEntry["details"]?["family"]?.GetValue<string>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetV1Models_ReturnsUnionInOpenAiShape_AndAppendsCloudModel()
    {
        var models = new Dictionary<string, ModelThresholds>
        {
            ["llama3.2:latest"] = new()
            {
                CloudModel = "glm-5.3-flash:cloud"
            }
        };

        var (app, client, handler, _, _) = await CreateTestAppAsync(models);
        try
        {
            handler.ForAuthority("127.0.0.1:11435", new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses = { ["GET /v1/models"] = """{"object":"list","data":[{"id":"llama3.2:latest","object":"model","created":1,"owned_by":"local"}]}""" }
            });
            handler.ForAuthority("192.168.1.25:11434", new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses = { ["GET /v1/models"] = """{"object":"list","data":[{"id":"qwen3:14b","object":"model","created":2,"owned_by":"remote"}]}""" }
            });

            var response = await client.GetAsync("/v1/models");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.NotNull(json);

            var dataArray = json["data"]?.AsArray();
            Assert.NotNull(dataArray);
            Assert.Equal("list", json["object"]?.GetValue<string>());

            var ids = dataArray.Select(m => m?["id"]?.GetValue<string>()).ToList();
            Assert.Equal(new[] { "llama3.2:latest", "qwen3:14b", "glm-5.3-flash:cloud" }, ids);

            var cloudEntry = dataArray.Single(m => m?["id"]?.GetValue<string>() == "glm-5.3-flash:cloud");
            Assert.Equal("cloud", cloudEntry["owned_by"]?.GetValue<string>());
            Assert.Equal("model", cloudEntry["object"]?.GetValue<string>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetApiTags_SlowAsleepTarget_ServesStaleCatalogQuickly()
    {
        var (app, client, handler, _, _) = await CreateTestAppAsync();
        try
        {
            handler.ForAuthority("127.0.0.1:11435", new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses = { ["GET /api/tags"] = TagsBody("llama3") }
            });
            handler.ForAuthority("192.168.1.25:11434", new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses = { ["GET /api/tags"] = TagsBody("qwen3") }
            });

            // Warm the stale slots (both targets online and fast).
            var warm = await (await client.GetAsync("/api/tags")).Content.ReadAsStringAsync();
            Assert.Contains("qwen3", warm);

            // The remote target now misses the 500 ms budget (slow/asleep).
            handler.Behavior("192.168.1.25:11434").Latency = TimeSpan.FromSeconds(3);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await client.GetAsync("/api/tags");
            var body = await response.Content.ReadAsStringAsync();
            sw.Stop();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Hard gate: the response must not wait beyond the per-target budget + margin.
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1200),
                $"Response took {sw.Elapsed.TotalMilliseconds:F0} ms; a slow target must not hang the IDE.");

            // The stale remote catalog is still merged in.
            Assert.Contains("qwen3", body);
            Assert.Contains("llama3", body);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetApiPs_MergesPerTargetVram_WithRouterTargetField()
    {
        var (app, client, handler, _, _) = await CreateTestAppAsync();
        try
        {
            handler.ForAuthority("127.0.0.1:11435", new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses =
                {
                    ["GET /api/ps"] = """{"models":[{"name":"llama3","pid":100,"size_vram":1000}]}""",
                    ["GET /api/tags"] = TagsBody("llama3")
                }
            });
            handler.ForAuthority("192.168.1.25:11434", new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses =
                {
                    ["GET /api/ps"] = """{"models":[{"name":"llama3","pid":900,"size_vram":4000},{"name":"qwen3","pid":901,"size_vram":2000}]}""",
                    ["GET /api/tags"] = TagsBody("qwen3")
                }
            });

            var response = await client.GetAsync("/api/ps");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.NotNull(json);
            var modelsArray = json["models"]?.AsArray();
            Assert.NotNull(modelsArray);
            Assert.Equal(3, modelsArray.Count);

            var llamaOnRemote = modelsArray.Single(m => m?["pid"]?.GetValue<long>() == 900);
            Assert.Equal("Remote", llamaOnRemote["router_target"]?.GetValue<string>());
            Assert.Equal(4000, llamaOnRemote["size_vram"]?.GetValue<long>());
            var llamaOnLocal = modelsArray.Single(m => m?["pid"]?.GetValue<long>() == 100);
            Assert.Equal("Local", llamaOnLocal["router_target"]?.GetValue<string>());
            Assert.Equal(1000, llamaOnLocal["size_vram"]?.GetValue<long>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostApiShow_ModelDirectedToHighestPriorityHoster_ThenCached()
    {
        var (app, client, handler, _, _) = await CreateTestAppAsync();
        try
        {
            handler.ForAuthority("127.0.0.1:11435", new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses =
                {
                    ["GET /api/tags"] = TagsBody("llama3"),
                    ["POST /api/show"] = """{"modelfile":"local","details":{"family":"llama-local"}}"""
                }
            });
            handler.ForAuthority("192.168.1.25:11434", new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses =
                {
                    ["GET /api/tags"] = TagsBody("llama3", "qwen3"),
                    ["POST /api/show"] = """{"modelfile":"remote"}"""
                }
            });

            // The model is hosted on both targets; the highest-priority (Local first) must serve.
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/show")
            {
                Content = new StringContent("""{ "model": "llama3" }""", System.Text.Encoding.UTF8, "application/json")
            };
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("llama-local", body);

            // Second call within the 30 s window: served from the memory cache; the
            // /api/show request must NOT have been re-issued to the daemon.
            var showCallsAfterFirst = handler.CountCalls("127.0.0.1:11435", "POST /api/show");
            var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/show")
            {
                Content = new StringContent("""{ "model": "llama3" }""", System.Text.Encoding.UTF8, "application/json")
            };
            var response2 = await client.SendAsync(request2);
            Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
            var body2 = await response2.Content.ReadAsStringAsync();
            Assert.Contains("llama-local", body2);

            Assert.Equal(showCallsAfterFirst, handler.CountCalls("127.0.0.1:11435", "POST /api/show"));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostApiShow_NoOnlineTargetHostsModel_Returns502()
    {
        var (app, client, handler, _, _) = await CreateTestAppAsync();
        try
        {
            handler.ForAuthority("127.0.0.1:11435", new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses =
                {
                    ["GET /api/tags"] = TagsBody("gemma3"),
                    // The primary target (Local) does not host "mystery" → show fails → 502.
                    ["POST /api/show"] = "500 {\"error\":\"model not found\"}"
                }
            });
            handler.ForAuthority("192.168.1.25:11434", new FakeOllamaHttpHandler.TargetBehavior
            {
                Responses =
                {
                    ["GET /api/tags"] = TagsBody("qwen3"),
                    ["POST /api/show"] = """{"modelfile":"x"}"""
                }
            });

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/show")
            {
                Content = new StringContent("""{ "model": "mystery" }""", System.Text.Encoding.UTF8, "application/json")
            };
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("mystery", body);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
