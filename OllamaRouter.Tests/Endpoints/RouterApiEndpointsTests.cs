using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Endpoints;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Tests.Endpoints;

public class RouterApiEndpointsTests
{
    private static readonly IReadOnlyDictionary<RoutingTarget, TargetLivenessEntry> NoLiveness =
        new Dictionary<RoutingTarget, TargetLivenessEntry>();

    private static readonly IReadOnlyDictionary<RoutingTarget, IReadOnlyList<ContextLeaseView>> NoLeases =
        new Dictionary<RoutingTarget, IReadOnlyList<ContextLeaseView>>();

    private sealed class Harness : IAsyncDisposable
    {
        public WebApplication App { get; }
        public HttpClient Client { get; }
        public Mock<IOptions<OllamaRouterOptions>> Options { get; }
        public Mock<ITargetAvailabilityService> Availability { get; }
        public Mock<ITargetLivenessService>? Liveness { get; }
        public Mock<ITargetExecutionTracker>? Tracker { get; }
        public Mock<IContextAutoShrinkManager>? ShrinkManager { get; }
        public Mock<IGpuVramProvider>? GpuVramProvider { get; }
        public Mock<IModelCatalogCacheService>? CatalogCache { get; }

        public Harness(
            WebApplication app,
            HttpClient client,
            Mock<IOptions<OllamaRouterOptions>> options,
            Mock<ITargetAvailabilityService> availability,
            Mock<ITargetLivenessService>? liveness,
            Mock<ITargetExecutionTracker>? tracker,
            Mock<IContextAutoShrinkManager>? shrinkManager,
            Mock<IGpuVramProvider>? gpuVramProvider,
            Mock<IModelCatalogCacheService>? catalogCache)
        {
            App = app;
            Client = client;
            Options = options;
            Availability = availability;
            Liveness = liveness;
            Tracker = tracker;
            ShrinkManager = shrinkManager;
            GpuVramProvider = gpuVramProvider;
            CatalogCache = catalogCache;
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static async Task<Harness> CreateTestAppAsync(
        OllamaRouterOptions? options = null,
        IReadOnlyDictionary<RoutingTarget, bool>? availabilityFlags = null,
        IReadOnlyDictionary<RoutingTarget, TargetLivenessEntry>? livenessEntries = null,
        IReadOnlyDictionary<RoutingTarget, TargetExecutionState>? executionStates = null,
        IReadOnlyDictionary<RoutingTarget, IReadOnlyList<ContextLeaseView>>? leaseSnapshot = null,
        LocalGpuSnapshot? localGpuSnapshot = null,
        bool registerLiveness = true,
        bool registerTracker = true,
        bool registerShrinkManager = true,
        bool registerGpuVramProvider = true,
        bool registerCatalogCache = false,
        IFleetHardwareService? fleetHardware = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        var optionsMock = new Mock<IOptions<OllamaRouterOptions>>();
        optionsMock.Setup(o => o.Value).Returns(options ?? new OllamaRouterOptions());

        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.GetSnapshot())
            .Returns(new TargetAvailabilitySnapshot(availabilityFlags
                ?? new Dictionary<RoutingTarget, bool>
                {
                    [RoutingTarget.Local] = true,
                    [RoutingTarget.Remote] = true,
                    [RoutingTarget.Cloud] = true
                }));

        var liveness = livenessEntries is null
            ? null
            : new Mock<ITargetLivenessService>();
        if (liveness is not null)
        {
            liveness.Setup(l => l.GetEntries()).Returns(livenessEntries);
        }

        var tracker = executionStates is null
            ? null
            : new Mock<ITargetExecutionTracker>();
        if (tracker is not null)
        {
            tracker
                .Setup(t => t.GetState(It.IsAny<RoutingTarget>()))
                .Returns<RoutingTarget>(t => executionStates.GetValueOrDefault(t));
        }

        var shrinkManager = new Mock<IContextAutoShrinkManager>();
        shrinkManager.Setup(s => s.GetLeaseSnapshot()).Returns(leaseSnapshot ?? NoLeases);

        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider
            .Setup(g => g.GetLocalGpuSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(localGpuSnapshot ?? LocalGpuSnapshot.Unknown);

        var catalogCache = new Mock<IModelCatalogCacheService>();
        catalogCache
            .Setup(c => c.HasAnyModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        builder.Services.AddSingleton(optionsMock.Object);
        builder.Services.AddSingleton(availability.Object);
        if (registerLiveness && liveness is not null)
        {
            builder.Services.AddSingleton(liveness.Object);
        }
        if (registerTracker && tracker is not null)
        {
            builder.Services.AddSingleton(tracker.Object);
        }
        if (registerShrinkManager)
        {
            builder.Services.AddSingleton(shrinkManager.Object);
        }
        if (registerGpuVramProvider)
        {
            builder.Services.AddSingleton(gpuVramProvider.Object);
        }
        if (registerCatalogCache)
        {
            builder.Services.AddSingleton(catalogCache.Object);
        }

        if (fleetHardware is not null)
        {
            builder.Services.AddSingleton(fleetHardware);
        }
        else if (localGpuSnapshot is not null && localGpuSnapshot != LocalGpuSnapshot.Unknown)
        {
            var mockFleet = new Mock<IFleetHardwareService>();
            var gpus = localGpuSnapshot.Devices.Select(d =>
            {
                var open = d.IndexOf('(');
                var name = open > 0 ? d[..open].Trim() : d.Trim();
                return new GpuDeviceProfile(localGpuSnapshot.Vendor, name, localGpuSnapshot.TotalVramMB, "GDDR6X", 256, 716.8);
            }).ToList();
            var profile = new NodeHardwareProfile(
                "workstation",
                "windows",
                "x64",
                false,
                65536,
                SystemRamType.Ddr5,
                gpus,
                localGpuSnapshot.TotalVramMB,
                716.8);
            mockFleet.Setup(f => f.GetProfile("Local")).Returns(profile);
            mockFleet.Setup(f => f.GetEffectiveMaxVramMB(It.Is<OllamaTargetOptions>(t => string.Equals(t.Name, "Local", StringComparison.OrdinalIgnoreCase))))
                .Returns(localGpuSnapshot.TotalVramMB);
            builder.Services.AddSingleton(mockFleet.Object);
        }

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var harness = new Harness(
            app,
            client,
            optionsMock,
            availability,
            registerLiveness ? liveness : null,
            registerTracker ? tracker : null,
            registerShrinkManager ? shrinkManager : null,
            registerGpuVramProvider ? gpuVramProvider : null,
            registerCatalogCache ? catalogCache : null);
        return harness;
    }

    private static OllamaRouterOptions ThreeTargetOptions(bool dynamicContextEnabled = false, int maxConsecutiveFailures = 0) =>
        new()
        {
            Targets =
            [
                new OllamaTargetOptions
                {
                    Name = "Local",
                    Url = "http://127.0.0.1:11435",
                    MaxVramMB = 16384,
                    MaxParallel = 1
                },
                new OllamaTargetOptions
                {
                    Name = "Server",
                    Url = "http://192.168.1.100:11434",
                    MaxVramMB = 98304,
                    MaxParallel = 4
                },
                new OllamaTargetOptions
                {
                    Name = "Laptop",
                    Url = "http://192.168.1.105:11435",
                    MaxVramMB = 8192,
                    MaxParallel = 1
                }
            ],
            DynamicContext =
            {
                Enabled = dynamicContextEnabled
            },
            MaxConsecutiveFailures = maxConsecutiveFailures,
            CloudOverflow =
            {
                Enabled = true
            }
        };

    private static TargetLivenessEntry Entry(RoutingTarget target, TargetLivenessState state, long? latencyMs = null) =>
        new(target, state, state == TargetLivenessState.Offline ? "connection error" : null, DateTimeOffset.UtcNow, latencyMs);

    private static JsonNode? TargetItem(JsonNode root, string name) =>
        root["targets"]!.AsArray().First(node => node!["name"]!.ToString() == name)!;

    // ------------------------------------------------------------------
    //  GET /api/router/info
    // ------------------------------------------------------------------

    [Fact]
    public async Task RouterInfo_Returns200_WithExpectedSchema_AndCountsTargetsIncludingCloud()
    {
        var options = ThreeTargetOptions();
        await using var harness = await CreateTestAppAsync(
            options,
            livenessEntries: new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [RoutingTarget.Local] = Entry(RoutingTarget.Local, TargetLivenessState.Online, 1),
                [(RoutingTarget)"Server"] = Entry((RoutingTarget)"Server", TargetLivenessState.Online, 4),
                [(RoutingTarget)"Laptop"] = Entry((RoutingTarget)"Laptop", TargetLivenessState.Offline)
            });

        using var response = await harness.Client.GetAsync("/api/router/info");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotNull(node);
        Assert.Equal("true", node!["isRouter"]!.ToString());
        Assert.False(string.IsNullOrEmpty(node["version"]!.ToString()));
        Assert.Equal("4", node["fleet"]!["totalTargets"]!.ToString()); // 3 configured + Cloud
        Assert.Equal("3", node["fleet"]!["onlineTargets"]!.ToString()); // Local + Server + Cloud (Laptop offline)
        Assert.Equal("true", node["fleet"]!["cloudEnabled"]!.ToString());
        Assert.Equal("false", node["features"]!["dynamicContext"]!.ToString());
        Assert.Equal("false", node["features"]!["autoShrink"]!.ToString());
        Assert.Equal("false", node["features"]!["failover"]!.ToString());
        Assert.Equal("true", node["features"]!["antiThrashingAffinity"]!.ToString());
    }

    [Fact]
    public async Task RouterInfo_FeatureFlags_MirrorApprovedOptionMapping()
    {
        var options = ThreeTargetOptions(dynamicContextEnabled: true, maxConsecutiveFailures: 3);
        options.CloudOverflow.Enabled = false;
        await using var harness = await CreateTestAppAsync(
            options,
            livenessEntries: new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [RoutingTarget.Local] = Entry(RoutingTarget.Local, TargetLivenessState.Online)
            });

        using var response = await harness.Client.GetAsync("/api/router/info");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("true", node!["features"]!["dynamicContext"]!.ToString());
        Assert.Equal("true", node["features"]!["autoShrink"]!.ToString());
        Assert.Equal("true", node["features"]!["failover"]!.ToString());
        Assert.Equal("true", node["features"]!["antiThrashingAffinity"]!.ToString());
        Assert.Equal("false", node["fleet"]!["cloudEnabled"]!.ToString());
        Assert.Equal("1", node["fleet"]!["onlineTargets"]!.ToString()); // Local only (cloud disabled)
        Assert.Equal("4", node["fleet"]!["totalTargets"]!.ToString());
    }

    [Fact]
    public async Task RouterInfo_NoLivenessService_UsesCatalogFallback_ForOnlineCount()
    {
        var options = new OllamaRouterOptions
        {
            LocalUrl = "http://127.0.0.1:11435",
            CloudOverflow = { Enabled = true }
        };
        await using var harness = await CreateTestAppAsync(
            options,
            registerLiveness: false,
            registerTracker: false,
            registerShrinkManager: false,
            registerGpuVramProvider: false,
            registerCatalogCache: true);
        harness.CatalogCache!
            .Setup(c => c.HasAnyModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var response = await harness.Client.GetAsync("/api/router/info");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        // 1 synthesized "Local" target (catalog says it has models) + Cloud (enabled).
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2", node!["fleet"]!["totalTargets"]!.ToString());
        Assert.Equal("2", node["fleet"]!["onlineTargets"]!.ToString());
        Assert.Equal("true", node["fleet"]!["cloudEnabled"]!.ToString());
    }

    [Fact]
    public async Task RouterInfo_PerformsNoLivenessOrCatalogIO_WithRegisteredLivenessService()
    {
        var options = ThreeTargetOptions();
        await using var harness = await CreateTestAppAsync(
            options,
            livenessEntries: new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [RoutingTarget.Local] = Entry(RoutingTarget.Local, TargetLivenessState.Online)
            });

        using var response = await harness.Client.GetAsync("/api/router/info");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var _infoBody = await response.Content.ReadAsStringAsync();

        // Pure in-memory reads: only the synchronous snapshot accessors may be touched.
        harness.Liveness!.Verify(l => l.GetEntries(), Times.Once);
        harness.Liveness.Verify(l => l.GetSnapshot(), Times.Never);
        harness.Liveness.Verify(l => l.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Liveness.Verify(l => l.IsOnline(It.IsAny<RoutingTarget>()), Times.Never);
        harness.Liveness.Verify(l => l.MarkHardFailureAsync(It.IsAny<RoutingTarget>()), Times.Never);
    }

    // ------------------------------------------------------------------
    //  GET /api/router/fleet
    // ------------------------------------------------------------------

    [Fact]
    public async Task RouterFleet_ReturnsFullTopology_InConfigOrder_WithCloudLast()
    {
        var options = ThreeTargetOptions();
        await using var harness = await CreateTestAppAsync(
            options,
            availabilityFlags: new Dictionary<RoutingTarget, bool>
            {
                [RoutingTarget.Local] = true,
                [(RoutingTarget)"Server"] = false,
                [(RoutingTarget)"Laptop"] = true,
                [RoutingTarget.Cloud] = true
            },
            livenessEntries: new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [RoutingTarget.Local] = Entry(RoutingTarget.Local, TargetLivenessState.Online, 1),
                [(RoutingTarget)"Server"] = Entry((RoutingTarget)"Server", TargetLivenessState.Online, 4),
                [(RoutingTarget)"Laptop"] = Entry((RoutingTarget)"Laptop", TargetLivenessState.Offline)
            },
            executionStates: new Dictionary<RoutingTarget, TargetExecutionState>
            {
                [RoutingTarget.Local] = new(
                    RoutingTarget.Local,
                    2,
                    [new TargetLoadedModel("qwen2.5-coder:32b", 30_000_000_000, 32768)],
                    30_000_000_000,
                    DateTimeOffset.UtcNow,
                    false),
                [(RoutingTarget)"Server"] = new(
                    (RoutingTarget)"Server",
                    0,
                    [],
                    0,
                    DateTimeOffset.UtcNow,
                    true)
            },
            leaseSnapshot: new Dictionary<RoutingTarget, IReadOnlyList<ContextLeaseView>>
            {
                [RoutingTarget.Local] = [new ContextLeaseView("qwen2.5-coder:32b", 65536, IsExpanded: true)],
                [(RoutingTarget)"Server"] = [new ContextLeaseView("llama3:8b", 32800, IsExpanded: false)]
            },
            localGpuSnapshot: new LocalGpuSnapshot("Nvidia", ["NVIDIA GeForce RTX 4080 (16384 MB)"], 16384));

        using var response = await harness.Client.GetAsync("/api/router/fleet");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var targets = node!["targets"]!.AsArray();
        Assert.Equal(4, targets.Count);

        // Config order + Cloud last.
        Assert.Equal("Local", targets[0]!["name"]!.ToString());
        Assert.Equal("Server", targets[1]!["name"]!.ToString());
        Assert.Equal("Laptop", targets[2]!["name"]!.ToString());
        Assert.Equal("Cloud", targets[3]!["name"]!.ToString());

        // Local: online, 1 ms latency, local GPU attached, 2 in flight, expanded lease.
        var local = TargetItem(node, "Local");
        Assert.Equal("http://127.0.0.1:11435", local["url"]!.ToString());
        Assert.Equal("true", local["enabled"]!.ToString());
        Assert.Equal("Online", local["liveness"]!.ToString());
        Assert.Equal("1", local["latencyMs"]!.ToString());
        Assert.Equal("Nvidia", local["hardware"]!["gpuVendor"]!.ToString());
        Assert.Equal("NVIDIA GeForce RTX 4080 (16384 MB)", local["hardware"]!["devices"]!.AsArray()[0]!.ToString());
        Assert.Equal("16384", local["hardware"]!["maxVramMB"]!.ToString());
        Assert.Equal("1", local["hardware"]!["maxParallel"]!.ToString());
        Assert.Equal("2", local["execution"]!["inFlightRequests"]!.ToString());
        Assert.Equal("qwen2.5-coder:32b", local["execution"]!["activeModel"]!.ToString());
        Assert.Equal("65.5k (expanded)", local["execution"]!["contextAllocation"]!.ToString());

        // Server: online, 4 ms latency, disabled via availability, no model, baseline lease.
        var server = TargetItem(node, "Server");
        Assert.Equal("http://192.168.1.100:11434", server["url"]!.ToString());
        Assert.Equal("false", server["enabled"]!.ToString());
        Assert.Equal("Online", server["liveness"]!.ToString());
        Assert.Equal("4", server["latencyMs"]!.ToString());
        Assert.Equal("Unknown", server["hardware"]!["gpuVendor"]!.ToString());
        Assert.Empty(server["hardware"]!["devices"]!.AsArray());
        Assert.Equal("98304", server["hardware"]!["maxVramMB"]!.ToString());
        Assert.Equal("4", server["hardware"]!["maxParallel"]!.ToString());
        Assert.Equal("0", server["execution"]!["inFlightRequests"]!.ToString());
        Assert.Null(server["execution"]!["activeModel"]);
        Assert.Equal("32.8k", server["execution"]!["contextAllocation"]!.ToString());

        // Laptop: offline → null latency; no lease → "baseline".
        var laptop = TargetItem(node, "Laptop");
        Assert.Equal("Offline", laptop["liveness"]!.ToString());
        Assert.Null(laptop["latencyMs"]);
        Assert.Equal("8192", laptop["hardware"]!["maxVramMB"]!.ToString());
        Assert.Equal("baseline", laptop["execution"]!["contextAllocation"]!.ToString());
        Assert.Null(laptop["execution"]!["activeModel"]);

        // Cloud pseudo-target.
        var cloud = TargetItem(node, "Cloud");
        Assert.Null(cloud["url"]);
        Assert.Equal("true", cloud["enabled"]!.ToString());
        Assert.Equal("Online", cloud["liveness"]!.ToString());
        Assert.Null(cloud["latencyMs"]);
        Assert.Equal("Cloud", cloud["hardware"]!["gpuVendor"]!.ToString());
        Assert.Equal("Cloud Overflow", cloud["hardware"]!["devices"]!.AsArray()[0]!.ToString());
        Assert.Null(cloud["hardware"]!["maxVramMB"]);
        Assert.Equal("10", cloud["hardware"]!["maxParallel"]!.ToString());
        Assert.Equal("0", cloud["execution"]!["inFlightRequests"]!.ToString());
        Assert.Null(cloud["execution"]!["activeModel"]);
        Assert.Equal("baseline", cloud["execution"]!["contextAllocation"]!.ToString());
    }

    [Fact]
    public async Task RouterFleet_SpeedFactor_RoundTrips_HintedValue_NullUnset_NullCloud()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", MaxVramMB = 16384, SpeedFactor = 2.0 },
                new OllamaTargetOptions { Name = "Server", Url = "http://192.168.1.100:11434", MaxVramMB = 98304 }
            ]
        };
        await using var harness = await CreateTestAppAsync(options);

        using var response = await harness.Client.GetAsync("/api/router/fleet");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        // Hinted target: speedFactor round-trips as 2.0.
        var local = TargetItem(node, "Local");
        Assert.Equal(2.0, local["hardware"]!["speedFactor"]!.GetValue<double>());

        // Unset target: effective factor 1.0 ⇒ always-present null (maxVramMB-style semantics).
        // (JsonNode's indexer returns null for a null-valued property, so check presence via the object.)
        var serverHardware = TargetItem(node, "Server")["hardware"]!.AsObject();
        Assert.True(serverHardware.ContainsKey("speedFactor"));
        Assert.Null(serverHardware["speedFactor"]);

        // Cloud pseudo-target: never carries a speed factor.
        var cloudHardware = TargetItem(node, "Cloud")["hardware"]!.AsObject();
        Assert.True(cloudHardware.ContainsKey("speedFactor"));
        Assert.Null(cloudHardware["speedFactor"]);
        }

    [Fact]
    public async Task RouterFleet_UnknownLivenessState_ReportsProbing_WithNullLatency()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" }
            ]
        };
        await using var harness = await CreateTestAppAsync(
            options,
            livenessEntries: new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [RoutingTarget.Local] = Entry(RoutingTarget.Local, TargetLivenessState.Unknown)
            });

        using var response = await harness.Client.GetAsync("/api/router/fleet");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var local = TargetItem(node, "Local");

        Assert.Equal("Probing", local["liveness"]!.ToString());
        Assert.Null(local["latencyMs"]);
        Assert.Equal("baseline", local["execution"]!["contextAllocation"]!.ToString());
        Assert.Equal("0", local["execution"]!["inFlightRequests"]!.ToString());
    }

    [Fact]
    public async Task RouterFleet_ReturnsCachedHardwareFromFleetHardware_ForAllTargets()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                // Named "Local" (host not loopback): still the local workstation by name.
                new OllamaTargetOptions { Name = "Local", Url = "http://10.0.0.5:11435" },
                // Second loopback-named target without profile.
                new OllamaTargetOptions { Name = "Workbench", Url = "http://127.0.0.1:11436" },
                new OllamaTargetOptions { Name = "Server", Url = "http://192.168.1.100:11434" }
            ]
        };
        var mockFleet = new Mock<IFleetHardwareService>();
        var localProfile = new NodeHardwareProfile(
            "workstation", "windows", "x64", false, 65536, SystemRamType.Ddr5,
            [new GpuDeviceProfile("Nvidia", "GeForce RTX 4080", 16384, "GDDR6X", 256, 716.8)], 16384, 716.8);
        var serverProfile = new NodeHardwareProfile(
            "server", "linux", "x64", false, 131072, SystemRamType.Ddr5,
            [new GpuDeviceProfile("Nvidia", "RTX 4090", 24576, "GDDR6X", 384, 1008.0)], 98304, 1008.0);
        mockFleet.Setup(f => f.GetProfile("Local")).Returns(localProfile);
        mockFleet.Setup(f => f.GetProfile("Server")).Returns(serverProfile);
        mockFleet.Setup(f => f.GetProfile("Workbench")).Returns((NodeHardwareProfile?)null);

        await using var harness = await CreateTestAppAsync(
            options,
            livenessEntries: new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [RoutingTarget.Local] = Entry(RoutingTarget.Local, TargetLivenessState.Online),
                [(RoutingTarget)"Workbench"] = Entry((RoutingTarget)"Workbench", TargetLivenessState.Online),
                [(RoutingTarget)"Server"] = Entry((RoutingTarget)"Server", TargetLivenessState.Online)
            },
            fleetHardware: mockFleet.Object);

        using var response = await harness.Client.GetAsync("/api/router/fleet");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        var local = TargetItem(node, "Local");
        Assert.Equal("Nvidia", local["hardware"]!["gpuVendor"]!.ToString());
        Assert.Equal("GeForce RTX 4080", local["hardware"]!["gpuName"]!.ToString());
        Assert.Equal(1, local["hardware"]!["devices"]!.AsArray().Count);

        var workbench = TargetItem(node, "Workbench");
        Assert.Equal("Unknown", workbench["hardware"]!["gpuVendor"]!.ToString());
        Assert.Null(workbench["hardware"]!["gpuName"]);
        Assert.Empty(workbench["hardware"]!["devices"]!.AsArray());

        var server = TargetItem(node, "Server");
        Assert.Equal("Nvidia", server["hardware"]!["gpuVendor"]!.ToString());
        Assert.Equal("RTX 4090", server["hardware"]!["gpuName"]!.ToString());
        Assert.Equal(1, server["hardware"]!["devices"]!.AsArray().Count);

        // The GPU CLI snapshot is NEVER probed during /api/router/fleet.
        harness.GpuVramProvider!.Verify(
            g => g.GetLocalGpuSnapshotAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RouterFleet_NoGpuVramProviderRegistered_AllTargetsReportUnknown()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" }
            ]
        };
        await using var harness = await CreateTestAppAsync(
            options,
            livenessEntries: new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [RoutingTarget.Local] = Entry(RoutingTarget.Local, TargetLivenessState.Online)
            },
            registerGpuVramProvider: false);

        using var response = await harness.Client.GetAsync("/api/router/fleet");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var local = TargetItem(node, "Local");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Unknown", local["hardware"]!["gpuVendor"]!.ToString());
        Assert.Empty(local["hardware"]!["devices"]!.AsArray());
        Assert.Null(harness.GpuVramProvider);
    }

    [Fact]
    public async Task RouterFleet_NoOptionalServices_StillReturns200_WithBaselineDegradation()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" }
            ]
        };
        await using var harness = await CreateTestAppAsync(
            options,
            registerLiveness: false,
            registerTracker: false,
            registerShrinkManager: false,
            registerGpuVramProvider: false);

        using var response = await harness.Client.GetAsync("/api/router/fleet");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var local = TargetItem(node, "Local");
        Assert.Equal("Offline", local["liveness"]!.ToString()); // no liveness service and no catalog cache registered
        Assert.Null(local["latencyMs"]);
        Assert.Equal("0", local["execution"]!["inFlightRequests"]!.ToString());
        Assert.Null(local["execution"]!["activeModel"]);
        Assert.Equal("baseline", local["execution"]!["contextAllocation"]!.ToString());

        var cloud = TargetItem(node, "Cloud");
        Assert.Equal("Online", cloud["liveness"]!.ToString()); // CloudOverflow.Enabled defaults true
    }

    [Fact]
    public async Task RouterFleet_Latency_EmittedForOnlineTargetsAndNullForOfflineOrUnprobed()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" },
                new OllamaTargetOptions { Name = "Laptop", Url = "http://192.168.1.105:11435" },
                new OllamaTargetOptions { Name = "Newbie", Url = "http://192.168.1.200:11434" }
            ]
        };
        await using var harness = await CreateTestAppAsync(
            options,
            livenessEntries: new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [RoutingTarget.Local] = Entry(RoutingTarget.Local, TargetLivenessState.Online, latencyMs: 4),
                [(RoutingTarget)"Laptop"] = Entry((RoutingTarget)"Laptop", TargetLivenessState.Offline, latencyMs: null),
                // Newbie never probed: fill-in entry without latency.
                [(RoutingTarget)"Newbie"] = Entry((RoutingTarget)"Newbie", TargetLivenessState.Unknown, latencyMs: null)
            });

        using var response = await harness.Client.GetAsync("/api/router/fleet");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("4", TargetItem(node, "Local")["latencyMs"]!.ToString());
        Assert.Null(TargetItem(node, "Laptop")["latencyMs"]);
        Assert.Null(TargetItem(node, "Newbie")["latencyMs"]);
    }

    [Fact]
    public async Task RouterFleet_ContextAllocation_FullApprovedStringSet()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" },
                new OllamaTargetOptions { Name = "Mid", Url = "http://192.168.1.10:11434" },
                new OllamaTargetOptions { Name = "Huge", Url = "http://192.168.1.20:11434" },
                new OllamaTargetOptions { Name = "Tiny", Url = "http://192.168.1.30:11434" }
            ]
        };
        await using var harness = await CreateTestAppAsync(
            options,
            livenessEntries: new Dictionary<RoutingTarget, TargetLivenessEntry>(),
            leaseSnapshot: new Dictionary<RoutingTarget, IReadOnlyList<ContextLeaseView>>
            {
                // Expanded lease at the 1.2M bucket.
                [RoutingTarget.Local] = [new ContextLeaseView("m", 1_200_000, IsExpanded: true)],
                // Baseline lease.
                [(RoutingTarget)"Mid"] = [new ContextLeaseView("m", 32_000, IsExpanded: false)],
                // "Tiny" and "Huge" have no leases → "baseline".
                [(RoutingTarget)"Huge"] = []
            });

        using var response = await harness.Client.GetAsync("/api/router/fleet");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("1.2M (expanded)", TargetItem(node, "Local")["execution"]!["contextAllocation"]!.ToString());
        Assert.Equal("32k", TargetItem(node, "Mid")["execution"]!["contextAllocation"]!.ToString());
        Assert.Equal("baseline", TargetItem(node, "Huge")["execution"]!["contextAllocation"]!.ToString());
        Assert.Equal("baseline", TargetItem(node, "Tiny")["execution"]!["contextAllocation"]!.ToString());
        Assert.Equal("baseline", TargetItem(node, "Cloud")["execution"]!["contextAllocation"]!.ToString());
    }

    [Fact]
    public async Task RouterFleet_CloudDisabled_ReportsCloudOffline()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" }
            ],
            CloudOverflow = { Enabled = false }
        };
        await using var harness = await CreateTestAppAsync(
            options,
            availabilityFlags: new Dictionary<RoutingTarget, bool>
            {
                [RoutingTarget.Local] = true,
                [RoutingTarget.Cloud] = true // persisted flag says enabled, but the config switch is off.
            },
            livenessEntries: new Dictionary<RoutingTarget, TargetLivenessEntry>());

        using var response = await harness.Client.GetAsync("/api/router/fleet");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var cloud = TargetItem(node, "Cloud");

        Assert.Equal("true", cloud["enabled"]!.ToString());
        Assert.Equal("Offline", cloud["liveness"]!.ToString());
    }

    [Fact]
    public async Task RouterFleet_ActiveModel_IsFirstLoadedModel_AndInFlightFromTracker()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" }
            ]
        };
        var state = new TargetExecutionState(
            RoutingTarget.Local,
            3,
            [
                new TargetLoadedModel("first-model:latest", 1_000_000, null),
                new TargetLoadedModel("second-model:latest", 2_000_000, null)
            ],
            3_000_000,
            DateTimeOffset.UtcNow,
            false);
        await using var harness = await CreateTestAppAsync(
            options,
            livenessEntries: new Dictionary<RoutingTarget, TargetLivenessEntry>(),
            executionStates: new Dictionary<RoutingTarget, TargetExecutionState>
            {
                [RoutingTarget.Local] = state
            });

        using var response = await harness.Client.GetAsync("/api/router/fleet");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var local = TargetItem(node, "Local");

        Assert.Equal("3", local["execution"]!["inFlightRequests"]!.ToString());
        Assert.Equal("first-model:latest", local["execution"]!["activeModel"]!.ToString());

        harness.Tracker!.Verify(t => t.GetState(RoutingTarget.Local), Times.Once);
        harness.Tracker.Verify(t => t.GetAllStates(), Times.Never);
    }
}
