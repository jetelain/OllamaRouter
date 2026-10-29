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
using OllamaRouter.Tests.Services;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OllamaRouter.Tests.Endpoints;

public class RouterHardwareEndpointsTests
{
    private static readonly IReadOnlyDictionary<RoutingTarget, TargetLivenessEntry> NoLiveness =
        new Dictionary<RoutingTarget, TargetLivenessEntry>();

    private sealed class Harness : IAsyncDisposable
    {
        public WebApplication App { get; }
        public HttpClient Client { get; }

        public Harness(WebApplication app, HttpClient client)
        {
            App = app;
            Client = client;
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static NodeHardwareProfile SampleProfile(string name = "node", SystemRamType ramType = SystemRamType.Ddr5) =>
        new(name, "linux", "x64", ramType == SystemRamType.AppleUnified, 32768, ramType,
            [new GpuDeviceProfile("Nvidia", "GeForce RTX 4080", 16384, "GDDR6X", 384, 936)],
            ramType == SystemRamType.AppleUnified ? 32768 : 16384, 936);

    private static async Task<Harness> CreateTestAppAsync(
        OllamaRouterOptions options,
        IHardwareProfileProvider? localProvider = null,
        IFleetHardwareService? fleetHardware = null,
        LocalGpuSnapshot? localGpuSnapshot = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        var optionsMock = new Mock<IOptions<OllamaRouterOptions>>();
        optionsMock.Setup(o => o.Value).Returns(options);

        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.GetSnapshot()).Returns(new TargetAvailabilitySnapshot(new Dictionary<RoutingTarget, bool>()
        {
            [RoutingTarget.Local] = true,
            [RoutingTarget.Remote] = true,
            [RoutingTarget.Cloud] = true
        }));

        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.GetEntries()).Returns(NoLiveness);

        var tracker = new Mock<ITargetExecutionTracker>();
        tracker.Setup(t => t.GetState(It.IsAny<RoutingTarget>())).Returns((TargetExecutionState?)null);

        var shrinkManager = new Mock<IContextAutoShrinkManager>();
        shrinkManager.Setup(s => s.GetLeaseSnapshot())
            .Returns(new Dictionary<RoutingTarget, IReadOnlyList<ContextLeaseView>>());

        var gpuVram = new Mock<IGpuVramProvider>();
        gpuVram
            .Setup(g => g.GetLocalGpuSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(localGpuSnapshot ?? LocalGpuSnapshot.Unknown);

        var catalogCache = new Mock<IModelCatalogCacheService>();
        catalogCache.Setup(c => c.HasAnyModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        builder.Services.AddSingleton(optionsMock.Object);
        builder.Services.AddSingleton(availability.Object);
        builder.Services.AddSingleton(liveness.Object);
        builder.Services.AddSingleton(tracker.Object);
        builder.Services.AddSingleton(shrinkManager.Object);
        builder.Services.AddSingleton(gpuVram.Object);
        builder.Services.AddSingleton(catalogCache.Object);
        if (localProvider is not null)
        {
            builder.Services.AddSingleton(localProvider);
        }
        if (fleetHardware is not null)
        {
            builder.Services.AddSingleton(fleetHardware);
        }

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();

        return new Harness(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static OllamaRouterOptions LocalPlusPeerOptions() => new()
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
                Name = "Peer",
                Url = "http://192.168.1.50:11434",
                MaxVramMB = 98304,
                MaxParallel = 4
            }
        ],
        CloudOverflow = { Enabled = true }
    };

    private static IHardwareProfileProvider FakeLocalProvider(NodeHardwareProfile? profile = null)
    {
        var mock = new Mock<IHardwareProfileProvider>();
        mock.Setup(l => l.GetLocalProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile ?? SampleProfile("local-node"));
        return mock.Object;
    }

    // ------------------------------------------------------------------
    //  GET /api/router/hardware
    // ------------------------------------------------------------------

    [Fact]
    public async Task HardwareEndpoint_Returns200_WithFullSchema_AndCachedReadIsFast()
    {
        await using var harness = await CreateTestAppAsync(
            LocalPlusPeerOptions(),
            localProvider: FakeLocalProvider());

        using var first = await harness.Client.GetAsync("/api/router/hardware");
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);

        var node = JsonNode.Parse(await first.Content.ReadAsStringAsync());
        Assert.NotNull(node);
        Assert.Equal("local-node", node!["nodeName"]!.ToString());
        Assert.Equal("linux", node["os"]!.ToString());
        Assert.False(string.IsNullOrEmpty(node["architecture"]!.ToString()));
        Assert.True(long.Parse(node["systemRamMB"]!.ToString()) > 0);
        Assert.Equal("Ddr5", node["systemRamType"]!.ToString());
        Assert.Single(node["gpus"]!.AsArray());
        Assert.Equal("GDDR6X", node["gpus"]!.AsArray()[0]!["vramType"]!.ToString());
        Assert.True(long.Parse(node["totalFastMemoryMB"]!.ToString()) > 0);
        Assert.True(double.Parse(node["effectiveBandwidthGBs"]!.ToString()) > 0);

        // The second call is the cached read: it must complete quickly (< 15 ms is the
        // production budget for a cached profile; allow scheduling slack in CI).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var second = await harness.Client.GetAsync("/api/router/hardware");
        sw.Stop();

        Assert.Equal(System.Net.HttpStatusCode.OK, second.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100), $"cached read took {sw.Elapsed}");
    }

    [Fact]
    public async Task HardwareEndpoint_FailSoft_Returns200EvenWhenProbeDegrades()
    {
        var degraded = new NodeHardwareProfile(
            "degraded", "unknown", "x64", false, 0, SystemRamType.Unknown,
            Array.Empty<GpuDeviceProfile>(), 0, 0);
        await using var harness = await CreateTestAppAsync(LocalPlusPeerOptions(), localProvider: FakeLocalProvider(degraded));

        using var response = await harness.Client.GetAsync("/api/router/hardware");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0L, long.Parse(node!["systemRamMB"]!.ToString()));
        Assert.Equal(0.0, double.Parse(node["effectiveBandwidthGBs"]!.ToString()), precision: 2);
    }

    // ------------------------------------------------------------------
    //  GET /api/router/fleet/hardware
    // ------------------------------------------------------------------

    [Fact]
    public async Task FleetHardwareEndpoint_ListsEveryEffectiveTarget_WithFailSoftReachability()
    {
        var fleet = new Mock<IFleetHardwareService>();
        var canned = new List<FleetHardwareNode>
        {
            new("Local", "local", SampleProfile("local-node"), true),
            new("Peer", "none", null, false)
        };
        fleet.Setup(f => f.GetFleetHardwareAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(canned.AsReadOnly());

        await using var harness = await CreateTestAppAsync(LocalPlusPeerOptions(), fleetHardware: fleet.Object);

        using var response = await harness.Client.GetAsync("/api/router/fleet/hardware");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var nodes = node!["nodes"]!.AsArray();
        Assert.Equal(2, nodes.Count);
        Assert.Equal("Local", nodes[0]!["target"]!.ToString());
        Assert.Equal("local", nodes[0]!["source"]!.ToString());
        Assert.Equal("true", nodes[0]!["reachable"]!.ToString());
        Assert.NotNull(nodes[0]!["profile"]);
        Assert.Equal("Peer", nodes[1]!["target"]!.ToString());
        Assert.Equal("false", nodes[1]!["reachable"]!.ToString());
        // profile is null for the unreachable target with no stored profile.
        Assert.True(nodes[1]!["profile"] is null || nodes[1]!["profile"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null);
    }

    // ------------------------------------------------------------------
    //  PUT /api/router/fleet/hardware/{targetName}
    // ------------------------------------------------------------------

    private static StringContent ProfileBody(NodeHardwareProfile profile) => new(
        System.Text.Json.JsonSerializer.Serialize(profile, OllamaRouterJsonSerializerContext.Default.NodeHardwareProfile),
        System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task PutFleetHardware_StoresProfile_VisibleInFleetRead_AndPersistsToStateFile()
    {
        using var stateDir = new TempDir();
        var stateFile = System.IO.Path.Combine(stateDir.Path, "targets-hardware.json");
        var options = LocalPlusPeerOptions();

        var localProvider = FakeLocalProvider();
        var probeClient = new HttpClient(new HardwareProfileTests.ThrowingHandler())
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        var fleet = new FleetHardwareService(
            MsOptions.Create(options),
            localProvider,
            probeClient,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FleetHardwareService>.Instance,
            System.IO.Path.Combine(stateDir.Path, "targets-hardware.json"));

        await using var harness = await CreateTestAppAsync(options, localProvider: localProvider, fleetHardware: fleet);

        var profile = SampleProfile("pushed-node");
        using var put = await harness.Client.PutAsync("/api/router/fleet/hardware/Peer", ProfileBody(profile));
        Assert.Equal(System.Net.HttpStatusCode.OK, put.StatusCode);
        var echoed = JsonNode.Parse(await put.Content.ReadAsStringAsync());
        Assert.Equal("pushed-node", echoed!["nodeName"]!.ToString());

        // Visible in the next fleet read...
        using var get = await harness.Client.GetAsync("/api/router/fleet/hardware");
        Assert.Equal(System.Net.HttpStatusCode.OK, get.StatusCode);
        var node = JsonNode.Parse(await get.Content.ReadAsStringAsync());
        var peerRow = node!["nodes"]!.AsArray().First(n => n!["target"]!.ToString() == "Peer")!;
        Assert.Equal("pushed-node", peerRow["profile"]!["nodeName"]!.ToString());

        // ...and persisted to the state file (target key present, round-trips).
        Assert.True(File.Exists(stateFile));
        var raw = await File.ReadAllTextAsync(stateFile);
        Assert.Contains("Peer", raw);
        Assert.Contains("pushed-node", raw);

        // Restart the service on the same state dir: served without any peer contact.
        var restarted = new FleetHardwareService(
            MsOptions.Create(options),
            localProvider,
            probeClient,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FleetHardwareService>.Instance,
            stateFile);
        var reloaded = restarted.GetProfile("peer");
        Assert.NotNull(reloaded);
        Assert.Equal("pushed-node", reloaded.NodeName);
    }

    [Fact]
    public async Task PutFleetHardware_UnknownTarget_Returns404()
    {
        await using var harness = await CreateTestAppAsync(LocalPlusPeerOptions(), fleetHardware: new Mock<IFleetHardwareService>().Object);

        using var response = await harness.Client.PutAsync(
            "/api/router/fleet/hardware/NoSuchTarget", ProfileBody(SampleProfile()));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(node!["error"]!.ToString()));
    }

    [Fact]
    public async Task PutFleetHardware_MalformedBody_Returns400()
    {
        await using var harness = await CreateTestAppAsync(LocalPlusPeerOptions(), fleetHardware: new Mock<IFleetHardwareService>().Object);

        using var response = await harness.Client.PutAsync(
            "/api/router/fleet/hardware/Peer",
            new StringContent("{ not valid json !!", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(node!["error"]!.ToString()));

        // An empty body is also 400 (null profile).
        using var empty = await harness.Client.PutAsync(
            "/api/router/fleet/hardware/Peer",
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, empty.StatusCode);
    }

    // ------------------------------------------------------------------
    //  GET /api/router/fleet enrichment (backward compatible + new fields)
    // ------------------------------------------------------------------

    [Fact]
    public async Task FleetEndpoint_RemainsBackwardCompatible_AndEnrichesLocalTargetHardware()
    {
        var localProfile = SampleProfile("local-node");
        var localProvider = FakeLocalProvider(localProfile);
        var fleet = new Mock<IFleetHardwareService>();
        fleet.Setup(f => f.GetProfile("Local")).Returns((NodeHardwareProfile?)localProfile);
        fleet.Setup(f => f.GetProfile("Peer")).Returns((NodeHardwareProfile?)null);

        await using var harness = await CreateTestAppAsync(
            LocalPlusPeerOptions(),
            localProvider: localProvider,
            fleetHardware: fleet.Object,
            localGpuSnapshot: new LocalGpuSnapshot("Nvidia", ["NVIDIA GeForce RTX 4080 (16384 MB)"], 16384));

        using var response = await harness.Client.GetAsync("/api/router/fleet");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var targets = node!["targets"]!.AsArray();

        var local = targets.First(t => t!["name"]!.ToString() == "Local")!;
        // Pre-existing fields unchanged.
        Assert.Equal("http://127.0.0.1:11435", local["url"]!.ToString());
        Assert.Equal("true", local["enabled"]!.ToString());
        Assert.False(string.IsNullOrEmpty(local["liveness"]!.ToString()));
        Assert.Equal("16384", local["hardware"]!["maxVramMB"]!.ToString());
        Assert.Equal("Nvidia", local["hardware"]!["gpuVendor"]!.ToString());
        Assert.Equal("1", local["hardware"]!["maxParallel"]!.ToString());
        Assert.NotNull(local["execution"]!["inFlightRequests"]);
        // New memory-tier fields on the local target.
        Assert.Equal("false", local["hardware"]!["isUnifiedMemory"]!.ToString());
        Assert.True(long.Parse(local["hardware"]!["systemRamMB"]!.ToString()) > 0);
        Assert.False(string.IsNullOrEmpty(local["hardware"]!["memoryType"]!.ToString()));
        Assert.True(double.Parse(local["hardware"]!["bandwidthGBs"]!.ToString()) > 0);

        // A peer target WITHOUT a stored profile keeps the defaults: isUnifiedMemory
        // emitted as false, the three nullable fields omitted.
        var peer = targets.First(t => t!["name"]!.ToString() == "Peer")!;
        Assert.Equal("false", peer["hardware"]!["isUnifiedMemory"]!.ToString());
        Assert.Null(peer["hardware"]!["systemRamMB"]);
        Assert.Null(peer["hardware"]!["memoryType"]);
        Assert.Null(peer["hardware"]!["bandwidthGBs"]);
    }

    [Fact]
    public async Task PutFleetHardware_EmptyBody_Returns400_WithErrorMessage()
    {
        await using var harness = await CreateTestAppAsync(LocalPlusPeerOptions(), fleetHardware: new Mock<IFleetHardwareService>().Object);

        using var empty = await harness.Client.PutAsync(
            "/api/router/fleet/hardware/Peer",
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, empty.StatusCode);
        var node = JsonNode.Parse(await empty.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(node!["error"]!.ToString()));
    }

    [Fact]
    public async Task PutFleetHardware_ProfilesMissingNodeName_Return400()
    {
        await using var harness = await CreateTestAppAsync(LocalPlusPeerOptions(), fleetHardware: new Mock<IFleetHardwareService>().Object);

        // Valid JSON but an empty node name is rejected as an invalid profile body.
        var noName = new NodeHardwareProfile(
            "", "linux", "x64", false, 1024, SystemRamType.Ddr4,
            Array.Empty<GpuDeviceProfile>(), 1024, 0);
        using var response = await harness.Client.PutAsync(
            "/api/router/fleet/hardware/Peer", ProfileBody(noName));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FleetEndpoint_WithoutHardwareServices_StillReturns200_WithDefaults()
    {
        // No IHardwareProfileProvider / IFleetHardwareService registered: the fleet
        // endpoint must still answer with the new memory-tier fields at their defaults.
        var options = LocalPlusPeerOptions();
        await using var harness = await CreateTestAppAsync(options);

        using var response = await harness.Client.GetAsync("/api/router/fleet");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var targets = node!["targets"]!.AsArray();
        foreach (var target in targets)
        {
            var hw = target!["hardware"]!;
            Assert.Equal("false", hw["isUnifiedMemory"]!.ToString());
            Assert.Null(hw["systemRamMB"]);
            Assert.Null(hw["memoryType"]);
            Assert.Null(hw["bandwidthGBs"]);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hw-endpoint-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
