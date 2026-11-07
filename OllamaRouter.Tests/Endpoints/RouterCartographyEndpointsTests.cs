using OllamaRouter.Tests.TestHelpers;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Tests.Services;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Endpoints;

public class RouterCartographyEndpointsTests
{
    private const string LocalUrl = "http://127.0.0.1:11435";
    private const string ServerUrl = "http://192.168.1.100:11434";
    private const string LaptopUrl = "http://192.168.1.105:11435";

    private const string MoeModel = "qwen3.6-35b-a3b-code:latest";
    private const string DenseModel = "qwen2.5-coder:32b";
    private const string CloudModelName = "cloud-coder";

    private static readonly RoutingTarget Local = RoutingTarget.Local;
    private static readonly RoutingTarget Server = new("Server");
    private static readonly RoutingTarget Laptop = new("Laptop");

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(WebApplication app, HttpClient client, Mock<IOllamaModelCatalogClient>? catalogClient)
        {
            App = app;
            Client = client;
            CatalogClient = catalogClient;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public Mock<IOllamaModelCatalogClient>? CatalogClient { get; }

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
                    MaxVramMB = 16384,
                    MaxParallel = 1
                },
                new OllamaTargetOptions
                {
                    Name = "Server",
                    Url = ServerUrl,
                    MaxVramMB = 98304,
                    MaxParallel = 4
                },
                new OllamaTargetOptions
                {
                    Name = "Laptop",
                    Url = LaptopUrl,
                    MaxVramMB = 8192,
                    MaxParallel = 1
                }
            ],
            Models = (models as Dictionary<string, ModelThresholds>) ?? new(),
            DynamicContext = new DynamicContextOptions { Enabled = true },
            CloudOverflow =
            {
                Enabled = cloudEnabled,
                DefaultModel = cloudDefaultModel
            }
        };

    private static TargetLivenessEntry Entry(RoutingTarget target, TargetLivenessState state) =>
        new(target, state, state == TargetLivenessState.Offline ? "connection error" : null, DateTimeOffset.UtcNow, null);

    /// <summary>
    /// Tags array with optional per-model details (size + quantization_level).
    /// </summary>
    private static JsonArray TagsBody(params (string Name, long Size, string? Quant)[] models)
    {
        var arr = new JsonArray();
        foreach (var (name, size, quant) in models)
        {
            var obj = new JsonObject { ["name"] = name, ["model"] = name, ["size"] = size };
            var details = new JsonObject();
            if (quant is not null)
            {
                details["quantization_level"] = quant;
            }

            obj["details"] = details;
            arr.Add(obj);
        }

        return arr;
    }

    private static JsonObject MoeShow(long parameterCount, string? showQuant = null)
    {
        var details = new JsonObject { ["family"] = "qwen3.6", ["parameter_size"] = "35.0B" };
        if (showQuant is not null)
        {
            details["quantization_level"] = showQuant;
        }

        return new JsonObject
        {
            ["model_info"] = new JsonObject
            {
                ["general.parameter_count"] = parameterCount,
                ["qnn.expert_count"] = (JsonNode)32L
            },
            ["details"] = details
        };
    }

    private static JsonObject DenseShow(long parameterCount, int? numCtx = null, string? showQuant = null)
    {
        var details = new JsonObject { ["family"] = "qwen2.5" };
        if (showQuant is not null)
        {
            details["quantization_level"] = showQuant;
        }

        var obj = new JsonObject
        {
            ["model_info"] = new JsonObject { ["general.parameter_count"] = parameterCount },
            ["details"] = details
        };

        if (numCtx is { } value)
        {
            obj["modelfile"] = $"# Modelfile\nPARAMETER num_ctx {value}\n";
        }

        return obj;
    }

    /// <summary>
    /// The task matrix fleet: MoE model resident on Local + Server (tags carry the
    /// catalog quantization; Local omits it on tags to exercise the /api/show fallback),
    /// dense 32B present-but-not-resident on Laptop (small node → LargeDense veto),
    /// cloud-only synthesis when cloud is enabled. STRICT mock: any other call fails.
    /// </summary>
    private static Mock<IOllamaModelCatalogClient> CreateCartographyCatalogMock()
    {
        var moeShows = new Dictionary<string, JsonObject>
        {
            [MoeModel] = MoeShow(35_000_000_000, showQuant: "Q4_K_M") // Local: tags omit quant → show fallback
        };
        var serverShows = new Dictionary<string, JsonObject>
        {
            [MoeModel] = MoeShow(35_000_000_000),
            [DenseModel] = DenseShow(32_500_000_000)
        };
        var laptopShows = new Dictionary<string, JsonObject>
        {
            [DenseModel] = DenseShow(32_500_000_000, showQuant: "Q5_K_M")
        };

        // NOTE: Local + Laptop share LaptopUrl in this fixture only to keep the harness small —
        // wait, no: Local/Server/Laptop each use DISTINCT urls, so the catalog name-set cache
        // (keyed by URL) stays independent per node. Laptop is OFFLINE and its tags still carry
        // the dense model (stale-while-revalidate): its dense deployment row is present=true,
        // while the MoE model is absent on it.
        return CreateCatalogClientMock(
            (LocalUrl, [(MoeModel, 20L * 1024 * 1024 * 1024, "Q4_K_M")], moeShows),
            (ServerUrl, [(MoeModel, 20L * 1024 * 1024 * 1024, "Q4_K_M"), (DenseModel, 19L * 1024 * 1024 * 1024, "Q3_K_S")], serverShows),
            (LaptopUrl, [(DenseModel, 19L * 1024 * 1024 * 1024, "Q5_K_M")], laptopShows));
    }

    private static Mock<IOllamaModelCatalogClient> CreateCatalogClientMock(
        params (string Url, (string Name, long Size, string? Quant)[] Tags, IReadOnlyDictionary<string, JsonObject> Shows)[] targets)
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
        Action<IServiceCollection>? additionalServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        var states = livenessStates ?? new Dictionary<RoutingTarget, TargetLivenessState>
        {
            [Local] = TargetLivenessState.Online,
            [Server] = TargetLivenessState.Online,
            [Laptop] = TargetLivenessState.Offline // The offline node in the join matrix.
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
            .Setup(l => l.IsOnline(It.IsAny<RoutingTarget>(), It.IsAny<TimeSpan>()))
            .Returns<RoutingTarget, TimeSpan>((t, _) => states.TryGetValue(t, out var state) && state == TargetLivenessState.Online);
        liveness
            .SetupGet(l => l.ServiceStartTimeUtc)
            .Returns(DateTimeOffset.UtcNow.AddMinutes(1));

        var tracker = new Mock<ITargetExecutionTracker>();
        tracker
            .Setup(t => t.GetState(It.IsAny<RoutingTarget>()))
            .Returns<RoutingTarget>(t => executionStates?.GetValueOrDefault(t));

        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(new TestOptionsMonitor<OllamaRouterOptions>(options));
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

        additionalServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new Harness(app, client, catalogClient);
    }

    private static JsonObject? NodeNode(JsonObject root, string name)
    {
        foreach (var node in root["nodes"]!.AsArray())
        {
            if (node?["name"]?.ToString() == name)
            {
                return node is JsonObject obj ? obj : null;
            }
        }

        return null;
    }

    private static JsonObject? ModelNode(JsonObject root, string name)
    {
        foreach (var node in root["models"]!.AsArray())
        {
            if (node?["name"]?.ToString() == name)
            {
                return node is JsonObject obj ? obj : null;
            }
        }

        return null;
    }

    private static JsonObject? DeploymentNode(JsonObject modelNode, string target)
    {
        foreach (var node in modelNode["deployments"]!.AsArray())
        {
            if (node?["target"]?.ToString() == target)
            {
                return node is JsonObject obj ? obj : null;
            }
        }

        return null;
    }

    private static TargetExecutionState State(RoutingTarget target, bool stale, int inFlight = 0, params (string Name, long Size, string? Quant, int? Ctx)[] models)
    {
        var loaded = models
            .Select(m => new TargetLoadedModel(m.Name, m.Size, m.Ctx, m.Quant))
            .ToArray();
        return new TargetExecutionState(
            target,
            inFlight,
            loaded,
            loaded.Sum(m => m.SizeVramBytes),
            DateTimeOffset.UtcNow,
            stale);
    }

    // ------------------------------------------------------------------
    //  AC2/AC3 — full join matrix (mocked 3-node + cloud fleet)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cartography_JoinMatrix_FullPayloadShape_DeploymentsAndTrackerMirror()
    {
        var options = ThreeTargetOptions(cloudEnabled: true, cloudDefaultModel: CloudModelName);
        var catalog = CreateCartographyCatalogMock();

        var moeSize = 20L * 1024 * 1024 * 1024;
        var denseSize = 19L * 1024 * 1024 * 1024;
        var executionStates = new Dictionary<RoutingTarget, TargetExecutionState>
        {
            // MoE resident on Local (ps quant differs from the catalog: the daemon swapped
            // the quantization of the resident process) and on Server.
            [Local] = State(Local, false, 1,
                (MoeModel, moeSize, "Q8_0", 16384)),
            [Server] = State(Server, false, 0,
                (MoeModel, moeSize, "Q4_K_M", null),
                (DenseModel, denseSize, null, 8192)),
            // Laptop offline + its tracker slot is stale (last-known data still served).
            [Laptop] = State(Laptop, true, 0)
        };

        await using var harness = await CreateTestAppAsync(options, catalog, executionStates: executionStates);

        using var response = await harness.Client.GetAsync("/api/router/cartography");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        // Top-level shape: exactly nodes + models + loadedModels.
        Assert.Equal(new[] { "loadedModels", "models", "nodes" }, root.Select(n => n.Key).OrderBy(k => k).ToArray());

        // ---- nodes[]: configuration order, Cloud last ----
        var nodes = root["nodes"]!.AsArray();
        Assert.Equal(new[] { "Local", "Server", "Laptop", "Cloud" },
            nodes.Select(n => n?["name"]?.ToString()).ToArray());

        var local = NodeNode(root, "Local")!;
        Assert.Equal(LocalUrl, local["url"]!.ToString());
        Assert.Equal("local", local["location"]!.ToString());
        Assert.Equal("true", local["enabled"]!.ToString());
        Assert.Equal("Online", local["liveness"]!.ToString());
        Assert.Equal("false", local["stale"]!.ToString());
        Assert.NotNull(local["lastRefreshedAtUtc"]);
        var localHw = local["hardware"]! as JsonObject;
        Assert.Equal("16384", localHw!["totalVramMB"]!.ToString());
        Assert.Equal("false", localHw["isUnifiedMemory"]!.ToString());
        Assert.Equal("baseline", local["contextAllocation"]!.ToString());
        Assert.Equal("1", local["inFlightRequests"]!.ToString());
        Assert.Equal(moeSize.ToString(), local["totalLoadedVramBytes"]!.ToString());
        Assert.False(local.TryGetPropertyValue("freeVramMB", out _), "freeVramMB must not exist (Q3)");

        var localLoaded = local["loadedModels"]!.AsArray().Select(l => (JsonObject)l!).Single();
        Assert.Equal(MoeModel, localLoaded["name"]!.ToString());
        Assert.Equal(moeSize.ToString(), localLoaded["sizeVramBytes"]!.ToString());
        // ps-sourced quantization (the live process), NOT the catalog value.
        Assert.Equal("Q8_0", localLoaded["quantization"]!.ToString());
        Assert.Equal("16384", localLoaded["contextLength"]!.ToString());

        var server = NodeNode(root, "Server")!;
        Assert.Equal("remote", server["location"]!.ToString());
        Assert.Equal("98304", ((JsonObject)server["hardware"]!)["totalVramMB"]!.ToString());
        Assert.Equal((moeSize + denseSize).ToString(), server["totalLoadedVramBytes"]!.ToString());
        var serverLoaded = server["loadedModels"]!.AsArray().Select(l => (JsonObject)l!).ToArray();
        Assert.Equal(2, serverLoaded.Length);
        Assert.Equal("Q4_K_M", serverLoaded[0]["quantization"]!.ToString());
        Assert.False(serverLoaded[1].TryGetPropertyValue("quantization", out _), "absent ps quant must be null-omitted");

        var laptop = NodeNode(root, "Laptop")!;
        Assert.Equal("Offline", laptop["liveness"]!.ToString());
        Assert.Equal("true", laptop["stale"]!.ToString()); // stale tracker slot flagged
        Assert.Equal("0", laptop["totalLoadedVramBytes"]!.ToString());
        Assert.Empty(((JsonArray)laptop["loadedModels"]!));
        Assert.Equal("8192", ((JsonObject)laptop["hardware"]!)["totalVramMB"]!.ToString());

        var cloud = NodeNode(root, "Cloud")!;
        Assert.Equal("cloud", cloud["location"]!.ToString());
        Assert.Equal("Online", cloud["liveness"]!.ToString());
        Assert.Equal("true", cloud["enabled"]!.ToString());
        Assert.Equal("false", cloud["stale"]!.ToString());
        Assert.Equal("baseline", cloud["contextAllocation"]!.ToString());
        Assert.Equal(0, (int)cloud["inFlightRequests"]!.GetValue<int>());
        Assert.Equal(0L, (long)cloud["totalLoadedVramBytes"]!.GetValue<long>());
        Assert.False((cloud as JsonObject)!.TryGetPropertyValue("hardware", out _), "Cloud hardware must be absent");
        Assert.False(cloud.TryGetPropertyValue("url", out _), "Cloud url must be null-omitted");
        Assert.False(cloud.TryGetPropertyValue("lastRefreshedAtUtc", out _));

        // ---- models[]: merged fleet catalog + cloud synthesis ----
        var models = root["models"]!.AsArray();
        Assert.Equal(3, models.Count);

        var moe = ModelNode(root, MoeModel)!;
        Assert.Equal("Moe", moe["architecture"]!.ToString());
        Assert.Equal("35000000000", moe["parameterCount"]!.ToString());
        Assert.Equal(moeSize.ToString(), moe["weightSizeBytes"]!.ToString());
        // Catalog-sourced quantization: Local's tags entry carries it (the hoster of
        // primary metadata).
        Assert.Equal("Q4_K_M", moe["quantization"]!.ToString());
        Assert.Equal("true", moe["cloudOverflowAvailable"]!.ToString());

        var moeLocal = DeploymentNode(moe, "Local")!;
        Assert.Equal("true", moeLocal["present"]!.ToString());
        Assert.Equal("true", moeLocal["capable"]!.ToString());
        Assert.Equal("true", moeLocal["isResident"]!.ToString());
        // The EXACT tracker size_vram of the resident process.
        Assert.Equal(moeSize.ToString(), moeLocal["loadedSizeVramBytes"]!.ToString());
        Assert.Equal("1", moeLocal["inFlightRequests"]!.ToString());
        // Per-target catalog quantization (Local's tags value).
        Assert.Equal("Q4_K_M", moeLocal["quantization"]!.ToString());
        Assert.False(moeLocal.TryGetPropertyValue("vetoReason", out _), "capable target: vetoReason omitted");

        var moeServer = DeploymentNode(moe, "Server")!;
        Assert.Equal("true", moeServer["capable"]!.ToString());
        Assert.Equal("true", moeServer["isResident"]!.ToString());
        Assert.Equal(moeSize.ToString(), moeServer["loadedSizeVramBytes"]!.ToString());
        Assert.Equal("0", moeServer["inFlightRequests"]!.ToString());
        Assert.Equal("Q4_K_M", moeServer["quantization"]!.ToString());

        var moeLaptop = DeploymentNode(moe, "Laptop")!;
        // The offline node's catalog cache is empty/expired → absent veto (AC5).
        Assert.Equal("false", moeLaptop["present"]!.ToString());
        Assert.Equal("false", moeLaptop["capable"]!.ToString());
        Assert.Equal("model not present in target catalog", moeLaptop["vetoReason"]!.ToString());
        Assert.Equal("false", moeLaptop["isResident"]!.ToString());
        Assert.False(moeLaptop.TryGetPropertyValue("loadedSizeVramBytes", out _), "non-resident: loadedSizeVramBytes omitted");
        Assert.False(moeLaptop.TryGetPropertyValue("quantization", out _), "absent metadata: quantization omitted");

        var dense = ModelNode(root, DenseModel)!;
        Assert.Equal("Dense", dense["architecture"]!.ToString());
        Assert.Equal("32500000000", dense["parameterCount"]!.ToString());
        Assert.Equal(denseSize.ToString(), dense["weightSizeBytes"]!.ToString());
        // Primary metadata is from the FIRST hoster in declaration order (Local doesn't host
        // the dense model, so Server is the primary hoster) — whose tags carry Q3_K_S.
        Assert.Equal("Q3_K_S", dense["quantization"]!.ToString());

        var denseServer = DeploymentNode(dense, "Server")!;
        Assert.Equal("true", denseServer["capable"]!.ToString());
        Assert.Equal("true", denseServer["isResident"]!.ToString());
        Assert.Equal(denseSize.ToString(), denseServer["loadedSizeVramBytes"]!.ToString());
        Assert.Equal("Q3_K_S", denseServer["quantization"]!.ToString()); // Server's tags value

        var denseLaptop = DeploymentNode(dense, "Laptop")!;
        // Laptop is the small node (8 GB < 12 GB): the dense 32B model is present in its
        // (stale) catalog but not resident, and is vetoed as LargeDense on small-class HW.
        Assert.Equal("true", denseLaptop["present"]!.ToString());
        Assert.Equal("false", denseLaptop["capable"]!.ToString());
        Assert.Equal(ModelCapabilityEngine.LargeDenseVetoReason, denseLaptop["vetoReason"]!.ToString());
        Assert.Equal("false", denseLaptop["isResident"]!.ToString());
        Assert.False(denseLaptop.TryGetPropertyValue("loadedSizeVramBytes", out _), "non-resident: loadedSizeVramBytes omitted");
        Assert.Equal("Q5_K_M", denseLaptop["quantization"]!.ToString()); // Laptop's tags value

        // The cloud-only synthesis model: no fleet target hosts it.
        var cloudModel = ModelNode(root, CloudModelName)!;
        Assert.Equal("Unknown", cloudModel["architecture"]!.ToString());
        Assert.Equal("0", cloudModel["nativeContextWindow"]!.ToString());
        Assert.Equal("0", cloudModel["recommendedContextWindow"]!.ToString());
        Assert.Equal("true", cloudModel["cloudOverflowAvailable"]!.ToString());
        Assert.Empty(((JsonArray)cloudModel["deployments"]!)); // deployments: []
        Assert.False(cloudModel.TryGetPropertyValue("quantization", out _));

        // ---- loadedModels[]: flat cartesian of node rows, no cross-target dedup ----
        var flat = root["loadedModels"]!.AsArray();
        Assert.Equal(3, flat.Count); // Local moe + Server moe + Server dense (Laptop stale/empty, Cloud none)
        var flat0 = flat[0]! as JsonObject;
        Assert.Equal(MoeModel, flat0!["name"]!.ToString());
        Assert.Equal("Local", flat0["target"]!.ToString());
        Assert.Equal(moeSize.ToString(), flat0["sizeVramBytes"]!.ToString());
        Assert.Equal("Q8_0", flat0["quantization"]!.ToString());
        Assert.Equal("baseline", flat0["contextAllocation"]!.ToString());
        Assert.Equal("Online", flat0["liveness"]!.ToString());
        Assert.Equal("false", flat0["stale"]!.ToString());
        Assert.Equal("16384", flat0["contextLength"]!.ToString());

        var flat1 = flat[1]! as JsonObject;
        Assert.Equal(MoeModel, flat1!["name"]!.ToString());
        Assert.Equal("Server", flat1["target"]!.ToString());

        var flat2 = flat[2]! as JsonObject;
        Assert.Equal(DenseModel, flat2!["name"]!.ToString());
        Assert.Equal("Server", flat2["target"]!.ToString());
        Assert.False(flat2.TryGetPropertyValue("quantization", out _));
        Assert.Equal("8192", flat2["contextLength"]!.ToString());
        Assert.Equal("false", flat2["stale"]!.ToString());
    }

    [Fact]
    public async Task Cartography_OfflineNode_DeploymentRowsAgreeWithModelDetailEndpoint()
    {
        // AC3: for every (model, node) pair the deployment row agrees with
        // GET /api/router/models/{model} on present/capable/contextCeiling/vetoReason/
        // isResident/inFlightRequests.
        var options = ThreeTargetOptions(cloudEnabled: false);
        var catalog = CreateCartographyCatalogMock();
        var moeSize = 20L * 1024 * 1024 * 1024;
        var executionStates = new Dictionary<RoutingTarget, TargetExecutionState>
        {
            [Server] = State(Server, false, 0, (MoeModel, moeSize, "Q4_K_M", null))
        };

        await using var harness = await CreateTestAppAsync(options, catalog, executionStates: executionStates);

        using var cartography = await harness.Client.GetAsync("/api/router/cartography");
        Assert.Equal(HttpStatusCode.OK, cartography.StatusCode);
        var cartRoot = (JsonObject)JsonNode.Parse(await cartography.Content.ReadAsStringAsync())!;
        var moe = ModelNode(cartRoot, MoeModel)!;

        using var detail = await harness.Client.GetAsync("/api/router/models/" + MoeModel);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailRoot = (JsonObject)JsonNode.Parse(await detail.Content.ReadAsStringAsync())!;

        var detailTargets = detailRoot["targets"]!.AsArray()
            .ToDictionary(n => n?["target"]?.ToString(), n => (JsonObject)n!, StringComparer.Ordinal);

        foreach (var target in new[] { "Local", "Server", "Laptop" })
        {
            var detailTarget = detailTargets[target];

            var cartTarget = DeploymentNode(moe, target)!;
            Assert.Equal(detailTarget["capable"]!.ToString(), cartTarget["capable"]!.ToString());
            Assert.Equal(detailTarget["contextCeiling"]!.ToString(), cartTarget["contextCeiling"]!.ToString());
            Assert.Equal(detailTarget["isResident"]!.ToString(), cartTarget["isResident"]!.ToString());
            Assert.Equal(detailTarget["inFlightRequests"]!.ToString(), cartTarget["inFlightRequests"]!.ToString());
            var detailVeto = detailTarget.TryGetPropertyValue("vetoReason", out var v) ? v?.ToString() : null;
            var cartVeto = cartTarget.TryGetPropertyValue("vetoReason", out var cv) ? cv?.ToString() : null;
            Assert.Equal(detailVeto, cartVeto);
        }
    }

    [Fact]
    public async Task Cartography_LocalTagsOmitQuantization_FallsBackToShowDetails()
    {
        // AC4: Local's tags entry carries NO quantization_level; Local is the first hoster
        // (primary metadata), so the catalog quantization must come from /api/show details.
        var catalog = CreateCatalogClientMock(
            (LocalUrl, [(MoeModel, 20L * 1024 * 1024 * 1024, null)],
                new Dictionary<string, JsonObject> { [MoeModel] = MoeShow(35_000_000_000, showQuant: "Q6_K_L") }));

        var options = new OllamaRouterOptions
        {
            Targets = [new OllamaTargetOptions { Name = "Local", Url = LocalUrl, MaxVramMB = 16384 }]
        };
        await using var harness = await CreateTestAppAsync(options, catalog);

        using var response = await harness.Client.GetAsync("/api/router/cartography");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        var moe = ModelNode(root, MoeModel)!;
        Assert.Equal("Q6_K_L", moe["quantization"]!.ToString());
        var local = DeploymentNode(moe, "Local")!;
        Assert.Equal("Q6_K_L", local["quantization"]!.ToString());
    }

    // ------------------------------------------------------------------
    //  AC6 — no-hang + capability-cache no-reissue (fake daemon pattern)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cartography_HangingDaemon_CompletesWithinBudget_AndSecondCallReissuesNoShow()
    {
        var handler = new FakeOllamaHttpHandler();
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var realCatalogClient = new OllamaModelCatalogClient(httpClient);

        handler.ForAuthority("127.0.0.1:11435", new FakeOllamaHttpHandler.TargetBehavior
        {
            Responses =
            {
                ["GET /api/tags"] = """{"models":[{"name":"llama3:latest","size":5000,"details":{"quantization_level":"Q4_K_M"}}]}""",
                ["POST /api/show"] = """{"model_info":{"general.parameter_count":8000000000},"details":{"family":"llama"}}"""
            }
        });
        handler.ForAuthority("192.168.1.100:11434", new FakeOllamaHttpHandler.TargetBehavior
        {
            Responses =
            {
                ["GET /api/tags"] = """{"models":[{"name":"llama3:latest","size":5000,"details":{"quantization_level":"Q4_K_M"}}]}"""
            }
        });

        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = LocalUrl, MaxVramMB = 16384 },
                new OllamaTargetOptions { Name = "Server", Url = ServerUrl, MaxVramMB = 98304 }
            ]
        };

        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.GetEntries()).Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
        {
            [Local] = Entry(Local, TargetLivenessState.Online),
            [Server] = Entry(Server, TargetLivenessState.Online)
        });
        liveness.Setup(l => l.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        liveness.Setup(l => l.IsOnline(It.IsAny<RoutingTarget>(), It.IsAny<TimeSpan>())).Returns(true);
        liveness.SetupGet(l => l.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddMinutes(1));
        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);
        availability.Setup(t => t.GetSnapshot()).Returns(new TargetAvailabilitySnapshot(
            new Dictionary<RoutingTarget, bool> { [Local] = true, [Server] = true }));
        // STRICT: the endpoint must NEVER trigger a tracker refresh (the hosted service owns /api/ps).
        var tracker = new Mock<ITargetExecutionTracker>(MockBehavior.Strict);
        tracker
            .Setup(t => t.GetState(It.IsAny<RoutingTarget>()))
            .Returns<RoutingTarget>(_ => null);
        tracker
            .Setup(t => t.RefreshAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });
        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(new TestOptionsMonitor<OllamaRouterOptions>(options));
        builder.Services.AddSingleton<IOllamaModelCatalogClient>(realCatalogClient);
        builder.Services.AddSingleton(liveness.Object);
        builder.Services.AddSingleton(availability.Object);
        builder.Services.AddSingleton(tracker.Object);
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<ITargetFailureStreakService, TargetFailureStreakService>();
        builder.Services.AddSingleton<IModelCatalogCacheService>(sp =>
            new ModelCatalogCacheService(
                sp.GetRequiredService<IOllamaModelCatalogClient>(),
                sp.GetRequiredService<IMemoryCache>(),
                NullLogger<ModelCatalogCacheService>.Instance));
        builder.Services.AddSingleton<ModelCapabilityEngine>();
        builder.Services.AddSingleton<IFleetAggregationService, FleetAggregationService>();

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        try
        {
            // Warm the name-set + capability caches (both daemons fast).
            using (var warm = await client.GetAsync("/api/router/cartography"))
            {
                Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
            }

            var showAfterFirst = handler.CountCalls("127.0.0.1:11435", "POST /api/show");
            Assert.True(showAfterFirst >= 1);

            // The local daemon now hangs (budget-observant cancellation, as in
            // ModelCatalogTests.GetApiTags_SlowAsleepTarget_ServesStaleCatalogQuickly).
            handler.Behavior("127.0.0.1:11435").Hang = true;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var response = await client.GetAsync("/api/router/cartography");
            var body = await response.Content.ReadAsStringAsync();
            sw.Stop();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1200),
                $"Cartography took {sw.Elapsed.TotalMilliseconds:F0} ms; a hanging daemon must not hang the endpoint.");

            var root = (JsonObject)JsonNode.Parse(body)!;
            // The model list comes from the stale catalog cache (stale-while-revalidate).
            var moe = ModelNode(root, "llama3:latest");
            Assert.NotNull(moe);

            // Within the 30 s capability cache: ZERO additional POST /api/show re-issues.
            Assert.Equal(showAfterFirst, handler.CountCalls("127.0.0.1:11435", "POST /api/show"));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            httpClient.Dispose();
        }

        // The endpoint never called ITargetExecutionTracker.RefreshAsync (strict mock).
        tracker.Verify(t => t.RefreshAsync(It.IsAny<CancellationToken>()), Times.Never());
    }

    // ------------------------------------------------------------------
    //  AC9 — minimal host degradation
    // ------------------------------------------------------------------

    [Fact]
    public async Task MinimalHost_OnlyOptionsAndAvailability_Returns200WithDegradedRows()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = LocalUrl, MaxVramMB = 16384 },
                new OllamaTargetOptions { Name = "Server", Url = ServerUrl, MaxVramMB = 98304 }
            ]
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);
        availability.Setup(t => t.GetSnapshot()).Returns(new TargetAvailabilitySnapshot(
            new Dictionary<RoutingTarget, bool> { [Local] = true, [Server] = true }));
        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(new TestOptionsMonitor<OllamaRouterOptions>(options));
        builder.Services.AddSingleton(availability.Object);

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        try
        {
            using var response = await client.GetAsync("/api/router/cartography");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var root = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            var nodes = root["nodes"]!.AsArray();
            // Every effective target + Cloud row, degraded (Offline: no liveness/catalog stack).
            Assert.Equal(new[] { "Local", "Server", "Cloud" }, nodes.Select(n => n?["name"]?.ToString()).ToArray());
            var local = nodes[0]! as JsonObject;
            Assert.Equal("Offline", local!["liveness"]!.ToString());
            Assert.False(local.TryGetPropertyValue("lastRefreshedAtUtc", out _));
            var hw = local["hardware"]! as JsonObject;
            Assert.Equal("16384", hw!["totalVramMB"]!.ToString()); // options MaxVramMB still flows
            Assert.Equal("0", local["inFlightRequests"]!.ToString());
            Assert.Equal(0L, (long)local["totalLoadedVramBytes"]!.GetValue<long>());
            Assert.Empty(((JsonArray)local["loadedModels"]!));

            Assert.Empty(root["models"]!.AsArray());
            Assert.Empty(root["loadedModels"]!.AsArray());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------
    //  AC8 — AOT serialization round-trip via the source-gen context
    // ------------------------------------------------------------------

    private static CartographyNodeHardware FullHardware() => new(
        GpuName: "NVIDIA GeForce RTX 4090",
        GpuVendor: "NVIDIA",
        TotalVramMB: 24576,
        IsUnifiedMemory: false,
        SystemRamMB: 65536);

    [Fact]
    public void CartographyResponse_SerializationRoundTrip_FullyPopulatedFixture()
    {
        var fixture = new RouterCartographyResponse(
            Nodes: new List<CartographyNodeRow>
            {
                new CartographyNodeRow(
                    Name: "Local",
                    Location: "local",
                    Enabled: true,
                    Liveness: "Online",
                    Stale: false,
                    ContextAllocation: "32k (expanded)",
                    InFlightRequests: 2,
                    TotalLoadedVramBytes: 31L * 1024 * 1024 * 1024,
                    LoadedModels: new List<CartographyNodeLoadedModel>
                    {
                        new CartographyNodeLoadedModel("llama3:latest", 31L * 1024 * 1024 * 1024, "Q4_K_M", 16384)
                    },
                    Url: LocalUrl,
                    LastRefreshedAtUtc: DateTimeOffset.UtcNow,
                    Hardware: FullHardware())
            },
            Models: new List<CartographyModelRow>
            {
                new CartographyModelRow(
                    Name: "llama3:latest",
                    Architecture: "Dense",
                    WeightSizeBytes: 5L * 1024 * 1024 * 1024,
                    NativeContextWindow: 131_072,
                    RecommendedContextWindow: 131_072,
                    CloudOverflowAvailable: true,
                    Deployments: new List<CartographyDeploymentRow>
                    {
                        new CartographyDeploymentRow(
                            Target: "Local",
                            Present: true,
                            Capable: true,
                            ContextCeiling: 131_072,
                            IsResident: true,
                            InFlightRequests: 2,
                            VetoReason: null,
                            LoadedSizeVramBytes: 31L * 1024 * 1024 * 1024,
                            Quantization: "Q4_K_M")
                    },
                    ParameterCount: 8_000_000_000,
                    Quantization: "Q4_K_M"),
                new CartographyModelRow(
                    Name: "cloud-model",
                    Architecture: "Unknown",
                    WeightSizeBytes: 0L,
                    NativeContextWindow: 0,
                    RecommendedContextWindow: 0,
                    CloudOverflowAvailable: true,
                    Deployments: new List<CartographyDeploymentRow>(),
                    ParameterCount: null,
                    Quantization: null)
            },
            LoadedModels: new List<CartographyLoadedModelRow>
            {
                new CartographyLoadedModelRow(
                    Name: "llama3:latest",
                    Target: "Local",
                    SizeVramBytes: 31L * 1024 * 1024 * 1024,
                    ContextAllocation: "32k (expanded)",
                    Liveness: "Online",
                    Stale: false,
                    Quantization: "Q4_K_M",
                    ContextLength: 16384)
            });

        var json = JsonSerializer.Serialize(fixture, OllamaRouterJsonSerializerContext.Default.RouterCartographyResponse);
        var roundTripped = JsonSerializer.Deserialize(json, OllamaRouterJsonSerializerContext.Default.RouterCartographyResponse)!;

        // Record value-equality cannot deep-compare the collection-valued positional
        // properties (reference identity), so the round-trip is proven by re-serializing the
        // deserialized object and asserting byte-stable JSON (nothing lost or transformed),
        // plus explicit field spot-checks on the null-omitted optionals.
        var json2 = JsonSerializer.Serialize(roundTripped, OllamaRouterJsonSerializerContext.Default.RouterCartographyResponse);
        Assert.Equal(json, json2);

        var node = roundTripped.Nodes.Single();
        Assert.Equal(LocalUrl, node.Url);
        Assert.NotNull(node.Hardware);
        Assert.Equal("NVIDIA", node.Hardware!.GpuVendor);
        Assert.Equal(31L * 1024 * 1024 * 1024, node.LoadedModels.Single().SizeVramBytes);
        var model = roundTripped.Models.Single(m => m.Name == "llama3:latest");
        Assert.Equal(8_000_000_000L, model.ParameterCount);
        Assert.Equal("Q4_K_M", model.Quantization);
        Assert.Equal(31L * 1024 * 1024 * 1024, model.Deployments.Single().LoadedSizeVramBytes);
        Assert.Equal(31L * 1024 * 1024 * 1024, roundTripped.LoadedModels.Single().SizeVramBytes);
    }

    [Fact]
    public void CartographyResponse_SerializationRoundTrip_FullyDegradedFixture()
    {
        // Every optional is null → all null-omitted in the JSON, and deserialization brings
        // them back as null (round-trip equality).
        var fixture = new RouterCartographyResponse(
            Nodes: new List<CartographyNodeRow>
            {
                new CartographyNodeRow(
                    Name: "Cloud",
                    Location: "cloud",
                    Enabled: true,
                    Liveness: "Online",
                    Stale: false,
                    ContextAllocation: "baseline",
                    InFlightRequests: 0,
                    TotalLoadedVramBytes: 0L,
                    LoadedModels: new List<CartographyNodeLoadedModel>(),
                    Url: null,
                    LastRefreshedAtUtc: null,
                    Hardware: null)
            },
            Models: new List<CartographyModelRow>
            {
                new CartographyModelRow(
                    Name: "mystery:latest",
                    Architecture: "Unknown",
                    WeightSizeBytes: 0L,
                    NativeContextWindow: 4096,
                    RecommendedContextWindow: 4096,
                    CloudOverflowAvailable: false,
                    Deployments: new List<CartographyDeploymentRow>
                    {
                        new CartographyDeploymentRow(
                            Target: "Local",
                            Present: false,
                            Capable: false,
                            ContextCeiling: 4096,
                            IsResident: false,
                            InFlightRequests: 0,
                            VetoReason: "model not present in target catalog",
                            LoadedSizeVramBytes: null,
                            Quantization: null)
                    },
                    ParameterCount: null,
                    Quantization: null)
            },
            LoadedModels: new List<CartographyLoadedModelRow>());

        var json = JsonSerializer.Serialize(fixture, OllamaRouterJsonSerializerContext.Default.RouterCartographyResponse);
        Assert.DoesNotContain("\"url\"", json);
        Assert.DoesNotContain("\"hardware\"", json);
        Assert.DoesNotContain("\"lastRefreshedAtUtc\"", json);
        Assert.DoesNotContain("\"parameterCount\"", json);
        Assert.DoesNotContain("\"loadedSizeVramBytes\"", json);
        Assert.DoesNotContain("freeVramMB", json);
        var roundTripped = JsonSerializer.Deserialize(json, OllamaRouterJsonSerializerContext.Default.RouterCartographyResponse)!;

        // Null-omitted optionals must deserialize back to null, and re-serialization must be
        // byte-stable (the omission is preserved).
        var node = roundTripped.Nodes.Single();
        Assert.Null(node.Url);
        Assert.Null(node.Hardware);
        Assert.Null(node.LastRefreshedAtUtc);
        var model = roundTripped.Models.Single();
        Assert.Null(model.ParameterCount);
        Assert.Null(model.Quantization);
        Assert.Equal("model not present in target catalog", model.Deployments.Single().VetoReason);
        Assert.Null(model.Deployments.Single().LoadedSizeVramBytes);

        var json2 = JsonSerializer.Serialize(roundTripped, OllamaRouterJsonSerializerContext.Default.RouterCartographyResponse);
        Assert.Equal(json, json2);
    }
}
