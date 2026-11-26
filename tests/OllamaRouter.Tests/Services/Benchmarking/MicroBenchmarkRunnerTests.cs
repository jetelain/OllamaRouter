using OllamaRouter.Tests.TestHelpers;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MsOptions = Microsoft.Extensions.Options.Options;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.ContextSizing;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Services.Benchmarking;

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
    /// first-in-first-out queue of response bodies — STRICT 1:1 with the measurement
    /// generates (task #167: no warmup generates exist and cold-start evictions never reach
    /// this handler; they ride the Moq'd catalog client). /api/pull answers from a FIFO queue
    /// of scriptable NDJSON bodies (task #174; the default — nothing enqueued — is the
    /// single-line {"status":"success"} response, which IS valid one-line NDJSON), and an
    /// optional mid-stream gate can stall a pull after a configurable number of lines so
    /// tests can assert the live DownloadingModel progress. One-shot generate gates can
    /// stall successive measurement rungs in sequence (single-flight contract + the live
    /// Benchmarking phase + incremental partial rows).
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public sealed record Request(string Method, string Path, string Body);

        private readonly object _gateLock = new();
        private readonly object _requestsLock = new();
        public List<Request> Requests { get; } = new();
        private readonly Queue<string> _generateResponses = new();

        /// <summary>Task #174: scriptable NDJSON /api/pull bodies (FIFO; default single-line
        /// success). <paramref name="LinesBeforeGate"/> (0-based count; null = no stall): when
        /// a pull gate is set, the response streams exactly that many lines, flushes and
        /// BLOCKS until released — the mid-stream observation point.</summary>
        private readonly Queue<(string Body, int? LinesBeforeGate)> _pullResponses = new();

        /// <summary>One-shot generate gates (task #174): each enqueued gate stalls exactly ONE
        /// generate call (picked up in FIFO order), so rung 1 and rung 2 can be gated in
        /// sequence. <see cref="_activeGenerateGate"/> is the gate currently blocking a call.</summary>
        private readonly Queue<TaskCompletionSource> _pendingGenerateGates = new();
        private TaskCompletionSource? _activeGenerateGate;

        /// <summary>Task #174: when set, the next scriptable pull (with a
        /// <paramref name="linesBeforeGate"/>) stalls mid-stream until released.</summary>
        private TaskCompletionSource? _pullGate;

        /// <summary>Stalls the NEXT generate call (replaces any previously enqueued gates —
        /// the pre-#174 single-gate semantics; the run loop is strictly sequential, so the
        /// next call is always the one that matters).</summary>
        public void SetGenerateGate(TaskCompletionSource gate)
        {
            lock (_gateLock)
            {
                _pendingGenerateGates.Clear();
                _pendingGenerateGates.Enqueue(gate);
            }
        }

        /// <summary>Enqueues a gate that stalls the NEXT still-pending generate call (task
        /// #174: queue TWO gates to gate rung 1 and rung 2 of the same candidate).</summary>
        public void EnqueueGenerateGate(TaskCompletionSource gate)
        {
            lock (_gateLock)
            {
                _pendingGenerateGates.Enqueue(gate);
            }
        }

        /// <summary>Releases the in-flight (picked-up) gate; falls back to the oldest pending
        /// one when the release runs before the runner picks it up (deterministic either way).</summary>
        public void ReleaseGenerateGate()
        {
            TaskCompletionSource? gate;
            lock (_gateLock)
            {
                gate = _activeGenerateGate
                    ?? (_pendingGenerateGates.Count > 0 ? _pendingGenerateGates.Dequeue() : null);
                _activeGenerateGate = null;
            }
            gate?.TrySetResult();
        }

        /// <summary>Task #174: sets the mid-stream pull gate (stalls the next scriptable pull
        /// after its <paramref name="linesBeforeGate"/> lines).</summary>
        public void SetPullGate(TaskCompletionSource gate)
        {
            lock (_gateLock)
            {
                _pullGate = gate;
            }
        }

        /// <summary>Task #174: releases the mid-stream pull gate.</summary>
        public void ReleasePullGate()
        {
            TaskCompletionSource? gate;
            lock (_gateLock)
            {
                gate = _pullGate;
                _pullGate = null;
            }
            gate?.TrySetResult();
        }

        public void EnqueueGenerateResponse(string body) => _generateResponses.Enqueue(body);

        /// <summary>Task #174: enqueues a scriptable NDJSON /api/pull response (newline-
        /// separated lines; the default single-line success body is the pre-#174 daemon
        /// behavior).</summary>
        public void EnqueuePullResponse(string ndjsonBody, int? linesBeforeGate = null)
        {
            lock (_requestsLock)
            {
                _pullResponses.Enqueue((ndjsonBody, linesBeforeGate));
            }
        }

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
                // Task #174: one-shot generate gates — the call picks up (dequeues) the oldest
                // pending gate and blocks on it until released.
                TaskCompletionSource? gate;
                lock (_gateLock)
                {
                    gate = _pendingGenerateGates.Count > 0 ? _pendingGenerateGates.Dequeue() : null;
                    if (gate is not null)
                    {
                        _activeGenerateGate = gate;
                    }
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
                (string Body, int? LinesBeforeGate) scripted;
                lock (_requestsLock)
                {
                    scripted = _pullResponses.Count > 0
                        ? _pullResponses.Dequeue()
                        : ("{\"status\":\"success\"}", (int?)null);
                }

                // Task #174: mid-stream stall — serve the first N lines, then HOLD the read
                // (the pull is "in flight") until the gate opens, then serve the rest. The
                // blocking lives in the STREAM's Read (run off the response path), wrapped in
                // StreamContent so HttpClient streams it LIVE with ResponseHeadersRead — the
                // same proven shape as FakeOllamaDaemonHandler in RouterModelPullEndpointTests.
                TaskCompletionSource? pullGate = null;
                int? linesBeforeGate = null;
                if (scripted.LinesBeforeGate is { } lines && lines >= 0)
                {
                    linesBeforeGate = lines;
                    lock (_gateLock)
                    {
                        pullGate = _pullGate;
                    }
                }
                if (pullGate is not null && linesBeforeGate is { } cut)
                {
                    var allLines = scripted.Body.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                    var head = string.Join('\n', allLines.Take(cut)) + "\n";
                    var tail = string.Join('\n', allLines.Skip(cut)) + (allLines.Count > cut ? "\n" : string.Empty);
                    var gated = new StreamContent(new GatedNdjsonStream(
                        Encoding.UTF8.GetBytes(head), Encoding.UTF8.GetBytes(tail), pullGate.Task, cancellationToken));
                    gated.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = gated };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(scripted.Body, Encoding.UTF8, "application/x-ndjson")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"no route\"}", Encoding.UTF8, "application/json")
            };
        }

        /// <summary>
        /// The gated /api/pull response stream (task #174 test seam, mirroring
        /// <c>FakeOllamaDaemonHandler.GatedNdjsonStream</c>): serves <paramref name="head"/>
        /// (the first N NDJSON lines) immediately, then the synchronous
        /// <see cref="Read(byte[], int, int)"/> HOLDs (the StreamReader's async read runs it
        /// off the response path) until the gate opens or the token cancels, then serves
        /// <paramref name="tail"/> and EOFs. This is what lets the runner observe the first
        /// progress lines LIVE (as DownloadingModel progress) while the pull is "in flight".
        /// </summary>
        private sealed class GatedNdjsonStream(byte[] head, byte[] tail, Task gate, CancellationToken ct) : Stream
        {
            private readonly byte[] _head = head;
            private readonly byte[] _tail = tail;
            private readonly Task _gate = gate;
            private readonly CancellationToken _ct = ct;
            private int _phase; // 0 = head, 1 = gated (in flight), 2 = tail, 3 = EOF
            private int _offset;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count == 0)
                {
                    return 0;
                }

                while (true)
                {
                    switch (_phase)
                    {
                        case 0:
                        {
                            if (_head.Length == 0)
                            {
                                _phase = 1;
                                continue;
                            }

                            var nHead = Math.Min(count, _head.Length - _offset);
                            Buffer.BlockCopy(_head, _offset, buffer, offset, nHead);
                            _offset += nHead;
                            if (_offset >= _head.Length)
                            {
                                // Head fully out — the NEXT read call holds the pull "in flight".
                                _phase = 1;
                                _offset = 0;
                            }

                            return nHead;
                        }

                        case 1:
                            // Hold the pull open until the gate opens; a token cancellation
                            // ends the stream with an OCE (the silent-cancellation path).
                            _gate.Wait(_ct);
                            _phase = 2;
                            continue;

                        case 2:
                        {
                            if (_tail.Length == 0)
                            {
                                _phase = 3;
                                continue;
                            }

                            var nTail = Math.Min(count, _tail.Length - _offset);
                            Buffer.BlockCopy(_tail, _offset, buffer, offset, nTail);
                            _offset += nTail;
                            if (_offset >= _tail.Length)
                            {
                                _phase = 3;
                            }

                            return nTail;
                        }

                        default:
                            return 0; // EOF
                    }
                }
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
            new TestOptionsMonitor<OllamaRouterOptions>(_options),
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
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        Assert.Equal(2, _handler.GenerateCallCount); // 2 ladder rungs (no warmup — task #167)
    }

    [Fact]
    public async Task ModelAbsent_ExactlyOnePull_WithStreamBody()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: false);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        Assert.Equal(1, _handler.PullCallCount);
        var pullBody = JsonNode.Parse(_handler.FirstByPath("/api/pull")!.Body) as JsonObject;
        Assert.Equal(TestModel.Name, pullBody?["model"]?.GetValue<string>());
        // Task #174: the pull is now STREAMING NDJSON (live DownloadingModel progress).
        Assert.Equal(true, pullBody?["stream"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GenerateRequests_CarryNumCtxAndNumPredict64_AndStreamFalse()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 32k

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        // A6 (task #167): a 2-rung single-model run issues EXACTLY 2 /api/generate calls on
        // the runner's HTTP client — no warmup generate remains (WarmupAsync deleted).
        var generates = _handler.RequestsByPath("/api/generate");
        Assert.Equal(2, generates.Count);

        // Each measured body: options.num_ctx = its rung (ascending), num_predict 64,
        // stream false, and a bounded real-text prompt.
        var expectedLadder = new[] { 16_384, 32_768 };
        for (var i = 0; i < generates.Count; i++)
        {
            var body = JsonNode.Parse(generates[i].Body) as JsonObject;
            Assert.Equal(expectedLadder[i], body?["options"]?["num_ctx"]?.GetValue<int>());
            Assert.Equal(64, body?["options"]?["num_predict"]?.GetValue<int>());
            Assert.Equal(false, body?["stream"]?.GetValue<bool>());

            var prompt = body?["prompt"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(prompt));
            // Fixed-size filler (task #167): ~768 estimated tokens (4 chars = 1 token in
            // the FixedTokenEstimator), inside the ~512–1,024 window — NOT the old
            // numCtx-scaled multi-megabyte prompt — with the benchmark suffix.
            Assert.InRange(prompt!.Length / 4, 512, 1_024);
            Assert.True(prompt.Length < 16_000, $"filler prompt unbounded: {prompt.Length} chars");
            Assert.EndsWith("Summarize the above text in one paragraph. Begin now:", prompt);
        }

        // Rung invariance: the SAME byte-identical prompt at every rung (cold-start KV
        // sizing comes from options.num_ctx, never from the prompt length).
        Assert.Equal(
            (JsonNode.Parse(generates[0].Body) as JsonObject)?["prompt"]?.GetValue<string>(),
            (JsonNode.Parse(generates[1].Body) as JsonObject)?["prompt"]?.GetValue<string>());

        // No request body anywhere carries the removed warmup shape.
        foreach (var request in _handler.Requests)
        {
            Assert.DoesNotContain("Write a short sentence.", request.Body);
            Assert.DoesNotContain("\"num_predict\":16", request.Body);
        }
    }

    // ------------------------------------------------------------------
    //  Viability gate + stop rule
    // ------------------------------------------------------------------

    [Fact]
    public async Task Exactly20Tps_IsViable()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 3_200_000_000)); // 16k → 64 / 3.2 s = exactly 20.0
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 32k rung (ladder continues)

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
        Assert.Equal(1, _handler.GenerateCallCount); // 16k only; 32k never requested
    }

    [Fact]
    public async Task OllamaError_IsOomRow_AndStopsLadder()
    {
        var runner = CreateRunner(Profile(32_768), modelPresent: true);
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
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k (gated)
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 32k

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        try
        {
            // Wait until the run is actually in flight (16k rung measurement in progress).
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
            new TestOptionsMonitor<OllamaRouterOptions>(_options),
            _catalogClient.Object,
            new HttpClient(_handler),
            capturingLifetime,
            NullLogger<MicroBenchmarkRunner>.Instance,
            statePath,
            Path.Combine(_stateDirectory, "targets-speed.json"));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetGenerateGate(gate);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k (gated)
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 32k

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        try
        {
            // Wait until the run is actually in flight (16k rung measurement blocked on the gate).
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
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k

        Assert.True(runner.TryStartRun(target: null, roles: [UsageRole.AgenticChat], model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.All(results.Results, r => Assert.Equal(TestModel.Name, r.Model));
        Assert.All(results.Results, r => Assert.Equal(UsageRole.AgenticChat, r.Role));
        Assert.Equal(1, _handler.GenerateCallCount); // 1 rung (no warmup); basic:4b never touched
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
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k

        Assert.True(runner.TryStartRun(target: null, roles: null, model: "BASIC:4b"));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.All(results.Results, r => Assert.Equal("basic:4b", r.Model));
        Assert.Equal(1, _handler.GenerateCallCount); // only basic:4b benchmarked (no warmup)
    }

    // ------------------------------------------------------------------
    //  Run-scoped per-target model exclusions (task #152)
    // ------------------------------------------------------------------

    private static CandidateModel SmallModel() => new(
        "small:4b", [UsageRole.BasicModel], "Dense",
        TotalParams: 4_500_000_000, ActiveParams: null,
        "q4", 4, Layers: 36, KvHeads: 8, HeadDim: 256,
        NativeContext: 131_072, DefaultContext: 32_768);

    private static CandidateModelCatalogOptions TwoModelCatalog() => new()
    {
        Models = [TestModel, SmallModel()],
        // 64 GB fast memory: both models fit at both ladder rungs (full measurement).
        BenchmarkContextLadder = [16_384, 32_768]
    };

    [Fact]
    public async Task TryStartRun_ExcludedModel_IsNeverPulledNorBenchmarked()
    {
        var runner = CreateRunner(Profile(65_536), catalog: TwoModelCatalog(), modelPresent: false);
        // Only small:4b is measured: 2 ladder rungs = 2 generates (no warmup — task #167).
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null,
            excludedModels: new Dictionary<string, IReadOnlyList<string>>
            {
                [TargetName] = [TestModel.Name]
            }));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Succeeded, results.Status);

        // The excluded model (bench:27b) is invisible: no rows, no pull, no generate.
        Assert.All(results.Results, r => Assert.Equal("small:4b", r.Model));
        Assert.Equal(2, results.Results.Count);
        Assert.All(results.Results, r => Assert.True(r.Viable));
        Assert.Equal(1, _handler.PullCallCount); // small:4b only
        Assert.Equal("small:4b", (JsonNode.Parse(_handler.FirstByPath("/api/pull")!.Body) as JsonObject)?["model"]?.GetValue<string>());
        Assert.Equal(2, _handler.GenerateCallCount);
        foreach (var request in _handler.RequestsByPath("/api/generate"))
        {
            var body = JsonNode.Parse(request.Body) as JsonObject;
            Assert.Equal("small:4b", body?["model"]?.GetValue<string>());
        }
    }

    [Fact]
    public async Task TryStartRun_AllModelsExcluded_TargetEndsFailedWithNoRows_AndNoPulls()
    {
        // D6: a target whose models are ALL excluded is processed with zero candidates and
        // ends Failed with empty results — the same treatment the runner gives an empty
        // catalog. No new status is invented.
        var runner = CreateRunner(Profile(65_536), catalog: TwoModelCatalog(), modelPresent: false);

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null,
            excludedModels: new Dictionary<string, IReadOnlyList<string>>
            {
                [TargetName] = [TestModel.Name, "small:4b"]
            }));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Failed, results.Status);
        Assert.Empty(results.Results);
        Assert.Equal(0, _handler.PullCallCount);
        Assert.Equal(0, _handler.GenerateCallCount);
    }

    [Fact]
    public async Task TryStartRun_EmptyExclusionDictionary_BehavesExactlyLikeAbsent()
    {
        var runner = CreateRunner(Profile(65_536), catalog: TwoModelCatalog(), modelPresent: false);
        // Two runs × two models × 2 rungs = 8 generates (no warmup — task #167), 4 pulls.
        for (var i = 0; i < 8; i++)
        {
            _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        }

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null, excludedModels: null));
        await WaitUntilRunCompleteAsync(runner);
        var pullsAfterRun1 = _handler.PullCallCount;
        var generatesAfterRun1 = _handler.GenerateCallCount;
        var rowsAfterRun1 = runner.GetAllResults().Single().Results
            .Select(r => (r.Model, r.NumCtx, r.DecodeTokensPerSecond, r.Viable, r.Reason))
            .OrderBy(t => t.Model).ThenBy(t => t.NumCtx).ToArray();

        Assert.True(pullsAfterRun1 > 0);
        Assert.True(generatesAfterRun1 > 0);

        // The EMPTY map is byte-for-byte the same run as an ABSENT one.
        var pullsBeforeRun2 = _handler.PullCallCount;
        var generatesBeforeRun2 = _handler.GenerateCallCount;
        Assert.True(runner.TryStartRun(target: null, roles: null, model: null,
            excludedModels: new Dictionary<string, IReadOnlyList<string>>()));
        await WaitUntilRunCompleteAsync(runner);

        Assert.Equal(pullsAfterRun1, _handler.PullCallCount - pullsBeforeRun2);
        Assert.Equal(generatesAfterRun1, _handler.GenerateCallCount - generatesBeforeRun2);
        var rowsAfterRun2 = runner.GetAllResults().Single().Results
            .Select(r => (r.Model, r.NumCtx, r.DecodeTokensPerSecond, r.Viable, r.Reason))
            .OrderBy(t => t.Model).ThenBy(t => t.NumCtx).ToArray();
        Assert.Equal(rowsAfterRun1, rowsAfterRun2);
    }

    [Fact]
    public async Task TryStartRun_ExclusionIsCaseInsensitive_AndSkipsBlankEntries()
    {
        var runner = CreateRunner(Profile(65_536), catalog: TwoModelCatalog(), modelPresent: false);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 32k

        // Target key AND model name arrive upper-cased; the blank entries are dropped.
        Assert.True(runner.TryStartRun(target: null, roles: null, model: null,
            excludedModels: new Dictionary<string, IReadOnlyList<string>>
            {
                [TargetName.ToUpperInvariant()] = [TestModel.Name.ToUpperInvariant(), "  ", ""]
            }));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.All(results.Results, r => Assert.Equal("small:4b", r.Model));
        Assert.Equal(2, _handler.GenerateCallCount); // small:4b fully benchmarked (2 rungs, no warmup)
        Assert.Equal(0, _handler.RequestsByPath("/api/pull").Count(p =>
            JsonNode.Parse(p.Body) is { } node &&
            string.Equals((node as JsonObject)?["model"]?.GetValue<string>(), TestModel.Name, StringComparison.OrdinalIgnoreCase)));
    }

    // ------------------------------------------------------------------
    //  GetBenchmarkCandidates (task #152): the endpoint uses the SAME derivation as the run loop
    // ------------------------------------------------------------------

    private static readonly IReadOnlyList<int> CandidateTestLadder = [8_192, 16_384, 32_768];

    private static CandidateModel SmallDense() => new(
        "small:2b", [UsageRole.BasicModel], "Dense",
        TotalParams: 2_000_000_000, ActiveParams: null,
        "q4", 4, Layers: 36, KvHeads: 8, HeadDim: 256,
        NativeContext: 32_768, DefaultContext: 16_384);

    private static CandidateModel BigDense() => new(
        "big:27b", [UsageRole.AgenticChat], "Dense",
        TotalParams: 27_800_000_000, ActiveParams: null,
        "q4", 4, Layers: 64, KvHeads: 8, HeadDim: 128,
        NativeContext: 16_384, DefaultContext: 16_384);

    private static CandidateModel MoeModel() => new(
        "moe:35b", [UsageRole.JuniorDeveloper], "MoE",
        TotalParams: 35_000_000_000, ActiveParams: 3_300_000_000,
        "q4", 4, Layers: 64, KvHeads: 8, HeadDim: 128,
        NativeContext: 32_768, DefaultContext: 32_768,
        ModelClass: CandidateModelClass.MoE,
        AllowCpuOffload: true);

    private static CandidateModelCatalogOptions CandidateTestCatalog() => new()
    {
        Models = [SmallDense(), BigDense(), MoeModel()],
        BenchmarkContextLadder = CandidateTestLadder
    };

    /// <summary>
    /// The test-side recomputation of the expected per-model verdict via
    /// <see cref="CandidateSizingCalculator.Evaluate"/> (the same inputs the run loop uses)
    /// — the endpoint must agree with this, and with a real run.
    /// </summary>
    private static (BenchmarkCandidateFit Fit, int MaxFitsContext) ExpectedFit(
        CandidateModel model,
        NodeHardwareProfile? profile)
    {
        var assumed = profile is null || profile.TotalFastMemoryMB <= 0;
        var anySurvived = false;
        var maxSurviving = 0;
        foreach (var ctx in CandidateTestLadder)
        {
            var estimate = CandidateSizingCalculator.Evaluate(model, ctx, profile);
            if (estimate.Status is MemoryFitStatus.Fits or MemoryFitStatus.Assumed)
            {
                anySurvived = true;
                if (ctx <= model.NativeContext && ctx > maxSurviving)
                {
                    maxSurviving = ctx;
                }
            }
        }

        var fit = assumed
            ? BenchmarkCandidateFit.Assumed
            : anySurvived
                ? BenchmarkCandidateFit.Fits
                : BenchmarkCandidateFit.Pruned;
        return (fit, maxSurviving);
    }

    [Fact]
    public async Task GetBenchmarkCandidates_MatchesTheRunLoopDerivation()
    {
        // (a) BIG-RAM profile: per-model Fits + the exact maxFitsContext (recomputed via
        // CandidateSizingCalculator.Evaluate), including the nativeContext cap of big:27b
        // (the 32k rung is above its 16k native context, so 16_384 even though it "fits").
        var runner = CreateRunner(Profile(65_536), catalog: CandidateTestCatalog(), modelPresent: true);
        var big = runner.GetBenchmarkCandidates();
        Assert.Single(big.Targets);
        var bigGroup = big.Targets[0];
        Assert.Equal(TargetName, bigGroup.Target);
        Assert.NotNull(bigGroup.Hardware);
        Assert.Equal(3, bigGroup.Candidates.Count);
        foreach (var candidate in new[] { SmallDense(), BigDense(), MoeModel() })
        {
            var row = bigGroup.Candidates.Single(c => c.Model == candidate.Name);
            var (fit, maxCtx) = ExpectedFit(candidate, Profile(65_536));
            Assert.Equal(fit, row.Fit);
            Assert.Equal(maxCtx, row.MaxFitsContext);
            Assert.Equal(candidate.Roles, row.Roles);
            Assert.Equal(candidate.Architecture, row.Architecture);
            Assert.Equal(candidate.TotalParams, row.TotalParams);
            Assert.Equal(candidate.ActiveParams, row.ActiveParams);
            Assert.Equal(candidate.NativeContext, row.NativeContext);
            // Task #169 (item 5): the class metadata passes through from the catalog — the
            // synthetic fixtures carry NO EstimatedWeightsMB (adding one would change their
            // sizing verdicts and break the section-(b) run expectations).
            Assert.Equal(candidate.ModelClass.ToString(), row.ModelClass);
            Assert.Equal(candidate.AllowCpuOffload, row.AllowCpuOffload);
            Assert.Null(row.EstimatedWeightsMB);
        }
        Assert.Equal("Expert", bigGroup.Candidates[0].ModelClass);
        Assert.False(bigGroup.Candidates[0].AllowCpuOffload);
        Assert.Equal("Expert", bigGroup.Candidates[1].ModelClass);
        Assert.False(bigGroup.Candidates[1].AllowCpuOffload);
        Assert.Equal("MoE", bigGroup.Candidates[2].ModelClass);
        Assert.True(bigGroup.Candidates[2].AllowCpuOffload);
        Assert.Equal(BenchmarkCandidateFit.Fits, bigGroup.Candidates[0].Fit);
        Assert.Equal(32_768, bigGroup.Candidates[0].MaxFitsContext);
        // The nativeContext cap: big:27b fits at 32k but its native context is 16k.
        Assert.Equal(BenchmarkCandidateFit.Fits, bigGroup.Candidates[1].Fit);
        Assert.Equal(16_384, bigGroup.Candidates[1].MaxFitsContext);

        // (b) SMALL profile: the large dense model fully prunes (Pruned + maxFitsContext 0)
        // and the endpoint's Pruned set EXACTLY matches the models that a real run only
        // writes as sizing-pruned rows (never measured).
        var smallProfile = Profile(8_192);
        var runnerSmall = CreateRunner(smallProfile, catalog: CandidateTestCatalog(), modelPresent: false);
        var small = runnerSmall.GetBenchmarkCandidates();
        var smallGroup = small.Targets[0];
        foreach (var candidate in new[] { SmallDense(), BigDense(), MoeModel() })
        {
            var row = smallGroup.Candidates.Single(c => c.Model == candidate.Name);
            var (fit, maxCtx) = ExpectedFit(candidate, smallProfile);
            Assert.Equal(fit, row.Fit);
            Assert.Equal(maxCtx, row.MaxFitsContext);
        }
        Assert.Equal(BenchmarkCandidateFit.Pruned, smallGroup.Candidates[1].Fit);
        Assert.Equal(0, smallGroup.Candidates[1].MaxFitsContext);
        Assert.Equal(BenchmarkCandidateFit.Fits, smallGroup.Candidates[0].Fit);
        Assert.Equal(BenchmarkCandidateFit.Fits, smallGroup.Candidates[2].Fit);

        var expectedPrunedInRun = smallGroup.Candidates
            .Where(c => c.Fit == BenchmarkCandidateFit.Pruned)
            .Select(c => c.Model)
            .ToArray();

        // The real run on the same small profile: small:2b + moe:35b measure (2 rungs
        // each = 4 generates — no warmup, task #167; the 32k rung is sizing-pruned);
        // big:27b is sizing-pruned at every rung, never pulled.
        for (var i = 0; i < 4; i++)
        {
            _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        }
        Assert.True(runnerSmall.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runnerSmall);
        var runResults = runnerSmall.GetAllResults().Single();
        var neverMeasured = runResults.Results
            .GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .Where(g => !g.Any(r => r.Reason is null))
            .Select(g => g.Key)
            .ToArray();
        Assert.Equal(expectedPrunedInRun.OrderBy(s => s).ToArray(),
            neverMeasured.OrderBy(s => s).ToArray());
        Assert.Single(expectedPrunedInRun);
        Assert.Equal(0, _handler.RequestsByPath("/api/pull").Count(p =>
            JsonNode.Parse(p.Body) is { } node &&
            string.Equals((node as JsonObject)?["model"]?.GetValue<string>(), BigDense().Name, StringComparison.OrdinalIgnoreCase)));

        // (c) NO profile: every model is Assumed (fail-open) with the full ladder capped by
        // the model's native context.
        var runnerNoProfile = CreateRunner(profile: null, catalog: CandidateTestCatalog(), modelPresent: true);
        var noProfile = runnerNoProfile.GetBenchmarkCandidates();
        var noProfileGroup = noProfile.Targets[0];
        Assert.Null(noProfileGroup.Hardware);
        Assert.All(noProfileGroup.Candidates, c => Assert.Equal(BenchmarkCandidateFit.Assumed, c.Fit));
        Assert.Equal(32_768, noProfileGroup.Candidates[0].MaxFitsContext);
        Assert.Equal(16_384, noProfileGroup.Candidates[1].MaxFitsContext); // native cap
        Assert.Equal(32_768, noProfileGroup.Candidates[2].MaxFitsContext);

        // (d) A target with NO catalog models → an empty (never null) candidates array.
        var runnerEmptyCatalog = CreateRunner(profile: null, catalog: new CandidateModelCatalogOptions
        {
            Models = [],
            BenchmarkContextLadder = CandidateTestLadder
        });
        var empty = runnerEmptyCatalog.GetBenchmarkCandidates();
        Assert.Single(empty.Targets);
        Assert.NotNull(empty.Targets[0].Candidates);
        Assert.Empty(empty.Targets[0].Candidates);
    }

    [Fact]
    public void GetBenchmarkCandidates_CuratedDefaultCatalog_ReturnsAll7ModelsWithExactClassMetadata()
    {
        // Task #169 (AC1, item 6): the DEFAULT catalog (all 7 curated models) on a 96 GB
        // node — every model reports its exact (ModelClass, AllowCpuOffload, EstimatedWeightsMB)
        // pin in catalog order, and at 0.92 × 98_304 = 90_439.68 MiB budget the heaviest 256k
        // residency (Ornith: 71_583 + 6_656 KV = 78_239 MiB) still fits, so all 7 = Fits @ 262_144.
        var runner = CreateRunner(Profile(98_304), catalog: new CandidateModelCatalogOptions(), modelPresent: true);
        var response = runner.GetBenchmarkCandidates();
        Assert.Single(response.Targets);
        var group = response.Targets[0];
        var rows = group.Candidates;
        Assert.Equal(7, rows.Count);

        // Exact (ModelClass string, AllowCpuOffload, EstimatedWeightsMB) pins in catalog order.
        Assert.Equal(("MoE", true, 23_859.0), (rows[0].ModelClass, rows[0].AllowCpuOffload, rows[0].EstimatedWeightsMB));
        Assert.Equal(("MoE", true, 40_346.0), (rows[1].ModelClass, rows[1].AllowCpuOffload, rows[1].EstimatedWeightsMB));
        Assert.Equal(("MoE", true, 22_528.0), (rows[2].ModelClass, rows[2].AllowCpuOffload, rows[2].EstimatedWeightsMB));
        Assert.Equal(("MoE", true, 40_960.0), (rows[3].ModelClass, rows[3].AllowCpuOffload, rows[3].EstimatedWeightsMB));
        Assert.Equal(("Expert", false, 12_288.0), (rows[4].ModelClass, rows[4].AllowCpuOffload, rows[4].EstimatedWeightsMB));
        Assert.Equal(("Expert", false, 18_944.0), (rows[5].ModelClass, rows[5].AllowCpuOffload, rows[5].EstimatedWeightsMB));
        Assert.Equal(("Expert", false, 71_583.0), (rows[6].ModelClass, rows[6].AllowCpuOffload, rows[6].EstimatedWeightsMB));

        // All 7 fit on the 96 GB node at the full 256k ladder rung (recomputed truth via the
        // shared CandidateSizingCalculator derivation — the Ornith anchor-verified 78_239 MiB
        // 256k residency is the heaviest and is still under the 90_439.68 MiB budget).
        Assert.All(rows, r => Assert.Equal(BenchmarkCandidateFit.Fits, r.Fit));
        Assert.All(rows, r => Assert.Equal(262_144, r.MaxFitsContext));
    }

    // ------------------------------------------------------------------
    //  Task #167: empirical ladder, Modelfile cap removal, fixed-size prompt, cold-start
    //  eviction (evict → measure)
    // ------------------------------------------------------------------

    /// <summary>
    /// A 2B dense model with the curated NativeContext (262_144) that FITS at every ladder
    /// rung on a 96 GB fast-memory node (weights 953.7 MiB + KV 73_728 MiB @ 256k = 74_681.7
    /// MiB &lt; 92% × 98_304 = 90_439.7 MiB).
    /// </summary>
    private static CandidateModel WideModel(string name) => new(
        name, [UsageRole.AgenticChat], "Dense",
        TotalParams: 2_000_000_000, ActiveParams: null,
        "q4", 4, Layers: 36, KvHeads: 8, HeadDim: 256,
        NativeContext: 262_144, DefaultContext: 131_072);

    [Fact]
    public void DefaultLadder_IsTheEmpiricalBreakpoints()
    {
        // A1 (task #167): the old ladder { 16384, 32768, 49152, 65536, 131072, 184320,
        // 262144 } lacked the 96k/160k/192k decision boundaries and carried the 16k start /
        // 180k rung that match no empirical breakpoint.
        var ladder = new CandidateModelCatalogOptions().BenchmarkContextLadder;
        Assert.Equal([32_768, 49_152, 65_536, 98_304, 131_072, 163_840, 196_608, 262_144],
            ladder.ToArray());
        // 131072 (128k) is the downstream role-eligibility floor — explicitly present.
        Assert.Contains(131_072, ladder);
    }

    [Fact]
    public async Task ModelfileNumCtx_DoesNotCapTheLadder_UpToNativeContext()
    {
        // A2 (task #167): the Modelfile PARAMETER num_ctx (here 65536 — the exact shape
        // ModelMetadataParser.ParseNumCtx matches) used to cap effectiveMaxCtx and SILENTLY
        // produced NO rows above 65536. Now only candidate.NativeContext (refined downward
        // by the live GGUF context_length — absent here) caps the ladder.
        var wide = WideModel("wide:2b");
        var options = new CandidateModelCatalogOptions
        {
            Models = [wide],
            BenchmarkContextLadder = [32_768, 65_536, 98_304, 131_072, 163_840, 196_608, 262_144]
        };

        // The /api/show response declares Modelfile num_ctx 65536 and NO model_info (no
        // GGUF cap → NativeContext 262_144 governs).
        _catalogClient
            .Setup(c => c.GetShowAsync(TargetUrl, wide.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject
            {
                ["parameters"] = "num_ctx 65536\nnum_predict 2048\n"
            });

        var runner = CreateRunner(Profile(98_304), catalog: options, modelPresent: false);
        for (var i = 0; i < 7; i++)
        {
            _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        }

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Succeeded, results.Status);

        // Measured rows exist for ALL rungs up to 262144 — including 131072/163840/196608
        // and 262144, the ones the old Modelfile cap silently dropped.
        Assert.Equal([32_768, 65_536, 98_304, 131_072, 163_840, 196_608, 262_144],
            results.Results.Select(r => r.NumCtx).ToArray());
        Assert.All(results.Results, r => Assert.True(r.Viable));
        Assert.Equal(7, _handler.GenerateCallCount);

        // Every measurement body carries its rung's FULL options.num_ctx (ascending).
        var bodies = _handler.RequestsByPath("/api/generate")
            .Select(r => JsonNode.Parse(r.Body) as JsonObject)
            .ToList();
        Assert.Equal(7, bodies.Count);
        for (var i = 0; i < bodies.Count; i++)
        {
            Assert.Equal(results.Results[i].NumCtx, bodies[i]?["options"]?["num_ctx"]?.GetValue<int>());
        }
    }

    [Fact]
    public async Task GgufContextLength_BelowNativeContext_CapsTheLadder()
    {
        // A3 (task #167): the live GGUF architectural ceiling
        // (model_info["*.context_length"]) is the ONLY cap signal — here 98304 below
        // NativeContext 262_144 → NO measurement rows above 98304 (GenerateCallCount =
        // surviving rungs only). The control candidate (show → null, fail-soft) is NOT
        // capped: NativeContext governs and the full ladder is measured.
        var capped = WideModel("capped:2b");
        var uncapped = WideModel("uncapped:2b");
        var ladder = new CandidateModelCatalogOptions().BenchmarkContextLadder; // the 8 empirical rungs
        var options = new CandidateModelCatalogOptions
        {
            Models = [capped, uncapped],
            BenchmarkContextLadder = ladder
        };

        _catalogClient
            .Setup(c => c.GetShowAsync(TargetUrl, capped.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject
            {
                ["model_info"] = new JsonObject { ["test.context_length"] = 98_304 }
            });
        // uncapped:2b falls through to the constructor default → show null (fail-soft).

        var runner = CreateRunner(Profile(98_304), catalog: options, modelPresent: false);
        // capped: 4 rungs (≤ 98304) + uncapped: all 8 rungs = 12 measurement generates.
        for (var i = 0; i < 12; i++)
        {
            _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        }

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Succeeded, results.Status);
        Assert.Equal(12, _handler.GenerateCallCount);

        var cappedRows = results.Results.Where(r => r.Model == capped.Name).ToArray();
        Assert.Equal([32_768, 49_152, 65_536, 98_304], cappedRows.Select(r => r.NumCtx).ToArray());
        Assert.All(cappedRows, r => Assert.True(r.Viable));

        // The control candidate is NOT capped: it measures the full ladder up to 262144.
        var uncappedRows = results.Results.Where(r => r.Model == uncapped.Name).ToArray();
        Assert.Equal(ladder.ToArray(), uncappedRows.Select(r => r.NumCtx).ToArray());
        Assert.Contains(uncappedRows, r => r.NumCtx == 262_144);
    }

    [Fact]
    public void BuildFillerPrompt_IsFixedSize_AndRungInvariant()
    {
        // A4 (task #167): the old BuildFillerPrompt(262144) built a ~262k-token
        // multi-megabyte string (whose prefill alone exceeded the 5-minute GenerateTimeout).
        // Now: the SAME bounded (~768 estimated tokens) real-text prompt at every rung.
        var runner = CreateRunner(Profile(32_768), modelPresent: true);

        var small = runner.BuildFillerPrompt(32_768);
        var large = runner.BuildFillerPrompt(262_144);

        // Byte-identical across rungs.
        Assert.Equal(small, large);

        // Bounded: ~768 estimated tokens (4 chars = 1 token in the FixedTokenEstimator),
        // inside the ~512–1,024 window, far below the old prompt size, with the benchmark
        // suffix.
        var estimatedTokens = new FixedTokenEstimator().EstimateTokens([large]);
        Assert.InRange(estimatedTokens, 512, 1_024);
        Assert.True(large.Length < 16_000, $"filler prompt unbounded: {large.Length} chars");
        Assert.EndsWith("Summarize the above text in one paragraph. Begin now:", large);

        // O(1) build: a reintroduced O(numCtx) scaling must not slip back in.
        var sw = Stopwatch.StartNew();
        _ = runner.BuildFillerPrompt(262_144);
        Assert.True(sw.ElapsedMilliseconds < 50, $"filler build took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Eviction_IsSentBeforeEveryMeasuredRung_AndThePreviousCandidateBetweenThem()
    {
        // A5 (task #167, design D3): the old code NEVER called UnloadModelAsync — Ollama's
        // default 5-minute keep-alive kept the previous candidate's weights + KV resident
        // during the next candidate's cold loads (distorted offloading / spurious OOMs).
        // Now: one eviction immediately before EVERY measured rung of the current
        // candidate, plus one eviction of the previously measured candidate before a new
        // candidate's first rung.
        var evictionOrder = new List<string>();
        _catalogClient
            .Setup(c => c.UnloadModelAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string?, string, CancellationToken>((url, model, ct) => evictionOrder.Add(model ?? ""))
            .ReturnsAsync(true);

        var runner = CreateRunner(Profile(65_536), catalog: TwoModelCatalog(), modelPresent: false);
        // Two models × 2 measured rungs = 4 measurement generates (no warmup).
        for (var i = 0; i < 4; i++)
        {
            _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        }

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Succeeded, results.Status);
        Assert.Equal(4, _handler.GenerateCallCount);
        Assert.All(results.Results, r => Assert.True(r.Viable));

        // bench:27b (measured FIRST, 2 rungs): 2 per-rung + 1 cross-candidate (it is
        // evicted before small:4b's first rung) = 3. small:4b (measured SECOND, 2 rungs):
        // 2 per-rung = 2.
        _catalogClient.Verify(
            c => c.UnloadModelAsync(TargetUrl, TestModel.Name, It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        _catalogClient.Verify(
            c => c.UnloadModelAsync(TargetUrl, "small:4b", It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // Ordering: [bench:27b, bench:27b, (cross) bench:27b, small:4b, small:4b] — the
        // cross-candidate call for small:4b carries bench:27b's name, and every measured
        // rung is preceded by an eviction of the model being measured.
        Assert.Equal(
            [TestModel.Name, TestModel.Name, TestModel.Name, "small:4b", "small:4b"],
            evictionOrder);
    }

    [Fact]
    public async Task Eviction_FailSoft_RunStillCompletes()
    {
        // A5 (task #167, design D1): an eviction that does not land (the catalog client
        // reports false) NEVER fails the run — it only distorts VRAM residency for that
        // rung; the next rung's own eviction re-attempts for the same model.
        _catalogClient
            .Setup(c => c.UnloadModelAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var runner = CreateRunner(Profile(32_768), modelPresent: true);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 32k

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Succeeded, results.Status);
        Assert.Equal(2, results.Results.Count);
        Assert.All(results.Results, r => Assert.True(r.Viable));
        // Both rungs still got their evict-before-measure call.
        _catalogClient.Verify(
            c => c.UnloadModelAsync(TargetUrl, TestModel.Name, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ------------------------------------------------------------------
    //  Task #174: live progress, streaming pull, incremental partial rows
    // ------------------------------------------------------------------

    /// <summary>
    /// Polls (10 ms ticks, deterministic bounds — no sleeps above 10 ms) until the sync
    /// <paramref name="condition"/> holds; asserts the named observation happened in time.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 8_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), $"timed out waiting for: {what}");
    }

    [Fact]
    public async Task StreamingPull_PublishesLiveDownloadingModelProgress_WithPerDigestAggregation()
    {
        // The daemon stream: manifest + two layer digests (aaa: 10 MB with 1 MB done,
        // bbb: 1 MB fully done), then success. The handler stalls AFTER the four progress
        // lines, so the test can assert the LIVE DownloadingModel progress mid-stream.
        var runner = CreateRunner(Profile(32_768), modelPresent: false);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetPullGate(gate);
        _handler.EnqueuePullResponse(
            "{\"status\":\"pulling manifest\"}\n" +
            "{\"status\":\"verifying sha256 digest\",\"digest\":\"sha256:aaa\",\"total\":10485760,\"completed\":0}\n" +
            "{\"status\":\"pulling 34%\",\"digest\":\"sha256:aaa\",\"total\":10485760,\"completed\":1048576}\n" +
            "{\"status\":\"pulling 90%\",\"digest\":\"sha256:bbb\",\"total\":1048576,\"completed\":1048576}\n" +
            "{\"status\":\"success\"}",
            linesBeforeGate: 4);
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        try
        {
            // The gate holds the stream after the four head lines — wait until ALL of them
            // have been consumed (the per-digest aggregation reached its final pre-gate value).
            await WaitUntilAsync(
                () => runner.GetAllResults().FirstOrDefault() is { Progress.TotalBytes: 11_534_336, Progress.CompletedBytes: 2_097_152 },
                "the aggregated DownloadingModel progress (all four pre-gate lines consumed)");

            // Per-digest aggregation (ollama-CLI style): Σ of the LATEST per-digest values →
            // completed 2,097,152 / total 11,534,336 → 18% (floor of 18.18…).
            var progress = runner.GetAllResults().Single().Progress!;
            Assert.Equal(BenchmarkPhase.DownloadingModel, progress.Phase);
            Assert.Equal(TestModel.Name, progress.Model);
            Assert.Equal("pulling 90%", progress.Detail);
            Assert.Equal(2_097_152, progress.CompletedBytes);
            Assert.Equal(11_534_336, progress.TotalBytes);
            Assert.Equal("Downloading bench:27b (18% · 2 MB / 11 MB)", progress.Message);
        }
        finally
        {
            _handler.ReleasePullGate();
        }

        await WaitUntilRunCompleteAsync(runner);
        var results = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Succeeded, results.Status);
        Assert.Null(results.Progress); // a terminal row carries no live progress
        Assert.Equal(2, results.Results.Count);
    }

    [Fact]
    public async Task PullErrorLine_IsPullFailureRow_AndClearsProgress()
    {
        // A daemon {"error": "…"} line anywhere in the NDJSON stream fails the pull → the
        // target ends Failed with the error:pull row and NO live progress.
        var runner = CreateRunner(Profile(32_768), modelPresent: false);
        _handler.EnqueuePullResponse(
            "{\"status\":\"pulling manifest\"}\n" +
            "{\"error\":\"model not found on registry\"}");

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Failed, results.Status);
        var row = results.Results.Single();
        Assert.False(row.Viable);
        Assert.Equal("error:pull", row.Reason);
        Assert.Null(results.Progress);
    }

    [Fact]
    public async Task SingleLinePullResponse_IsValidOneLineNdJson_AndSucceeds()
    {
        // The pre-#174 daemon shape — a single-line {"status":"success"} body — IS valid
        // one-line NDJSON and terminates the streaming loop exactly as before.
        var runner = CreateRunner(Profile(32_768), modelPresent: false);
        _handler.EnqueuePullResponse("{\"status\":\"success\"}");
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        await WaitUntilRunCompleteAsync(runner);

        var results = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Succeeded, results.Status);
        Assert.Equal(2, results.Results.Count);
        Assert.All(results.Results, r => Assert.True(r.Viable));
    }

    [Fact]
    public async Task PhaseTransitions_TwoTargets_QueuedCheckingDownloadingBenchmarking_TerminalNull()
    {
        // The full phase ladder, observed deterministically: the run drains targets
        // sequentially, so while node-a is frozen at a gated point, node-b stays in the
        // Queued phase written by TryStartRun.
        const string urlA = "http://node-a:11434";
        const string urlB = "http://node-b:11434";
        var options2 = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "node-a", Url = urlA },
                new OllamaTargetOptions { Name = "node-b", Url = urlB }
            ]
        };

        _fleetHardware.Setup(f => f.GetProfile(It.IsAny<string>())).Returns(Profile(32_768));

        // node-a: /api/tags BLOCKS on gateA → the run freezes in the CheckingModel phase.
        var gateA = new TaskCompletionSource<JsonArray>(TaskCreationOptions.RunContinuationsAsynchronously);
        _catalogClient
            .Setup(c => c.GetTagsAsync(urlA, It.IsAny<CancellationToken>()))
            .Returns(() => gateA.Task);

        // node-a's pull: the manifest line, then a mid-stream STALL (DownloadingModel gate).
        var pullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetPullGate(pullGate);
        _handler.EnqueuePullResponse(
            "{\"status\":\"pulling manifest\"}\n" +
            "{\"status\":\"success\"}",
            linesBeforeGate: 1);

        // node-a's rung-1 measurement generate is gated → freezes in Benchmarking rung 1.
        _handler.EnqueueGenerateGate(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        // 4 measurement responses: node-a 16k/32k, then node-b 16k/32k (node-b pulls the
        // default single-line success body — its NDJSON compatibility is covered there).
        for (var i = 0; i < 4; i++)
        {
            _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000));
        }

        var runner = new MicroBenchmarkRunner(
            MsOptions.Create(new CandidateModelCatalogOptions
            {
                Models = [TestModel],
                BenchmarkContextLadder = [16_384, 32_768]
            }),
            _fleetHardware.Object,
            new FixedTokenEstimator(),
            new TestOptionsMonitor<OllamaRouterOptions>(options2),
            _catalogClient.Object,
            new HttpClient(_handler),
            _lifetime.Object,
            NullLogger<MicroBenchmarkRunner>.Instance,
            Path.Combine(_stateDirectory, "benchmarks.json"),
            Path.Combine(_stateDirectory, "targets-speed.json"));

        BenchmarkTargetResult? A() => runner.GetAllResults().FirstOrDefault(r => r.Target == "node-a");
        BenchmarkTargetResult? B() => runner.GetAllResults().FirstOrDefault(r => r.Target == "node-b");

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        try
        {
            // node-a freezes in CheckingModel (the /api/tags presence check is gated);
            // node-b is still Queued (no work started for it yet).
            await WaitUntilAsync(() => A()?.Progress?.Phase == BenchmarkPhase.CheckingModel, "node-a to reach CheckingModel");
            Assert.Equal($"Checking {TestModel.Name}…", A()!.Progress!.Message);
            Assert.Equal(TestModel.Name, A()!.Progress!.Model);
            Assert.Equal(BenchmarkPhase.Queued, B()!.Progress!.Phase);
            Assert.Equal("Queued", B()!.Progress!.Message);
            Assert.Null(B()!.Progress!.Model);

            // Release the tags check → the streaming pull begins and STALLS after the
            // manifest line (no bytes aggregated yet → the detail-only download message).
            gateA.TrySetResult(new JsonArray());
            await WaitUntilAsync(() => A()?.Progress?.Phase == BenchmarkPhase.DownloadingModel, "node-a to reach DownloadingModel");
            Assert.Equal("pulling manifest", A()!.Progress!.Detail);
            Assert.Equal(0, A()!.Progress!.CompletedBytes);
            Assert.Equal(0, A()!.Progress!.TotalBytes);
            Assert.Equal($"Downloading {TestModel.Name} (pulling manifest)", A()!.Progress!.Message);

            // Release the pull → node-a inspects, self-evicts and reaches Benchmarking
            // rung 1, where the first measurement generate is gated.
            _handler.ReleasePullGate();
            await WaitUntilAsync(() => A()?.Progress?.Phase == BenchmarkPhase.Benchmarking, "node-a to reach Benchmarking");
            var pA = A()!.Progress!;
            Assert.Equal(16_384, pA.NumCtx);
            Assert.Equal(0, pA.CandidateIndex);
            Assert.Equal(1, pA.CandidateCount);
            Assert.Equal(0, pA.RungIndex);
            Assert.Equal(2, pA.RungCount);
            Assert.Equal($"Benchmarking {TestModel.Name} @ 16384 ctx (model 1/1, rung 1/2)", pA.Message);
        }
        finally
        {
            gateA.TrySetResult(new JsonArray());
            _handler.ReleasePullGate();
            _handler.ReleaseGenerateGate();
        }

        await WaitUntilRunCompleteAsync(runner);

        var all = runner.GetAllResults();
        Assert.Equal(2, all.Count);
        Assert.All(all, r => Assert.Equal(BenchmarkRunStatus.Succeeded, r.Status));
        Assert.All(all, r => Assert.Null(r.Progress)); // terminal rows carry no live progress
        Assert.All(all, r => Assert.Equal(2, r.Results.Count));
    }

    [Fact]
    public async Task IncrementalPartialResults_AreVisibleWhileTargetStillRunning()
    {
        // The measured rows stream to the polling clients LIVE: rung 1's row is visible
        // while rung 2 is still in flight (the target is Running with a partial snapshot).
        var runner = CreateRunner(Profile(32_768), modelPresent: true);

        // Two one-shot gates: rung 1 and rung 2 of the same candidate are gated in sequence.
        _handler.EnqueueGenerateGate(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _handler.EnqueueGenerateGate(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 16k
        _handler.EnqueueGenerateResponse(GenerateJson(64, 2_000_000_000)); // 32k

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        try
        {
            // Rung 1 in flight (gated): nothing measured yet → the partial is still empty.
            await WaitUntilAsync(() => _handler.GenerateCallCount == 1, "rung 1 to start");
            var whileRung1 = runner.GetAllResults().Single();
            Assert.Equal(BenchmarkRunStatus.Running, whileRung1.Status);
            Assert.Empty(whileRung1.Results);
            Assert.Equal(BenchmarkPhase.Benchmarking, whileRung1.Progress!.Phase);
            Assert.Equal(16_384, whileRung1.Progress!.NumCtx);
            Assert.Equal(0, whileRung1.Progress!.RungIndex);

            // Release rung 1 → its row is published IMMEDIATELY (partial snapshot #1) and
            // rung 2 starts (gated).
            _handler.ReleaseGenerateGate();
            await WaitUntilAsync(() => _handler.GenerateCallCount == 2, "rung 2 to start");

            var whileRung2 = runner.GetAllResults().Single();
            Assert.Equal(BenchmarkRunStatus.Running, whileRung2.Status);
            var partialRow = Assert.Single(whileRung2.Results); // the 16k row is visible LIVE
            Assert.Equal(16_384, partialRow.NumCtx);
            Assert.True(partialRow.Viable);
            Assert.Null(partialRow.Reason);
            Assert.Equal(1, whileRung2.Progress!.RungIndex); // rung 2 in flight
            Assert.Equal(32_768, whileRung2.Progress!.NumCtx);
        }
        finally
        {
            _handler.ReleaseGenerateGate();
        }

        await WaitUntilRunCompleteAsync(runner);
        var final = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Succeeded, final.Status);
        Assert.Equal(2, final.Results.Count);
        Assert.Null(final.Progress);
    }

    [Fact]
    public async Task Cancellation_MidPull_MarksTargetCancelled_AndClearsProgress()
    {
        // Cancelling the host while the pull stream is in flight: the target must reach
        // Cancelled (not stuck in Running) and its live progress entry must be dropped.
        var statePath = Path.Combine(_stateDirectory, "benchmarks.json");
        var capturingLifetime = new CapturingLifetime();
        _fleetHardware.Setup(f => f.GetProfile(It.IsAny<string>())).Returns(Profile(32_768));

        var pullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetPullGate(pullGate);
        _handler.EnqueuePullResponse(
            "{\"status\":\"pulling manifest\"}\n" +
            "{\"status\":\"success\"}",
            linesBeforeGate: 1);

        var runner = new MicroBenchmarkRunner(
            MsOptions.Create(new CandidateModelCatalogOptions
            {
                Models = [TestModel],
                BenchmarkContextLadder = [16_384, 32_768]
            }),
            _fleetHardware.Object,
            new FixedTokenEstimator(),
            new TestOptionsMonitor<OllamaRouterOptions>(_options),
            _catalogClient.Object,
            new HttpClient(_handler),
            capturingLifetime,
            NullLogger<MicroBenchmarkRunner>.Instance,
            statePath,
            Path.Combine(_stateDirectory, "targets-speed.json"));

        Assert.True(runner.TryStartRun(target: null, roles: null, model: null));
        try
        {
            await WaitUntilAsync(
                () => runner.GetAllResults().FirstOrDefault()?.Progress?.Phase == BenchmarkPhase.DownloadingModel,
                "the DownloadingModel phase");

            // Simulate the host stopping while the pull stream is mid-flight.
            capturingLifetime.StopApplication();
        }
        finally
        {
            _handler.ReleasePullGate();
        }

        await WaitUntilAsync(
            () => runner.GetAllResults().FirstOrDefault() is { } r && r.Status != BenchmarkRunStatus.Running,
            "the terminal state");

        var result = runner.GetAllResults().Single();
        Assert.Equal(BenchmarkRunStatus.Cancelled, result.Status);
        Assert.Null(result.Progress); // the cancelled target's live progress is cleared
        Assert.Contains("\"Cancelled\"", File.ReadAllText(statePath));
    }
}
