using OllamaRouter.Tests.TestHelpers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Extensions;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Recommendations;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Endpoint tests for the role recommendation API (task #66): GET /api/router/recommendations
/// (stable empty shape + full matrix), POST .../generate (200 recompute + persist + activate,
/// 409 with the benchmark hint when no terminal benchmark data exists), POST .../apply (200
/// idempotent re-activation, 409 with the generate hint when no file exists) and the route
/// shape (the three routes coexist with the full existing route table — append-last
/// registration, no catch-all shadowing). Same minimal WebApplication + temp-state-dir +
/// DI-stubbed runner/hardware pattern as RouterBenchmarkEndpointsTests.
/// </summary>
public sealed class RouterRecommendationEndpointsTests : IDisposable
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public Harness(WebApplication app, HttpClient client, RecommendationStore store)
        {
            _app = app;
            Client = client;
            Store = store;
        }

        public HttpClient Client { get; }
        public RecommendationStore Store { get; }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class StubRunner : IMicroBenchmarkRunner
    {
        private readonly IReadOnlyList<BenchmarkTargetResult> _results;
        public StubRunner(IReadOnlyList<BenchmarkTargetResult> results) => _results = results;

        public bool TryStartRun(
            RoutingTarget? target,
            IEnumerable<UsageRole>? roles,
            string? model,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? excludedModels = null) => true;
        public Task<BenchmarkTargetResult?> GetStatusAsync(RoutingTarget target) => Task.FromResult<BenchmarkTargetResult?>(null);
        public IReadOnlyList<BenchmarkTargetResult> GetAllResults() => _results;
        public IReadOnlyList<string> GetEffectiveTargetNames() => ["Desktop", "Aiserver"];
        public RouterBenchmarkCandidatesResponse GetBenchmarkCandidates() => new([]);
        public void ClearResults(bool keepRunning = false) { }
    }

    private sealed class StubHardware : IFleetHardwareService
    {
        private readonly IReadOnlyDictionary<string, NodeHardwareProfile> _profiles;
        public StubHardware(IReadOnlyDictionary<string, NodeHardwareProfile> profiles) => _profiles = profiles;

        public NodeHardwareProfile? GetProfile(string targetName) =>
            _profiles.TryGetValue(targetName, out var profile) ? profile : null;

        public int? GetEffectiveMaxVramMB(OllamaTargetOptions target) => null;
        public Task<IReadOnlyList<FleetHardwareNode>> GetFleetHardwareAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FleetHardwareNode>>([]);
        public Task<bool> UpsertProfileAsync(string targetName, NodeHardwareProfile profile, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> StoreProbeProfileAsync(
            string targetName, NodeHardwareProfile profile, string source, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private readonly string _stateDirectory;

    public RouterRecommendationEndpointsTests()
    {
        _stateDirectory = Directory.CreateTempSubdirectory("recendpoints-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private static NodeHardwareProfile Profile(string name, long totalFastMemoryMB) => new(
        name, "linux", "x64", false, 32_768, SystemRamType.Ddr5,
        [new GpuDeviceProfile("Nvidia", "GeForce", (int)totalFastMemoryMB, "GDDR6", 256, 448)],
        totalFastMemoryMB, 448.0);

    /// <summary>Terminal benchmark rows: jetelain/Qwen3.8-27B:latest viable to 49 152 on Desktop / 262 144 on Aiserver;
    /// jetelain/Qwen3.6-35B-A3B:q4 (MoE, all five roles) viable to 131 072.</summary>
    private static BenchmarkContextResult Row(string model, int numCtx, double tps) =>
        new(model, UsageRole.AgenticChat, numCtx, 64, (long)(64e9 / tps), tps, 5_000, Viable: true, Reason: null);

    private static IReadOnlyList<BenchmarkTargetResult> TerminalResults() =>
    [
        new BenchmarkTargetResult("Desktop", BenchmarkRunStatus.Succeeded,
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow,
        [
            Row("jetelain/Qwen3.8-27B:latest", 16_384, 45.0),
            Row("jetelain/Qwen3.8-27B:latest", 32_768, 40.0),
            Row("jetelain/Qwen3.8-27B:latest", 49_152, 33.0),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 16_384, 90.0),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 131_072, 48.0)
        ]),
        new BenchmarkTargetResult("Aiserver", BenchmarkRunStatus.Succeeded,
            DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow,
        [
            Row("jetelain/Qwen3.8-27B:latest", 16_384, 30.0),
            Row("jetelain/Qwen3.8-27B:latest", 262_144, 22.5),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 131_072, 61.0)
        ])
    ];

    private async Task<Harness> CreateAppAsync(StubRunner runner)
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Desktop", Url = "http://127.0.0.1:11435" },
                new OllamaTargetOptions { Name = "Aiserver", Url = "http://192.168.1.50:11434" }
            ]
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });
        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(new TestOptionsMonitor<OllamaRouterOptions>(options));
        builder.Services.AddSingleton<IMicroBenchmarkRunner>(runner);
        builder.Services.AddSingleton<IFleetHardwareService>(new StubHardware(new Dictionary<string, NodeHardwareProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Desktop"] = Profile("Desktop", 16_384),
            ["Aiserver"] = Profile("Aiserver", 98_304)
        }));
        builder.Services.AddSingleton<IRecommendationEngine, RecommendationEngine>();

        // The REAL store over the temp state directory (machine-generated state, never config/).
        var store = new RecommendationStore(Path.Combine(_stateDirectory, "recommended.json"));
        builder.Services.AddSingleton<IRecommendationStore>(store);

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();

        return new Harness(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, store);
    }

    // ------------------------------------------------------------------
    //  GET /api/router/recommendations
    // ------------------------------------------------------------------

    [Fact]
    public async Task Get_NothingActive_200_StableEmptyShape()
    {
        await using var harness = await CreateAppAsync(new StubRunner(TerminalResults()));

        using var response = await harness.Client.GetAsync("/api/router/recommendations");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Null(body?["generatedAt"]);
        Assert.Equal(0, body?["roles"]?.AsObject().Count ?? -1);
        Assert.Equal(0, body?["models"]?.AsObject().Count ?? -1);
    }

    [Fact]
    public async Task Get_AfterActivation_200_FullMatrix()
    {
        await using var harness = await CreateAppAsync(new StubRunner(TerminalResults()));

        // Seed the live store (the activate half of the generate flow, directly).
        var matrix = new RecommendationEngine().Generate(
            TerminalResults(), CandidateModelCatalogOptions.DefaultModels,
            hardwareProfileLookup: name => name switch
            {
                "Desktop" => Profile(name, 16_384),
                "Aiserver" => Profile(name, 98_304),
                _ => null
            },
            generatedAt: DateTimeOffset.UtcNow);
        harness.Store.Activate(matrix);

        using var response = await harness.Client.GetAsync("/api/router/recommendations");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotNull(body?["generatedAt"]?.GetValue<DateTimeOffset>());
        var roles = body?["roles"]?.AsObject()!;
        // INTERIM pre-#168 winner behavior (task #166): the MoE catalog models now carry
        // ALL five roles, and jetelain/Qwen3.6-35B-A3B:q4 wins every role tie-break (262 144
        // MoE ceiling, 61.0 tps on Aiserver) — so ALL FIVE roles are PRESENT (junior included)
        // and resolve to the SAME native model. Superseded by #168's ModelClass comparator.
        Assert.Equal(5, roles.Count);
        foreach (var alias in new[]
        {
            "basic", "agentic_chat", "software_architect", "senior_developer", "junior_developer"
        })
        {
            Assert.Equal("jetelain/Qwen3.6-35B-A3B:q4", roles[alias]["model"]?.GetValue<string>());
            Assert.Equal(262_144, roles[alias]["recommendedContextWindow"]!.GetValue<int>());
        }
        var seniorNodes = roles["senior_developer"]["nodes"]!.AsArray();
        Assert.Equal(2, seniorNodes.Count);
        // MoE node shape: ceiling = NativeContext (262 144), baseline = largest viable row,
        // tps = that row's measured decode speed.
        Assert.Equal("Desktop", seniorNodes[0]?["target"]?.GetValue<string>());
        Assert.Equal(262_144, seniorNodes[0]?["contextCeiling"]!.GetValue<int>());
        Assert.Equal(131_072, seniorNodes[0]?["baselineContext"]!.GetValue<int>());
        Assert.Equal(48.0, seniorNodes[0]?["tokensPerSecond"]!.GetValue<double>());
        Assert.Equal("Aiserver", seniorNodes[1]?["target"]?.GetValue<string>());
        Assert.Equal(262_144, seniorNodes[1]?["contextCeiling"]!.GetValue<int>());
        Assert.Equal(131_072, seniorNodes[1]?["baselineContext"]!.GetValue<int>());
        Assert.Equal(61.0, seniorNodes[1]?["tokensPerSecond"]!.GetValue<double>());

        var models = body?["models"]?.AsObject()!;
        Assert.Single(models);
        Assert.True(models.ContainsKey("jetelain/Qwen3.6-35B-A3B:q4"));
    }

    // ------------------------------------------------------------------
    //  POST /api/router/recommendations/generate
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostGenerate_TerminalResults_200_PersistsAndActivates()
    {
        await using var harness = await CreateAppAsync(new StubRunner(TerminalResults()));

        using var response = await harness.Client.PostAsync("/api/router/recommendations/generate", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body?["generated"]?.GetValue<bool>() == true);
        Assert.NotNull(body?["generatedAt"]?.GetValue<DateTimeOffset>());
        // INTERIM pre-#168: all five roles resolve to the single MoE winner.
        Assert.Equal(5, body?["roleCount"]?.GetValue<int>());
        Assert.Equal(1, body?["modelCount"]?.GetValue<int>());
        Assert.Equal(["Desktop", "Aiserver"], body?["targets"]?.AsArray().Select(n => n.ToString()).ToList());
        Assert.Equal("jetelain/Qwen3.6-35B-A3B:q4", body?["recommendations"]["roles"]["senior_developer"]["model"]?.GetValue<string>());

        // recommended.json exists in the state directory.
        var filePath = Path.Combine(_stateDirectory, "recommended.json");
        Assert.True(File.Exists(filePath));

        // Activation happened: a subsequent GET returns the new matrix.
        using var get = await harness.Client.GetAsync("/api/router/recommendations");
        var getBody = JsonNode.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal("jetelain/Qwen3.6-35B-A3B:q4", getBody?["roles"]["senior_developer"]["model"]?.GetValue<string>());
        Assert.True(harness.Store.TryGetNodeContextCeiling("Desktop", "jetelain/Qwen3.6-35B-A3B:q4", out var ceiling));
        Assert.Equal(262_144, ceiling);
    }

    [Fact]
    public async Task PostGenerate_ZeroTerminalResults_409_BenchmarkHint_FileNotWritten()
    {
        // Only a Running row contributes: the ladder is incomplete → no terminal data.
        var running = new BenchmarkTargetResult("Desktop", BenchmarkRunStatus.Running,
            DateTimeOffset.UtcNow, null, [Row("jetelain/Qwen3.8-27B:latest", 16_384, 30.0)]);
        await using var harness = await CreateAppAsync(new StubRunner([running]));

        using var response = await harness.Client.PostAsync("/api/router/recommendations/generate", null);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("no benchmark results available; run POST /api/router/benchmark first",
            body?["error"]?.GetValue<string>());

        Assert.False(File.Exists(Path.Combine(_stateDirectory, "recommended.json")));
    }

    // ------------------------------------------------------------------
    //  POST /api/router/recommendations/apply
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostApply_NoFile_409_GenerateHint()
    {
        await using var harness = await CreateAppAsync(new StubRunner(TerminalResults()));

        using var response = await harness.Client.PostAsync("/api/router/recommendations/apply", null);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("no recommendations file; run POST /api/router/recommendations/generate first",
            body?["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task PostApply_PresentFile_200_PerTargetSummary_Idempotent()
    {
        await using var harness = await CreateAppAsync(new StubRunner(TerminalResults()));

        // Seed the file via generate (recompute + persist + activate), then apply twice.
        using (var generate = await harness.Client.PostAsync("/api/router/recommendations/generate", null))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, generate.StatusCode);
        }

        using var first = await harness.Client.PostAsync("/api/router/recommendations/apply", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        var firstBody = JsonNode.Parse(await first.Content.ReadAsStringAsync());
        Assert.True(firstBody?["applied"]?.GetValue<bool>() == true);
        var targets = firstBody?["targets"]?.AsArray()!;
        var desktop = targets.Single(t => t?["target"]?.GetValue<string>() == "Desktop")!;
        // INTERIM pre-#168: the single MoE winner is the only model on both nodes; its MoE
        // node ceiling is the 262 144 NativeContext on every node.
        Assert.Equal(1, desktop["models"]!.GetValue<int>());
        Assert.Equal(262_144, desktop["maxContextCeiling"]!.GetValue<int>());
        var aiserver = targets.Single(t => t?["target"]?.GetValue<string>() == "Aiserver")!;
        Assert.Equal(262_144, aiserver["maxContextCeiling"]!.GetValue<int>());

        // Idempotent: the second call returns the identical per-target summary.
        using var second = await harness.Client.PostAsync("/api/router/recommendations/apply", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, second.StatusCode);
        var secondBody = JsonNode.Parse(await second.Content.ReadAsStringAsync());
        Assert.True(secondBody?["applied"]?.GetValue<bool>() == true);
        var secondTargets = secondBody?["targets"]?.AsArray()!;
        Assert.Equal(targets.Count, secondTargets.Count);
        for (var i = 0; i < targets.Count; i++)
        {
            Assert.Equal(targets[i]?["target"]?.GetValue<string>(), secondTargets[i]?["target"]?.GetValue<string>());
            Assert.Equal(targets[i]?["models"]?.GetValue<int>(), secondTargets[i]?["models"]?.GetValue<int>());
            Assert.Equal(targets[i]?["maxContextCeiling"]?.GetValue<int>(), secondTargets[i]?["maxContextCeiling"]?.GetValue<int>());
        }
    }

    // ------------------------------------------------------------------
    //  Route shape: the three routes coexist with the full route table
    // ------------------------------------------------------------------

    [Fact]
    public async Task RouteShape_ThreeRoutesCoexist_NoCatchAllShadowing()
    {
        await using var harness = await CreateAppAsync(new StubRunner(TerminalResults()));

        // All three recommendation routes resolve (no shadowing by sibling areas).
        using var get = await harness.Client.GetAsync("/api/router/recommendations");
        Assert.Equal(System.Net.HttpStatusCode.OK, get.StatusCode);
        using var generate = await harness.Client.PostAsync("/api/router/recommendations/generate", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, generate.StatusCode);
        using var apply = await harness.Client.PostAsync("/api/router/recommendations/apply", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, apply.StatusCode);

        // Sibling routes keep resolving (append-last registration preserves the route table).
        using var benchmarkResults = await harness.Client.GetAsync("/api/router/benchmark/results");
        Assert.Equal(System.Net.HttpStatusCode.OK, benchmarkResults.StatusCode);
        using var info = await harness.Client.GetAsync("/api/router/info");
        Assert.Equal(System.Net.HttpStatusCode.OK, info.StatusCode);
    }

    // ------------------------------------------------------------------
    //  DI wiring (production extension)
    // ------------------------------------------------------------------

    [Fact]
    public void AddOllamaRouter_RegistersEngineAndStore_AndStateFileFollowsStateDirectory()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Desktop", Url = "http://127.0.0.1:11435" }
            ],
            TerminalMonitoring = new TerminalMonitoringOptions { Enabled = false }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOllamaRouter(options, stateDirectory: _stateDirectory, enableTui: false);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<RecommendationEngine>(provider.GetRequiredService<IRecommendationEngine>());
        var store = provider.GetRequiredService<IRecommendationStore>();
        Assert.IsType<RecommendationStore>(store);
        Assert.Equal(Path.Combine(_stateDirectory, "recommended.json"), ((RecommendationStore)store).FilePath);

        // The routing decision service receives the store (lowered Stage-1 ceilings active).
        _ = provider.GetRequiredService<IRoutingDecisionService>();
    }
}
