using OllamaRouter.Tests.TestHelpers;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Endpoints;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Models;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Endpoint tests for <c>POST /api/router/models/pull</c> (task #125): the pre-stream
/// validation (400 invalid model / aliasContext &lt;= 0 / alias collision / non-Ollama
/// target, 404 unknown target, 409 disabled / offline) BEFORE any stream starts, the 200
/// streaming NDJSON event sequence (started → progress* → [alias_started → alias_created]
/// → success; the <c>present</c> short-circuit; the terminal <c>error</c> event with
/// stage "pull"), the daemon contract (<c>POST /api/pull</c> stream:true,
/// <c>POST /api/create</c> with from + parameters.num_ctx), the post-success catalog
/// refresh, and the ROUTE PRECEDENCE regression (the literal pull route outranks the
/// POST /api/router/models/{*tail} preload catch-all while the preload route still works).
/// The REAL ModelPullService runs against a fake in-process Ollama daemon
/// (<see cref="FakeOllamaDaemonHandler"/> via the "ModelPull" HttpClient).
/// </summary>
public class RouterModelPullEndpointTests
{
    private const string DaemonUrl = "http://127.0.0.1:11435";
    private const string OpenAiUrl = "http://127.0.0.1:11436";
    private const string Model = "llama3.2:13b-q4_K_M";

    /// <summary>A fake in-process Ollama daemon behind the "ModelPull" HttpClient: scripts the
    /// <c>/api/pull</c> NDJSON lines, the <c>/api/create</c> response, and records both
    /// request bodies for the wire-contract assertions.</summary>
    private sealed class FakeOllamaDaemonHandler : HttpMessageHandler
    {
        /// <summary>The NDJSON lines of the <c>POST /api/pull</c> response (scripted).</summary>
        public List<string> PullLines { get; set; } =
        [
            """{"status":"pulling 34%","digest":"sha256:abc","total":8123456789,"completed":2761975308}""",
            """{"status":"success"}"""
        ];

        /// <summary>Whether the <c>POST /api/pull</c> response itself is a non-2xx error.</summary>
        public (HttpStatusCode Status, string Body)? PullErrorResponse { get; set; }

        /// <summary>The <c>POST /api/create</c> response (status + JSON body).</summary>
        public (HttpStatusCode Status, string Body) CreateResponse { get; set; } =
            (HttpStatusCode.OK, """{"status":"success"}""");

        /// <summary>The raw <c>POST /api/pull</c> request body (wire-contract assertion).</summary>
        public string? PullRequestBody { get; private set; }

        /// <summary>The raw <c>POST /api/create</c> request body (wire-contract assertion).</summary>
        public string? CreateRequestBody { get; private set; }

        /// <summary>
        /// When set, the <c>POST /api/pull</c> response is a GATED stream: the FIRST
        /// <see cref="PullLines"/> entry is served immediately, then the read HOLDS (the pull
        /// is "in flight" — a stand-in for a real GB-scale pull) until this TCS is set or the
        /// request token cancels; the remaining lines follow. The LIVE-progress regression
        /// test (AC2): a progress line must reach the client WHILE the pull is still in
        /// flight — which fails if the service buffers the progress events.
        /// </summary>
        public TaskCompletionSource? PullStreamGate { get; set; }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/pull")
            {
                PullRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
                if (PullErrorResponse is { } error)
                {
                    return new HttpResponseMessage(error.Status)
                    {
                        Content = new StringContent(error.Body, Encoding.UTF8, "application/json")
                    };
                }

                if (PullStreamGate is { } gate)
                {
                    var first = Encoding.UTF8.GetBytes(PullLines[0] + "\n");
                    var rest = Encoding.UTF8.GetBytes(string.Join('\n', PullLines.Skip(1)) + "\n");
                    var gated = new StreamContent(new GatedNdjsonStream(first, rest, gate.Task, cancellationToken));
                    gated.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = gated };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Join('\n', PullLines) + '\n', Encoding.UTF8, "application/x-ndjson")
                };
            }

            if (path == "/api/create")
            {
                CreateRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(CreateResponse.Status)
                {
                    Content = new StringContent(CreateResponse.Body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":"not found"}""", Encoding.UTF8, "application/json")
            };
        }

        /// <summary>
        /// The gated /api/pull response stream: serves <paramref name="first"/> (the first
        /// progress line) immediately, then the synchronous <see cref="Stream.Read"/> HOLDs
        /// (the StreamReader's async read runs it off the response path) until the gate opens
        /// or the token cancels (an in-flight pull holding the connection), then serves
        /// <paramref name="rest"/> and EOFs.
        /// </summary>
        private sealed class GatedNdjsonStream(byte[] first, byte[] rest, Task gate, CancellationToken ct) : Stream
        {
            private readonly byte[] _first = first;
            private readonly byte[] _rest = rest;
            private readonly Task _gate = gate;
            private readonly CancellationToken _ct = ct;
            private int _phase; // 0 = first chunk, 1 = gated (in flight), 2 = rest chunk, 3 = EOF
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

            public override void Flush() => throw new NotSupportedException();

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
                            var n = Math.Min(count, _first.Length - _offset);
                            Buffer.BlockCopy(_first, _offset, buffer, offset, n);
                            _offset += n;
                            if (_offset >= _first.Length)
                            {
                                // The first line is fully out — the NEXT read call will hold
                                // the pull "in flight" (phase 1).
                                _phase = 1;
                                _offset = 0;
                            }

                            return n;
                        }

                        case 1:
                            // Hold the "in-flight pull" open until the test opens the gate (a
                            // real GB-scale pull holds here for minutes); a token cancellation
                            // ends the stream with an OCE (the silent-cancellation path).
                            _gate.Wait(_ct);
                            _phase = 2;
                            continue;

                        case 2:
                        {
                            var n = Math.Min(count, _rest.Length - _offset);
                            Buffer.BlockCopy(_rest, _offset, buffer, offset, n);
                            _offset += n;
                            if (_offset >= _rest.Length)
                            {
                                _phase = 3;
                            }

                            return n;
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

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            WebApplication app, HttpClient client, FakeOllamaDaemonHandler daemon,
            Mock<IModelCatalogCacheService> catalogCache, Mock<ITargetLivenessService> liveness)
        {
            App = app;
            Client = client;
            Daemon = daemon;
            CatalogCache = catalogCache;
            Liveness = liveness;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public FakeOllamaDaemonHandler Daemon { get; }
        public Mock<IModelCatalogCacheService> CatalogCache { get; }
        public Mock<ITargetLivenessService> Liveness { get; }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static OllamaRouterOptions OllamaOptions() => new()
    {
        Targets =
        [
            new OllamaTargetOptions { Name = "Local", Url = DaemonUrl, Enabled = true, Type = TargetType.Ollama }
        ],
        Models = new Dictionary<string, ModelThresholds>(),
        DynamicContext = new DynamicContextOptions { Enabled = true },
        CloudOverflow = new CloudOverflowOptions { Enabled = false }
    };

    private static async Task<Harness> CreateTestAppAsync(
        OllamaRouterOptions options, bool modelPresent = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        var daemon = new FakeOllamaDaemonHandler();
        builder.Services.AddHttpClient("ModelPull", client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => daemon);

        var catalogCache = new Mock<IModelCatalogCacheService>(MockBehavior.Strict);
        catalogCache
            .Setup(c => c.ModelExistsAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelPresent);
        catalogCache
            .Setup(c => c.RefreshAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        liveness.SetupGet(l => l.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddMinutes(1));

        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(new TestOptionsMonitor<OllamaRouterOptions>(options));
        builder.Services.AddSingleton<IModelCatalogCacheService>(catalogCache.Object);
        builder.Services.AddSingleton(liveness.Object);
        builder.Services.AddSingleton<IModelPullService>(sp => new ModelPullService(
            sp.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>(),
            sp.GetService<IModelCatalogCacheService>(),
            sp.GetService<ITargetLivenessService>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("ModelPull"),
            NullLogger<ModelPullService>.Instance));

        var app = builder.Build();
        app.MapOllamaRouterApiEndpoints();
        await app.StartAsync();

        return new Harness(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, daemon, catalogCache, liveness);
    }

    private static StringContent PullBody(object model, object? target = null, object? aliasContext = null)
    {
        var payload = new Dictionary<string, object?> { ["model"] = model };
        if (target is not null)
        {
            payload["target"] = target;
        }

        if (aliasContext is not null)
        {
            payload["aliasContext"] = aliasContext;
        }

        return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    }

    /// <summary>The NDJSON lines of a 200 pull response as parsed (event, target, ...) objects.</summary>
    private static async Task<List<JsonObject>> ReadNdjsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return body.Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => (JsonObject)JsonNode.Parse(line)!)
            .ToList();
    }

    // ------------------------------------------------------------------
    //  Pre-stream validation (400 / 404 / 409) — no stream starts
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("a:b:c")]
    [InlineData("llama3:")]
    public async Task Pull_InvalidModelReference_Returns400BeforeTheStream(object? model)
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody(model!));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(harness.Daemon.PullRequestBody); // the daemon was never called
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains("model reference", body["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-8192)]
    public async Task Pull_AliasContextNotPositive_Returns400BeforeTheStream(int aliasContext)
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody(Model, aliasContext: aliasContext));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(harness.Daemon.PullRequestBody);
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains("aliasContext", body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pull_ReferenceEndingWithTheSameSuffix_StillPulls_WithADistinctAlias()
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());

        // The alias always appends '-{suffix}' to the effective tag, so a reference that ALREADY
        // ends with the suffix still yields a DISTINCT alias (no collision, the pull proceeds).
        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody("llama3:latest-8k", aliasContext: 8192));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = await ReadNdjsonAsync(response);
        var createRequest = (JsonObject)JsonNode.Parse(harness.Daemon.CreateRequestBody!)!;
        Assert.Equal("llama3:latest-8k-8k", createRequest["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pull_NonOllamaTarget_Returns400BeforeTheStream()
    {
        var options = OllamaOptions();
        options.Targets.Add(new OllamaTargetOptions { Name = "CloudApi", Url = OpenAiUrl, Enabled = true, Type = TargetType.OpenAI });

        await using var harness = await CreateTestAppAsync(options);

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody(Model, target: "CloudApi"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(harness.Daemon.PullRequestBody);
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains("Ollama-protocol", body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pull_UnknownTarget_Returns404BeforeTheStream()
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody(Model, target: "ghost"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(harness.Daemon.PullRequestBody);
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains("ghost", body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pull_DisabledTarget_Returns409BeforeTheStream()
    {
        var options = OllamaOptions();
        options.Targets[0] = new OllamaTargetOptions { Name = "Local", Url = DaemonUrl, Enabled = false, Type = TargetType.Ollama };

        await using var harness = await CreateTestAppAsync(options);

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody(Model, target: "Local"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(harness.Daemon.PullRequestBody);
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains("disabled", body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pull_OfflineTarget_Returns409BeforeTheStream()
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());
        harness.Liveness
            .Setup(l => l.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Offline);

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody(Model, target: "Local"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(harness.Daemon.PullRequestBody);
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains("offline", body["error"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    //  200 streaming NDJSON (AC2, AC3)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Pull_HappyPath_StreamsStartedProgressAliasSuccess_AndRefreshesCatalog()
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody(Model, target: "Local", aliasContext: 8192));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        var lines = await ReadNdjsonAsync(response);

        // The exact wire sequence: started → progress → alias_started → alias_created → success.
        var events = lines.Select(line => line["event"]!.GetValue<string>()).ToList();
        Assert.Equal(["started", "progress", "alias_started", "alias_created", "success"], events);

        var started = lines[0]!;
        Assert.Equal("Local", started["target"]!.GetValue<string>());
        Assert.Equal(Model, started["model"]!.GetValue<string>());

        var progress = lines[1]!;
        Assert.Equal("pulling 34%", progress["status"]!.GetValue<string>());
        Assert.Equal("sha256:abc", progress["digest"]!.GetValue<string>());
        Assert.Equal(8_123_456_789L, progress["totalBytes"]!.GetValue<long>());
        Assert.Equal(2_761_975_308L, progress["completedBytes"]!.GetValue<long>());

        var aliasStarted = lines[2]!;
        Assert.Equal("llama3.2:13b-q4_K_M-8k", aliasStarted["alias"]!.GetValue<string>());
        Assert.Equal(8192, aliasStarted["context"]!.GetValue<int>());

        var success = lines[^1]!;
        Assert.Equal("Local", success["target"]!.GetValue<string>());
        Assert.Equal(Model, success["model"]!.GetValue<string>());
        Assert.Equal("llama3.2:13b-q4_K_M-8k", success["alias"]!.GetValue<string>());
        Assert.NotNull(success["durationMs"]);

        // The daemon contract: /api/pull (stream: true, the exact model) + /api/create
        // (the alias over the model with parameters.num_ctx, stream: false).
        var pullRequest = (JsonObject)JsonNode.Parse(harness.Daemon.PullRequestBody!)!;
        Assert.Equal(Model, pullRequest["model"]!.GetValue<string>());
        Assert.True((bool)pullRequest["stream"]!);
        var createRequest = (JsonObject)JsonNode.Parse(harness.Daemon.CreateRequestBody!)!;
        Assert.Equal("llama3.2:13b-q4_K_M-8k", createRequest["name"]!.GetValue<string>());
        Assert.Equal(Model, createRequest["from"]!.GetValue<string>());
        Assert.Equal(8192, createRequest["parameters"]!["num_ctx"]!.GetValue<int>());
        Assert.False((bool)createRequest["stream"]!);

        // The post-success catalog refresh (the cartography matrix shows the model immediately).
        harness.CatalogCache.Verify(
            c => c.RefreshAsync(DaemonUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Pull_LiveProgress_ProgressLinesReachTheClient_WhileTheDaemonPullIsStillInFlight()
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());
        // The daemon serves the FIRST progress line, then HOLDS the connection (the pull is
        // "in flight" — a stand-in for a real GB-scale pull) until the gate opens.
        harness.Daemon.PullLines =
        [
            """{"status":"pulling 34%","digest":"sha256:abc","total":1000,"completed":340}""",
            """{"status":"pulling 67%","digest":"sha256:abc","total":1000,"completed":670}""",
            """{"status":"success"}"""
        ];
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Daemon.PullStreamGate = gate;

        var lines = new ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var readTask = Task.Run(async () =>
        {
            using var response = await harness.Client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "/api/router/models/pull") { Content = PullBody(Model, target: "Local") },
                HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
            {
                lines.Enqueue(line);
            }
        });

        try
        {
            // AC2 (live progress): the client must observe 'started' + the first 'progress'
            // line WHILE the daemon pull is still in flight (the gate is closed). A service
            // that buffers the progress events only sends 'started' here — the wait times out.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (lines.Count < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.True(
                lines.Count >= 2,
                "the first progress line must reach the client while the pull is in flight (live streaming)");
            var inFlight = lines.ToList()
                .Select(line => (JsonObject)JsonNode.Parse(line)!).ToList();
            Assert.DoesNotContain(inFlight, line => line["event"]!.GetValue<string>() == "success");
            var firstProgress = inFlight[^1]!;
            Assert.Equal("progress", firstProgress["event"]!.GetValue<string>());
            Assert.Equal(340L, firstProgress["completedBytes"]!.GetValue<long>());
            Assert.Equal(1000L, firstProgress["totalBytes"]!.GetValue<long>());
        }
        finally
        {
            gate.TrySetResult(); // release the "in flight" pull (no unbounded hold)
        }

        await readTask;
        var events = lines.Select(line => ((JsonObject)JsonNode.Parse(line)!)["event"]!.GetValue<string>()).ToList();
        Assert.Equal(["started", "progress", "progress", "success"], events);
    }

    [Fact]
    public async Task Pull_WithoutAliasContext_SkipsCreate_AndOmitsAliasFields()
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody(Model, target: "Local"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = await ReadNdjsonAsync(response);

        var events = lines.Select(line => line["event"]!.GetValue<string>()).ToList();
        Assert.Equal(["started", "progress", "success"], events);
        Assert.Null(harness.Daemon.CreateRequestBody); // no /api/create call
        Assert.Null(lines[^1]!["alias"]);
    }

    [Fact]
    public async Task Pull_ModelAlreadyPresent_ShortCircuitsTheDaemonPull()
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions(), modelPresent: true);

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody(Model, target: "Local"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = await ReadNdjsonAsync(response);

        // The 'present' event replaces the progress lines; the daemon /api/pull was never hit.
        var events = lines.Select(line => line["event"]!.GetValue<string>()).ToList();
        Assert.Equal(["started", "present", "success"], events);
        Assert.Null(harness.Daemon.PullRequestBody);
        Assert.Null(harness.Daemon.CreateRequestBody);
    }

    [Fact]
    public async Task Pull_DaemonErrorLine_YieldsTerminalErrorEventWithStagePull()
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());
        harness.Daemon.PullLines = ["""{"error":"model 'ghost' not found"}"""];

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody("ghost", target: "Local"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the stream started; the failure is IN-band
        var lines = await ReadNdjsonAsync(response);

        var events = lines.Select(line => line["event"]!.GetValue<string>()).ToList();
        Assert.Equal(["started", "error"], events);
        var error = lines[^1]!;
        Assert.Equal("pull", error["stage"]!.GetValue<string>());
        Assert.Contains("not found", error["error"]!.GetValue<string>());
        Assert.Null(harness.Daemon.CreateRequestBody); // the alias step never ran
    }

    [Fact]
    public async Task Pull_DaemonNonSuccessPullResponse_YieldsTerminalErrorEvent()
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());
        harness.Daemon.PullErrorResponse = (HttpStatusCode.BadRequest, """{"error":"boom"}""");

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody(Model, target: "Local"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = await ReadNdjsonAsync(response);
        var events = lines.Select(line => line["event"]!.GetValue<string>()).ToList();
        Assert.Equal(["started", "error"], events);
        Assert.Equal("boom", lines[^1]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pull_NullTarget_FallsBackToThePrimaryTarget()
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody(Model));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = await ReadNdjsonAsync(response);
        Assert.Equal("Local", lines[0]!["target"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pull_AliasCreateError_YieldsTerminalErrorEventWithStageAlias()
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());
        harness.Daemon.CreateResponse = (HttpStatusCode.InternalServerError, """{"error":"disk full"}""");

        var response = await harness.Client.PostAsync("/api/router/models/pull", PullBody(Model, target: "Local", aliasContext: 8192));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = await ReadNdjsonAsync(response);
        var events = lines.Select(line => line["event"]!.GetValue<string>()).ToList();
        Assert.Equal(["started", "progress", "alias_started", "error"], events);
        var error = lines[^1]!;
        Assert.Equal("alias", error["stage"]!.GetValue<string>());
        Assert.Contains("disk full", error["error"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    //  Route precedence regression (AC7): the literal pull route vs the preload catch-all
    // ------------------------------------------------------------------

    [Fact]
    public async Task RoutePrecedence_PullLiteralRoute_OutscoresThePreloadCatchAll()
    {
        await using var harness = await CreateTestAppAsync(OllamaOptions());

        // POST /api/router/models/pull must reach the PULL handler (not the catch-all):
        // a 200 text/x-ndjson stream, never a catch-all 400 "invalid preload path".
        var pullResponse = await harness.Client.PostAsync("/api/router/models/pull", PullBody(Model, target: "Local"));
        Assert.Equal(HttpStatusCode.OK, pullResponse.StatusCode);
        Assert.Equal("text/x-ndjson", pullResponse.Content.Headers.ContentType!.MediaType);

        // The catch-all preload route still resolves for a different shape (without the
        // IModelPreloadService registered in this minimal host it fails SOFT with a 503 —
        // the point is that it did NOT fall through to a 404 / the pull stream).
        var preloadResponse = await harness.Client.PostAsync($"/api/router/models/{Model}/preload", content: null);
        Assert.True(
            preloadResponse.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.OK,
            $"preload catch-all still resolves: {(int)preloadResponse.StatusCode}");
    }
}
