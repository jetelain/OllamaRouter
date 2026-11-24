using OllamaRouter.Tests.TestHelpers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Extensions;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Endpoint tests for the model-benchmarking API (task #28): the status-code matrix of
/// POST /api/router/benchmark (202 / 404 / 409), the per-target run-scoped exclusion map
/// pass-through (task #152), the keyless GET /api/router/benchmark/candidates plan read
/// (task #152), GET /api/router/benchmark/results (including in-flight Running rows) and
/// DELETE /api/router/benchmark/results. The runner is resolved from DI, so the tests
/// replace the singleton with a stub — the real runner's behavior is covered by
/// MicroBenchmarkRunnerTests.
/// </summary>
public sealed class RouterBenchmarkEndpointsTests : IDisposable
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public Harness(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class StubRunner : IMicroBenchmarkRunner
    {
        private readonly IReadOnlyList<string> _targets;
        private readonly bool _tryStartResult;
        public IReadOnlyList<BenchmarkTargetResult> Results { get; }

        /// <summary>The plan <see cref="GetBenchmarkCandidates"/> returns (task #152).</summary>
        public RouterBenchmarkCandidatesResponse CandidatesResponse { get; set; } =
            new([]);

        public bool? LastClearKeepRunning { get; private set; }
        public RoutingTarget? LastTarget { get; private set; }
        public IReadOnlyList<UsageRole>? LastRoles { get; private set; }
        public string? LastModel { get; private set; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>>? LastExcludedModels { get; private set; }
        public int TryStartCalls { get; private set; }

        public StubRunner(
            IReadOnlyList<string> targets,
            bool tryStartResult,
            IReadOnlyList<BenchmarkTargetResult> results)
        {
            _targets = targets;
            _tryStartResult = tryStartResult;
            Results = results;
        }

        public bool TryStartRun(
            RoutingTarget? target,
            IEnumerable<UsageRole>? roles,
            string? model,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? excludedModels = null)
        {
            TryStartCalls++;
            LastTarget = target;
            LastRoles = roles?.ToList();
            LastModel = model;
            LastExcludedModels = excludedModels;
            return _tryStartResult;
        }

        public Task<BenchmarkTargetResult?> GetStatusAsync(RoutingTarget target) =>
            Task.FromResult(Results.FirstOrDefault(r =>
                string.Equals(r.Target, target.Value, StringComparison.OrdinalIgnoreCase)));

        public IReadOnlyList<BenchmarkTargetResult> GetAllResults() => Results;

        public IReadOnlyList<string> GetEffectiveTargetNames() => _targets;

        public RouterBenchmarkCandidatesResponse GetBenchmarkCandidates() => CandidatesResponse;

        public void ClearResults(bool keepRunning = false)
        {
            LastClearKeepRunning = keepRunning;
        }
    }

    private readonly string _stateDirectory;

    public RouterBenchmarkEndpointsTests()
    {
        _stateDirectory = Directory.CreateTempSubdirectory("benchendpoints-").FullName;
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

    private static async Task<Harness> CreateAppAsync(StubRunner runner)
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" },
                new OllamaTargetOptions { Name = "Peer", Url = "http://192.168.1.50:11434" }
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

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();

        return new Harness(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string? body)
    {
        return body is null
            ? client.PostAsync(url, null)
            : client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
    }

    // ------------------------------------------------------------------
    //  POST /api/router/benchmark
    // ------------------------------------------------------------------

    [Fact]
    public async Task Post_OmittedTarget_202_WithAllEffectiveTargetsQueued()
    {
        var runner = new StubRunner(["Local", "Peer"], true, []);
        await using var harness = await CreateAppAsync(runner);

        using var response = await PostJsonAsync(harness.Client, "/api/router/benchmark", body: null);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body?["started"]?.GetValue<bool>() == true);
        Assert.Equal(["Local", "Peer"],
            body?["queuedTargets"]?.AsArray().Select(n => n.ToString()).ToList());

        // The runner was invoked with no explicit target.
        Assert.Equal(1, runner.TryStartCalls);
        Assert.Null(runner.LastTarget);
    }

    [Fact]
    public async Task Post_WithTargetAndFilters_202_PassesFilterThrough()
    {
        var runner = new StubRunner(["Local", "Peer"], true, []);
        await using var harness = await CreateAppAsync(runner);

        using var response = await PostJsonAsync(harness.Client, "/api/router/benchmark",
            "{\"target\":\"Peer\",\"roles\":[\"AgenticChat\"],\"model\":\"qwen3.8:27b\"}");
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["Peer"], body?["queuedTargets"]?.AsArray().Select(n => n.ToString()).ToList());

        Assert.Equal("Peer", runner.LastTarget?.Value);
        Assert.Equal([UsageRole.AgenticChat], runner.LastRoles);
        Assert.Equal("qwen3.8:27b", runner.LastModel);
    }

    [Fact]
    public async Task Post_UnknownTarget_404_RunNotStarted()
    {
        var runner = new StubRunner(["Local", "Peer"], true, []);
        await using var harness = await CreateAppAsync(runner);

        using var response = await PostJsonAsync(harness.Client, "/api/router/benchmark",
            "{\"target\":\"NoSuchNode\"}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotNull(body?["error"]?.GetValue<string>());

        // The 404 short-circuits before the run is queued.
        Assert.Equal(0, runner.TryStartCalls);
    }

    [Fact]
    public async Task Post_RunInFlight_409()
    {
        var runner = new StubRunner(["Local"], false, []);
        await using var harness = await CreateAppAsync(runner);

        using var response = await PostJsonAsync(harness.Client, "/api/router/benchmark", body: null);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("benchmark run already in progress", body?["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task Post_WithExcludedModels_PassesPerTargetMapThrough()
    {
        var runner = new StubRunner(["Local", "Peer"], true, []);
        await using var harness = await CreateAppAsync(runner);

        using var response = await PostJsonAsync(harness.Client, "/api/router/benchmark",
            """{"excludedModels":{"Local":["qwen3.8:27b"]}}""");
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        Assert.Equal(1, runner.TryStartCalls);
        Assert.Null(runner.LastTarget);
        Assert.Null(runner.LastRoles);
        Assert.Null(runner.LastModel);
        Assert.NotNull(runner.LastExcludedModels);
        Assert.Single(runner.LastExcludedModels!);
        Assert.Equal(["qwen3.8:27b"], runner.LastExcludedModels["Local"].ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"target":"Peer","roles":["AgenticChat"],"model":"qwen3.8:27b"}""")]
    public async Task Post_WithoutExcludedModels_NullPassesThrough(string? body)
    {
        var runner = new StubRunner(["Local", "Peer"], true, []);
        await using var harness = await CreateAppAsync(runner);

        using var response = await PostJsonAsync(harness.Client, "/api/router/benchmark", body);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        // No exclusion map on today's bodies → the runner sees null (no behavior change).
        Assert.Null(runner.LastExcludedModels);
    }

    [Fact]
    public async Task Post_MalformedExcludedModels_400_RunNotStarted()
    {
        var runner = new StubRunner(["Local"], true, []);
        await using var harness = await CreateAppAsync(runner);

        using var response = await PostJsonAsync(harness.Client, "/api/router/benchmark",
            """{"excludedModels":{"Local":"not-a-list"}}""");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, runner.TryStartCalls);
    }

    // ------------------------------------------------------------------
    //  GET /api/router/benchmark/results
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetResults_EmptyWhenNothingRan()
    {
        var runner = new StubRunner(["Local"], true, []);
        await using var harness = await CreateAppAsync(runner);

        using var response = await harness.Client.GetAsync("/api/router/benchmark/results");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body?["targets"]?.AsArray().Count ?? -1);
    }

    [Fact]
    public async Task GetResults_IncludesInFlightRunningRows()
    {
        var running = new BenchmarkTargetResult(
            "Peer", BenchmarkRunStatus.Running,
            DateTimeOffset.UtcNow, null,
            [new BenchmarkContextResult("bench:27b", UsageRole.AgenticChat, 16_384,
                64, 2_000_000_000, 32.0, 5000.0, Viable: true, Reason: null)]);
        var completed = new BenchmarkTargetResult(
            "Local", BenchmarkRunStatus.Succeeded,
            DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow,
            []);
        var runner = new StubRunner(["Local", "Peer"], true, [completed, running]);
        await using var harness = await CreateAppAsync(runner);

        using var response = await harness.Client.GetAsync("/api/router/benchmark/results");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var targets = body?["targets"]?.AsArray()!;
        Assert.Equal(2, targets.Count);
        Assert.Contains(targets.Select(t => t?["status"]?.GetValue<string>()), s => s == "Running");
        Assert.Contains(targets.Select(t => t?["status"]?.GetValue<string>()), s => s == "Succeeded");
    }

    // ------------------------------------------------------------------
    //  GET /api/router/benchmark/candidates (task #152)
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetCandidates_ReturnsTheRunnerPlan_Keyless()
    {
        var profile = new NodeHardwareProfile(
            "local", "linux", "x64", false, 32_768, SystemRamType.Ddr5,
            [new GpuDeviceProfile("nvidia", "RTX 4090", 24_576, "GDDR6X", 384, 1008)],
            24_576, 1008.0);
        var runner = new StubRunner(["Local", "Peer"], true, [])
        {
            CandidatesResponse = new RouterBenchmarkCandidatesResponse(
            [
                new RouterBenchmarkCandidateTarget("Local", profile,
                [
                    new RouterBenchmarkCandidateModel(
                        "qwen3.8:27b", [UsageRole.AgenticChat, UsageRole.SeniorDeveloper], "q4", "Dense",
                        27_800_000_000, null, 131_072, BenchmarkCandidateFit.Fits, 65_536,
                        // Task #169: the curated-catalog class metadata (Fits dense — anchored).
                        ModelClass: "Expert", AllowCpuOffload: false, EstimatedWeightsMB: 12_288.0),
                    new RouterBenchmarkCandidateModel(
                        "gemma4:e4b", [UsageRole.BasicModel], "q4", "Dense",
                        4_500_000_000, null, 131_072, BenchmarkCandidateFit.Pruned, 0,
                        // Task #169: Pruned dense — no curated anchor (null stays a JSON null key).
                        ModelClass: "Expert", AllowCpuOffload: false, EstimatedWeightsMB: null),
                ]),
                new RouterBenchmarkCandidateTarget("Peer", null,
                [
                    new RouterBenchmarkCandidateModel(
                        "qwen3.8:27b", [UsageRole.AgenticChat, UsageRole.SeniorDeveloper], "q4", "Dense",
                        27_800_000_000, null, 131_072, BenchmarkCandidateFit.Assumed, 131_072,
                        // Task #169: Assumed MoE (synthetic, offload-eligible, anchored).
                        ModelClass: "MoE", AllowCpuOffload: true, EstimatedWeightsMB: 22_528.0),
                ]),
            ])
        };
        await using var harness = await CreateAppAsync(runner);

        // D8: KEYLESS read — NO API key is sent and the response is 200.
        using var response = await harness.Client.GetAsync("/api/router/benchmark/candidates");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var targets = body!["targets"]!.AsArray()!;
        Assert.Equal(2, targets.Count); // both targets, in order

        // Group 1: full hardware annotation + both candidates (fit + maxFitsContext round-trip).
        var first = targets[0]!.AsObject()!;
        Assert.Equal("Local", first["target"]!.GetValue<string>());
        var hardware = first["hardware"]!.AsObject()!;
        Assert.Equal("RTX 4090", hardware!["gpus"]!.AsArray()![0]!["modelName"]!.GetValue<string>());
        Assert.Equal(24_576, hardware!["totalFastMemoryMB"]!.GetValue<long>());
        Assert.Equal(1008.0, hardware!["effectiveBandwidthGBs"]!.GetValue<double>(), precision: 6);
        var firstCandidates = first["candidates"]!.AsArray()!;
        Assert.Equal(2, firstCandidates.Count);
        Assert.Equal("Fits", firstCandidates[0]!["fit"]!.GetValue<string>());
        Assert.Equal(65_536, firstCandidates[0]!["maxFitsContext"]!.GetValue<int>());
        Assert.Equal(["AgenticChat", "SeniorDeveloper"],
            firstCandidates[0]!["roles"]!.AsArray().Select(r => r!.GetValue<string>()!).ToArray());
        Assert.Equal(27_800_000_000, firstCandidates[0]!["totalParams"]!.GetValue<long>());
        Assert.Null(firstCandidates[0]!["activeParams"]);
        // Task #169: the curated-catalog class metadata round-trips over the wire.
        Assert.Equal("Expert", firstCandidates[0]!["modelClass"]!.GetValue<string>());
        Assert.False(firstCandidates[0]!["allowCpuOffload"]!.GetValue<bool>());
        Assert.Equal(12_288.0, firstCandidates[0]!["estimatedWeightsMB"]!.GetValue<double>());
        Assert.Equal("Pruned", firstCandidates[1]!["fit"]!.GetValue<string>());
        Assert.Equal(0, firstCandidates[1]!["maxFitsContext"]!.GetValue<int>()); // Pruned → 0
        Assert.Equal("Expert", firstCandidates[1]!["modelClass"]!.GetValue<string>());
        Assert.False(firstCandidates[1]!["allowCpuOffload"]!.GetValue<bool>());
        // .NET 10 JsonNode gotcha: a JSON null-valued property indexes as a null node —
        // assert the key IS present, then that its value is null (a missing key would pass
        // Assert.Null alone).
        Assert.True(firstCandidates[1]!.AsObject()!.ContainsKey("estimatedWeightsMB"));
        Assert.Null(firstCandidates[1]!["estimatedWeightsMB"]);

        // Group 2: unknown profile → hardware null (never a probe), still the same candidates.
        var second = targets[1]!.AsObject()!;
        Assert.Equal("Peer", second["target"]!.GetValue<string>());
        // The key IS present, with a JSON null value (a null-valued property indexes as a null node).
        Assert.True(second.ContainsKey("hardware"));
        Assert.Null(second["hardware"]);
        var secondCandidates = second["candidates"]!.AsArray()!;
        Assert.Single(secondCandidates);
        Assert.Equal("Assumed", secondCandidates[0]!["fit"]!.GetValue<string>());
        Assert.Equal(131_072, secondCandidates[0]!["maxFitsContext"]!.GetValue<int>());
        // Task #169: the Assumed MoE candidate's class metadata.
        Assert.Equal("MoE", secondCandidates[0]!["modelClass"]!.GetValue<string>());
        Assert.True(secondCandidates[0]!["allowCpuOffload"]!.GetValue<bool>());
        Assert.Equal(22_528.0, secondCandidates[0]!["estimatedWeightsMB"]!.GetValue<double>());
    }

    [Fact]
    public void CandidateModel_RoundTrips_ThroughTheServerAotContext_WithTheTask169Fields()
    {
        // Task #169 (AC5): the source-generated SERVER context covers the three new
        // properties at RUNTIME, not just compile time — serialize to raw JSON, assert the
        // wire field names/values (a plain string — no JsonStringEnumConverter), then
        // deserialize back through the same context.
        var response = new RouterBenchmarkCandidatesResponse(
        [
            new RouterBenchmarkCandidateTarget("Local", null,
            [
                new RouterBenchmarkCandidateModel(
                    "jetelain/Qwen3.6-35B-A3B:q4",
                    [UsageRole.BasicModel, UsageRole.JuniorDeveloper],
                    "UD-Q4_K_XL", "MoE", 35_000_000_000L, 3_200_000_000L, 262_144,
                    BenchmarkCandidateFit.Fits, 262_144,
                    ModelClass: "MoE", AllowCpuOffload: true, EstimatedWeightsMB: 22_528.0),
                new RouterBenchmarkCandidateModel(
                    "jetelain/Ornith-1.5-122b:latest",
                    [UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper],
                    "UD-Q4_K_XL", "Hybrid MoE", 124_600_000_000L, 10_000_000_000L, 262_144,
                    BenchmarkCandidateFit.Pruned, 0,
                    ModelClass: "Expert", AllowCpuOffload: false, EstimatedWeightsMB: null),
            ]),
        ]);

        var json = JsonSerializer.Serialize(response, OllamaRouterJsonSerializerContext.Default.RouterBenchmarkCandidatesResponse);
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement.GetProperty("targets")[0].GetProperty("candidates")[0];
        Assert.Equal("MoE", first.GetProperty("modelClass").GetString());
        Assert.True(first.GetProperty("allowCpuOffload").GetBoolean());
        Assert.Equal(22_528.0, first.GetProperty("estimatedWeightsMB").GetDouble());
        var second = doc.RootElement.GetProperty("targets")[0].GetProperty("candidates")[1];
        Assert.Equal("Expert", second.GetProperty("modelClass").GetString());
        Assert.False(second.GetProperty("allowCpuOffload").GetBoolean());
        Assert.True(second.GetProperty("estimatedWeightsMB").ValueKind == System.Text.Json.JsonValueKind.Null);

        var roundTripped = JsonSerializer.Deserialize(json, OllamaRouterJsonSerializerContext.Default.RouterBenchmarkCandidatesResponse)!;
        var a = roundTripped.Targets[0].Candidates[0];
        Assert.Equal("MoE", a.ModelClass);
        Assert.True(a.AllowCpuOffload);
        Assert.Equal(22_528.0, a.EstimatedWeightsMB);
        Assert.Equal(BenchmarkCandidateFit.Fits, a.Fit);
        Assert.Equal(262_144, a.MaxFitsContext);
        var b = roundTripped.Targets[0].Candidates[1];
        Assert.Equal("Expert", b.ModelClass);
        Assert.False(b.AllowCpuOffload);
        Assert.Null(b.EstimatedWeightsMB);
        Assert.Equal(BenchmarkCandidateFit.Pruned, b.Fit);
        Assert.Equal(0, b.MaxFitsContext);
    }

    // ------------------------------------------------------------------
    //  DELETE /api/router/benchmark/results
    // ------------------------------------------------------------------

    [Fact]
    public async Task Delete_ClearedTrue_KeepsRunningTargets()
    {
        var runner = new StubRunner(["Local"], true, []);
        await using var harness = await CreateAppAsync(runner);

        using var response = await harness.Client.DeleteAsync("/api/router/benchmark/results");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body?["cleared"]?.GetValue<bool>() == true);

        // The endpoint applies the in-flight-safe clear (completed targets only).
        Assert.Equal(true, runner.LastClearKeepRunning);
    }

    // ------------------------------------------------------------------
    //  DI wiring (production extension)
    // ------------------------------------------------------------------

    [Fact]
    public void AddOllamaRouter_RegistersRealBenchmarkRunner_AndStateFilesFollowStateDirectory()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" }
            ],
            TerminalMonitoring = new TerminalMonitoringOptions { Enabled = false }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOllamaRouter(options, stateDirectory: _stateDirectory, enableTui: false);

        using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IMicroBenchmarkRunner>();
        Assert.IsType<MicroBenchmarkRunner>(runner);
    }
}
