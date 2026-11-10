using OllamaRouter.Tests.TestHelpers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Endpoints;
using OllamaRouter.Options;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Preload;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Services.Preload;

/// <summary>
/// Selection + execution matrix for the router-owned model pre-load service (task #44): the
/// ordered safety gates (routable -> catalog presence -> discoverable capability -> hardware ->
/// context ceiling), the ranking keys (idle-first busy bypass, server-class preference for
/// large intents, least-busy, declaration order), the keep-alive class defaults, the fail-soft
/// load/confirmation, and the side-effect-free preview.
/// </summary>
public class ModelPreloadServiceTests
{
    private const string DesktopUrl = "http://127.0.0.1:11435";
    private const string ServerUrl = "http://192.168.1.10:11434";
    private const string LaptopUrl = "http://192.168.1.20:11435";
    private const string Model = "llama3:8b";
    private const long Dense8B = 8_000_000_000L;

    private sealed record Fleet(
        ModelPreloadService Sut,
        Mock<IOllamaModelCatalogClient> Catalog,
        OllamaRouterOptions Options);

    private static OllamaTargetOptions Target(string name, string url, int? vramMb) => new()
    {
        Name = name,
        Url = url,
        MaxVramMB = vramMb
    };

    private static JsonArray TagsBody(params (string Name, long Size)[] models)
    {
        var arr = new JsonArray();
        foreach (var (name, size) in models)
        {
            arr.Add(new JsonObject { ["name"] = name, ["model"] = name, ["size"] = size });
        }

        return arr;
    }

    private static JsonObject DenseShow(long parameterCount, int? numCtx = null)
    {
        var obj = new JsonObject
        {
            ["model_info"] = new JsonObject { ["general.parameter_count"] = parameterCount },
            ["details"] = new JsonObject { ["family"] = "llama" }
        };

        if (numCtx is { } value)
        {
            obj["modelfile"] = $"# Modelfile\nPARAMETER num_ctx {value}\n";
        }

        return obj;
    }

    private static TargetExecutionState State(RoutingTarget target, int inFlight, params string[] loaded) =>
        new(target, inFlight, loaded.Select(n => new TargetLoadedModel(n, 1_000_000)).ToList(), 0, DateTimeOffset.UtcNow, false);

    /// <summary>
    /// Builds the real service on top of a strict-mocked catalog client: the catalog presence
    /// path (GetTagsWithDigestAsync) and the capability path (GetShowAsync) are scripted per
    /// target, liveness/availability/tracker are scripted from dictionaries (all online/enabled
    /// by default), and the real ModelCatalogCacheService + ModelCapabilityEngine run in between.
    /// </summary>
    private static Fleet CreateFleet(
        OllamaTargetOptions[] targets,
        Dictionary<string, (JsonArray Tags, IReadOnlyDictionary<string, JsonObject> Shows)> catalogs,
        IReadOnlyDictionary<RoutingTarget, TargetLivenessState>? livenessStates = null,
        IReadOnlyDictionary<RoutingTarget, TargetExecutionState>? executionStates = null,
        bool cloudEnabled = false,
        string? cloudDefaultModel = null)
    {
        var options = new OllamaRouterOptions
        {
            Targets = targets,
            Models = new Dictionary<string, ModelThresholds>(),
            DynamicContext = new DynamicContextOptions { Enabled = true },
            CloudOverflow = { Enabled = cloudEnabled, DefaultModel = cloudDefaultModel }
        };

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

        var states = livenessStates ?? new Dictionary<RoutingTarget, TargetLivenessState>();
        var liveness = new Mock<ITargetLivenessService>();
        liveness
            .Setup(l => l.IsOnline(It.IsAny<RoutingTarget>()))
            .Returns<RoutingTarget>(t => states.TryGetValue(t, out var s) ? s == TargetLivenessState.Online : true);
        liveness
            .SetupGet(l => l.ServiceStartTimeUtc)
            .Returns(DateTimeOffset.UtcNow.AddMinutes(1)); // outside the 10 s startup grace window

        var availability = new Mock<ITargetAvailabilityService>();
        availability
            .Setup(a => a.IsEnabled(It.IsAny<RoutingTarget>()))
            .Returns(true);

        var tracker = new Mock<ITargetExecutionTracker>();
        tracker
            .Setup(t => t.GetState(It.IsAny<RoutingTarget>()))
            .Returns<RoutingTarget>(t => executionStates?.GetValueOrDefault(t));

        var catalogCache = new ModelCatalogCacheService(catalog.Object, new MemoryCache(new MemoryCacheOptions()));
        var engine = new ModelCapabilityEngine(
            catalog.Object,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ModelCapabilityEngine>.Instance,
            catalogCache);

        var sut = new ModelPreloadService(
            new TestOptionsMonitor<OllamaRouterOptions>(options),
            catalog.Object,
            catalogCache,
            engine,
            liveness.Object,
            availability.Object,
            tracker.Object,
            null,
            NullLogger<ModelPreloadService>.Instance);

        return new Fleet(sut, catalog, options);
    }

    /// <summary>Two-target fleet: Desktop (16 GB) declared first + Server (96 GB), both hosting the 8B model.</summary>
    private static Fleet DesktopThenServer(
        int? desktopNumCtx = null,
        int? serverNumCtx = null,
        IReadOnlyDictionary<RoutingTarget, TargetExecutionState>? executionStates = null,
        string model = Model,
        long parameters = Dense8B,
        IReadOnlyDictionary<RoutingTarget, TargetLivenessState>? livenessStates = null)
        {
        OllamaTargetOptions[] targets =
        [
            Target("Desktop", DesktopUrl, 16_384),
            Target("Server", ServerUrl, 98_304)
        ];
        var catalogs = new Dictionary<string, (JsonArray, IReadOnlyDictionary<string, JsonObject>)>
        {
            [DesktopUrl] = (TagsBody((model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [model] = DenseShow(parameters, desktopNumCtx) }),
            [ServerUrl] = (TagsBody((model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [model] = DenseShow(parameters, serverNumCtx) })
        };
        return CreateFleet(targets, catalogs, livenessStates, executionStates);
    }

    // ------------------------------------------------------------------
    //  Context-intent gate + server-class preference (AC5)
    // ------------------------------------------------------------------

    [Fact]
    public async Task LargeContextIntent_SelectsServerOverIdleDesktopWithSmallerCeiling()
    {
        // Desktop ceiling = 128k fallback < 260k + margin -> skipped; Server ceiling = 270k.
        var fleet = DesktopThenServer(serverNumCtx: 270_000);

        var result = await fleet.Sut.PreviewAsync(Model, null, 260_000, null);

        Assert.NotNull(result);
        Assert.Equal("Server", result!.SelectedTarget);
        Assert.Equal(260_000, result.ContextIntentTokens);
        var desktop = result.Candidates.First(c => c.Target == "Desktop");
        Assert.False(desktop.Eligible);
        Assert.Equal("context ceiling 131072 below intent 260000", desktop.SkipReason);
        var server = result.Candidates.First(c => c.Target == "Server");
        Assert.True(server.Eligible);
        Assert.True(server.ServerClass);
        Assert.False(desktop.ServerClass);
    }

    [Fact]
    public async Task LargeContextIntent_AllCeilingsEqual_ServerClassPreferredWhenBothIdle()
    {
        // Both idle, both 270k ceilings, Desktop declared first -> the large-intent
        // server-class key still wins.
        var fleet = DesktopThenServer(desktopNumCtx: 270_000, serverNumCtx: 270_000);

        var result = await fleet.Sut.PreviewAsync(Model, null, 260_000, null);

        Assert.Equal("Server", result!.SelectedTarget);
    }

    [Fact]
    public async Task NoExplicitIntent_UsesRecommendedWindowSoftFallback()
    {
        // Recommended = max ceiling (270k); the 128k Desktop then fails the soft gate
        // (upper-bound preference), the Server is selected with num_ctx = intent = 270k.
        var fleet = DesktopThenServer(serverNumCtx: 270_000);

        var result = await fleet.Sut.PreviewAsync(Model, null, null, null);

        Assert.Equal("Server", result!.SelectedTarget);
        Assert.Equal(270_000, result.ContextIntentTokens);
        Assert.Equal(270_000, result.AppliedNumCtx);
        Assert.False(result.Candidates.First(c => c.Target == "Desktop").Eligible);
    }

    // ------------------------------------------------------------------
    //  Ranking: idle-first busy bypass, least-busy, declaration order (AC6)
    // ------------------------------------------------------------------

    [Fact]
    public async Task BusyServer_IsBypassedInFavorOfIdleDesktop()
    {
        // Server declared FIRST and capable, but 2 requests in flight; Desktop idle.
        var fleet = CreateFleet(
            [Target("Server", ServerUrl, 98_304), Target("Desktop", DesktopUrl, 16_384)],
            new Dictionary<string, (JsonArray, IReadOnlyDictionary<string, JsonObject>)>
            {
                [ServerUrl] = (TagsBody((Model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [Model] = DenseShow(Dense8B) }),
                [DesktopUrl] = (TagsBody((Model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [Model] = DenseShow(Dense8B) })
            },
            executionStates: new Dictionary<RoutingTarget, TargetExecutionState>
            {
                [(RoutingTarget)"Server"] = State((RoutingTarget)"Server", inFlight: 2),
                [(RoutingTarget)"Desktop"] = State((RoutingTarget)"Desktop", inFlight: 0)
            });

        var result = await fleet.Sut.PreviewAsync(Model, null, 8192, null);

        Assert.Equal("Desktop", result!.SelectedTarget);
        var server = result.Candidates.First(c => c.Target == "Server");
        Assert.True(server.Eligible);
        Assert.True(server.Busy);
    }

    [Fact]
    public async Task BothBusy_SelectsLowestInFlight()
    {
        var fleet = CreateFleet(
            [Target("Server", ServerUrl, 98_304), Target("Desktop", DesktopUrl, 16_384)],
            new Dictionary<string, (JsonArray, IReadOnlyDictionary<string, JsonObject>)>
            {
                [ServerUrl] = (TagsBody((Model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [Model] = DenseShow(Dense8B) }),
                [DesktopUrl] = (TagsBody((Model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [Model] = DenseShow(Dense8B) })
            },
            executionStates: new Dictionary<RoutingTarget, TargetExecutionState>
            {
                [(RoutingTarget)"Server"] = State((RoutingTarget)"Server", inFlight: 1),
                [(RoutingTarget)"Desktop"] = State((RoutingTarget)"Desktop", inFlight: 3)
            });

        var result = await fleet.Sut.PreviewAsync(Model, null, 8192, null);

        // Small intent (no server-class key): least in-flight wins despite declaration order.
        Assert.Equal("Server", result!.SelectedTarget);
    }

    [Fact]
    public async Task SmallIntent_AllIdle_DeclarationOrderWins()
    {
        var fleet = DesktopThenServer();

        var result = await fleet.Sut.PreviewAsync(Model, null, 8192, null);

        Assert.Equal("Desktop", result!.SelectedTarget);
    }

    [Fact]
    public async Task Candidates_ListEveryTargetInDeclarationOrder()
    {
        var fleet = DesktopThenServer();

        var result = await fleet.Sut.PreviewAsync(Model, null, 8192, null);

        Assert.Equal(["Desktop", "Server"], result!.Candidates.Select(c => c.Target).ToList());
    }

    // ------------------------------------------------------------------
    //  Ordered safety gates: routable -> present -> discoverable -> hardware (AC4)
    // ------------------------------------------------------------------

    [Fact]
    public async Task OfflineTarget_IsSkippedWithRoutableReason()
    {
        OllamaTargetOptions[] targets = [Target("Desktop", DesktopUrl, 16_384), Target("Laptop", LaptopUrl, 8_192)];
        var catalogs = new Dictionary<string, (JsonArray, IReadOnlyDictionary<string, JsonObject>)>
        {
            [DesktopUrl] = (TagsBody((Model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [Model] = DenseShow(Dense8B) }),
            [LaptopUrl] = (TagsBody((Model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [Model] = DenseShow(Dense8B) })
        };
        var fleet = CreateFleet(
            targets,
            catalogs,
            livenessStates: new Dictionary<RoutingTarget, TargetLivenessState>
            {
                [(RoutingTarget)"Desktop"] = TargetLivenessState.Online,
                [(RoutingTarget)"Laptop"] = TargetLivenessState.Offline
            });

        var result = await fleet.Sut.PreviewAsync(Model, null, 8192, null);

        Assert.Equal("Desktop", result!.SelectedTarget);
        Assert.Equal("not routable (disabled/offline)", result.Candidates.First(c => c.Target == "Laptop").SkipReason);
    }

    [Fact]
    public async Task ModelAbsentFromTargetCatalog_IsSkipped()
    {
        OllamaTargetOptions[] targets = [Target("Desktop", DesktopUrl, 16_384), Target("Laptop", LaptopUrl, 8_192)];
        var catalogs = new Dictionary<string, (JsonArray, IReadOnlyDictionary<string, JsonObject>)>
        {
            [DesktopUrl] = (TagsBody((Model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [Model] = DenseShow(Dense8B) }),
            // Laptop hosts a DIFFERENT model only.
            [LaptopUrl] = (TagsBody(("other:1b", 1_000_000_000L)), new Dictionary<string, JsonObject>())
        };
        var fleet = CreateFleet(targets, catalogs);

        var result = await fleet.Sut.PreviewAsync(Model, null, 8192, null);

        Assert.Equal("Desktop", result!.SelectedTarget);
        Assert.Equal("model not present in target catalog", result.Candidates.First(c => c.Target == "Laptop").SkipReason);
    }

    [Fact]
    public async Task UndiscoverableCapability_IsSkipped()
    {
        // The Laptop catalog lists the model (presence gate passes) but reports an EMPTY
        // digest (no digest-keyed metadata to serve) -> the discoverability gate skips it.
        // Moq: the latest setup wins, so the digest override below replaces the default one.
        OllamaTargetOptions[] targets = [Target("Desktop", DesktopUrl, 16_384), Target("Laptop", LaptopUrl, 8_192)];
        var catalogs = new Dictionary<string, (JsonArray, IReadOnlyDictionary<string, JsonObject>)>
        {
            [DesktopUrl] = (TagsBody((Model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [Model] = DenseShow(Dense8B) }),
            [LaptopUrl] = (TagsBody((Model, 4_000_000_000L)), new Dictionary<string, JsonObject>())
        };
        var fleet = CreateFleet(targets, catalogs);
        fleet.Catalog
            .Setup(c => c.GetTagsWithDigestAsync(LaptopUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TagsBody((Model, 4_000_000_000L)), string.Empty));

        var result = await fleet.Sut.PreviewAsync(Model, null, 8192, null);

        Assert.Equal("Desktop", result!.SelectedTarget);
        Assert.Equal("capability undiscoverable", result.Candidates.First(c => c.Target == "Laptop").SkipReason);
    }

    [Fact]
    public async Task LargeDenseOnSmallTarget_IsVetoedWithExactReason()
    {
        const string LargeModel = "qwen2.5-coder:32b";
        OllamaTargetOptions[] targets = [Target("Laptop", LaptopUrl, 8_192), Target("Desktop", DesktopUrl, 16_384)];
        var catalogs = new Dictionary<string, (JsonArray, IReadOnlyDictionary<string, JsonObject>)>
        {
            [LaptopUrl] = (TagsBody((LargeModel, 20_000_000_000L)), new Dictionary<string, JsonObject> { [LargeModel] = DenseShow(32_000_000_000L) }),
            [DesktopUrl] = (TagsBody((LargeModel, 20_000_000_000L)), new Dictionary<string, JsonObject> { [LargeModel] = DenseShow(32_000_000_000L) })
        };
        var fleet = CreateFleet(targets, catalogs);

        var result = await fleet.Sut.PreviewAsync(LargeModel, null, 8192, null);

        // 16 GB Desktop is above the 12 GB small-target line -> capable; 8 GB Laptop vetoed.
        Assert.Equal("Desktop", result!.SelectedTarget);
        Assert.Equal(ModelCapabilityEngine.LargeDenseVetoReason, result.Candidates.First(c => c.Target == "Laptop").SkipReason);
    }

    [Fact]
    public async Task NoEligibleTarget_ReturnsNullSelectionWithSkipReasons()
    {
        var fleet = DesktopThenServer(); // both ceilings 128k fallback

        var result = await fleet.Sut.PreviewAsync(Model, null, 260_000, null);

        Assert.NotNull(result);
        Assert.Null(result!.SelectedTarget);
        Assert.Null(result.Error);
        Assert.Contains("no eligible target", result.Reason);
        Assert.All(result.Candidates, c => Assert.False(c.Eligible));
        Assert.All(result.Candidates, c => Assert.Equal("context ceiling 131072 below intent 260000", c.SkipReason));
    }

    [Fact]
    public async Task CloudSynthesisOnlyModel_ResolvesButNoFleetTargetHostsIt()
    {
        // Cloud overflow names resolve in the fleet listing, but the Cloud pseudo-target is
        // never a preload candidate -> 503 shape (no Error, no SelectedTarget, skip reasons).
        var fleet = CreateFleet(
            [Target("Desktop", DesktopUrl, 16_384)],
            new Dictionary<string, (JsonArray, IReadOnlyDictionary<string, JsonObject>)>
            {
                [DesktopUrl] = (TagsBody((Model, 4_000_000_000L)), new Dictionary<string, JsonObject> { [Model] = DenseShow(Dense8B) })
            },
            cloudEnabled: true,
            cloudDefaultModel: "cloud-only:1b");

        var result = await fleet.Sut.PreviewAsync("cloud-only:1b", null, 8192, null);

        Assert.NotNull(result);
        Assert.Null(result!.Error);
        Assert.Null(result.SelectedTarget);
        Assert.Equal("model not present in target catalog", result.Candidates.Single().SkipReason);
    }

    // ------------------------------------------------------------------
    //  Pins + 404 shapes (AC3)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PinnedTarget_IsHonoredCaseInsensitively_OverSelection()
    {
        // Both ceilings equal at 270k + large intent would pick the Server; the pin wins.
        var fleet = DesktopThenServer(desktopNumCtx: 270_000, serverNumCtx: 270_000);

        var result = await fleet.Sut.PreviewAsync(Model, null, 260_000, "desktop");

        Assert.Equal("Desktop", result!.SelectedTarget);
        // Pinning restricts the evaluation to the pinned target (single candidate row).
        Assert.Single(result.Candidates);
        Assert.Equal("Desktop", result.Candidates.Single().Target);
    }

    [Fact]
    public async Task PinnedUnknownTarget_ReturnsTargetNotFound()
    {
        var fleet = DesktopThenServer();

        var result = await fleet.Sut.PreviewAsync(Model, null, 8192, "bogus");

        Assert.Equal("target not found: bogus", result!.Error);
        Assert.Null(result.SelectedTarget);
    }

    [Fact]
    public async Task UnknownModel_ReturnsModelNotFound()
    {
        var fleet = DesktopThenServer();

        var result = await fleet.Sut.PreviewAsync("nonexistent:9b", null, 8192, null);

        Assert.Equal("model not found", result!.Error);
        Assert.Null(result.SelectedTarget);
        Assert.Empty(result.Candidates);
    }

    // ------------------------------------------------------------------
    //  Keep-alive defaults + execution (AC9, AC10)
    // ------------------------------------------------------------------

    [Fact]
    public async Task NoExplicitKeepAlive_DesktopClassResolvesTo15Minutes()
    {
        var fleet = DesktopThenServer();

        var result = await fleet.Sut.PreviewAsync(Model, null, 8192, null);

        Assert.Equal("Desktop", result!.SelectedTarget);
        Assert.Equal(900, result.AppliedKeepAliveSeconds);
    }

    [Fact]
    public async Task NoExplicitKeepAlive_ServerClassResolvesToIndefinite()
    {
        var fleet = DesktopThenServer(serverNumCtx: 270_000);

        var result = await fleet.Sut.PreviewAsync(Model, null, 260_000, null);

        Assert.Equal("Server", result!.SelectedTarget);
        Assert.Equal(-1, result.AppliedKeepAliveSeconds);
    }

    [Fact]
    public async Task ExplicitKeepAlive_WinsVerbatimOverClassDefault()
    {
        var fleet = DesktopThenServer(serverNumCtx: 270_000);

        var result = await fleet.Sut.PreviewAsync(Model, 300, 260_000, null);

        Assert.Equal("Server", result!.SelectedTarget);
        Assert.Equal(300, result.AppliedKeepAliveSeconds);
    }

    [Fact]
    public async Task PreloadAsync_IssuesLoadAndConfirmsResidency()
    {
        var fleet = DesktopThenServer();
        int? calledKeepAlive = null;
        int? calledNumCtx = null;
        string? calledUrl = null;
        string? calledModel = null;
        fleet.Catalog
            .Setup(c => c.PreloadModelAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<string?, string, int, int?, CancellationToken>((url, model, keepAlive, numCtx, _) =>
            {
                calledUrl = url;
                calledModel = model;
                calledKeepAlive = keepAlive;
                calledNumCtx = numCtx;
            })
            .ReturnsAsync(new PreloadOutcome(true));
        fleet.Catalog
            .Setup(c => c.GetRunningModelNamesAsync(DesktopUrl, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { Model }));

        var result = await fleet.Sut.PreloadAsync(Model, null, 8192, null);

        Assert.Equal("Desktop", result!.SelectedTarget);
        Assert.True(result.LoadAccepted);
        Assert.True(result.ResidencyConfirmed);
        Assert.NotNull(result.PreloadDurationMs);
        Assert.Equal(DesktopUrl, calledUrl);
        Assert.Equal(Model, calledModel);
        Assert.Equal(900, calledKeepAlive); // desktop class default
        Assert.Equal(8192, calledNumCtx); // explicit intent, applied as min(intent, selected ceiling)
    }

    [Fact]
    public async Task PreloadAsync_LoadRejectedAndResidencyAbsent_IsFailSoft()
    {
        var fleet = DesktopThenServer();
        fleet.Catalog
            .Setup(c => c.PreloadModelAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreloadOutcome(false, "http 503"));
        fleet.Catalog
            .Setup(c => c.GetRunningModelNamesAsync(DesktopUrl, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<string>>(new string[0]));

        var result = await fleet.Sut.PreloadAsync(Model, null, 8192, null);

        Assert.Equal("Desktop", result!.SelectedTarget);
        Assert.False(result.LoadAccepted);
        Assert.False(result.ResidencyConfirmed);
        Assert.NotNull(result.Reason);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task PreviewAsync_MakesZeroLoadCalls()
    {
        // The strict catalog mock has NO PreloadModelAsync / GetRunningModelNamesAsync setup:
        // any call on the preview path would throw and fail this test.
        var fleet = DesktopThenServer();

        var result = await fleet.Sut.PreviewAsync(Model, null, 8192, null);

        Assert.Equal("Desktop", result!.SelectedTarget);
        Assert.Null(result.LoadAccepted);
        Assert.Null(result.ResidencyConfirmed);
        Assert.Null(result.PreloadDurationMs);
    }

    [Fact]
    public async Task ResidentTarget_IsReportedInCandidateEvaluation()
    {
        var fleet = DesktopThenServer(
            executionStates: new Dictionary<RoutingTarget, TargetExecutionState>
            {
                [(RoutingTarget)"Desktop"] = State((RoutingTarget)"Desktop", inFlight: 0, Model),
                [(RoutingTarget)"Server"] = State((RoutingTarget)"Server", inFlight: 0)
            });

        var result = await fleet.Sut.PreviewAsync(Model, null, 8192, null);

        Assert.True(result!.Candidates.First(c => c.Target == "Desktop").IsResident);
        Assert.False(result.Candidates.First(c => c.Target == "Server").IsResident);
    }
}
