using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Endpoints;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OllamaRouter.Tests.Endpoints;

public class RouterModelApiEndpointsTests
{
    private const string LocalUrl = "http://127.0.0.1:11435";
    private const string ServerUrl = "http://192.168.1.100:11434";
    private const string LaptopUrl = "http://192.168.1.105:11435";

    private const string MoeModel = "qwen3.6-35b-a3b-code:latest";
    private const string DenseModel = "qwen2.5-coder:32b";
    private const string SmallModel = "llama3:8b";
    private const string CloudModelName = "cloud-coder";
    private const string CloudModelAlias = "deepseek-coder-cloud";

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            WebApplication app,
            HttpClient client,
            Mock<IOllamaModelCatalogClient> catalogClient,
            Mock<ITargetLivenessService> liveness,
            Mock<ITargetAvailabilityService> availability,
            Mock<ITargetExecutionTracker> tracker)
        {
            App = app;
            Client = client;
            CatalogClient = catalogClient;
            Liveness = liveness;
            Availability = availability;
            Tracker = tracker;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public Mock<IOllamaModelCatalogClient> CatalogClient { get; }
        public Mock<ITargetLivenessService> Liveness { get; }
        public Mock<ITargetAvailabilityService> Availability { get; }
        public Mock<ITargetExecutionTracker> Tracker { get; }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static OllamaRouterOptions ThreeTargetOptions(
        bool cloudEnabled = true,
        string? cloudDefaultModel = null,
        IReadOnlyDictionary<string, ModelThresholds>? models = null) => new()
        {
            Targets =
            [
                new OllamaTargetOptions
                {
                    Name = "Local",
                    Url = LocalUrl,
                    HardwareHint = "16gb",
                    MaxParallel = 1
                },
                new OllamaTargetOptions
                {
                    Name = "Server",
                    Url = ServerUrl,
                    HardwareHint = "96gb",
                    MaxVramMB = 98304,
                    MaxParallel = 4
                },
                new OllamaTargetOptions
                {
                    Name = "Laptop",
                    Url = LaptopUrl,
                    HardwareHint = "8gb",
                    MaxParallel = 1
                }
            ],
            Models = (models as Dictionary<string, ModelThresholds>) ?? new Dictionary<string, ModelThresholds>(),
            CloudOverflow =
            {
                Enabled = cloudEnabled,
                DefaultModel = cloudDefaultModel
            }
        };

    private static TargetLivenessEntry Entry(RoutingTarget target, TargetLivenessState state, long? latencyMs = null) =>
        new(target, state, state == TargetLivenessState.Offline ? "connection error" : null, DateTimeOffset.UtcNow, latencyMs);

    private static JsonArray TagsBody(params (string Name, long Size)[] models)
    {
        var arr = new JsonArray();
        foreach (var (name, size) in models)
        {
            arr.Add(new JsonObject { ["name"] = name, ["model"] = name, ["size"] = size });
        }

        return arr;
    }

    private static JsonObject MoeShow(long parameterCount) => new()
    {
        ["model_info"] = new JsonObject
        {
            ["general.parameter_count"] = parameterCount,
            ["qnn.expert_count"] = (JsonNode)32L
        },
        ["details"] = new JsonObject
        {
            ["family"] = "qwen3.6",
            ["parameter_size"] = "35.0B"
        }
    };

    private static JsonObject DenseShow(long parameterCount, int? numCtx = null)
    {
        var obj = new JsonObject
        {
            ["model_info"] = new JsonObject
            {
                ["general.parameter_count"] = parameterCount
            },
            ["details"] = new JsonObject
            {
                ["family"] = "qwen2.5"
            }
        };

        if (numCtx is { } value)
        {
            obj["modelfile"] = $"# Modelfile\nPARAMETER num_ctx {value}\n";
        }

        return obj;
    }

    /// <summary>
    /// Scripts the Moq'd catalog client for one fleet: per-URL /api/tags (names + sizes),
    /// the digest + /api/tags-with-digest, the bounded aggregation fetch, and per-model
    /// /api/show bodies. The mock is STRICT: any call other than the documented discovery
    /// surface (tags/show) fails the test.
    /// </summary>
    private static Mock<IOllamaModelCatalogClient> CreateCatalogClientMock(
        params (string Url, (string Name, long Size)[] Tags, IReadOnlyDictionary<string, JsonObject> Shows)[] targets)
    {
        var mock = new Mock<IOllamaModelCatalogClient>(MockBehavior.Strict);
        foreach (var (url, tags, shows) in targets)
        {
            var tagsArray = TagsBody(tags);
            var digest = $"digest-{url}";

            mock
                .Setup(c => c.GetTagsAsync(url, It.IsAny<CancellationToken>()))
                .ReturnsAsync(tagsArray);
            mock
                .Setup(c => c.GetTagsWithDigestAsync(url, It.IsAny<CancellationToken>()))
                .ReturnsAsync((tagsArray, digest));
            mock
                .Setup(c => c.FetchArrayBoundedAsync(url, "/api/tags", "models", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((tagsArray, true));
            mock
                .Setup(c => c.GetShowAsync(url, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((_, model, _) =>
                    Task.FromResult<JsonObject?>(model is not null && shows.TryGetValue(model, out var show) ? show : null));
        }

        return mock;
    }

    private static async Task<Harness> CreateTestAppAsync(
        OllamaRouterOptions options,
        Mock<IOllamaModelCatalogClient> catalogClient,
        IReadOnlyDictionary<RoutingTarget, TargetLivenessState>? livenessStates = null,
        IReadOnlyDictionary<RoutingTarget, TargetExecutionState>? executionStates = null,
        IReadOnlyDictionary<RoutingTarget, bool>? availabilityFlags = null,
        bool registerCapabilityStack = true,
        bool registerDecisionPipeline = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        var states = livenessStates ?? new Dictionary<RoutingTarget, TargetLivenessState>
        {
            [RoutingTarget.Local] = TargetLivenessState.Online,
            [(RoutingTarget)"Server"] = TargetLivenessState.Online,
            [(RoutingTarget)"Laptop"] = TargetLivenessState.Online
        };

        var availability = new Mock<ITargetAvailabilityService>();
        availability
            .Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>()))
            .Returns<RoutingTarget>(t => availabilityFlags?.TryGetValue(t, out var flag) == true ? flag : true);
        availability.Setup(t => t.GetSnapshot()).Returns(new TargetAvailabilitySnapshot(
            states.Keys.ToDictionary(k => k, k => availabilityFlags?.TryGetValue(k, out var flag) == true ? flag : true)));

        var liveness = new Mock<ITargetLivenessService>();
        liveness
            .Setup(l => l.GetEntries())
            .Returns(states.ToDictionary(kv => kv.Key, kv => Entry(kv.Key, kv.Value)));
        liveness
            .Setup(l => l.IsOnline(It.IsAny<RoutingTarget>()))
            .Returns<RoutingTarget>(t => states.TryGetValue(t, out var state) && state == TargetLivenessState.Online);
        liveness
            .SetupGet(l => l.ServiceStartTimeUtc)
            .Returns(DateTimeOffset.UtcNow.AddMinutes(1));

        var tracker = new Mock<ITargetExecutionTracker>();
        tracker
            .Setup(t => t.GetState(It.IsAny<RoutingTarget>()))
            .Returns<RoutingTarget>(t => executionStates?.GetValueOrDefault(t));

        builder.Services.AddSingleton(MsOptions.Create(options));
        builder.Services.AddSingleton(catalogClient.Object);
        builder.Services.AddSingleton(liveness.Object);
        builder.Services.AddSingleton(availability.Object);
        builder.Services.AddSingleton(tracker.Object);
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<ITargetFailureStreakService, TargetFailureStreakService>();

        if (registerCapabilityStack)
        {
            builder.Services.AddSingleton<IModelCatalogCacheService>(sp =>
                new ModelCatalogCacheService(
                    sp.GetRequiredService<IOllamaModelCatalogClient>(),
                    sp.GetRequiredService<IMemoryCache>()));
            builder.Services.AddSingleton<ModelCapabilityEngine>();
            builder.Services.AddSingleton<IFleetAggregationService, FleetAggregationService>();
        }

        if (registerDecisionPipeline)
        {
            builder.Services.AddTransient<CloudRoutingTargetHandler>(sp => new CloudRoutingTargetHandler(
                sp.GetRequiredService<ITargetAvailabilityService>(),
                sp.GetRequiredService<IOptions<OllamaRouterOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CloudRoutingTargetHandler>>()));
            builder.Services.AddTransient<IRoutingDecisionService>(sp => new RoutingDecisionService(
                sp.GetRequiredService<IOptions<OllamaRouterOptions>>(),
                sp.GetRequiredService<IModelCatalogCacheService>(),
                sp.GetRequiredService<ITargetLivenessService>(),
                sp.GetRequiredService<ITargetAvailabilityService>(),
                sp.GetRequiredService<ITargetExecutionTracker>(),
                sp.GetRequiredService<ModelCapabilityEngine>(),
                sp.GetRequiredService<CloudRoutingTargetHandler>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RoutingDecisionService>>(),
                sp.GetRequiredService<ITargetFailureStreakService>()));
        }

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new Harness(app, client, catalogClient, liveness, availability, tracker);
    }

    private static Mock<IOllamaModelCatalogClient> DefaultFleetCatalog(bool moeOnAllTargets = true)
    {
        var moeTags = (MoeModel, 20L * 1024 * 1024 * 1024);
        var denseTags = (DenseModel, 19L * 1024 * 1024 * 1024);
        var smallTags = (SmallModel, 5L * 1024 * 1024 * 1024);

        var moeShows = new Dictionary<string, JsonObject>
        {
            [MoeModel] = MoeShow(35_000_000_000),
            [DenseModel] = DenseShow(32_500_000_000),
            [SmallModel] = DenseShow(8_000_000_000, numCtx: 131_072)
        };

        return CreateCatalogClientMock(
            (LocalUrl, [moeTags, denseTags, smallTags], moeShows),
            (ServerUrl, moeOnAllTargets ? [moeTags, denseTags, smallTags] : [denseTags, smallTags], moeShows),
            (LaptopUrl, moeOnAllTargets ? [moeTags, denseTags, smallTags] : [], new Dictionary<string, JsonObject>()));
    }

    private static JsonObject? ModelNode(JsonObject? root, string name)
    {
        foreach (var node in root?["models"]?.AsArray() ?? new JsonArray())
        {
            if (node?["name"]?.ToString() == name)
            {
                return node is JsonObject obj ? obj : null;
            }
        }

        return null;
    }

    private static JsonNode? TargetNode(JsonObject root, string name)
    {
        foreach (var node in root["targets"]!.AsArray())
        {
            if (node?["target"]?.ToString() == name)
            {
                return node;
            }
        }

        return null;
    }

    private static void VerifyOnlyDiscoveryCallsWereMade(Mock<IOllamaModelCatalogClient> catalogClient)
    {
        // The preview pipeline performs ONLY catalog discovery: GET /api/tags (names,
        // digest, bounded fetch) and POST /api/show. Never merged catalogs, never
        // /api/ps, never unload — and the client has no inference path at all.
        catalogClient.Verify(c => c.GetMergedTagsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never());
        catalogClient.Verify(c => c.GetMergedOpenAIModelsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never());
        catalogClient.Verify(c => c.GetMergedRunningModelsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never());
        catalogClient.Verify(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        catalogClient.Verify(c => c.GetRunningModelNamesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never());
        catalogClient.Verify(c => c.UnloadModelAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        catalogClient.Verify(c => c.StopRunningModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    // ------------------------------------------------------------------
    //  GET /api/router/models
    // ------------------------------------------------------------------

    [Fact]
    public async Task ModelsList_MoeAndDenseMix_ReturnsWireArchitectures_FleetMaxCeiling_IgnoresLaptop()
    {
        var catalog = DefaultFleetCatalog(moeOnAllTargets: true);
        await using var harness = await CreateTestAppAsync(ThreeTargetOptions(cloudEnabled: false), catalog);

        using var response = await harness.Client.GetAsync("/api/router/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        var root = (JsonObject?)JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotNull(root);

        var moe = ModelNode(root!, MoeModel);
        Assert.NotNull(moe);
        Assert.Equal("Moe", moe!["architecture"]!.ToString());
        Assert.Equal("262144", moe["recommendedContextWindow"]!.ToString()); // Server CMax wins; Laptop must not drag it down
        Assert.Equal("4096", moe["nativeContextWindow"]!.ToString()); // no Modelfile num_ctx

        var dense = ModelNode(root, DenseModel);
        Assert.NotNull(dense);
        Assert.Equal("Dense", dense!["architecture"]!.ToString());
        Assert.Equal("32500000000", dense["parameterCount"]!.ToString());
        Assert.Equal("4096", dense["nativeContextWindow"]!.ToString());
        Assert.Equal("262144", dense["recommendedContextWindow"]!.ToString());

        // A model with "PARAMETER num_ctx 131072" in its Modelfile reports it as the native window.
        var small = ModelNode(root, SmallModel);
        Assert.NotNull(small);
        Assert.Equal("131072", small!["nativeContextWindow"]!.ToString());
    }

    [Fact]
    public async Task ModelsList_CloudSynthesisAppearsWithUnknownMetadata_AndNeverInflatesCeiling()
    {
        var options = ThreeTargetOptions(
            cloudEnabled: true,
            cloudDefaultModel: CloudModelName,
            models: new Dictionary<string, ModelThresholds>
            {
                ["qwen2.5-coder"] = new() { CloudModel = CloudModelAlias }
            });
        var catalog = DefaultFleetCatalog(moeOnAllTargets: true);
        await using var harness = await CreateTestAppAsync(options, catalog);

        using var response = await harness.Client.GetAsync("/api/router/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        var cloud = ModelNode(root, CloudModelName);
        Assert.NotNull(cloud);
        Assert.Equal("Unknown", cloud!["architecture"]!.ToString());
        Assert.Equal("0", cloud["nativeContextWindow"]!.ToString());
        Assert.Equal("0", cloud["recommendedContextWindow"]!.ToString());
        Assert.Equal("true", cloud["cloudOverflowAvailable"]!.ToString());

        var alias = ModelNode(root, CloudModelAlias);
        Assert.NotNull(alias);
        Assert.Equal("true", alias!["cloudOverflowAvailable"]!.ToString());

        // A fleet model with a per-model CloudModel reports the hint but keeps its
        // fleet-only ceiling (no cloud inflation).
        var dense = ModelNode(root, DenseModel)!;
        Assert.Equal("true", dense["cloudOverflowAvailable"]!.ToString());
        Assert.Equal("262144", dense["recommendedContextWindow"]!.ToString());

        // The default cloud model makes the hint available for EVERY model.
        var moe = ModelNode(root, MoeModel)!;
        Assert.Equal("true", moe["cloudOverflowAvailable"]!.ToString());
        Assert.Equal("262144", moe["recommendedContextWindow"]!.ToString());
    }

    [Fact]
    public async Task ModelsList_CloudDisabled_NoSynthesis_AndHintFalse()
    {
        var options = ThreeTargetOptions(cloudEnabled: false, cloudDefaultModel: CloudModelName);
        var catalog = DefaultFleetCatalog(moeOnAllTargets: true);
        await using var harness = await CreateTestAppAsync(options, catalog);

        using var response = await harness.Client.GetAsync("/api/router/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Null(ModelNode(root, CloudModelName));

        var dense = ModelNode(root, DenseModel)!;
        Assert.Equal("false", dense["cloudOverflowAvailable"]!.ToString());
        Assert.Equal("262144", dense["recommendedContextWindow"]!.ToString());
    }

    // ------------------------------------------------------------------
    //  GET /api/router/models/{modelName}
    // ------------------------------------------------------------------

    [Fact]
    public async Task ModelDetail_Dense32b_ExposesLargeDenseVetoOnLaptop_AndServerResidentState()
    {
        var options = ThreeTargetOptions(cloudEnabled: false);
        var catalog = DefaultFleetCatalog(moeOnAllTargets: true);
        var serverState = new TargetExecutionState(
            (RoutingTarget)"Server",
            InFlightRequests: 1,
            LoadedModels: [new TargetLoadedModel(DenseModel, 19L * 1024 * 1024 * 1024, null)],
            19L * 1024 * 1024 * 1024,
            DateTimeOffset.UtcNow,
            Stale: false);
        await using var harness = await CreateTestAppAsync(
            options,
            catalog,
            executionStates: new Dictionary<RoutingTarget, TargetExecutionState>
            {
                [(RoutingTarget)"Server"] = serverState
            });

        using var response = await harness.Client.GetAsync("/api/router/models/" + DenseModel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(DenseModel, root["name"]!.ToString());
        Assert.Equal("Dense", root["architecture"]!.ToString());
        Assert.Equal("32500000000", root["parameterCount"]!.ToString());
        Assert.Equal("4096", root["nativeContextWindow"]!.ToString());
        Assert.Equal("262144", root["recommendedContextWindow"]!.ToString());

        var targets = root["targets"]!.AsArray();
        Assert.Equal(3, targets.Count); // declaration order, Cloud never listed
        Assert.Equal("Local", targets[0]!["target"]!.ToString());
        Assert.Equal("Server", targets[1]!["target"]!.ToString());
        Assert.Equal("Laptop", targets[2]!["target"]!.ToString());

        var laptop = TargetNode(root, "Laptop")!;
        Assert.Equal("false", laptop["capable"]!.ToString());
        Assert.Equal(ModelCapabilityEngine.LargeDenseVetoReason, laptop["vetoReason"]!.ToString());

        var server = TargetNode(root, "Server")!;
        Assert.Equal("true", server["capable"]!.ToString());
        Assert.Equal("262144", server["contextCeiling"]!.ToString());
        Assert.Equal("true", server["isResident"]!.ToString());
        Assert.Equal("1", server["inFlightRequests"]!.ToString());
        Assert.False((server as JsonObject)?.TryGetPropertyValue("vetoReason", out _) == true,
            "vetoReason must be ABSENT (not \"null\") for capable targets");

        var local = TargetNode(root, "Local")!;
        Assert.Equal("true", local["capable"]!.ToString());
        Assert.Equal("49152", local["contextCeiling"]!.ToString());
        Assert.Equal("false", local["isResident"]!.ToString());
    }

    [Fact]
    public async Task ModelDetail_ModelAbsentFromTarget_ReportsCatalogVetoReason()
    {
        // MoE model present ONLY on Local (MoE is hardware-capable everywhere → the only
        // veto on the other targets is catalog absence).
        var options = ThreeTargetOptions(cloudEnabled: false);
        var catalog = CreateCatalogClientMock(
            (LocalUrl, [(MoeModel, 20L * 1024 * 1024 * 1024)], new Dictionary<string, JsonObject> { [MoeModel] = MoeShow(35_000_000_000) }),
            (ServerUrl, [], new Dictionary<string, JsonObject>()),
            (LaptopUrl, [], new Dictionary<string, JsonObject>()));
        await using var harness = await CreateTestAppAsync(options, catalog);

        using var response = await harness.Client.GetAsync("/api/router/models/" + MoeModel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        var local = TargetNode(root, "Local")!;
        Assert.Equal("true", local["capable"]!.ToString());
        Assert.Equal("49152", local["contextCeiling"]!.ToString());

        var server = TargetNode(root, "Server")!;
        Assert.Equal("false", server["capable"]!.ToString());
        Assert.Equal("model not present in target catalog", server["vetoReason"]!.ToString());

        var laptop = TargetNode(root, "Laptop")!;
        Assert.Equal("false", laptop["capable"]!.ToString());
        Assert.Equal("model not present in target catalog", laptop["vetoReason"]!.ToString());
    }

    [Fact]
    public async Task ModelDetail_UnknownModel_Returns404ModelNotFound()
    {
        var catalog = DefaultFleetCatalog(moeOnAllTargets: true);
        await using var harness = await CreateTestAppAsync(ThreeTargetOptions(cloudEnabled: false), catalog);

        using var response = await harness.Client.GetAsync("/api/router/models/definitely-not-a-model:latest");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var root = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("model not found", root["error"]!.ToString());
    }

    [Fact]
    public async Task ModelDetail_TaglessPrefixAndCaseInsensitiveExactMatch_Resolve()
    {
        var catalog = DefaultFleetCatalog(moeOnAllTargets: true);
        await using var harness = await CreateTestAppAsync(ThreeTargetOptions(cloudEnabled: false), catalog);

        // Tagless name falls back to the ":latest" catalog entry.
        using var tagless = await harness.Client.GetAsync("/api/router/models/qwen3.6-35b-a3b-code");
        Assert.Equal(HttpStatusCode.OK, tagless.StatusCode);
        var taglessRoot = (JsonObject)JsonNode.Parse(await tagless.Content.ReadAsStringAsync())!;
        Assert.Equal(MoeModel, taglessRoot["name"]!.ToString());

        // Case-insensitive exact full-name match.
        using var mixedCase = await harness.Client.GetAsync("/api/router/models/QWEN3.6-35B-A3B-CODE:LATEST");
        Assert.Equal(HttpStatusCode.OK, mixedCase.StatusCode);
        var mixedRoot = (JsonObject)JsonNode.Parse(await mixedCase.Content.ReadAsStringAsync())!;
        Assert.Equal(MoeModel, mixedRoot["name"]!.ToString());
    }

    [Fact]
    public async Task ModelDetail_CloudOnlySynthesis_ReturnsEmptyTargetMatrix()
    {
        var options = ThreeTargetOptions(cloudEnabled: true, cloudDefaultModel: CloudModelName);
        var catalog = DefaultFleetCatalog(moeOnAllTargets: true);
        await using var harness = await CreateTestAppAsync(options, catalog);

        using var response = await harness.Client.GetAsync("/api/router/models/" + CloudModelName);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(CloudModelName, root["name"]!.ToString());
        Assert.Equal("Unknown", root["architecture"]!.ToString());
        Assert.Equal("0", root["nativeContextWindow"]!.ToString());
        Assert.Equal("0", root["recommendedContextWindow"]!.ToString());
        Assert.Empty(root["targets"]!.AsArray());
    }

    // ------------------------------------------------------------------
    //  POST /api/router/context/evaluate
    // ------------------------------------------------------------------

    private static async Task<(int Status, JsonObject? Root)> PostEvaluateAsync(HttpClient client, object body)
    {
        var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body),
            System.Text.Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync("/api/router/context/evaluate", content);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length > 0 ? (JsonObject)JsonNode.Parse(text)! : null);
    }

    [Fact]
    public async Task Evaluate_MoeRequest_MatchesProductionDecision_AndReportsBaselineBucket()
    {
        var options = ThreeTargetOptions(cloudEnabled: false);
        var catalog = DefaultFleetCatalog(moeOnAllTargets: true);
        await using var harness = await CreateTestAppAsync(options, catalog);

        var (status, root) = await PostEvaluateAsync(harness.Client,
            new { model = MoeModel, estimatedTokens = 45000, clientRequestedNumCtx = (object?)null });
        Assert.Equal(200, status);
        Assert.NotNull(root);

        // The preview must match the production decision pipeline exactly (same inputs).
        var decisionService = (RoutingDecisionService)harness.App.Services
            .GetRequiredService<IRoutingDecisionService>();
        var expectedTarget = await decisionService.DecideAsync(45000, MoeModel);
        Assert.Equal(expectedTarget.Value, root!["selectedTarget"]!.ToString());

        // Recomputed sizing: MoE on the winner's VRAM with the winner's CMax.
        var target = options.GetEffectiveTargets()
            .Single(t => string.Equals(t.Name, root!["selectedTarget"]!.ToString(), StringComparison.OrdinalIgnoreCase));
        var capability = new ModelCapabilityEngine(
                (IOllamaModelCatalogClient)harness.CatalogClient.Object,
                harness.App.Services.GetRequiredService<IMemoryCache>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelCapabilityEngine>.Instance,
                (IModelCatalogCacheService)harness.App.Services.GetRequiredService<IModelCatalogCacheService>())
            .DeriveCapability(MoeShowMetadata(), target.Name!, target, pinnedArchitecture: null);
        var expected = DynamicContextSizingEngine.Decide(
            options.DynamicContext, ModelArchitectureKind.Moe, target.GetEffectiveMaxVramMB(), capability.CMax, 45000, null);
        Assert.Equal(expected.InjectedNumCtx, (int)root!["injectedNumCtx"]!.GetValue<int>());
        Assert.Equal(expected.EffectiveCeiling, (int)root["targetMaxContext"]!.GetValue<int>());

        var profile = options.DynamicContext.GetProfile(expected.ProfileName);
        var expectedMode = expected.InjectedNumCtx > profile.DefaultContext ? "expanded" : "baseline";
        Assert.Equal(expectedMode, root["contextMode"]!.ToString());
        Assert.Equal(56250, (int)root["estimatedTokensWithMargin"]!.GetValue<int>()); // ceil(45000 * 1.25)

        // The winner's evaluation score is reported as the affinity score.
        Assert.True((double)root["affinityScore"]!.GetValue<double>() >= 0);

        // No inference: only catalog /api/tags + /api/show discovery happened.
        VerifyOnlyDiscoveryCallsWereMade(harness.CatalogClient);
    }

    // A metadata twin for in-test recomputation (same values the /api/show mock serves).
    private static ModelMetadata MoeShowMetadata() => new(
        MoeModel,
        WeightSizeBytes: 20L * 1024 * 1024 * 1024,
        ParameterCount: 35_000_000_000,
        ExpertCount: 32,
        ModelFamily: "qwen3.6",
        ModelfileNumCtx: null,
        HasModelInfo: true,
        ParametersRaw: null);

    [Fact]
    public async Task Evaluate_SmallDenseCrossingBaselineBucket_ReportsExpandedMode()
    {
        var options = ThreeTargetOptions(cloudEnabled: false);
        var catalog = DefaultFleetCatalog(moeOnAllTargets: true);
        await using var harness = await CreateTestAppAsync(options, catalog);

        // 40000 tokens: Local (CMax 49152) is overflow-disqualified (50000 > 49152), so the
        // decision winner is the next qualified target; the SmallDense requirement 40000
        // crosses the 32768 baseline → the intermediate 65536 bucket ("expanded").
        var (status, root) = await PostEvaluateAsync(harness.Client,
            new { model = SmallModel, estimatedTokens = 40000, clientRequestedNumCtx = (object?)null });
        Assert.Equal(200, status);
        Assert.NotNull(root);

        var decisionService = (RoutingDecisionService)harness.App.Services
            .GetRequiredService<IRoutingDecisionService>();
        var expectedTarget = await decisionService.DecideAsync(40000, SmallModel);
        Assert.Equal(expectedTarget.Value, root!["selectedTarget"]!.ToString());

        var target = options.GetEffectiveTargets()
            .Single(t => string.Equals(t.Name, root!["selectedTarget"]!.ToString(), StringComparison.OrdinalIgnoreCase));
        var smallMetadata = new ModelMetadata(SmallModel, 5L * 1024 * 1024 * 1024, 8_000_000_000, 0, "llama", 131_072, true, null);
        var capability = new ModelCapabilityEngine(
                (IOllamaModelCatalogClient)harness.CatalogClient.Object,
                harness.App.Services.GetRequiredService<IMemoryCache>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelCapabilityEngine>.Instance,
                (IModelCatalogCacheService)harness.App.Services.GetRequiredService<IModelCatalogCacheService>())
            .DeriveCapability(smallMetadata, target.Name!, target, pinnedArchitecture: null);
        var expected = DynamicContextSizingEngine.Decide(
            options.DynamicContext, capability.Architecture, target.GetEffectiveMaxVramMB(), capability.CMax, 40000, null);
        Assert.Equal(expected.InjectedNumCtx, (int)root!["injectedNumCtx"]!.GetValue<int>());

        var profile = options.DynamicContext.GetProfile(expected.ProfileName);
        Assert.True((int)root!["injectedNumCtx"]!.GetValue<int>() > profile.DefaultContext);
        Assert.Equal("expanded", root["contextMode"]!.ToString());
        Assert.Equal(50000, (int)root["estimatedTokensWithMargin"]!.GetValue<int>());
    }

    [Fact]
    public async Task Evaluate_ClientNumCtxClampedToTargetCeiling()
    {
        var options = ThreeTargetOptions(cloudEnabled: false);
        var catalog = DefaultFleetCatalog(moeOnAllTargets: true);
        await using var harness = await CreateTestAppAsync(options, catalog);

        // Small prompt on the desktop-class Local target: the client's static 131072 is an
        // upper-bound preference, clamped to Local's CMax (49152).
        var (status, root) = await PostEvaluateAsync(harness.Client,
            new { model = SmallModel, estimatedTokens = 1000, clientRequestedNumCtx = 131072 });
        Assert.Equal(200, status);
        Assert.NotNull(root);

        Assert.Equal("Local", root!["selectedTarget"]!.ToString());

        var target = options.GetEffectiveTargets().Single(t => t.Name == "Local");
        var smallMetadata = new ModelMetadata(SmallModel, 5L * 1024 * 1024 * 1024, 8_000_000_000, 0, "llama", 131_072, true, null);
        var capability = new ModelCapabilityEngine(
                (IOllamaModelCatalogClient)harness.CatalogClient.Object,
                harness.App.Services.GetRequiredService<IMemoryCache>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelCapabilityEngine>.Instance,
                (IModelCatalogCacheService)harness.App.Services.GetRequiredService<IModelCatalogCacheService>())
            .DeriveCapability(smallMetadata, "Local", target, pinnedArchitecture: null);
        var expected = DynamicContextSizingEngine.Decide(
            options.DynamicContext, capability.Architecture, target.GetEffectiveMaxVramMB(), capability.CMax, 1000, 131072);
        Assert.Equal(expected.InjectedNumCtx, (int)root!["injectedNumCtx"]!.GetValue<int>());
        Assert.True((int)root!["injectedNumCtx"]!.GetValue<int>() <= capability.CMax);

        var profile = options.DynamicContext.GetProfile(expected.ProfileName);
        Assert.Equal(expected.InjectedNumCtx > profile.DefaultContext ? "expanded" : "baseline",
            root["contextMode"]!.ToString());
        Assert.Equal(1250, (int)root["estimatedTokensWithMargin"]!.GetValue<int>());
    }

    [Fact]
    public async Task Evaluate_BlankModel_OrZeroTokens_Returns400()
    {
        var catalog = DefaultFleetCatalog(moeOnAllTargets: true);
        await using var harness = await CreateTestAppAsync(ThreeTargetOptions(cloudEnabled: false), catalog);

        var (status1, root1) = await PostEvaluateAsync(harness.Client,
            new { model = "  ", estimatedTokens = 1000 });
        Assert.Equal(400, status1);
        Assert.NotNull(root1?["error"]);

        var (status2, root2) = await PostEvaluateAsync(harness.Client,
            new { model = DenseModel, estimatedTokens = 0 });
        Assert.Equal(400, status2);
        Assert.NotNull(root2?["error"]);
    }

    [Fact]
    public async Task Evaluate_AllTargetsOfflineAndCloudDisabled_Returns503()
    {
        var options = ThreeTargetOptions(cloudEnabled: false);
        var catalog = DefaultFleetCatalog(moeOnAllTargets: true);
        await using var harness = await CreateTestAppAsync(
            options,
            catalog,
            livenessStates: new Dictionary<RoutingTarget, TargetLivenessState>
            {
                [RoutingTarget.Local] = TargetLivenessState.Offline,
                [(RoutingTarget)"Server"] = TargetLivenessState.Offline,
                [(RoutingTarget)"Laptop"] = TargetLivenessState.Offline
            },
            availabilityFlags: new Dictionary<RoutingTarget, bool>
            {
                [RoutingTarget.Local] = true,
                [(RoutingTarget)"Server"] = true,
                [(RoutingTarget)"Laptop"] = true,
                [RoutingTarget.Cloud] = false
            });

        var (status, root) = await PostEvaluateAsync(harness.Client,
            new { model = DenseModel, estimatedTokens = 1000 });
        Assert.Equal(503, status);
        Assert.NotNull(root);
        Assert.Equal("no available routing target", root!["error"]!.ToString());
    }

    // ------------------------------------------------------------------
    //  Minimal-host degradation
    // ------------------------------------------------------------------

    [Fact]
    public async Task MinimalHost_NoOptionalServices_ModelsEndpointStillAnswers200()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = LocalUrl, HardwareHint = "16gb" }
            ]
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);
        builder.Services.AddSingleton(MsOptions.Create(options));
        builder.Services.AddSingleton(availability.Object);

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        try
        {
            using var response = await client.GetAsync("/api/router/models");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var root = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Empty(root["models"]!.AsArray()); // no catalog services → degraded empty list, no 500

            using var detail = await client.GetAsync("/api/router/models/" + DenseModel);
            Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

            // No decision pipeline registered → the preview cannot resolve a target.
            var (status, evalRoot) = await PostEvaluateAsync(client, new { model = DenseModel, estimatedTokens = 1000 });
            Assert.Equal(503, status);
            Assert.Equal("no available routing target", evalRoot!["error"]!.ToString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
