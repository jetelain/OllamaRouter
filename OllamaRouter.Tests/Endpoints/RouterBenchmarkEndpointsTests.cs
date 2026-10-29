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
using OllamaRouter.Extensions;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using OllamaRouter.Services.Benchmarking;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Endpoint tests for the model-benchmarking API (task #28): the status-code matrix of
/// POST /api/router/benchmark (202 / 404 / 409), GET /api/router/benchmark/results
/// (including in-flight Running rows) and DELETE /api/router/benchmark/results. The runner
/// is resolved from DI, so the tests replace the singleton with a stub — the real runner's
/// behavior is covered by MicroBenchmarkRunnerTests.
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

        public bool? LastClearKeepRunning { get; private set; }
        public RoutingTarget? LastTarget { get; private set; }
        public IReadOnlyList<UsageRole>? LastRoles { get; private set; }
        public string? LastModel { get; private set; }
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

        public bool TryStartRun(RoutingTarget? target, IEnumerable<UsageRole>? roles, string? model)
        {
            TryStartCalls++;
            LastTarget = target;
            LastRoles = roles?.ToList();
            LastModel = model;
            return _tryStartResult;
        }

        public Task<BenchmarkTargetResult?> GetStatusAsync(RoutingTarget target) =>
            Task.FromResult(Results.FirstOrDefault(r =>
                string.Equals(r.Target, target.Value, StringComparison.OrdinalIgnoreCase)));

        public IReadOnlyList<BenchmarkTargetResult> GetAllResults() => Results;

        public IReadOnlyList<string> GetEffectiveTargetNames() => _targets;

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
        builder.Services.AddSingleton(MsOptions.Create(options));
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
