using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OllamaRouter.Converters;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Services;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.ContextSizing;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using OllamaRouter.Tests.TestHelpers;
using Yarp.ReverseProxy.Forwarder;

namespace OllamaRouter.Tests.Middleware;

/// <summary>
/// Task #61: the dual-path dispatch engine of <see cref="OllamaRoutingMiddleware"/>.
/// Cross-protocol requests (Ollama client ↔ OpenAI target, OpenAI client ↔ Ollama target) ride
/// the CONVERTER path (parser → target IChatClient → protocol encoder) inside the same
/// middleware-owned dispatch loop as the YARP fast path, so every telemetry/booking/failover
/// semantic applies uniformly (acceptance criteria AC2–AC6, AC10).
/// </summary>
public class DualPathRoutingMiddlewareTests
{
    private const string OpenRouter = "OpenRouter";
    private const string Local = "Local";

    /// <summary>
    /// Deterministic fake <see cref="IChatClient"/>: replays a canned update/response list,
    /// optionally throws on entry, and records the canonical request it received (messages +
    /// options) so the tests can assert the canonical mapping end to end.
    /// </summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly IAsyncEnumerable<ChatResponseUpdate>? _streaming;
        private readonly ChatResponse? _nonStreaming;
        private readonly Exception? _failure;
        private int _invoked;

        public FakeChatClient(IReadOnlyList<ChatResponseUpdate> streamingUpdates, Exception? failure = null)
        {
            _streaming = EnumerateStreaming(streamingUpdates);
            _failure = failure;
        }

        public FakeChatClient(ChatResponse response, Exception? failure = null)
        {
            _nonStreaming = response;
            _failure = failure;
        }

        public (IList<ChatMessage> Messages, ChatOptions? Options)? LastRequest { get; private set; }

        public bool WasInvoked => Volatile.Read(ref _invoked) == 1;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _invoked, 1);
            LastRequest = ([.. messages], options);
            if (_failure is not null)
            {
                throw _failure;
            }

            return Task.FromResult(_nonStreaming
                ?? new ChatResponse
                {
                    Messages = [new ChatMessage(ChatRole.Assistant, "ok")]
                });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _invoked, 1);
            LastRequest = ([.. messages], options);
            if (_failure is not null)
            {
                throw _failure;
            }

            if (_streaming is null)
            {
                yield break;
            }

            await foreach (var update in _streaming)
            {
                yield return update;
            }
        }

        private static IAsyncEnumerable<ChatResponseUpdate> EnumerateStreaming(IReadOnlyList<ChatResponseUpdate> updates) =>
            ToAsync(updates);

        private static async IAsyncEnumerable<ChatResponseUpdate> ToAsync(IReadOnlyList<ChatResponseUpdate> updates)
        {
            foreach (var update in updates)
            {
                yield return update;
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class FakeTargetChatClientProvider(IDictionary<string, IChatClient> clients) : ITargetChatClientProvider
    {
        public IChatClient? GetClient(string targetName) => TryResolve(targetName, out var client) ? client : null;

        public bool TryResolve(string targetName, out IChatClient? client)
        {
            client = null;
            return !string.IsNullOrWhiteSpace(targetName)
                && clients.TryGetValue(targetName, out client)
                && client is not null;
        }
    }

    private static OllamaRouterOptions HeterogeneousFleetOptions() => new()
    {
        Targets =
        [
            new OllamaTargetOptions
            {
                Name = Local,
                Url = "http://local-daemon:11434",
                MaxParallel = 2
            },
            new OllamaTargetOptions
            {
                Name = OpenRouter,
                Url = "http://router:8000/v1",
                Type = TargetType.OpenAI,
                ApiKey = "sk-target-key",
                Provider = "openrouter",
                MaxParallel = 2
            }
        ]
    };

    private sealed record SutBuilder(
        OllamaRouterOptions Options,
        ITargetChatClientProvider? ChatClientProvider,
        Mock<IHttpForwarder> Forwarder,
        Mock<ITargetFailureStreakService> Streak,
        Mock<ITargetLivenessService> Liveness,
        Mock<ITokenEstimator> TokenEstimator,
        Mock<IRoutingDecisionService> Decision);

    private static OllamaRoutingMiddleware BuildSut(
        RequestDelegate next,
        OllamaRouterOptions options,
        RoutingTarget decidedTarget,
        ITargetChatClientProvider? chatClientProvider,
        Mock<IHttpForwarder> forwarder,
        Mock<ITargetFailureStreakService> streak,
        out SutBuilder builder)
    {
        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var decision = new Mock<IRoutingDecisionService>();
        decision
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decidedTarget);
        decision
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decidedTarget);

        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow);
        liveness.Setup(t => t.GetFleetTargets())
            .Returns(options.GetEffectiveTargets().Select(t => (RoutingTarget)t.Name!).ToArray());

        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);

        var capabilityEngine = new ModelCapabilityEngine(
            new Mock<IOllamaModelCatalogClient>().Object,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ModelCapabilityEngine>.Instance);

        var sut = new OllamaRoutingMiddleware(
            next,
            tokenEstimator.Object,
            decision.Object,
            new Mock<IModelCatalogCacheService>().Object,
            new Mock<IActivityMonitorService>().Object,
            new Mock<IActivityStatisticsService>().Object,
            new Mock<IInferenceRequestRecorder>().Object,
            availability.Object,
            liveness.Object,
            capabilityEngine,
            new TestOptionsMonitor<OllamaRouterOptions>(options),
            NullLogger<OllamaRoutingMiddleware>.Instance,
            forwarder.Object,
            streak.Object,
            new Mock<ITargetExecutionTracker>().Object,
            contextShrinkManager: null,
            sessionAffinity: null,
            tokenMetricsRecorder: null,
            ollamaRequestParser: new OllamaChatRequestParser(),
            openAiRequestParser: new OpenAIChatRequestParser(),
            targetChatClientProvider: chatClientProvider);

        builder = new SutBuilder(options, chatClientProvider, forwarder, streak, liveness, tokenEstimator, decision);
        return sut;
    }

    private static HttpContext BuildContext(string path, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    // Applies the captured transformer to a synthetic outbound proxy request (mirrors what
    // YARP's HttpTransformer composition does) so the per-candidate header/path rewrite can be
    // asserted: returns the transformed proxy message.
    private static async Task<HttpRequestMessage> ApplyTransformerAsync(
        HttpContext context,
        HttpTransformer transformer,
        string destinationPrefix,
        string outboundPath)
    {
        var proxy = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://placeholder{outboundPath}"));
        await transformer.TransformRequestAsync(context, proxy, destinationPrefix, CancellationToken.None).AsTask();
        return proxy;
    }

    /// <summary>
    /// YARP stand-in: captures (destination, transformer) per attempt, applies the transformer
    /// to a synthetic proxy message (recorded in <paramref name="capturedProxy"/>) and then
    /// commits a protocol-shaped response body via <paramref name="commitResponse"/>.
    /// </summary>
    private static void SetupForwarderServing(
        Mock<IHttpForwarder> forwarder,
        Func<HttpContext, string, HttpRequestMessage, Task> commitResponse,
        Action<HttpContext, string, HttpTransformer>? inspect = null)
    {
        forwarder
            .Setup(f => f.SendAsync(
                It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
                It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()))
            .Callback<HttpContext, string, HttpMessageInvoker, ForwarderRequestConfig, HttpTransformer, CancellationToken>(async (c, destination, _, _, transformer, _) =>
            {
                var proxy = await ApplyTransformerAsync(c, transformer, destination, c.Request.Path.Value ?? "/");
                inspect?.Invoke(c, destination, transformer);
                await commitResponse(c, destination, proxy);
            })
            .ReturnsAsync(ForwarderError.None);
    }

    // ------------------------------------------------------------------
    //  AC2: Ollama client → OpenAI target (converter path, NDJSON out)
    // ------------------------------------------------------------------

    [Fact]
    public async Task OllamaChat_ToOpenAiTarget_ConverterPath_ServesNdjsonWithUsage()
    {
        var context = BuildContext("/api/chat", """
            { "model": "openai/gpt-4o", "stream": true, "messages": [ { "role": "user", "content": "hi there" } ] }
            """);
        var client = new FakeChatClient(new List<ChatResponseUpdate>
        {
            new(ChatRole.Assistant, [new TextContent("Hello")]),
            new(ChatRole.Assistant, [new TextContent("!")]),
            new(null, [new UsageContent(new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3, TotalTokenCount = 10 })])
        });

        var forwarder = new Mock<IHttpForwarder>();
        var streak = new Mock<ITargetFailureStreakService>();
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(OpenRouter),
            new FakeTargetChatClientProvider(new Dictionary<string, IChatClient> { [OpenRouter] = client }),
            forwarder, streak, out _);

        await sut.InvokeAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/x-ndjson", context.Response.ContentType);

        var body = ReadBody(context);
        // Per-token NDJSON frames + terminal done frame with the usage counts (AC2).
        Assert.Contains("\"content\":\"Hello\"", body, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"!\"", body, StringComparison.Ordinal);
        Assert.Contains("\"done\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"prompt_eval_count\":7", body, StringComparison.Ordinal);
        Assert.Contains("\"eval_count\":3", body, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"openai/gpt-4o\"", body, StringComparison.Ordinal);

        // The target's IChatClient received the canonical message list and the client's model name.
        var (messages, options) = client.LastRequest!.Value;
        Assert.Single(messages);
        Assert.Equal("hi there", messages[0].Contents.OfType<TextContent>().Single().Text);
        Assert.Equal("openai/gpt-4o", options!.ModelId);

        // Dispatch-mode telemetry + the served target; the YARP fast path was never touched.
        Assert.Equal("converter", context.Response.Headers["X-OllamaRouter-Dispatch-Mode"].ToString());
        Assert.Equal(OpenRouter, context.Response.Headers["X-OllamaRouter-Target"].ToString());
        forwarder.Verify(f => f.SendAsync(
            It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
            It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // A successful conversion never feeds a streak.
        streak.Verify(s => s.RecordFailure(It.IsAny<RoutingTarget>()), Times.Never);
        }

    // ------------------------------------------------------------------
    //  AC3: OpenAI client → Ollama target (converter path, SSE out)
    // ------------------------------------------------------------------

    [Fact]
    public async Task OpenAiChat_ToOllamaTarget_ConverterPath_ServesSseWithDoneMarker()
    {
        var context = BuildContext("/v1/chat/completions", """
            { "model": "llama3", "stream": true, "messages": [ { "role": "user", "content": "hello" } ] }
            """);
        var client = new FakeChatClient(new List<ChatResponseUpdate>
        {
            new(ChatRole.Assistant, [new TextContent("Hi")]),
            new(null, [new UsageContent(new UsageDetails { InputTokenCount = 5, OutputTokenCount = 2, TotalTokenCount = 7 })])
        });

        var forwarder = new Mock<IHttpForwarder>();
        var streak = new Mock<ITargetFailureStreakService>();
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(Local),
            new FakeTargetChatClientProvider(new Dictionary<string, IChatClient> { [Local] = client }),
            forwarder, streak, out _);

        await sut.InvokeAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/event-stream", context.Response.ContentType);

        var body = ReadBody(context);
        Assert.Contains("data: ", body, StringComparison.Ordinal);
        Assert.Contains("Hi", body, StringComparison.Ordinal);
        Assert.Contains("data: [DONE]", body, StringComparison.Ordinal);

        // Canonical mapping on the Ollama target's client.
        var (messages, options) = client.LastRequest!.Value;
        Assert.Equal("hello", messages[0].Contents.OfType<TextContent>().Single().Text);
        Assert.Equal("llama3", options!.ModelId);

        Assert.Equal("converter", context.Response.Headers["X-OllamaRouter-Dispatch-Mode"].ToString());
        Assert.Equal(Local, context.Response.Headers["X-OllamaRouter-Target"].ToString());
        forwarder.Verify(f => f.SendAsync(
            It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
            It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OpenAiChat_ToOllamaTarget_NonStreamingFlag_ServesSingleSseBlock()
    {
        var context = BuildContext("/v1/chat/completions", """
            { "model": "llama3", "stream": false, "messages": [ { "role": "user", "content": "hello" } ] }
            """);
        var client = new FakeChatClient(new ChatResponse
        {
            ModelId = "llama3",
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 2, TotalTokenCount = 7 },
            Messages = [new ChatMessage(ChatRole.Assistant, "Non-streamed answer")]
        });

        var forwarder = new Mock<IHttpForwarder>();
        var streak = new Mock<ITargetFailureStreakService>();
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(Local),
            new FakeTargetChatClientProvider(new Dictionary<string, IChatClient> { [Local] = client }),
            forwarder, streak, out _);

        await sut.InvokeAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        var body = ReadBody(context);
        // The non-streaming encoder emits ONE chat.completion JSON object (not SSE frames).
        Assert.Contains("\"object\":\"chat.completion\"", body, StringComparison.Ordinal);
        Assert.Contains("Non-streamed answer", body, StringComparison.Ordinal);
        Assert.DoesNotContain("data: [DONE]", body, StringComparison.Ordinal);
        Assert.Contains("\"prompt_tokens\":5", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  AC4: same-protocol OpenAI fast path (YARP + auth injection + /v1 de-duplication)
    // ------------------------------------------------------------------

    [Fact]
    public async Task OpenAiChat_ToOpenAiTarget_YarpFastPath_InjectsTargetKeyAndDeDupsV1Prefix()
    {
        var context = BuildContext("/v1/chat/completions", """
            { "model": "openai/gpt-4o", "stream": true, "messages": [ { "role": "user", "content": "hi" } ] }
            """);
        // A CLIENT bearer key must never reach the destination (the transformer strips it).
        context.Request.Headers.Authorization = "Bearer sk-inbound-client-key";

        var forwarder = new Mock<IHttpForwarder>();
        string? observedDestination = null;
        HttpRequestMessage? observedProxy = null;
        SetupForwarderServing(forwarder, async (c, destination, proxy) =>
        {
            observedDestination = destination;
            observedProxy = proxy;
            c.Response.ContentType = "text/event-stream";
            await c.Response.WriteAsync("data: {\"choices\":[]}\n\ndata: [DONE]\n\n");
        });

        var streak = new Mock<ITargetFailureStreakService>();
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(OpenRouter),
            chatClientProvider: null, // converter-less host: the fast path must still serve same-protocol.
            forwarder, streak, out _);

        await sut.InvokeAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("http://router:8000/v1", observedDestination);

        // (a) the inbound client key was REMOVED and (b) the target's key INJECTED on the outbound.
        Assert.NotNull(observedProxy);
        Assert.Equal("Bearer", observedProxy!.Headers.Authorization?.Scheme);
        Assert.Equal("sk-target-key", observedProxy.Headers.Authorization?.Parameter);
        Assert.DoesNotContain("sk-inbound-client-key", observedProxy.Headers.ToString(), StringComparison.Ordinal);

        // (c) /v1-prefixed base + /v1-prefixed inbound path ⇒ the segment appears exactly ONCE.
        Assert.Equal("/v1/chat/completions", observedProxy.RequestUri!.PathAndQuery);

        // Same-protocol telemetry: the fast path is reported as "yarp".
        Assert.Equal("yarp", context.Response.Headers["X-OllamaRouter-Dispatch-Mode"].ToString());
        Assert.Equal(OpenRouter, context.Response.Headers["X-OllamaRouter-Target"].ToString());
    }

    [Fact]
    public async Task OllamaChat_ToOllamaTarget_YarpFastPath_StripsInboundAuthorization()
    {
        var context = BuildContext("/api/chat", """
            { "model": "llama3", "stream": true, "messages": [ { "role": "user", "content": "hi" } ] }
            """);
        context.Request.Headers.Authorization = "Bearer sk-inbound";

        var forwarder = new Mock<IHttpForwarder>();
        HttpRequestMessage? observedProxy = null;
        SetupForwarderServing(forwarder, async (c, _, proxy) =>
        {
            observedProxy = proxy;
            c.Response.ContentType = "application/x-ndjson";
            await c.Response.WriteAsync("{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":true}\n");
        });

        var streak = new Mock<ITargetFailureStreakService>();
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(Local),
            chatClientProvider: null, forwarder, streak, out _);

        await sut.InvokeAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        // No Authorization survives on the outbound for a keyless Ollama target.
        Assert.Null(observedProxy?.Headers.Authorization);
        Assert.Equal("yarp", context.Response.Headers["X-OllamaRouter-Dispatch-Mode"].ToString());
    }

    // ------------------------------------------------------------------
    //  AC5: telemetry + accounting parity on the converter path
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConverterPath_EmitsFullTelemetryHeadersAndBookkeeping()
    {
        var context = BuildContext("/api/chat", """
            { "model": "openai/gpt-4o", "stream": true, "messages": [ { "role": "user", "content": "hi" } ] }
            """);
        context.Request.Headers["X-OllamaRouter-Session-Id"] = "sess-42";

        var client = new FakeChatClient(new List<ChatResponseUpdate>
        {
            new(ChatRole.Assistant, [new TextContent("ok")]),
            new(null, [new UsageContent(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 1, TotalTokenCount = 4 })])
        });
        var forwarder = new Mock<IHttpForwarder>();
        var streak = new Mock<ITargetFailureStreakService>();
        var activityMonitor = new Mock<IActivityMonitorService>();
        var requestId = Guid.NewGuid();
        activityMonitor.Setup(m => m.StartRequest(
                It.IsAny<RoutingTarget>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(requestId);
        var statistics = new Mock<IActivityStatisticsService>();
        var recorder = new Mock<IInferenceRequestRecorder>();

        var options = HeterogeneousFleetOptions();
        var decided = new RoutingTarget(OpenRouter);

        // Rebuild the middleware with the recording services so the booking calls can be verified.
        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);
        var decision = new Mock<IRoutingDecisionService>();
        decision.Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decided);
        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow);
        liveness.Setup(t => t.GetFleetTargets())
            .Returns(options.GetEffectiveTargets().Select(t => (RoutingTarget)t.Name!).ToArray());
        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);
        var tracker = new Mock<ITargetExecutionTracker>();
        var sessionAffinity = new SessionAffinityService(
            new TestOptionsMonitor<OllamaRouterOptions>(options),
            NullLogger<SessionAffinityService>.Instance);

        var sut = new OllamaRoutingMiddleware(
            _ => Task.CompletedTask,
            tokenEstimator.Object,
            decision.Object,
            new Mock<IModelCatalogCacheService>().Object,
            activityMonitor.Object,
            statistics.Object,
            recorder.Object,
            availability.Object,
            liveness.Object,
            new ModelCapabilityEngine(new Mock<IOllamaModelCatalogClient>().Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ModelCapabilityEngine>.Instance),
            new TestOptionsMonitor<OllamaRouterOptions>(options),
            NullLogger<OllamaRoutingMiddleware>.Instance,
            forwarder.Object,
            streak.Object,
            tracker.Object,
            contextShrinkManager: null,
            sessionAffinity: sessionAffinity,
            tokenMetricsRecorder: null,
            ollamaRequestParser: new OllamaChatRequestParser(),
            openAiRequestParser: new OpenAIChatRequestParser(),
            targetChatClientProvider: new FakeTargetChatClientProvider(new Dictionary<string, IChatClient> { [OpenRouter] = client }));

        await sut.InvokeAsync(context);

        // AC5 header parity: target / model / estimated-tokens / session / dispatch-mode — all on the converter path.
        Assert.Equal(OpenRouter, context.Response.Headers["X-OllamaRouter-Target"].ToString());
        Assert.Equal("openai/gpt-4o", context.Response.Headers["X-OllamaRouter-Model"].ToString());
        Assert.Equal("100", context.Response.Headers["X-OllamaRouter-Estimated-Tokens"].ToString());
        Assert.Equal("sess-42", context.Response.Headers["X-OllamaRouter-Session-Id"].ToString());
        Assert.Equal("converter", context.Response.Headers["X-OllamaRouter-Dispatch-Mode"].ToString());

        // Booking parity: StartRequest/CompleteRequest once, statistics once, in-flight balanced.
        activityMonitor.Verify(m => m.StartRequest(decided, "openai/gpt-4o", 100, It.IsAny<string?>(), "sess-42", null), Times.Once);
        activityMonitor.Verify(m => m.CompleteRequest(requestId, It.IsAny<ActivityLogEntry>()), Times.Once);
        statistics.Verify(s => s.Add(It.Is<ActivityLogEntry>(e => e.StatusCode == 200 && e.Success)), Times.Once);
        tracker.Verify(t => t.IncrementInFlight(decided), Times.Once);
        tracker.Verify(t => t.DecrementInFlight(decided), Times.Once);
    }

    // ------------------------------------------------------------------
    //  AC6: failover parity on the converter path
    // ------------------------------------------------------------------

    [Fact]
    public async Task Converter_UncommittedTransportFailure_FailsOverToYarpCandidate_RecordsStreak()
    {
        var context = BuildContext("/api/chat", """
            { "model": "openai/gpt-4o", "stream": true, "messages": [ { "role": "user", "content": "hi" } ] }
            """);
        // The OpenAI target's client dies BEFORE the first byte (connection refused).
        var deadClient = new FakeChatClient(new List<ChatResponseUpdate>(), failure: new HttpRequestException("connection refused"));

        var forwarder = new Mock<IHttpForwarder>();
        int forwardAttempts = 0;
        SetupForwarderServing(forwarder, async (c, _, _) =>
        {
            forwardAttempts++;
            c.Response.ContentType = "application/x-ndjson";
            await c.Response.WriteAsync("{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\",\"content\":\"rescued\"},\"done\":true,\"prompt_eval_count\":3,\"eval_count\":2}\n");
        });

        var streak = new Mock<ITargetFailureStreakService>();
        streak.Setup(s => s.ShouldSkip(It.IsAny<RoutingTarget>(), It.IsAny<int>())).Returns(false);
        var liveness = new Mock<ITargetLivenessService>();
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(OpenRouter),
            new FakeTargetChatClientProvider(new Dictionary<string, IChatClient> { [OpenRouter] = deadClient }),
            forwarder, streak, out var builder);

        await sut.InvokeAsync(context);

        // Served by the YARP failover candidate — telemetry reflects the SERVED target/path.
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Contains("rescued", ReadBody(context), StringComparison.Ordinal);
        Assert.Equal(Local, context.Response.Headers["X-OllamaRouter-Target"].ToString());
        Assert.Equal("yarp", context.Response.Headers["X-OllamaRouter-Dispatch-Mode"].ToString());
        Assert.Equal(1, forwardAttempts);

        // Uncommitted parity: streak recorded + one best-effort liveness strike, failover applied.
        streak.Verify(s => s.RecordFailure(new RoutingTarget(OpenRouter)), Times.Once);
        builder.Liveness.Verify(l => l.MarkHardFailureAsync(new RoutingTarget(OpenRouter)), Times.Once);
        // The served candidate's success resets ITS streak.
        streak.Verify(s => s.RecordSuccess(new RoutingTarget(Local)), Times.Once);
    }

    [Fact]
    public async Task Converter_UpstreamHttpError_TerminalWithProtocolShapedErrorBody_NoStreak()
    {
        var context = BuildContext("/api/chat", """
            { "model": "nope", "stream": true, "messages": [ { "role": "user", "content": "hi" } ] }
            """);
        var errorClient = new FakeChatClient(new List<ChatResponseUpdate>(),
            failure: new OllamaRouter.Services.Clients.OpenAiHttpException(
                "OpenAI endpoint error (HTTP 404): model 'nope' not found",
                System.Net.HttpStatusCode.NotFound, "invalid_request_error", "model_not_found"));

        var forwarder = new Mock<IHttpForwarder>();
        var streak = new Mock<ITargetFailureStreakService>();
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(OpenRouter),
            new FakeTargetChatClientProvider(new Dictionary<string, IChatClient> { [OpenRouter] = errorClient }),
            forwarder, streak, out _);

        await sut.InvokeAsync(context);

        // Committed-equivalent terminal: upstream status + Ollama-protocol error envelope.
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal("application/x-ndjson", context.Response.ContentType);
        var body = ReadBody(context);
        Assert.Contains("\"error\"", body, StringComparison.Ordinal);
        Assert.Contains("(HTTP 404)", body, StringComparison.Ordinal);
        Assert.Contains("not found", body, StringComparison.Ordinal);

        // NO streak (the provider answered — transport was reliable) and NO failover.
        streak.Verify(s => s.RecordFailure(It.IsAny<RoutingTarget>()), Times.Never);
        forwarder.Verify(f => f.SendAsync(
            It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
            It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Converter_UpstreamHttpError_OpenAiInbound_ServesOpenAiErrorEnvelope()
    {
        var context = BuildContext("/v1/chat/completions", """
            { "model": "llama3", "stream": true, "messages": [ { "role": "user", "content": "hi" } ] }
            """);
        var errorClient = new FakeChatClient(new List<ChatResponseUpdate>(),
            failure: new OllamaRouter.Services.Clients.OllamaHttpException(
                "Ollama endpoint error (HTTP 400): unknown model", System.Net.HttpStatusCode.BadRequest, null));

        var forwarder = new Mock<IHttpForwarder>();
        var streak = new Mock<ITargetFailureStreakService>();
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(Local),
            new FakeTargetChatClientProvider(new Dictionary<string, IChatClient> { [Local] = errorClient }),
            forwarder, streak, out _);

        await sut.InvokeAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        var body = ReadBody(context);
        // OpenAI envelope shape: {"error": {"message", "type", "code"}}.
        Assert.Contains("\"error\"", body, StringComparison.Ordinal);
        Assert.Contains("\"message\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\"", body, StringComparison.Ordinal);
        streak.Verify(s => s.RecordFailure(It.IsAny<RoutingTarget>()), Times.Never);
    }

    [Fact]
    public async Task Converter_MidStreamFailure_SetsForwarderErrorFeature_Terminal()
    {
        var context = BuildContext("/api/chat", """
            { "model": "openai/gpt-4o", "stream": true, "messages": [ { "role": "user", "content": "hi" } ] }
            """);
        // One healthy chunk, then the stream breaks mid-flight.
        var flakyClient = new FakeChatClient(new List<ChatResponseUpdate>
        {
            new(ChatRole.Assistant, [new TextContent("part")]),
            new(null, [new UsageContent(new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1, TotalTokenCount = 2 })])
        }, failure: null);
        var forwarder = new Mock<IHttpForwarder>();
        var streak = new Mock<ITargetFailureStreakService>();
        streak.Setup(s => s.ShouldSkip(It.IsAny<RoutingTarget>(), It.IsAny<int>())).Returns(false);
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(OpenRouter),
            new FakeTargetChatClientProvider(new Dictionary<string, IChatClient> { [OpenRouter] = new ThrowingAfterFirstChunkClient() }),
            forwarder, streak, out _);

        await sut.InvokeAsync(context);

        // The post-dispatch mid-stream classification reads IForwarderErrorFeature: the loop
        // must have published the Exception classification for the converter's mid-stream break.
        var feature = context.Features.Get<IForwarderErrorFeature>();
        Assert.NotNull(feature);
        Assert.NotEqual(ForwarderError.None, feature!.Error);
        // Mid-stream failures feed the streak exactly once (parity with the YARP branch).
        streak.Verify(s => s.RecordFailure(new RoutingTarget(OpenRouter)), Times.Once);
    }

    // ------------------------------------------------------------------
    //  Task #96: client-aborted ResponseBodyDestination vs. genuine destination reset
    // ------------------------------------------------------------------

    // Task #96 (T6): YARP path — a client disconnect mid-stream that YARP classifies as
    // ResponseBodyDestination (activity-timeout race) with a TaskCanceledException must NOT
    // strike the streak: the exception chain evidences our cancellation as the initiator.
    [Fact]
    public async Task YarpPath_ClientAbortedMidStream_ResponseBodyDestination_TokenCanceledException_NoStreak()
    {
        var context = BuildContext("/api/chat", """
            { "model": "llama3", "stream": true, "messages": [ { "role": "user", "content": "hi" } ] }
            """);
        var cts = new CancellationTokenSource();
        context.RequestAborted = cts.Token;

        var forwarder = new Mock<IHttpForwarder>();
        var attempts = new int[1];
        SetupForwarderMidStreamError(forwarder, ForwarderError.ResponseBodyDestination,
            () => { cts.Cancel(); return new TaskCanceledException("stream canceled by client"); }, attempts);

        var streak = new Mock<ITargetFailureStreakService>();
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(Local),
            null, forwarder, streak, out _);

        await sut.InvokeAsync(context);

        // Client abort ⇒ no streak in either direction, and committed ⇒ terminal (no failover).
        streak.Verify(s => s.RecordFailure(It.IsAny<RoutingTarget>()), Times.Never);
        streak.Verify(s => s.RecordSuccess(It.IsAny<RoutingTarget>()), Times.Never);
        Assert.Equal(1, attempts[0]);
        var feature = context.Features.Get<IForwarderErrorFeature>();
        Assert.NotNull(feature);
        Assert.Equal(ForwarderError.ResponseBodyDestination, feature!.Error);
    }

    // Task #96 (T7): YARP path — the daemon genuinely reset the TCP connection mid-stream
    // (IOException, client still connected) ⇒ the failure IS recorded, exactly once.
    [Fact]
    public async Task YarpPath_DestinationResetMidStream_IOException_TokenNotCanceled_StreakRecorded()
    {
        var context = BuildContext("/api/chat", """
            { "model": "llama3", "stream": true, "messages": [ { "role": "user", "content": "hi" } ] }
            """);

        var forwarder = new Mock<IHttpForwarder>();
        var attempts = new int[1];
        SetupForwarderMidStreamError(forwarder, ForwarderError.ResponseBodyDestination,
            () => new IOException("connection reset"), attempts);

        var streak = new Mock<ITargetFailureStreakService>();
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(Local),
            null, forwarder, streak, out _);

        await sut.InvokeAsync(context);

        streak.Verify(s => s.RecordFailure(new RoutingTarget(Local)), Times.Once);
        Assert.Equal(1, attempts[0]); // committed ⇒ terminal, no failover
    }

    // Task #96 (T8, parity lock): converter path — a client abort mid-stream (TaskCanceledException
    // after the first chunk) is caught by the converter's OperationCanceledException guard and
    // published as ForwarderError.RequestCanceled: terminal, no streak, single attempt.
    [Fact]
    public async Task ConverterPath_ClientAbortedMidStream_TokenCancelled_TerminatedNoStreak()
    {
        var context = BuildContext("/api/chat", """
            { "model": "openai/gpt-4o", "stream": true, "messages": [ { "role": "user", "content": "hi" } ] }
            """);
        var cts = new CancellationTokenSource();
        context.RequestAborted = cts.Token;

        var forwarder = new Mock<IHttpForwarder>();
        var streak = new Mock<ITargetFailureStreakService>();
        streak.Setup(s => s.ShouldSkip(It.IsAny<RoutingTarget>(), It.IsAny<int>())).Returns(false);
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(OpenRouter),
            new FakeTargetChatClientProvider(new Dictionary<string, IChatClient> { [OpenRouter] = new TaskCanceledAfterFirstChunkClient(cts) }),
            forwarder, streak, out _);

        await sut.InvokeAsync(context);

        // The clean client abort publishes RequestCanceled (already suppressed by IsClientAbort)
        // and never strikes the streak; single attempt (no failover).
        streak.Verify(s => s.RecordFailure(It.IsAny<RoutingTarget>()), Times.Never);
        var feature = context.Features.Get<IForwarderErrorFeature>();
        Assert.NotNull(feature);
        Assert.Equal(ForwarderError.RequestCanceled, feature!.Error);
        forwarder.Verify(f => f.SendAsync(
            It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
            It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Task #96 YARP stand-in: commits a partial NDJSON frame, publishes IForwarderErrorFeature
    // (error + exception) exactly like the real YARP forwarder does via ReportProxyError, and
    // returns the error. Counts attempts for single-attempt (no failover) assertions.
    private static void SetupForwarderMidStreamError(
        Mock<IHttpForwarder> forwarder,
        ForwarderError error,
        Func<Exception> buildException,
        int[] attempts)
    {
        forwarder
            .Setup(f => f.SendAsync(
                It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
                It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()))
            .Callback<HttpContext, string, HttpMessageInvoker, ForwarderRequestConfig, HttpTransformer, CancellationToken>(async (c, _, _, _, _, _) =>
            {
                attempts[0]++;
                c.Response.ContentType = "application/x-ndjson";
                await c.Response.WriteAsync("{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\",\"content\":\"part\"},\"done\":false}\n");
                c.Features.Set<IForwarderErrorFeature>(new ErrorFeature(error, buildException()));
            })
            .ReturnsAsync(error);
    }

    // Task #96: the public IForwarderErrorFeature shape YARP populates (error + exception).
    private sealed class ErrorFeature(ForwarderError error, Exception? exception) : IForwarderErrorFeature
    {
        public ForwarderError Error { get; } = error;
        public Exception? Exception { get; } = exception;
    }

    // Task #96: one healthy chunk, then a TaskCanceledException AFTER the client token fires
    // (a real client disconnect mid-stream).
    private sealed class TaskCanceledAfterFirstChunkClient(CancellationTokenSource cts) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("part")]);
            cts.Cancel();
            throw new TaskCanceledException("stream canceled by client");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class ThrowingAfterFirstChunkClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("part")]);
            throw new IOException("upstream reset the connection");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // ------------------------------------------------------------------
    //  AC10: ineligibility rules (embeddings / OsNative)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Embeddings_ToOpenAiTarget_SkipsCandidateNoStreak_FailsOverToOllama()
    {
        var context = BuildContext("/api/embeddings", """
            { "model": "llama3", "prompt": "hi" }
            """);
        var client = new FakeChatClient(new List<ChatResponseUpdate>());

        var forwarder = new Mock<IHttpForwarder>();
        SetupForwarderServing(forwarder, async (c, _, _) =>
        {
            c.Response.ContentType = "application/json";
            await c.Response.WriteAsync("{\"embedding\":[0.1,0.2]}");
        });

        var streak = new Mock<ITargetFailureStreakService>();
        streak.Setup(s => s.ShouldSkip(It.IsAny<RoutingTarget>(), It.IsAny<int>())).Returns(false);
        var sut = BuildSut(_ => Task.CompletedTask, HeterogeneousFleetOptions(), new RoutingTarget(OpenRouter),
            new FakeTargetChatClientProvider(new Dictionary<string, IChatClient> { [OpenRouter] = client }),
            forwarder, streak, out _);

        await sut.InvokeAsync(context);

        // The OpenAI candidate was SKIPPED (no embeddings converter) — no streak, no liveness
        // side effects, no client invocation — and the Ollama candidate served instead.
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Contains("\"embedding\"", ReadBody(context), StringComparison.Ordinal);
        Assert.Equal(Local, context.Response.Headers["X-OllamaRouter-Target"].ToString());
        streak.Verify(s => s.RecordFailure(It.IsAny<RoutingTarget>()), Times.Never);
        Assert.False(client.WasInvoked);
    }

    [Fact]
    public async Task Embeddings_OnlyOpenAiTargetsExist_Terminal503Diagnostics()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions
                {
                    Name = OpenRouter,
                    Url = "http://router:8000/v1",
                    Type = TargetType.OpenAI,
                    ApiKey = "sk-x"
                }
            ]
        };
        var context = BuildContext("/api/embeddings", """
            { "model": "gpt-4o", "prompt": "hi" }
            """);
        var client = new FakeChatClient(new List<ChatResponseUpdate>());
        var forwarder = new Mock<IHttpForwarder>();
        var streak = new Mock<ITargetFailureStreakService>();
        var sut = BuildSut(_ => Task.CompletedTask, options, new RoutingTarget(OpenRouter),
            new FakeTargetChatClientProvider(new Dictionary<string, IChatClient> { [OpenRouter] = client }),
            forwarder, streak, out _);

        await sut.InvokeAsync(context);

        Assert.Equal(503, context.Response.StatusCode);
        var body = ReadBody(context);
        Assert.Contains("\"error\"", body, StringComparison.Ordinal);
        using var diagnostics = System.Text.Json.JsonDocument.Parse(body);
        Assert.False(diagnostics.RootElement.GetProperty("cloud_enabled").GetBoolean());
        streak.Verify(s => s.RecordFailure(It.IsAny<RoutingTarget>()), Times.Never);
    }

    [Fact]
    public async Task OsNativeTarget_NeverADispatchCandidate_Terminal503_NoStreak()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions
                {
                    Name = "Native",
                    Url = "native://inprocess",
                    Type = TargetType.OsNative
                }
            ]
        };
        // GetEffectiveTargets skips blank-URL targets only; a non-http "Url" still binds, so the
        // target participates in the candidate list and must be skipped by the eligibility gate.
        var context = BuildContext("/api/chat", """
            { "model": "phi4", "stream": true, "messages": [ { "role": "user", "content": "hi" } ] }
            """);
        var forwarder = new Mock<IHttpForwarder>();
        var streak = new Mock<ITargetFailureStreakService>();
        var sut = BuildSut(_ => Task.CompletedTask, options, new RoutingTarget("Native"),
            chatClientProvider: null, forwarder, streak, out _);

        await sut.InvokeAsync(context);

        Assert.Equal(503, context.Response.StatusCode);
        forwarder.Verify(f => f.SendAsync(
            It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
            It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()),
            Times.Never);
        streak.Verify(s => s.RecordFailure(It.IsAny<RoutingTarget>()), Times.Never);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static string ReadBody(HttpContext context)
    {
        var body = context.Response.Body as MemoryStream;
        Assert.NotNull(body);
        body!.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(body, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
