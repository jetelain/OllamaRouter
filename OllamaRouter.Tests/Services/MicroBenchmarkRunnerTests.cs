using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MsOptions = Microsoft.Extensions.Options.Options;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// Unit tests for the automated micro-benchmark engine (task #28): pre-flight sizing gates
/// the pull, the ascending ladder with <c>num_predict: 64</c>, the ≥ 20 t/s viability gate
/// (exactly 20.0 is viable), the stop rule, OOM handling, single-flight behavior and the
/// benchmarks.json / targets-speed.json persistence. All HTTP goes through a deterministic
/// recording fake handler (no real Ollama, no network, no sleeps above ~50 ms).
/// </summary>
public sealed class MicroBenchmarkRunnerTests : IDisposable
{
    private const string TargetName = "bench-node";
    private const string TargetUrl = "http://bench-node:11434";

    /// <summary>
    /// Records every request (method, absolute path, body) and answers /api/generate from a
    /// first-in-first-out queue of response bodies (one body per generate call, warmup
    /// included). /api/pull always answers 200 {"status":"success"}. An optional gate can
    /// stall the FIRST generate call so tests can assert the single-flight contract.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public sealed record Request(string Method, string Path, string Body);

        private readonly object _gateLock = new();
        private readonly object _requestsLock = new();
        public List<Request> Requests { get; } = new();
        private readonly Queue<string> _generateResponses = new();

        /// <summary>When set, the first generate call blocks until the gate is released.</summary>
        private TaskCompletionSource? _generateGate;

        public void SetGenerateGate(TaskCompletionSource gate)
        {
            lock (_gateLock)
            {
                _generateGate = gate;
            }
        }

        public void ReleaseGenerateGate()
        {
            TaskCompletionSource? gate;
            lock (_gateLock)
            {
                gate = _generateGate;
                _generateGate = null;
            }
            gate?.TrySetResult();
        }

        public void EnqueueGenerateResponse(string body) => _generateResponses.Enqueue(body);

        public int PullCallCount => CountByPath("/api/pull");
        public int GenerateCallCount => CountByPath("/api/generate");

        public int CountByPath(string path)
        {
            lock (_requestsLock)
            {
                return Requests.Count(r => r.Path == path);
            }
        }

        public Request? FirstByPath(string path)
        {
            lock (_requestsLock)
            {
                return Requests.FirstOrDefault(r => r.Path == path);
            }
        }

        public List<Request> RequestsByPath(string path)
        {
            lock (_requestsLock)
            {
                return Requests.Where(r => r.Path == path).ToList();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var absolutePath = request.RequestUri!.AbsolutePath;
            var body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : "";
            lock (_requestsLock)
            {
                Requests.Add(new Request(request.Method.Method, absolutePath, body));
            }

            if (absolutePath == "/api/generate")
            {
                TaskCompletionSource? gate;
                lock (_gateLock)
                {
                    gate = _generateGate;
                }
                if (gate is not null)
                {
                    await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }

                string response;
                lock (_generateResponses)
                {
                    response = _generateResponses.Count > 0
                        ? _generateResponses.Dequeue()
                        : "{\"error\":\"no response queued\"}";
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response, Encoding.UTF8, "application/json")
                };
            }

            if (absolutePath == "/api/pull")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"success\"}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"no route\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>Deterministic estimator: 4 characters = 1 token (stable filler sizing).</summary>
    private sealed class FixedTokenEstimator : ITokenEstimator
    {
        public int EstimateTokens(IReadOnlyList<string> texts) =>
            texts.Sum(t => t.Length) / 4;
    }

    private static readonly CandidateModel TestModel = new(
        Name: "bench:27b",
        Roles: [UsageRole.AgenticChat, UsageRole.SoftwareArchitect],
        Architecture: "Dense",
        TotalParams: 27_800_000_000,
        ActiveParams: null,
        Quantization: "q4",
        BitsPerWeight: 4,
        Layers: 64,
        KvHeads: 8,
        HeadDim: 128,
        NativeContext: 131_072,
        DefaultContext: 65_536);

    private readonly string _stateDirectory;
    private readonly RecordingHandler _handler;
    private readonly Mock<IOllamaModelCatalogClient> _catalogClient;
    private readonly Mock<IFleetHardwareService> _fleetHardware;
    private readonly Mock<IHostApplicationLifetime> _lifetime;
    private readonly OllamaRouterOptions _options;

    public MicroBenchmarkRunnerTests()
    {
        _stateDirectory = Directory.CreateTempSubdirectory("benchtest-").FullName;
        _handler = new RecordingHandler();
        _catalogClient = new Mock<IOllamaModelCatalogClient>();
        _catalogClient
            .Setup(c => c.GetTagsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray());
        _catalogClient
            .Setup(c => c.GetShowAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JsonObject?)null);
        _fleetHardware = new Mock<IFleetHardwareService>();
        _lifetime = new Mock<IHostApplicationLifetime>();
        _lifetime
            .Setup(l => l.ApplicationStopping)
            .Returns(CancellationToken.None);

        _options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = TargetName, Url = TargetUrl }
            ]
        };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
        catch
        {
            // Best effort; the OS reclaims temp files eventually.
        }
    }

    private MicroBenchmarkRunner CreateRunner(
        NodeHardwareProfile? profile,
        CandidateModelCatalogOptions? catalog = null,
        bool modelPresent = false)
    {
        _fleetHardware
            .Setup(f => f.GetProfile(It.IsAny<string>()))
            .Returns(profile);

        if (modelPresent)
        {
            var tags = new JsonArray
            {
                new JsonObject { ["name"] = TestModel.Name, ["model"] = TestModel.Name }
            };
            _catalogClient
                .Setup(c => c.GetTagsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(tags);
        }
        else
        {
            _catalogClient
                .Setup(c => c.GetTagsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new JsonArray());
        }

        var options = catalog ?? new CandidateModelCatalogOptions
        {
            Models = [TestModel],
            BenchmarkContextLadder = [16_384, 32_768]
        };

        var httpClient = new HttpClient(_handler);
        return new MicroBenchmarkRunner(
            MsOptions.Create(options),
            _fleetHardware.Object,
            new FixedTokenEstimator(),
            MsOptions.Create(_options),
            _catalogClient.Object,
            httpClient,
            _lifetime.Object,
            NullLogger<MicroBenchmarkRunner>.Instance,
            Path.Combine(_stateDirectory, "benchmarks.json"),
            Path.Combine(_stateDirectory, "targets-speed.json"));
    }

    private static NodeHardwareProfile Profile(long fastMB, long systemRamMB = 32_768, bool unified = false) =>
        new(TargetName, "linux", "x64", unified, systemRamMB,
            unified ? SystemRamType.AppleUnified : SystemRamType.Ddr5,
            [], fastMB, 0);

    private static string GenerateJson(long evalCount, long evalDurationNs) =>
        new JsonObject
        {
            ["model"] = TestModel.Name,
            ["eval_count"] = evalCount,
            ["eval_duration"] = evalDurationNs,
            ["prompt_eval_count"] = 1000,
            ["prompt_eval_duration"] = 1_000_000_000
        }.ToJsonString();


    private static async Task WaitUntilRunCompleteAsync(MicroBenchmarkRunner runner, int timeoutMs = 8_000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (runner.GetAllResults().Any(r => r.Status == BenchmarkRunStatus.Running))
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Benchmark run did not complete in time.");
            }
            await Task.Delay(10);
        }
    }

    // ------------------------------------------------------------------
    //  Pre-flight sizing gates the pull
    // ------------------------------------------------------------------

    [Fact]
    public async Task PrunedCandidate_NeverPulled_AllRowsSizingPruned()
    {
        // 8 GB fast: limit 7526.4 MB; 27B q4 weights are 13900.5 MB → pruned before any pull.
        var runner = CreateRunner(Profile(8_192));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Succeeded, results.Status);
        Assert.All(results.Results, r => Assert.Equal("sizing-pruned", r.Reason));
        Assert.All(results.Results, r => Assert.False(r.Viable));
        Assert.Equal(2, results.Results.Count); // one row per ladder rung, no measured rows
        Assert.Equal(0, _handler.PullCallCount);
        Assert.Equal(0, _handler.GenerateCallCount);
    }

    [Fact]
    public async Task UnknownProfile_FailOpen_BenchmarkProceeds()
    {
        var runner = CreateRunner(profile: null);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 32k

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Succeeded, results.Status);
        Assert.All(results.Results, r => Assert.True(r.Viable));
        Assert.All(results.Results, r => Assert.Equal(32.0, r.DecodeTokensPerSecond, precision: 3));

        // The persisted state records the assumed sizing source (fail-open decision #4).
        var statePath = Path.Combine(_stateDirectory, "benchmarks.json");
        Assert.True(File.Exists(statePath));
        Assert.Contains("\"assumed\"", File.ReadAllText(statePath));
    }

    // ------------------------------------------------------------------
    //  Pull gating + generate body shape
    // ------------------------------------------------------------------

    [Fact]
    public async Task ModelPresent_NoPullIssued()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        Assert.Equal(3, _handler.GenerateCallCount); // warmup + 2 ladder rungs
    }

    [Fact]
    public async Task ModelAbsent_ExactlyOnePull_WithNonStreamBody()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: false);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        Assert.Equal(1, _handler.PullCallCount);
        var pullBody = JsonNode.Parse(_handler.FirstByPath("/api/pull")!.Body) as JsonObject;
        Assert.Equal(TestModel.Name, pullBody?["model"]?.GetValue<string>());
        Assert.Equal(false, pullBody?["stream"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GenerateRequests_CarryNumCtxAndNumPredict64_AndStreamFalse()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var generates = _handler.RequestsByPath("/api/generate");
        Assert.Equal(3, generates.Count);

        // Warmup: the smallest context, num_predict 16, the fixed short prompt.
        var warmup = JsonNode.Parse(generates[0].Body) as JsonObject;
        Assert.Equal(16, warmup?["options"]?["num_predict"]?.GetValue<int>());
        Assert.Equal(16_384, warmup?["options"]?["num_ctx"]?.GetValue<int>());
        Assert.Equal(false, warmup?["stream"]?.GetValue<bool>());
        Assert.Equal("Write a short sentence.", warmup?["prompt"]?.GetValue<string>());

        // Ladder rungs: ascending num_ctx, num_predict 64, real-text filler prompt.
        var ladder = generates.Skip(1)
            .Select(r => JsonNode.Parse(r.Body) as JsonObject)
            .Select(b => b?["options"]?["num_ctx"]?.GetValue<int>())
            .ToList();
        Assert.Equal([16_384, 32_768], ladder);
        foreach (var request in generates.Skip(1))
        {
            var body = JsonNode.Parse(request.Body) as JsonObject;
            Assert.Equal(64, body?["options"]?["num_predict"]?.GetValue<int>());
            var prompt = body?["prompt"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(prompt));
            Assert.True(
                prompt!.Length / 4 >= 8_000,
                $"filler prompt too short for a 16k+ ctx: {prompt.Length / 4} estimated tokens");
            Assert.Contains("Summarize the above text in one paragraph. Begin now:", prompt);
        }
    }

    // ------------------------------------------------------------------
    //  Viability gate + stop rule
    // ------------------------------------------------------------------

    [Fact]
    public async Task Exactly20Tps_IsViable()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(GenerateJson(64, 3_200_000_000)); // 64 / 3.2 s = exactly 20.0
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));  // 32k rung (ladder continues)

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        var row = results.Results.Single(r => r.NumCtx == 16_384);
        Assert.True(row.Viable);
        Assert.Null(row.Reason);
        Assert.Equal(20.0, row.DecodeTokensPerSecond, precision: 9);

        // And 1 ns more → below threshold (the exact boundary check in floating point).
        Assert.True(64 / (3_200_000_001 * 1e-9) < 20.0);
    }

    [Fact]
    public async Task FirstNonViableRow_StopsTheLadder()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(GenerateJson(64, 6_400_000_000)); // 16k → 10 tps
        _handler.EnqueueGenerateResponse(GenerateJson(64, 1_000_000_000)); // must never be consumed

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        var row = results.Results.Single(r => r.NumCtx == 16_384);
        Assert.False(row.Viable);
        Assert.Equal("below-threshold", row.Reason);
        Assert.Equal(10.0, row.DecodeTokensPerSecond, precision: 3);
        Assert.Single(results.Results); // the boundary row is recorded; 32k is NOT
        Assert.Equal(2, _handler.GenerateCallCount); // warmup + 16k; 32k never requested
    }

    [Fact]
    public async Task OllamaError_IsOomRow_AndStopsLadder()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(
            "{\"error\":\"could not select any CUDA devices for model: out of memory\"}");

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        var row = results.Results.Single(r => r.NumCtx == 16_384);
        Assert.False(row.Viable);
        Assert.Equal("oom", row.Reason);
        Assert.Single(results.Results);
    }

    [Fact]
    public async Task CorruptMetrics_IsErrorRow_AndStopsLadder()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(GenerateJson(0, 0)); // eval_count 0 → bad metrics

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        var row = results.Results.Single(r => r.NumCtx == 16_384);
        Assert.False(row.Viable);
        Assert.Equal("error:bad-metrics", row.Reason);
        Assert.Single(results.Results);
    }

    // ------------------------------------------------------------------
    //  Single-flight
    // ------------------------------------------------------------------

    [Fact]
    public async Task SecondTryStartRunWhileInFlight_ReturnsFalse()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetGenerateGate(gate);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup (gated)
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        try
        {
            // Wait until the run is actually in flight (warmup generate in progress).
            var sw = Stopwatch.StartNew();
            while (_handler.GenerateCallCount == 0 && sw.ElapsedMilliseconds < 5_000)
            {
                await Task.Delay(10);
            }

            Assert.False(runner.TryStartRun(target: null, roles: null, model: null));
            Assert.False(runner.TryStartRun(RoutingTarget.Local, null, null));
        }
        finally
        {
            _handler.ReleaseGenerateGate();
        }

        await WaitUntilRunCompleteAsync(runner);
        // After completion the gate is free again. Poll briefly to absorb the small window
        // between the in-memory state flip and the finally-block gate release.
        var sw2 = Stopwatch.StartNew();
        var started = false;
        while (!started && sw2.ElapsedMilliseconds < 5_000)
        {
            started = runner.TryStartRun(target: null, roles: null, model: null);
            if (!started)
            {
                await Task.Delay(10);
            }
        }
        Assert.True(started);
    }

    [Fact]
    public async Task ApplicationStopping_MidRun_MarksCurrentTargetCancelled_AndPersistsIt()
    {
        // Regression: cancelling while the current target's HTTP work is in flight used to
        // rethrow the OperationCanceledException without writing a final state for that
        // target — it stayed "Running" forever in memory and in benchmarks.json.
        var statePath = Path.Combine(_stateDirectory, "benchmarks.json");
        var capturingLifetime = new CapturingLifetime();

        _fleetHardware.Setup(f => f.GetProfile(It.IsAny<string>())).Returns(Profile(32_768));
        _catalogClient
            .Setup(c => c.GetTagsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray { new JsonObject { ["name"] = TestModel.Name, ["model"] = TestModel.Name } });
        _catalogClient
            .Setup(c => c.GetShowAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JsonObject?)null);

        var runner = new MicroBenchmarkRunner(
            MsOptions.Create(new CandidateModelCatalogOptions
            {
                Models = [TestModel],
                BenchmarkContextLadder = [16_384, 32_768]
            }),
            _fleetHardware.Object,
            new FixedTokenEstimator(),
            MsOptions.Create(_options),
            _catalogClient.Object,
            new HttpClient(_handler),
            capturingLifetime,
            NullLogger<MicroBenchmarkRunner>.Instance,
            statePath,
            Path.Combine(_stateDirectory, "targets-speed.json"));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetGenerateGate(gate);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup (gated)
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        try
        {
            // Wait until the run is actually in flight (warmup generate blocked on the gate).
            var sw = Stopwatch.StartNew();
            while (_handler.GenerateCallCount == 0 && sw.ElapsedMilliseconds < 5_000)
            {
                await Task.Delay(10);
            }
            var runningResult = await runner.GetStatusAsync(new RoutingTarget(TargetName));
            Assert.Equal(BenchmarkRunStatus.Running, runningResult?.Status ?? BenchmarkRunStatus.NotStarted);

            // Simulate the host stopping while the target is mid-work.
            capturingLifetime.StopApplication();
        }
        finally
        {
            _handler.ReleaseGenerateGate();
        }

        // The current target must reach a terminal state (not stuck in "Running").
        var sw2 = Stopwatch.StartNew();
        BenchmarkTargetResult? result = null;
        while (sw2.ElapsedMilliseconds < 8_000)
        {
            result = await runner.GetStatusAsync(new RoutingTarget(TargetName));
            if (result is not null && result.Status != BenchmarkRunStatus.Running)
            {
                break;
            }
            await Task.Delay(10);
        }
        Assert.NotNull(result);
        Assert.Equal(BenchmarkRunStatus.Cancelled, result!.Status);

        // The Cancelled state must be persisted (benchmarks.json is written as "Running"
        // at start; the cancellation transition has to replace it on disk).
        var sw3 = Stopwatch.StartNew();
        var persisted = false;
        while (sw3.ElapsedMilliseconds < 8_000 && !persisted)
        {
            persisted = File.Exists(statePath) &&
                File.ReadAllText(statePath).Contains("\"Cancelled\"");
            if (!persisted)
            {
                await Task.Delay(10);
            }
        }
        Assert.True(persisted, "benchmarks.json must carry the Cancelled state, not a stale Running row");

        // The run gate must be released again after a cancelled run.
        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);
    }

    /// <summary>
    /// Mimics a real host: <see cref="ApplicationStopping"/> is a property returning the
    /// stopping token (the runner registers its run CTS callback on that token), and
    /// <see cref="StopApplication"/> cancels it mid-run.
    /// </summary>
    private sealed class CapturingLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();
    }

    // ------------------------------------------------------------------
    //  Persistence
    // ------------------------------------------------------------------

    [Fact]
    public async Task BenchmarksJson_RoundTrips_ToNewRunnerInstance()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var statePath = Path.Combine(_stateDirectory, "benchmarks.json");
        Assert.True(File.Exists(statePath));

        // A fresh runner instance reloads the persisted state.
        var reloaded = CreateRunner(Profile(32_768), modelPresent: true);
        var results = reloaded.GetAllResults();
        Assert.Single(results);
        Assert.Equal(TargetName, results[0].Target);
        Assert.Equal(BenchmarkRunStatus.Succeeded, results[0].Status);
        Assert.Equal(2, results[0].Results.Count);
        Assert.All(results[0].Results, r => Assert.True(r.Viable));
    }

    [Fact]
    public async Task CorruptBenchmarksJson_FailsSoft_EmptyStore()
    {
        File.WriteAllText(Path.Combine(_stateDirectory, "benchmarks.json"), "{ this is not json !");

        var runner = CreateRunner(Profile(32_768));
        Assert.Empty(runner.GetAllResults());
    }

    [Fact]
    public async Task SpeedSnapshot_SeedsBestViableContext_PreservesLearnedEntries()
    {
        var speedPath = Path.Combine(_stateDirectory, "targets-speed.json");

        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k → 32 tps
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_400_000_000)); // 32k → 26.667 tps

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        Assert.True(File.Exists(speedPath));
        var snapshot = JsonSerializer.Deserialize(
            File.ReadAllText(speedPath),
            OllamaRouterJsonSerializerContext.Default.DictionaryStringDictionaryStringSpeedSnapshotEntry)!;
        Assert.True(snapshot.TryGetValue(TargetName, out var models));
        Assert.True(models.TryGetValue(TestModel.Name, out var entry));
        // Largest viable context row (32k) is the representative steady-state speed.
        Assert.Equal(26.6667, entry.TokensPerSecond, precision: 3);
        Assert.Equal(2, entry.Samples);
        Assert.Equal("benchmark", entry.Source);

        // Now overwrite the entry with a "learned" value (simulating a later task #23 write)
        // and re-benchmark: the seed must NOT overwrite "learned" entries.
        models[TestModel.Name] = new SpeedSnapshotEntry(99.0, 5, "learned", DateTimeOffset.UtcNow);
        File.WriteAllText(speedPath, JsonSerializer.Serialize(
            snapshot,
            OllamaRouterJsonSerializerContext.Default.DictionaryStringDictionaryStringSpeedSnapshotEntry));

        var runner2 = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_400_000_000)); // 32k

        Assert.True(runner2.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner2);

        var reloaded = JsonSerializer.Deserialize(
            File.ReadAllText(speedPath),
            OllamaRouterJsonSerializerContext.Default.DictionaryStringDictionaryStringSpeedSnapshotEntry)!;
        Assert.Equal("learned", reloaded[TargetName][TestModel.Name].Source);
        Assert.Equal(99.0, reloaded[TargetName][TestModel.Name].TokensPerSecond);
        Assert.Equal(5, reloaded[TargetName][TestModel.Name].Samples);
    }

    [Fact]
    public async Task NoViableRows_SpeedSnapshotNotSeeded()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(GenerateJson(64, 6_400_000_000)); // 10 tps → below threshold

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        Assert.False(File.Exists(Path.Combine(_stateDirectory, "targets-speed.json")));
    }

    [Fact]
    public void ClearResults_RemovesBenchmarkSpeedEntries_KeepsLearned()
    {
        var speedPath = Path.Combine(_stateDirectory, "targets-speed.json");
        File.WriteAllText(
            Path.Combine(_stateDirectory, "benchmarks.json"),
            "{\"bench-node\":{\"target\":\"bench-node\",\"status\":\"Succeeded\"," +
            "\"startedAt\":null,\"finishedAt\":null,\"updatedAt\":\"2026-10-29T00:00:00Z\"," +
            "\"sizingSource\":\"profile\",\"results\":[]}}");

        var snapshot = new Dictionary<string, Dictionary<string, SpeedSnapshotEntry>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [TargetName] = new Dictionary<string, SpeedSnapshotEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [TestModel.Name] = new SpeedSnapshotEntry(30.0, 2, "benchmark", DateTimeOffset.UtcNow),
                ["other:7b"] = new SpeedSnapshotEntry(99.0, 5, "learned", DateTimeOffset.UtcNow)
            }
        };
        File.WriteAllText(speedPath, JsonSerializer.Serialize(
            snapshot,
            OllamaRouterJsonSerializerContext.Default.DictionaryStringDictionaryStringSpeedSnapshotEntry));

        var runner = CreateRunner(Profile(32_768));
        Assert.Single(runner.GetAllResults()); // reloaded from benchmarks.json
        runner.ClearResults();

        Assert.Empty(runner.GetAllResults());
        Assert.False(File.Exists(Path.Combine(_stateDirectory, "benchmarks.json")));

        var reloaded = JsonSerializer.Deserialize(
            File.ReadAllText(speedPath),
            OllamaRouterJsonSerializerContext.Default.DictionaryStringDictionaryStringSpeedSnapshotEntry)!;
        // The "benchmark" entry was removed; the "learned" entry survived.
        Assert.False(reloaded.ContainsKey(TargetName) && reloaded[TargetName].ContainsKey(TestModel.Name));
        Assert.Equal(99.0, reloaded[TargetName]["other:7b"].TokensPerSecond);
        Assert.Equal("learned", reloaded[TargetName]["other:7b"].Source);
    }

    // ------------------------------------------------------------------
    //  Candidate filtering
    // ------------------------------------------------------------------

    [Fact]
    public async Task RoleFilter_TagsFirstMatchingRole_AndSkipsNonMatchingCandidates()
    {
        var basicModel = new CandidateModel(
            "basic:4b", [UsageRole.BasicModel], "Dense",
            TotalParams: 4_500_000_000, ActiveParams: null,
            "q4", 4, Layers: 36, KvHeads: 8, HeadDim: 256,
            NativeContext: 131_072, DefaultContext: 32_768);
        var options = new CandidateModelCatalogOptions
        {
            Models = [TestModel, basicModel],
            BenchmarkContextLadder = [16_384]
        };

        var runner = CreateRunner(Profile(32_768), catalog: options, modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k

        Assert.True(runner.TryStartRun(target: null, roles: [UsageRole.AgenticChat], model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.All(results.Results, r => Assert.Equal(TestModel.Name, r.Model));
        Assert.All(results.Results, r => Assert.Equal(UsageRole.AgenticChat, r.Role));
        Assert.Equal(2, _handler.GenerateCallCount); // warmup + 1 rung; basic:4b never touched
    }

    [Fact]
    public async Task ModelFilter_MatchesSingleCandidate_CaseInsensitive()
    {
        var basicModel = new CandidateModel(
            "basic:4b", [UsageRole.BasicModel], "Dense",
            TotalParams: 4_500_000_000, ActiveParams: null,
            "q4", 4, Layers: 36, KvHeads: 8, HeadDim: 256,
            NativeContext: 131_072, DefaultContext: 32_768);
        var options = new CandidateModelCatalogOptions
        {
            Models = [TestModel, basicModel],
            BenchmarkContextLadder = [16_384]
        };

        var runner = CreateRunner(Profile(32_768), catalog: options, modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // warmup
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k

        Assert.True(runner.TryStartRun(target: null, roles: null, model: "BASIC:4b"));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.All(results.Results, r => Assert.Equal("basic:4b", r.Model));
        Assert.Equal(2, _handler.GenerateCallCount); // only basic:4b benchmarked
    }
}

