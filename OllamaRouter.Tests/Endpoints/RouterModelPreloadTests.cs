using OllamaRouter.Tests.TestHelpers;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Endpoints;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Preload;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Endpoint tests for the dedicated model pre-load API (task #44): the POST
/// /api/router/models/{model}/preload happy/fail-soft/error paths, the query-vs-body
/// parameter precedence (body wins, the vram-reclaim pattern), the GET
/// /api/router/preload/preview side-effect-free preview, and the source-generated
/// response serialization shape. The REAL ModelPreloadService runs on top of a strict-mocked
/// catalog client (the full capability/discovery path is exercised in-process).
/// </summary>
public class RouterModelPreloadTests
{
    private const string DesktopUrl = "http://127.0.0.1:11435";
    private const string ServerUrl = "http://192.168.1.10:11434";
    private const string Model = "llama3:8b";

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(WebApplication app, HttpClient client, Mock<IOllamaModelCatalogClient> catalog)
        {
            App = app;
            Client = client;
            Catalog = catalog;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public Mock<IOllamaModelCatalogClient> Catalog { get; }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static JsonArray TagsBody(params (string Name, long Size)[] models)
    {
        var arr = new JsonArray();
        foreach (var (name, size) in models)
        {
            arr.Add(new JsonObject { ["name"] = name, ["model"] = name, ["size"] = size });
        }

        return arr;
    }

    private static JsonObject DenseShow(long parameterCount) => new()
    {
        ["model_info"] = new JsonObject { ["general.parameter_count"] = parameterCount },
        ["details"] = new JsonObject { ["family"] = "llama" }
    };

    private static OllamaRouterOptions TwoTargetOptions() => new()
    {
        Targets =
        [
            new OllamaTargetOptions { Name = "Desktop", Url = DesktopUrl, MaxVramMB = 16_384 },
            new OllamaTargetOptions { Name = "Server", Url = ServerUrl, MaxVramMB = 98_304 }
        ],
        Models = new Dictionary<string, ModelThresholds>(),
        DynamicContext = new DynamicContextOptions { Enabled = true },
        CloudOverflow = new CloudOverflowOptions { Enabled = false }
    };

    private static async Task<Harness> CreateTestAppAsync(
        OllamaRouterOptions options,
        Dictionary<string, (JsonArray Tags, IReadOnlyDictionary<string, JsonObject> Shows)> catalogs,
        bool scriptLoad = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        var catalog = new Mock<IOllamaModelCatalogClient>(MockBehavior.Strict);
        foreach (var (url, (tags, shows)) in catalogs)
        {
            var digest = $"digest-{url}";
            catalog
                .Setup(c => c.GetTagsWithDigestAsync(url, It.IsAny<CancellationToken>()))
                .ReturnsAsync((tags, digest));
            catalog
                .Setup(c => c.GetShowAsync(url, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((_, model, _) =>
                    Task.FromResult<JsonObject?>(shows.TryGetValue(model, out var show) ? show : null));
        }

        if (scriptLoad)
        {
            catalog
                .Setup(c => c.PreloadModelAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PreloadOutcome(true));
            catalog
                .Setup(c => c.GetRunningModelNamesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { Model }));
        }

        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        liveness.SetupGet(l => l.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddMinutes(1));

        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(a => a.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);

        var tracker = new Mock<ITargetExecutionTracker>();
        tracker.Setup(t => t.GetState(It.IsAny<RoutingTarget>())).Returns((TargetExecutionState?)null);

        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(new TestOptionsMonitor<OllamaRouterOptions>(options));
        builder.Services.AddSingleton(catalog.Object);
        builder.Services.AddSingleton(liveness.Object);
        builder.Services.AddSingleton(availability.Object);
        builder.Services.AddSingleton(tracker.Object);
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<IModelCatalogCacheService>(sp =>
            new ModelCatalogCacheService(sp.GetRequiredService<IOllamaModelCatalogClient>(), sp.GetRequiredService<IMemoryCache>()));
        builder.Services.AddSingleton<ModelCapabilityEngine>();

        // The production DI shape (defensive nullable-tolerant registration).
        builder.Services.AddSingleton<IModelPreloadService>(sp => new ModelPreloadService(
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp.GetRequiredService<IOllamaModelCatalogClient>(),
            sp.GetService<IModelCatalogCacheService>(),
            sp.GetService<ModelCapabilityEngine>(),
            sp.GetService<ITargetLivenessService>(),
            sp.GetService<ITargetAvailabilityService>(),
            sp.GetService<ITargetExecutionTracker>(),
            sp.GetService<IFleetHardwareService>(),
            sp.GetRequiredService<ILogger<ModelPreloadService>>()));

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();

        return new Harness(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, catalog);
    }

    private static Dictionary<string, (JsonArray, IReadOnlyDictionary<string, JsonObject>)> BothTargetsHost() => new()
    {
        [DesktopUrl] = (TagsBody((Model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [Model] = DenseShow(8_000_000_000L) }),
        [ServerUrl] = (TagsBody((Model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [Model] = DenseShow(8_000_000_000L) })
    };

    private static JsonNode ReadJson(HttpResponseMessage response)
    {
        var body = response.Content.ReadAsStringAsync().Result;
        return JsonNode.Parse(body)!;
    }

    // ------------------------------------------------------------------
    //  POST happy path + fail-soft (AC1, AC10)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostPreload_HappyPath_Returns200WithFullSelectionReport()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost());

        var response = await harness.Client.PostAsync("/api/router/models/llama3:8b/preload", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = ReadJson(response).AsObject()!;

        // Source-generated camelCase shape, all documented fields present. The parameterless
        // request uses the recommended-window soft intent (128k fallback = both ceilings),
        // which is above the large-intent bar -> the server-class node is preferred.
        Assert.Equal(Model, body["model"]!.GetValue<string>());
        Assert.Equal("Server", body["selectedTarget"]!.GetValue<string>());
        Assert.Equal(-1, body["keepAliveSeconds"]!.GetValue<int>()); // server class default (indefinite)
        Assert.Equal(131_072, body["numCtx"]!.GetValue<int>()); // min(intent, selected ceiling)
        Assert.NotNull(body["numCtx"]);
        Assert.NotNull(body["contextIntentTokens"]);
        Assert.True((bool)body["loadAccepted"]!);
        Assert.True((bool)body["residencyConfirmed"]!);
        Assert.NotNull(body["preloadDurationMs"]);
        Assert.NotNull(body["reason"]);

        var candidates = body["candidates"]!.AsArray();
        Assert.Equal(2, candidates.Count);
        Assert.Equal("Desktop", candidates[0]!["target"]!.GetValue<string>());
        Assert.True((bool)candidates[0]!["eligible"]!);
        Assert.Equal("Server", candidates[1]!["target"]!.GetValue<string>());
        Assert.True((bool)candidates[1]!["serverClass"]!);
        Assert.False((bool)candidates[0]!["serverClass"]!);
    }

    [Fact]
    public async Task PostPreload_LoadRejected_FailSoft200WithFalseFlags()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost(), scriptLoad: false);
        harness.Catalog
            .Setup(c => c.PreloadModelAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreloadOutcome(false, "http 503"));
        harness.Catalog
            .Setup(c => c.GetRunningModelNamesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<string>>(new string[0]));

        var response = await harness.Client.PostAsync("/api/router/models/llama3:8b/preload?target=desktop", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = ReadJson(response).AsObject()!;
        Assert.Equal("Desktop", body["selectedTarget"]!.GetValue<string>());
        Assert.False((bool)body["loadAccepted"]!);
        Assert.False((bool)body["residencyConfirmed"]!);
    }

    // ------------------------------------------------------------------
    //  Validation: 400s (AC2)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task PostPreload_InvalidKeepAlive_Returns400(int keepAlive)
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost());

        var response = await harness.Client.PostAsync($"/api/router/models/llama3:8b/preload?keepAliveSeconds={keepAlive}", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("keepAliveSeconds must be -1", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostPreload_InvalidContextIntent_Returns400()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost());

        var response = await harness.Client.PostAsync("/api/router/models/llama3:8b/preload?contextIntentTokens=0", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("contextIntentTokens must be >= 1", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    //  404s + 503 (AC3, AC4)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostPreload_UnknownModel_Returns404()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost());

        var response = await harness.Client.PostAsync("/api/router/models/ghost:1b/preload", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("model not found", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostPreload_UnknownPinnedTarget_Returns404()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost());

        var response = await harness.Client.PostAsync("/api/router/models/llama3:8b/preload?target=bogus", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("target not found: bogus", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostPreload_MissingPreloadSuffix_Returns404InvalidPath()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost());

        // The POST catch-all requires the trailing /preload suffix; a bare model path is not one.
        var response = await harness.Client.PostAsync("/api/router/models/llama3:8b", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("invalid preload path", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostPreload_SuffixOnly_Returns404InvalidPath()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost());

        var response = await harness.Client.PostAsync("/api/router/models/preload", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("invalid preload path", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostPreload_NoEligibleTarget_Returns503()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost());

        // 260k intent: both 128k ceilings fail the gate.
        var response = await harness.Client.PostAsync("/api/router/models/llama3:8b/preload?contextIntentTokens=260000", content: null);

        Assert.Equal((HttpStatusCode)503, response.StatusCode);
        Assert.Contains("no capable target", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    //  Query vs body precedence (AC7)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostPreload_BodyFieldWinsOverQuery_AbsentFieldFallsBackToQuery()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost());

        // Body pins the target; the query carries the keep_alive (body has no such field).
        var body = new StringContent("{\"target\": \"server\"}", Encoding.UTF8, "application/json");
        var response = await harness.Client.PostAsync(
            "/api/router/models/llama3:8b/preload?keepAliveSeconds=300&contextIntentTokens=8192", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = ReadJson(response).AsObject()!;
        Assert.Equal("Server", json["selectedTarget"]!.GetValue<string>());
        Assert.Equal(300, json["keepAliveSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task PostPreload_BodyKeepAliveWinsOverQuery()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost());

        var body = new StringContent("{\"keepAliveSeconds\": 600}", Encoding.UTF8, "application/json");
        var response = await harness.Client.PostAsync(
            "/api/router/models/llama3:8b/preload?keepAliveSeconds=300", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(600, ReadJson(response).AsObject()!["keepAliveSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task PostPreload_MalformedBody_FallsThroughToQueryValues()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost());

        var body = new StringContent("{not json", Encoding.UTF8, "application/json");
        var response = await harness.Client.PostAsync(
            "/api/router/models/llama3:8b/preload?keepAliveSeconds=300", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(300, ReadJson(response).AsObject()!["keepAliveSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task PostPreload_TargetMatchIsCaseInsensitive()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost());

        var response = await harness.Client.PostAsync("/api/router/models/llama3:8b/preload?target=desktop", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Desktop", ReadJson(response).AsObject()!["selectedTarget"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    //  GET preview (AC8)
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetPreview_Returns200WithNullLoadFieldsAndZeroGenerateCalls()
    {
        // scriptLoad: false -> the strict catalog mock has NO PreloadModelAsync setup: the
        // preview path must make zero load calls (any call would throw and fail the test).
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost(), scriptLoad: false);

        var response = await harness.Client.GetAsync("/api/router/preload/preview?model=llama3:8b");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = ReadJson(response).AsObject()!;
        Assert.Equal("Server", body["selectedTarget"]!.GetValue<string>()); // large-intent soft fallback
        Assert.Equal(-1, body["keepAliveSeconds"]!.GetValue<int>()); // server class default
        // The source-generated serializer omits null-valued fields entirely.
        Assert.Null(body["loadAccepted"]);
        Assert.Null(body["residencyConfirmed"]);
        Assert.Null(body["preloadDurationMs"]);
    }

    [Fact]
    public async Task GetPreview_MissingModel_Returns400()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost(), scriptLoad: false);

        var response = await harness.Client.GetAsync("/api/router/preload/preview");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("model query parameter is required", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetPreview_UnknownModel_Returns404()
    {
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost(), scriptLoad: false);

        var response = await harness.Client.GetAsync("/api/router/preload/preview?model=ghost:1b");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("model not found", ReadJson(response).AsObject()!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetPreview_LegacyModelInPathShape_HitsModelDetailCatchAll()
    {
        // The spec's primary preview shape (model name in the path) cannot be expressed as a
        // route template (catch-all constraint). That URL instead hits the model-DETAIL
        // catch-all, whose tagless-prefix rule ("llama3:8b/preload/preview" -> prefix
        // "llama3" -> "llama3:8b") resolves the model -> 200 with the DETAIL shape (no
        // loadAccepted key) — the side-effect-free preview lives at
        // GET /api/router/preload/preview only.
        await using var harness = await CreateTestAppAsync(TwoTargetOptions(), BothTargetsHost(), scriptLoad: false);

        var response = await harness.Client.GetAsync("/api/router/models/llama3%3A8b/preload/preview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = ReadJson(response).AsObject()!;
        Assert.Null(body["loadAccepted"]);
    }
}
