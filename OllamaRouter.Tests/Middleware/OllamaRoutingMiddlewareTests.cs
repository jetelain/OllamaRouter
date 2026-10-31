using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Services;
using Yarp.ReverseProxy.Forwarder;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OllamaRouter.Tests.Middleware;

public class OllamaRoutingMiddlewareTests
{
    private static HttpContext BuildContext(string path, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context;
    }

    private static OllamaRoutingMiddleware CreateSut(
        RequestDelegate next,
        Mock<ITokenEstimator> tokenEstimator,
        Mock<IRoutingDecisionService> routingDecisionService,
        Mock<IModelCatalogCacheService>? modelCatalogCache = null,
        Mock<IActivityMonitorService>? activityMonitor = null,
        Mock<IActivityStatisticsService>? activityStatistics = null,
        Mock<IInferenceRequestRecorder>? inferenceRequestRecorder = null,
        Mock<ITargetAvailabilityService>? targetAvailability = null,
        Mock<ITargetLivenessService>? targetLiveness = null,
        ModelCapabilityEngine? capabilityEngine = null,
        Mock<IHttpForwarder>? forwarder = null,
        ITargetFailureStreakService? streakTracker = null,
        ISessionAffinityService? sessionAffinity = null,
        ITokenMetricsRecorder? tokenMetricsRecorder = null) =>
        CreateSut(next, tokenEstimator.Object, routingDecisionService, modelCatalogCache, activityMonitor, activityStatistics, inferenceRequestRecorder, targetAvailability, targetLiveness, capabilityEngine, forwarder, streakTracker, sessionAffinity, tokenMetricsRecorder);

    private static OllamaRoutingMiddleware CreateSut(
        RequestDelegate next,
        ITokenEstimator tokenEstimator,
        Mock<IRoutingDecisionService> routingDecisionService,
        Mock<IModelCatalogCacheService>? modelCatalogCache = null,
        Mock<IActivityMonitorService>? activityMonitor = null,
        Mock<IActivityStatisticsService>? activityStatistics = null,
        Mock<IInferenceRequestRecorder>? inferenceRequestRecorder = null,
        Mock<ITargetAvailabilityService>? targetAvailability = null,
        Mock<ITargetLivenessService>? targetLiveness = null,
        ModelCapabilityEngine? capabilityEngine = null,
        Mock<IHttpForwarder>? forwarder = null,
        ITargetFailureStreakService? streakTracker = null,
        ISessionAffinityService? sessionAffinity = null,
        ITokenMetricsRecorder? tokenMetricsRecorder = null)
        {
        var effectiveTargetAvailability = targetAvailability ?? new Mock<ITargetAvailabilityService>();
        if (targetAvailability is null)
        {
            effectiveTargetAvailability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);
        }

        // Permissive liveness default: everything is Online and the startup grace window
        // has already elapsed, so existing tests exercise normal routing (never 503).
        var effectiveTargetLiveness = targetLiveness ?? new Mock<ITargetLivenessService>();
        if (targetLiveness is null)
        {
            effectiveTargetLiveness
                .Setup(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(TargetLivenessState.Online);
            effectiveTargetLiveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
            effectiveTargetLiveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow);
            effectiveTargetLiveness
                .Setup(t => t.GetFleetTargets())
                .Returns(new[] { RoutingTarget.Local, RoutingTarget.Remote });
            effectiveTargetLiveness
                .Setup(t => t.GetEntries())
                .Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
                {
                    [RoutingTarget.Local] = new TargetLivenessEntry(RoutingTarget.Local, TargetLivenessState.Online, null, DateTimeOffset.UtcNow, null),
                    [RoutingTarget.Remote] = new TargetLivenessEntry(RoutingTarget.Remote, TargetLivenessState.Online, null, DateTimeOffset.UtcNow, null)
                });
            effectiveTargetLiveness
                .Setup(t => t.GetSnapshot())
                .Returns(new TargetLivenessSnapshot(new List<TargetLivenessEntry>()));
        }

        // By default the capability engine has an empty catalog digest and an unreachable
        // client, so dynamic context sizing degrades to a permissive pass-through (existing
        // tests keep their body untouched).
        var effectiveCapabilityEngine = capabilityEngine ?? new ModelCapabilityEngine(
            new Mock<IOllamaModelCatalogClient>().Object,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ModelCapabilityEngine>.Instance);

        return new OllamaRoutingMiddleware(
            next,
            tokenEstimator,
            routingDecisionService.Object,
            (modelCatalogCache ?? new Mock<IModelCatalogCacheService>()).Object,
            (activityMonitor ?? new Mock<IActivityMonitorService>()).Object,
            (activityStatistics ?? new Mock<IActivityStatisticsService>()).Object,
            (inferenceRequestRecorder ?? new Mock<IInferenceRequestRecorder>()).Object,
            effectiveTargetAvailability.Object,
            effectiveTargetLiveness.Object,
            effectiveCapabilityEngine,
            MsOptions.Create(new OllamaRouterOptions { LocalUrl = "http://localhost:11435", RemoteUrl = "http://remote:11434" }),
            NullLogger<OllamaRoutingMiddleware>.Instance,
            CreateForwarder(next, forwarder).Object,
            streakTracker ?? new TargetFailureStreakService(),
            new Mock<ITargetExecutionTracker>().Object,
            contextShrinkManager: null,
            sessionAffinity,
            tokenMetricsRecorder);
    }

            // Builds the forwarder used by the middleware. When the caller does not supply an explicit
            // mock (most pre-existing completion-path tests), a transparent proxy is returned whose
            // SendAsync simply invokes the test's `next` delegate and reports ForwarderError.None —
            // this preserves the historical YARP-via-`next` behavior for those tests. Callers that
            // need to inspect or steer failover pass their own Mock<IHttpForwarder>.
            internal static Mock<IHttpForwarder> CreateForwarder(RequestDelegate next, Mock<IHttpForwarder>? forwarder = null)
            {
            var effectiveForwarder = forwarder ?? new Mock<IHttpForwarder>();
            if (forwarder is null)
            {
            effectiveForwarder
                .Setup(f => f.SendAsync(
                    It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
                    It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()))
                .Callback<HttpContext, string, HttpMessageInvoker, ForwarderRequestConfig, HttpTransformer, CancellationToken>(
                    (c, _, _, _, _, _) =>
                    {
                        // The transparent proxy forwards to the test's `next` delegate.
                        // In-memory request/response streams complete synchronously, so the
                        // delegate's task is already done by the time we join it.
                        next(c).GetAwaiter().GetResult();
                    })
                .Returns(new ValueTask<ForwarderError>(ForwarderError.None));
            }
            return effectiveForwarder;
            }

    [Fact]
    public async Task InvokeAsync_NonInterceptedPath_SetsHeaderToLocal_WithoutInspection()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/pull";

        var tokenEstimator = new Mock<ITokenEstimator>();
        var routingDecisionService = new Mock<IRoutingDecisionService>();

        bool nextCalled = false;
        var sut = CreateSut(_ => { nextCalled = true; return Task.CompletedTask; }, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
        Assert.True(nextCalled);
        tokenEstimator.Verify(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>()), Times.Never);
        routingDecisionService.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_InterceptedPath_PassesTokenCountAndModelName_ToRoutingDecisionService()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
        routingDecisionService.Verify(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        // No options.num_ctx in the body ⇒ the client value is passed as null.
        routingDecisionService.Verify(r => r.DecideAsync(100, "llama3", (int?)null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_InterceptedPath_WithClientNumCtx_PassesItToRoutingDecisionService()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" , "options": { "num_ctx": 131072 } }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        // The client's static options.num_ctx is extracted from the body and fed to the
        // pipeline's context-fit clamp (upper-bound preference, never a disqualifier).
        routingDecisionService.Verify(r => r.DecideAsync(100, "llama3", 131_072, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_RoutingDecisionReturnsLocal_SetsHeaderToLocal()
    {
        var context = BuildContext("/api/generate", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(50);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(50, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
    }

    [Fact]
    public async Task InvokeAsync_ExceptionDuringInspection_FallsBackToRemote()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Throws(new InvalidOperationException("boom"));

        var routingDecisionService = new Mock<IRoutingDecisionService>();

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
    }

    [Fact]
    public async Task InvokeAsync_RoutingDecisionThrowsNoAvailableTargetException_Returns503()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NoAvailableTargetException("No enabled routing target is available."));

        var nextCalled = false;
        var sut = CreateSut(_ => { nextCalled = true; return Task.CompletedTask; }, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ExceptionDuringInspection_RemoteDisabled_FallsBackToLocal()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Throws(new InvalidOperationException("boom"));

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        var targetAvailability = new Mock<ITargetAvailabilityService>();
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Local)).Returns(true);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Remote)).Returns(false);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Cloud)).Returns(false);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService, targetAvailability: targetAvailability);

        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
    }

    [Fact]
    public async Task InvokeAsync_ExceptionDuringInspection_AllDisabled_Returns503()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Throws(new InvalidOperationException("boom"));

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        var targetAvailability = new Mock<ITargetAvailabilityService>();
        targetAvailability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(false);

        var nextCalled = false;
        var sut = CreateSut(_ => { nextCalled = true; return Task.CompletedTask; }, tokenEstimator, routingDecisionService, targetAvailability: targetAvailability);

        await sut.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_GenerateRequestWithPriorContext_AddsPriorContextTokenCount()
    {
        var context = BuildContext("/api/generate", """{ "model": "llama3", "prompt": "Continue", "context": [1, 2, 3, 4, 5] }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(105, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
        routingDecisionService.Verify(r => r.DecideAsync(105, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_ShowRequest_IsNotHandledByMiddleware_DefaultTargetHeaderSet()
    {
        // POST /api/show is now a short-circuited hybrid endpoint (served by
        // IFleetAggregationService); the middleware no longer inspects it and only falls
        // through to the default branch (harmless for the short-circuit).
        var context = BuildContext("/api/show", """{ "model": "llama3" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        var routingDecisionService = new Mock<IRoutingDecisionService>();

        var nextCalled = false;
        var sut = CreateSut(_ => { nextCalled = true; return Task.CompletedTask; }, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
        tokenEstimator.Verify(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>()), Times.Never);
        routingDecisionService.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PinnedManagementRequest_ValidOnlinePin_ForwardsToPinnedTarget_WithTelemetryHeader()
    {
        var context = BuildContext("/api/pull", """{ "model": "llama3" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "Remote";

        var sut = CreateSut(_ => Task.CompletedTask, new Mock<ITokenEstimator>(), new Mock<IRoutingDecisionService>());

        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
        Assert.Equal("Remote", context.Response.Headers["X-OllamaRouter-Target"]);
    }

    [Fact]
    public async Task PinnedManagementRequest_AbsentPin_FallsBackToPrimaryLocal()
    {
        var context = BuildContext("/api/pull", """{ "model": "llama3" }""");

        var sut = CreateSut(_ => Task.CompletedTask, new Mock<ITokenEstimator>(), new Mock<IRoutingDecisionService>());

        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
        Assert.Equal("Local", context.Response.Headers["X-OllamaRouter-Target"]);
    }

    [Fact]
    public async Task PinnedManagementRequest_InvalidPinName_FallsBackToPrimaryLocal()
    {
        var context = BuildContext("/api/pull", """{ "model": "llama3" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "DoesNotExist";

        var sut = CreateSut(_ => Task.CompletedTask, new Mock<ITokenEstimator>(), new Mock<IRoutingDecisionService>());

        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
    }

    [Fact]
    public async Task PinnedManagementRequest_DisabledPin_FallsBackToPrimaryLocal()
    {
        var context = BuildContext("/api/pull", """{ "model": "llama3" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "Remote";

        var targetAvailability = new Mock<ITargetAvailabilityService>();
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Remote)).Returns(false);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Local)).Returns(true);

        var sut = CreateSut(_ => Task.CompletedTask, new Mock<ITokenEstimator>(), new Mock<IRoutingDecisionService>(), targetAvailability: targetAvailability);

        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
    }

    [Fact]
    public async Task PinnedManagementRequest_OfflinePin_FallsBackToPrimaryLocal()
    {
        var context = BuildContext("/api/pull", """{ "model": "llama3" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "Remote";

        var targetAvailability = new Mock<ITargetAvailabilityService>();
        targetAvailability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);

        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(t => t.EnsureKnownAsync(RoutingTarget.Remote, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Offline);
        liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow);
        liveness.Setup(t => t.GetFleetTargets()).Returns(new[] { RoutingTarget.Local, RoutingTarget.Remote });
        liveness.Setup(t => t.GetEntries())
            .Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [RoutingTarget.Local] = new(RoutingTarget.Local, TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
                [RoutingTarget.Remote] = new(RoutingTarget.Remote, TargetLivenessState.Offline, "timeout", DateTimeOffset.UtcNow)
            });

        var sut = CreateSut(_ => Task.CompletedTask, new Mock<ITokenEstimator>(), new Mock<IRoutingDecisionService>(),
            targetAvailability: targetAvailability, targetLiveness: liveness);

        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
        Assert.Equal("Local", context.Response.Headers["X-OllamaRouter-Target"]);
    }

    [Fact]
    public async Task PinnedManagementRequest_DeleteMethod_PinHonored()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Delete;
        context.Request.Path = "/api/delete";
        context.Request.Body = Stream.Null;
        context.Request.Headers["X-OllamaRouter-Target"] = "Remote";

        var sut = CreateSut(_ => Task.CompletedTask, new Mock<ITokenEstimator>(), new Mock<IRoutingDecisionService>());

        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
        Assert.Equal("Remote", context.Response.Headers["X-OllamaRouter-Target"]);
    }

    [Fact]
    public async Task PinnedManagementRequest_CreateAndCopy_ArePinnedClass()
    {
        foreach (var path in new[] { "/api/create", "/api/copy" })
        {
            var context = BuildContext(path, """{ "model": "llama3" }""");
            context.Request.Headers["X-OllamaRouter-Target"] = "Remote";

            var sut = CreateSut(_ => Task.CompletedTask, new Mock<ITokenEstimator>(), new Mock<IRoutingDecisionService>());

            await sut.InvokeAsync(context);

            Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
            Assert.Equal("Remote", context.Response.Headers["X-OllamaRouter-Target"]);
        }
    }

    [Fact]
    public async Task EmbeddingsRequest_RidesCompletionPipeline_DecideAsyncWithEstimatedTokens()
    {
        var context = BuildContext("/api/embeddings", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
        routingDecisionService.Verify(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("Remote", context.Response.Headers["X-OllamaRouter-Target"]);
        Assert.Equal("llama3", context.Response.Headers["X-OllamaRouter-Model"]);
        Assert.Equal("100", context.Response.Headers["X-OllamaRouter-Estimated-Tokens"]);
    }

    [Fact]
    public async Task InvokeAsync_OpenAIStyleSseResponse_ExtractsUsageFromLastDataBlock()
    {
        var context = BuildContext("/v1/chat/completions", """{ "model": "qwen", "stream": true }""");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var requestId = Guid.NewGuid();
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.StartRequest(It.IsAny<RoutingTarget>(), "qwen", It.IsAny<int>())).Returns(requestId);

        const string sseResponse = """
            data: {"id":"chatcmpl-570","object":"chat.completion.chunk","created":1789194489,"model":"Qwen3.8-27B:latest","system_fingerprint":"fp_ollama","choices":[{"index":0,"delta":{"content":" me"},"finish_reason":null}]}

            data: {"id":"chatcmpl-570","object":"chat.completion.chunk","created":1789194489,"model":"Qwen3.8-27B:latest","system_fingerprint":"fp_ollama","choices":[{"index":0,"delta":{"content":"."},"finish_reason":null}]}

            data: {"id":"chatcmpl-570","object":"chat.completion.chunk","created":1789194489,"model":"Qwen3.8-27B:latest","system_fingerprint":"fp_ollama","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: {"id":"chatcmpl-570","object":"chat.completion.chunk","created":1789194489,"model":"Qwen3.8-27B:latest","system_fingerprint":"fp_ollama","choices":[],"usage":{"prompt_tokens":13889,"prompt_tokens_details":{"cached_tokens":0},"completion_tokens":33,"total_tokens":13922}}

            data: [DONE]

            """;

        RequestDelegate next = c =>
        {
            c.Response.WriteAsync(sseResponse);  // write through the captured stream
            return Task.CompletedTask;
        };

        var sut = CreateSut(next, tokenEstimator, routingDecisionService, activityMonitor: activityMonitor);

        await sut.InvokeAsync(context);

        activityMonitor.Verify(
            m => m.CompleteRequest(
                requestId,
                It.Is<ActivityLogEntry>(e => e.ActualPromptTokens == 13889 && e.ActualResponseTokens == 33)),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_CompletionRequest_ReportsEntryToStatistics()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var requestId = Guid.NewGuid();
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.StartRequest(RoutingTarget.Local, "llama3", 100)).Returns(requestId);

        var activityStatistics = new Mock<IActivityStatisticsService>();

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService, activityMonitor: activityMonitor, activityStatistics: activityStatistics);

        await sut.InvokeAsync(context);

        activityStatistics.Verify(
            s => s.Add(It.Is<ActivityLogEntry>(e => e.Target == RoutingTarget.Local && e.EstimatedPromptTokens == 100)),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_CompletionRequest_RecordsFullRequestBody()
    {
        const string requestBody = """{ "model": "qwen", "messages": [ { "role": "user", "content": "hello" } ] }""";
        var context = BuildContext("/api/chat", requestBody);

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "qwen", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var inferenceRequestRecorder = new Mock<IInferenceRequestRecorder>();

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService, inferenceRequestRecorder: inferenceRequestRecorder);

        await sut.InvokeAsync(context);

        inferenceRequestRecorder.Verify(
            r => r.Record(
                It.Is<ActivityLogEntry>(e => e.Target == RoutingTarget.Local),
                "/api/chat",
                requestBody),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_OllamaStyleNdjsonResponse_ExtractsTokenCountsFromLastLine()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen", "stream": true }""");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var requestId = Guid.NewGuid();
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.StartRequest(It.IsAny<RoutingTarget>(), "qwen", It.IsAny<int>())).Returns(requestId);

        const string ndjsonResponse =
            """{"model":"Qwen3.8-27B:latest","created_at":"2026-09-12T06:36:27.246174Z","message":{"role":"assistant","content":" action"},"done":false}""" + "\n" +
            """{"model":"Qwen3.8-27B:latest","created_at":"2026-09-12T06:36:27.271626Z","message":{"role":"assistant","content":" taken"},"done":false}""" + "\n" +
            """{"model":"Qwen3.8-27B:latest","created_at":"2026-09-12T06:36:27.2977767Z","message":{"role":"assistant","content":"."},"done":false}""" + "\n" +
            """{"model":"Qwen3.8-27B:latest","created_at":"2026-09-12T06:36:27.3250228Z","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","total_duration":13237340800,"load_duration":1505300,"prompt_eval_count":19553,"prompt_eval_cached_count":0,"prompt_eval_duration":11831027000,"eval_count":26,"eval_duration":667544000}""" + "\n";

        RequestDelegate next = c =>
        {
            c.Response.WriteAsync(ndjsonResponse);  // write through the captured stream
            return Task.CompletedTask;
        };

        var sut = CreateSut(next, tokenEstimator, routingDecisionService, activityMonitor: activityMonitor);

        await sut.InvokeAsync(context);

        activityMonitor.Verify(
            m => m.CompleteRequest(
                requestId,
                It.Is<ActivityLogEntry>(e => e.ActualPromptTokens == 19553 && e.ActualResponseTokens == 26)),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_InterruptedOpenAiSseResponse_MarksSuccessFalseAndRecordsStatus502()
    {
        var context = BuildContext("/v1/chat/completions", """{ "model": "qwen", "stream": true }""");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var requestId = Guid.NewGuid();
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.StartRequest(It.IsAny<RoutingTarget>(), "qwen", It.IsAny<int>())).Returns(requestId);

        // SSE response started and returned status 200, but was cut off before sending [DONE]
        const string incompleteSseResponse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n";

        RequestDelegate next = c =>
        {
            c.Response.StatusCode = 200;
            c.Response.ContentType = "text/event-stream";
            c.Response.WriteAsync(incompleteSseResponse);
            return Task.CompletedTask;
        };

        var sut = CreateSut(next, tokenEstimator, routingDecisionService, activityMonitor: activityMonitor);

        await sut.InvokeAsync(context);

        activityMonitor.Verify(
            m => m.CompleteRequest(
                requestId,
                It.Is<ActivityLogEntry>(e => e.Success == false && e.StatusCode == 502)),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_ClientAbortedRequest_MarksSuccessFalseAndRecordsStatus499()
    {
        var context = BuildContext("/v1/chat/completions", """{ "model": "qwen", "stream": true }""");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var cts = new CancellationTokenSource();
        // Hook context.RequestAborted
        var defaultHttpContext = (DefaultHttpContext)context;
        defaultHttpContext.RequestAborted = cts.Token;

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var requestId = Guid.NewGuid();
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.StartRequest(It.IsAny<RoutingTarget>(), "qwen", It.IsAny<int>())).Returns(requestId);

        RequestDelegate next = c =>
        {
            c.Response.StatusCode = 200;
            c.Response.ContentType = "text/event-stream";
            // Client disconnects mid-stream
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        var sut = CreateSut(next, tokenEstimator, routingDecisionService, activityMonitor: activityMonitor);

        await sut.InvokeAsync(context);

        activityMonitor.Verify(
            m => m.CompleteRequest(
                requestId,
                It.Is<ActivityLogEntry>(e => e.Success == false && e.StatusCode == 499)),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_YarpForwarderError_ClientCanceled_MarksSuccessFalseAndRecordsStatus499()
    {
        var context = BuildContext("/v1/chat/completions", """{ "model": "qwen", "stream": true }""");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var requestId = Guid.NewGuid();
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.StartRequest(It.IsAny<RoutingTarget>(), "qwen", It.IsAny<int>())).Returns(requestId);

        var forwarderErrorFeatureMock = new Mock<IForwarderErrorFeature>();
        forwarderErrorFeatureMock.SetupGet(f => f.Error).Returns(ForwarderError.ResponseBodyClient);

        RequestDelegate next = c =>
        {
            c.Response.StatusCode = 200;
            c.Response.ContentType = "text/event-stream";
            c.Features.Set<IForwarderErrorFeature>(forwarderErrorFeatureMock.Object);
            return Task.CompletedTask;
        };

        var sut = CreateSut(next, tokenEstimator, routingDecisionService, activityMonitor: activityMonitor);

        await sut.InvokeAsync(context);

        activityMonitor.Verify(
            m => m.CompleteRequest(
                requestId,
                It.Is<ActivityLogEntry>(e => e.Success == false && e.StatusCode == 499)),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_InterruptedNdjsonResponse_MarksSuccessFalseAndRecordsStatus502()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen", "stream": true }""");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var requestId = Guid.NewGuid();
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.StartRequest(It.IsAny<RoutingTarget>(), "qwen", It.IsAny<int>())).Returns(requestId);

        // Stream cut off with done: false, no done: true
        const string ndjsonResponse =
            """{"model":"Qwen","message":{"content":"Hi"},"done":false}""" + "\n";

        RequestDelegate next = c =>
        {
            c.Response.StatusCode = 200;
            c.Response.ContentType = "application/x-ndjson";
            c.Response.WriteAsync(ndjsonResponse);
            return Task.CompletedTask;
        };

        var sut = CreateSut(next, tokenEstimator, routingDecisionService, activityMonitor: activityMonitor);

        await sut.InvokeAsync(context);

        activityMonitor.Verify(
            m => m.CompleteRequest(
                requestId,
                It.Is<ActivityLogEntry>(e => e.Success == false && e.StatusCode == 502)),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_CompletedSseResponse_WithClientDisconnectAfterward_MarksSuccessTrueAndRecordsStatus200()
    {
        var context = BuildContext("/v1/chat/completions", """{ "model": "qwen", "stream": true }""");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var requestId = Guid.NewGuid();
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.StartRequest(It.IsAny<RoutingTarget>(), "qwen", It.IsAny<int>())).Returns(requestId);

        var forwarderErrorFeatureMock = new Mock<IForwarderErrorFeature>();
        forwarderErrorFeatureMock.SetupGet(f => f.Error).Returns(ForwarderError.ResponseBodyClient);

        RequestDelegate next = async c =>
        {
            c.Response.StatusCode = 200;
            c.Response.ContentType = "text/event-stream";
            await c.Response.WriteAsync("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":108500,\"completion_tokens\":416}}\n\ndata: [DONE]\n\n");
            // Client disconnects at or right after EOF
            c.Features.Set<IForwarderErrorFeature>(forwarderErrorFeatureMock.Object);
        };

        var sut = CreateSut(next, tokenEstimator, routingDecisionService, activityMonitor: activityMonitor);

        await sut.InvokeAsync(context);

        activityMonitor.Verify(
            m => m.CompleteRequest(
                requestId,
                It.Is<ActivityLogEntry>(e => e.Success == true && e.StatusCode == 200 && e.ActualResponseTokens == 416)),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_CompletedNdjsonResponse_WithClientDisconnectAfterward_MarksSuccessTrueAndRecordsStatus200()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen", "stream": true }""");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var requestId = Guid.NewGuid();
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.StartRequest(It.IsAny<RoutingTarget>(), "qwen", It.IsAny<int>())).Returns(requestId);

        var forwarderErrorFeatureMock = new Mock<IForwarderErrorFeature>();
        forwarderErrorFeatureMock.SetupGet(f => f.Error).Returns(ForwarderError.ResponseBodyClient);

        const string ndjsonResponse =
            """{"model":"Qwen","message":{"content":"Hi"},"done":false}""" + "\n" +
            """{"model":"Qwen","done":true,"prompt_eval_count":100,"eval_count":50}""" + "\n";

        RequestDelegate next = async c =>
        {
            c.Response.StatusCode = 200;
            c.Response.ContentType = "application/x-ndjson";
            await c.Response.WriteAsync(ndjsonResponse);
            c.Features.Set<IForwarderErrorFeature>(forwarderErrorFeatureMock.Object);
        };

        var sut = CreateSut(next, tokenEstimator, routingDecisionService, activityMonitor: activityMonitor);

        await sut.InvokeAsync(context);

        activityMonitor.Verify(
            m => m.CompleteRequest(
                requestId,
                It.Is<ActivityLogEntry>(e => e.Success == true && e.StatusCode == 200 && e.ActualResponseTokens == 50)),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_CompletionRequest_EmitsTransparencyHeaders()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen2.5-coder:32b", "prompt": "Hello" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(1420);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(1420, "qwen2.5-coder:32b", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Response.Headers["X-OllamaRouter-Target"]);
        Assert.Equal("qwen2.5-coder:32b", context.Response.Headers["X-OllamaRouter-Model"]);
        Assert.Equal("1420", context.Response.Headers["X-OllamaRouter-Estimated-Tokens"]);
    }

    [Fact]
    public async Task InvokeAsync_TargetPinningHeader_WhenEnabled_PinsTargetWithoutDecideAsync()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Hello" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "Cloud";

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(50);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        var targetAvailability = new Mock<ITargetAvailabilityService>();
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Cloud)).Returns(true);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService, targetAvailability: targetAvailability);

        await sut.InvokeAsync(context);

        Assert.Equal("Cloud", context.Response.Headers["X-OllamaRouter-Target"]);
        // When pinned and enabled, DecideAsync must not be called
        routingDecisionService.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_TargetPinningHeader_WhenDisabled_FallsBackToDecideAsync()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Hello" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "Remote";

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(50);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(50, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var targetAvailability = new Mock<ITargetAvailabilityService>();
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Remote)).Returns(false);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Local)).Returns(true);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService, targetAvailability: targetAvailability);

        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Response.Headers["X-OllamaRouter-Target"]);
        // Because Remote was disabled, it fell back to dynamic routing
        routingDecisionService.Verify(r => r.DecideAsync(50, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Build a liveness mock where every target is Offline: EnsureKnownAsync returns Offline,
    // IsOnline is false, and the entries expose an offline state with a last error.
    private static Mock<ITargetLivenessService> AllOfflineLiveness()
    {
        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Offline);
        liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(false);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow);
        liveness.Setup(t => t.GetFleetTargets())
            .Returns(new[] { RoutingTarget.Local, RoutingTarget.Remote });
        liveness.Setup(t => t.GetEntries())
            .Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [RoutingTarget.Local] = new(RoutingTarget.Local, TargetLivenessState.Offline, "connection error", DateTimeOffset.UtcNow),
                [RoutingTarget.Remote] = new(RoutingTarget.Remote, TargetLivenessState.Offline, "timeout", DateTimeOffset.UtcNow)
            });
        liveness.Setup(t => t.GetSnapshot())
            .Returns(new TargetLivenessSnapshot(
            [
                new(RoutingTarget.Local, TargetLivenessState.Offline, "connection error", DateTimeOffset.UtcNow),
                new(RoutingTarget.Remote, TargetLivenessState.Offline, "timeout", DateTimeOffset.UtcNow)
            ]));
        return liveness;
    }

    private static (OllamaRouterOptions Options, Microsoft.Extensions.Options.IOptions<OllamaRouterOptions> OptionsWrapper) OptionsWithCloud(string? cloudModel)
    {
        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11435", RemoteUrl = "http://remote:11434" };
        if (cloudModel is not null)
        {
            options.Models["llama3"] = new ModelThresholds { CloudModel = cloudModel };
        }

        return (options, MsOptions.Create(options));
    }

    [Fact]
    public async Task AllOffline_CloudDisabled_Returns503WithDiagnostics_ForwardsNothing()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var targetAvailability = new Mock<ITargetAvailabilityService>();
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Local)).Returns(true);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Remote)).Returns(true);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Cloud)).Returns(false);

        var activityMonitor = new Mock<IActivityMonitorService>();

        var (options, optionsWrapper) = OptionsWithCloud(cloudModel: null);
        var liveness = AllOfflineLiveness();

        var nextCalled = false;
        var forwarder = new Mock<IHttpForwarder>();
        // Manually wire the middleware so we can override the options instance.
        var sut = new OllamaRoutingMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            tokenEstimator.Object,
            routingDecisionService.Object,
            new Mock<IModelCatalogCacheService>().Object,
            activityMonitor.Object,
            new Mock<IActivityStatisticsService>().Object,
            new Mock<IInferenceRequestRecorder>().Object,
            targetAvailability.Object,
            liveness.Object,
            new ModelCapabilityEngine(new Mock<IOllamaModelCatalogClient>().Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ModelCapabilityEngine>.Instance),
            optionsWrapper,
            NullLogger<OllamaRoutingMiddleware>.Instance,
            forwarder.Object,
            new TargetFailureStreakService(),
            new Mock<ITargetExecutionTracker>().Object);

        await sut.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(nextCalled);
        Assert.False(context.Request.Headers.ContainsKey("X-Ollama-Target"));
        activityMonitor.Verify(m => m.StartRequest(It.IsAny<RoutingTarget>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        // The all-offline short-circuit returns before any SendAsync is issued.
        forwarder.Verify(f => f.SendAsync(It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(), It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()), Times.Never);

        responseBody.Seek(0, SeekOrigin.Begin);
        using var bodyReader = new StreamReader(responseBody);
        var bodyJson = bodyReader.ReadToEnd();
        var json = JsonDocument.Parse(bodyJson).RootElement;
        Assert.Equal("All fleet targets are offline or disabled.", json.GetProperty("error").GetString());
        Assert.False(json.GetProperty("cloud_enabled").GetBoolean());
        var targets = json.GetProperty("targets").EnumerateArray().ToList();
        Assert.Equal(2, targets.Count);
        Assert.Equal("Local", targets[0].GetProperty("name").GetString());
        Assert.Equal("offline", targets[0].GetProperty("state").GetString());
        Assert.Equal("connection error", targets[0].GetProperty("last_error").GetString());
        Assert.Equal("Remote", targets[1].GetProperty("name").GetString());
        Assert.Equal("offline", targets[1].GetProperty("state").GetString());
        Assert.Equal("timeout", targets[1].GetProperty("last_error").GetString());
    }

    [Fact]
    public async Task AllOffline_CloudAvailable_HandsOffToCloudEvenWhenRelayIsOffline()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");
        var requestBytes = new MemoryStream();

        var tokenEstimator = new Mock<ITokenEstimator>();        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var targetAvailability = new Mock<ITargetAvailabilityService>();
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Local)).Returns(true);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Remote)).Returns(true);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Cloud)).Returns(true);

        var (options, optionsWrapper) = OptionsWithCloud(cloudModel: "llama3-cloud");
        var liveness = AllOfflineLiveness(); // Even the relay (Local) is offline — Q4-A: hand off anyway.

        string? capturedModel = null;
        string? capturedTargetHeader = null;
        bool nextCalled = false;
        RequestDelegate next = c =>
        {
            nextCalled = true;
            capturedTargetHeader = c.Request.Headers["X-Ollama-Target"].ToString();
            c.Request.Body.Position = 0;
            using var reader = new StreamReader(c.Request.Body);
            var body = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(body);
            capturedModel = doc.RootElement.GetProperty("model").GetString();
            return Task.CompletedTask;
        };
        var sut = new OllamaRoutingMiddleware(
            next,
            tokenEstimator.Object,
            routingDecisionService.Object,
            new Mock<IModelCatalogCacheService>().Object,
            new Mock<IActivityMonitorService>().Object,
            new Mock<IActivityStatisticsService>().Object,
            new Mock<IInferenceRequestRecorder>().Object,
            targetAvailability.Object,
            liveness.Object,
            new ModelCapabilityEngine(new Mock<IOllamaModelCatalogClient>().Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ModelCapabilityEngine>.Instance),
            optionsWrapper,
            NullLogger<OllamaRoutingMiddleware>.Instance,
            CreateForwarder(next).Object,
            new TargetFailureStreakService(),
            new Mock<ITargetExecutionTracker>().Object);

        await sut.InvokeAsync(context);

        Assert.True(nextCalled);
        // Cloud is relayed through the Local-named cluster.
        Assert.Equal("Local", capturedTargetHeader);
        Assert.Equal("llama3-cloud", capturedModel);
        Assert.Equal("Cloud", context.Response.Headers["X-OllamaRouter-Target"]);
    }

    [Fact]
    public async Task OneTargetOnline_RoutesNormally_No503()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        // Local is online, Remote is offline → the fleet is NOT all offline.
        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        liveness.Setup(t => t.IsOnline(RoutingTarget.Local)).Returns(true);
        liveness.Setup(t => t.IsOnline(RoutingTarget.Remote)).Returns(false);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow);
        liveness.Setup(t => t.GetFleetTargets()).Returns(new[] { RoutingTarget.Local, RoutingTarget.Remote });

        var nextCalled = false;
        var sut = CreateSut(_ => { nextCalled = true; return Task.CompletedTask; }, tokenEstimator, routingDecisionService,
            targetLiveness: liveness);

        await sut.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
    }

    [Fact]
    public async Task PinnedTargetOffline_FallsBackToDynamicRouting()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "Remote";

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(50);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(50, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var targetAvailability = new Mock<ITargetAvailabilityService>();
        targetAvailability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);

        // Pinned Remote is offline → dynamic routing is used; Local is online so no 503.
        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(t => t.EnsureKnownAsync(RoutingTarget.Remote, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Offline);
        liveness.Setup(t => t.EnsureKnownAsync(RoutingTarget.Local, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        liveness.Setup(t => t.IsOnline(RoutingTarget.Local)).Returns(true);
        liveness.Setup(t => t.IsOnline(RoutingTarget.Remote)).Returns(false);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow);
        liveness.Setup(t => t.GetFleetTargets()).Returns(new[] { RoutingTarget.Local, RoutingTarget.Remote });

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService,
            targetAvailability: targetAvailability, targetLiveness: liveness);

        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
        routingDecisionService.Verify(r => r.DecideAsync(50, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartupUnknownWithinGraceWindow_IsRoutable_Not503()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var targetAvailability = new Mock<ITargetAvailabilityService>();
        targetAvailability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);

        // Unknown liveness, service just started (within the 10 s grace window), no Cloud.
        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Unknown);
        liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(false);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow);
        liveness.Setup(t => t.GetFleetTargets()).Returns(new[] { RoutingTarget.Local, RoutingTarget.Remote });
        liveness.Setup(t => t.GetEntries())
            .Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [RoutingTarget.Local] = new(RoutingTarget.Local, TargetLivenessState.Unknown, null, DateTimeOffset.MinValue),
                [RoutingTarget.Remote] = new(RoutingTarget.Remote, TargetLivenessState.Unknown, null, DateTimeOffset.MinValue)
            });

        var (options, optionsWrapper) = OptionsWithCloud(cloudModel: null);
        var nextCalled = false;
        RequestDelegate graceNext = c => { nextCalled = true; return Task.CompletedTask; };
        var sut = new OllamaRoutingMiddleware(
            graceNext,
            tokenEstimator.Object,
            routingDecisionService.Object,
            new Mock<IModelCatalogCacheService>().Object,
            new Mock<IActivityMonitorService>().Object,
            new Mock<IActivityStatisticsService>().Object,
            new Mock<IInferenceRequestRecorder>().Object,
            targetAvailability.Object,
            liveness.Object,
            new ModelCapabilityEngine(new Mock<IOllamaModelCatalogClient>().Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ModelCapabilityEngine>.Instance),
            optionsWrapper,
            NullLogger<OllamaRoutingMiddleware>.Instance,
            CreateForwarder(graceNext).Object,
            new TargetFailureStreakService(),
            new Mock<ITargetExecutionTracker>().Object);

        await sut.InvokeAsync(context);

        // During the grace window the Unknown target is treated as routable, so it forwards.
        Assert.True(nextCalled);
        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
    }

    [Fact]
    public async Task StreamingFailure_DoesNotTripLivenessState_NoFurtherLivenessCalls()
    {
        var context = BuildContext("/v1/chat/completions", """{ "model": "qwen", "stream": true }""");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow);
        liveness.Setup(t => t.GetFleetTargets()).Returns(new[] { RoutingTarget.Local, RoutingTarget.Remote });

        // A streamed response that fails mid-stream (YARP sets a ForwarderError).
        var forwarderErrorFeatureMock = new Mock<IForwarderErrorFeature>();
        forwarderErrorFeatureMock.SetupGet(f => f.Error).Returns(ForwarderError.ResponseBodyCanceled);

        RequestDelegate next = c =>
        {
            c.Response.StatusCode = 200;
            c.Response.ContentType = "text/event-stream";
            c.Response.WriteAsync("data: {\"choices\":[]}\n\n");
            c.Features.Set<IForwarderErrorFeature>(forwarderErrorFeatureMock.Object);
            return Task.CompletedTask;
        };

        var sut = CreateSut(next, tokenEstimator, routingDecisionService, targetLiveness: liveness);

        await sut.InvokeAsync(context);

        // The liveness service is consulted only up front (for the pinned/chosen gate) and as
        // read-only probes while the dispatch loop ranks candidates — it is NEVER mutated after
        // the streamed failure (the breaker is inference-isolated). Mid-stream drops never
        // apply a liveness strike (that is the streak counter's job, a separate invariant).
        liveness.Verify(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()), Times.Exactly(1));
        liveness.Verify(t => t.MarkHardFailureAsync(It.IsAny<RoutingTarget>()), Times.Never);
    }

    // ================================================================================
    // Dynamic context sizing (Phase 2): the decided target's capability drives the
    // options.num_ctx bucket injection for the four inference endpoints.
    // ================================================================================

    private static ModelCapabilityEngine CreateCapabilityEngine(out Mock<IOllamaModelCatalogClient> catalogClient)
    {
        catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient
            .Setup(c => c.GetTagsWithDigestAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new JsonArray(), "digest-1"));

        return new ModelCapabilityEngine(
            catalogClient.Object,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ModelCapabilityEngine>.Instance);
    }

    private static OllamaRoutingMiddleware CreateSizingSut(
        RequestDelegate next,
        Mock<ITokenEstimator> tokenEstimator,
        Mock<IRoutingDecisionService> routingDecisionService,
        OllamaRouterOptions options,
        ModelCapabilityEngine capabilityEngine,
        Mock<IModelCatalogCacheService> modelCatalogCache,
        Mock<IActivityMonitorService>? activityMonitor = null,
        bool enableDynamicContext = true)
    {
        if (enableDynamicContext && !options.DynamicContext.Enabled)
        {
            options.DynamicContext.Enabled = true;
        }

        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);

        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddHours(1));
        liveness.Setup(t => t.GetFleetTargets()).Returns(new[] { RoutingTarget.Local, RoutingTarget.Remote });

        return new OllamaRoutingMiddleware(
            next,
            tokenEstimator.Object,
            routingDecisionService.Object,
            modelCatalogCache.Object,
            (activityMonitor ?? new Mock<IActivityMonitorService>()).Object,
            new Mock<IActivityStatisticsService>().Object,
            new Mock<IInferenceRequestRecorder>().Object,
            availability.Object,
            liveness.Object,
            capabilityEngine,
            MsOptions.Create(options),
            NullLogger<OllamaRoutingMiddleware>.Instance,
            CreateForwarder(next).Object,
            new TargetFailureStreakService(),
            new Mock<ITargetExecutionTracker>().Object);
            }

    private static Mock<IModelCatalogCacheService> DigestCache(string digest)
    {
        var cache = new Mock<IModelCatalogCacheService>();
        cache.Setup(c => c.GetCurrentDigestAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(digest);
        return cache;
    }

    private static RequestDelegate CaptureBodyDelegate(string[] captured)
    {
        return async c =>
        {
            c.Request.Body.Position = 0;
            using var reader = new StreamReader(c.Request.Body);
            captured[0] = await reader.ReadToEndAsync();
        };
    }

    [Fact]
    public async Task Sizing_MoePrompt_ModelfilePin221184_Injects221184_AndEmitsBaselineMode()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen3.6-35b-a3b", "messages": [ { "role": "user", "content": "a long prompt" } ] }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(200_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(200_000, "qwen3.6-35b-a3b", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);
        // 35B-A3B MoE: expert_count present; the Modelfile pins num_ctx 221184 (hierarchy
        // level 1). No GGUF context_length is discoverable, so the two-bucket ladder (task #26)
        // uses the pre-configured 221,184 as BOTH the baseline and (floored) the extensible
        // limit — the engine expands to 221,184 (never below the node's own pin).
        catalogClient
            .Setup(c => c.GetShowAsync("http://localhost:11435", "qwen3.6-35b-a3b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject
            {
                ["parameters"] = "num_ctx 221184\nnum_predict 4096",
                ["model_info"] = new JsonObject { ["qwen35.expert_count"] = 128 }
            });

        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11435" };
        options.Targets.Add(new OllamaTargetOptions { Name = "Local", Url = "http://localhost:11435", MaxVramMB = 98304 });

        var captured = new string[1];
        var sut = CreateSizingSut(
            CaptureBodyDelegate(captured),
            tokenEstimator,
            routingDecisionService,
            options,
            capabilityEngine,
            DigestCache("digest-1"));

        await sut.InvokeAsync(context);

        Assert.NotNull(captured[0]);
        using var doc = JsonDocument.Parse(captured[0]!);
        Assert.Equal(221_184, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        // The routing decision is untouched: the bucket is hosted by the current (96 GB) target.
        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
        Assert.Equal("221184", context.Response.Headers["X-OllamaRouter-Context-Size"]);
        // The estimated-tokens telemetry header matches the estimator output (overhead factor 1.0).
        Assert.Equal("200000", context.Response.Headers["X-OllamaRouter-Estimated-Tokens"]);
        // Task #26 superset mode vocabulary: no Auto-Shrink manager in this pipeline, so the
        // engine mode is emitted (Baseline: 200k + 8,192 = 208,192 still fits the 221,184
        // pre-configured baseline bucket, so the baseline is injected).
        Assert.Equal("Baseline", context.Response.Headers["X-OllamaRouter-Context-Mode"]);
    }

    [Fact]
    public async Task Sizing_MoePromptAboveBaseline_NativeContext_ExpandsToExtensibleBucket()
    {
        // AC1 scenario: Modelfile num_ctx 184,320 (pre-configured baseline) with a 262,144
        // native GGUF context limit; a 205k prompt (205,000 + 8,192 = 213,192 > 184,320)
        // expands to the single extensible bucket (262,144).
        var context = BuildContext("/api/chat", """{ "model": "moe-262k", "messages": [ { "role": "user", "content": "a long prompt" } ] }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(205_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(205_000, "moe-262k", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);
        catalogClient
            .Setup(c => c.GetShowAsync("http://localhost:11435", "moe-262k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject
            {
                ["parameters"] = "num_ctx 184320",
                ["model_info"] = new JsonObject
                {
                    ["qwen35.expert_count"] = 128,
                    ["qwen35.context_length"] = 262_144
                }
            });

        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11435" };
        options.Targets.Add(new OllamaTargetOptions { Name = "Local", Url = "http://localhost:11435", MaxVramMB = 98304 });

        var captured = new string[1];
        var sut = CreateSizingSut(
            CaptureBodyDelegate(captured),
            tokenEstimator,
            routingDecisionService,
            options,
            capabilityEngine,
            DigestCache("digest-1"));

        await sut.InvokeAsync(context);

        Assert.NotNull(captured[0]);
        using var doc = JsonDocument.Parse(captured[0]!);
        // Expanded to the extensible bucket: the model's native 262,144 context limit.
        Assert.Equal(262_144, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal("262144", context.Response.Headers["X-OllamaRouter-Context-Size"]);
        // No Auto-Shrink manager in this pipeline: the engine mode (Expanded) is emitted.
        Assert.Equal("Expanded", context.Response.Headers["X-OllamaRouter-Context-Mode"]);
    }

    [Fact]
    public async Task Sizing_DensePrompt_NoClientNumCtx_PayloadUntouched_NoModeHeader()
    {
        // AC6: dense model, no client num_ctx (and client value ≤ pre-configured) → the
        // payload is byte-identical, no num_ctx injected, no mode header, no lease.
        const string body = """{ "model": "dense-49k", "messages": [ { "role": "user", "content": "hi" } ] }""";
        var context = BuildContext("/api/chat", body);

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(5_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(5_000, "dense-49k", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);
        catalogClient
            .Setup(c => c.GetShowAsync("http://localhost:11435", "dense-49k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject
            {
                ["parameters"] = "num_ctx 49152",
                ["model_info"] = new JsonObject { ["general.parameter_count"] = 27_800_000_000L }
            });

        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11435" };
        options.Targets.Add(new OllamaTargetOptions { Name = "Local", Url = "http://localhost:11435", MaxVramMB = 16384 });

        var captured = new string[1];
        var sut = CreateSizingSut(
            CaptureBodyDelegate(captured),
            tokenEstimator,
            routingDecisionService,
            options,
            capabilityEngine,
            DigestCache("digest-1"));

        await sut.InvokeAsync(context);

        // Byte-identical payload: dense pass-through never touches options.num_ctx.
        Assert.Equal(body, captured[0]);
        Assert.False(context.Response.Headers.ContainsKey("X-OllamaRouter-Context-Size"));
        Assert.False(context.Response.Headers.ContainsKey("X-OllamaRouter-Context-Mode"));
    }

    [Fact]
    public async Task Sizing_DenseClampDisabled_OversizedClientNumCtx_PayloadUntouched()
    {
        // AC7: DenseClampEnabled = false → even an oversized static client num_ctx leaves the
        // payload untouched (pure dense pass-through).
        var context = BuildContext("/api/chat", """{ "model": "dense-49k", "messages": [ { "role": "user", "content": "hi" } ], "options": { "num_ctx": 131072 } }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(5_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(5_000, "dense-49k", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);
        catalogClient
            .Setup(c => c.GetShowAsync("http://localhost:11435", "dense-49k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject
            {
                ["parameters"] = "num_ctx 49152",
                ["model_info"] = new JsonObject { ["general.parameter_count"] = 27_800_000_000L }
            });

        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11435" };
        options.Targets.Add(new OllamaTargetOptions { Name = "Local", Url = "http://localhost:11435", MaxVramMB = 16384 });
        options.DynamicContext.DenseClampEnabled = false;

        var captured = new string[1];
        var sut = CreateSizingSut(
            CaptureBodyDelegate(captured),
            tokenEstimator,
            routingDecisionService,
            options,
            capabilityEngine,
            DigestCache("digest-1"));

        await sut.InvokeAsync(context);

        Assert.NotNull(captured[0]);
        using var doc = JsonDocument.Parse(captured[0]!);
        // The client's 131,072 is preserved (clamping disabled).
        Assert.Equal(131_072, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.False(context.Response.Headers.ContainsKey("X-OllamaRouter-Context-Mode"));
    }

    [Fact]
    public async Task Sizing_DenseDesktop2kPrompt_Client131072_ClampedTo49152_RoutingUnchanged()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen3.8-27b", "messages": [ { "role": "user", "content": "hi" } ], "options": { "num_ctx": 131072 } }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(2_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(2_000, "qwen3.8-27b", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);
        // 27B dense: parameter count above the 14B threshold, no experts. The Modelfile pins
        // num_ctx 49152 — the pre-configured context that the task-#26 clamp targets.
        catalogClient
            .Setup(c => c.GetShowAsync("http://localhost:11435", "qwen3.8-27b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject
            {
                ["parameters"] = "num_ctx 49152\nnum_predict 4096",
                ["model_info"] = new JsonObject { ["general.parameter_count"] = 27_800_000_000L }
            });

        var options = new OllamaRouterOptions { Targets = [ new () { Name = "Local", Url = "http://localhost:11435",  MaxVramMB = 16384 }] };

        var captured = new string[1];
        var sut = CreateSizingSut(
            CaptureBodyDelegate(captured),
            tokenEstimator,
            routingDecisionService,
            options,
            capabilityEngine,
            DigestCache("digest-1"));

        await sut.InvokeAsync(context);

        Assert.NotNull(captured[0]);
        using var doc = JsonDocument.Parse(captured[0]!);
        // Task #26 dense clamp-only: the oversized static client value is clamped DOWN to the
        // node's pre-configured 49,152 context (one-time KV reallocation; never raised).
        Assert.Equal(49_152, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal("49152", context.Response.Headers["X-OllamaRouter-Context-Size"]);
        // Routing is unchanged: still the Desktop-equivalent 16 GB target.
        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
        Assert.Equal("Local", context.Response.Headers["X-OllamaRouter-Target"]);
    }

    [Fact]
    public async Task Fallback_WhenLargestCapacityOverflow_ClampsNumCtxAndSetsHeaders()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen3.8-27b", "prompt": "huge prompt" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(200_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService.Setup(r => r.DecideDetailedAsync(200_000, "qwen3.8-27b", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingDecisionResult(
                RoutingTarget.Remote,
                IsFallback: true,
                FallbackStrategy: "LargestCapacityOverflow",
                TargetCMax: 65536));

        var capabilityEngine = CreateCapabilityEngine(out _);
        var options = new OllamaRouterOptions { RemoteUrl = "http://remoteserver:11434" };
        options.Targets.Add(new OllamaTargetOptions { Name = "Remote", Url = "http://remoteserver:11434", MaxVramMB = 98304 });

        var captured = new string[1];
        var sut = CreateSizingSut(
            CaptureBodyDelegate(captured),
            tokenEstimator,
            routingDecisionService,
            options,
            capabilityEngine,
            DigestCache("digest-1"),
            enableDynamicContext: false); // Verifies fallback clamping works even when DynamicContext is disabled

        await sut.InvokeAsync(context);

        Assert.NotNull(captured[0]);
        using var doc = JsonDocument.Parse(captured[0]!);
        // Clamped to TargetCMax (65536)
        Assert.Equal(65_536, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal("65536", context.Response.Headers[OllamaReverseProxyConfig.TelemetryContextSizeHeader]);
        Assert.Equal("LargestCapacityOverflow", context.Response.Headers[OllamaReverseProxyConfig.FallbackHeader]);
        Assert.Equal("Requested 200000 exceeds capacity 65536; clamped to ceiling.", context.Response.Headers[OllamaReverseProxyConfig.ContextWarningHeader]);
        Assert.Equal("Remote", context.Response.Headers[OllamaReverseProxyConfig.PublicTargetHeader]);
    }

    [Fact]
    public async Task Fallback_WhenTargetIsCloud_DoesNotApplyClampingOrFallbackHeaders()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen3.8-27b", "prompt": "huge prompt" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(200_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        // Decision was Cloud (e.g. cloud overflow)
        routingDecisionService.Setup(r => r.DecideDetailedAsync(200_000, "qwen3.8-27b", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingDecisionResult(
                RoutingTarget.Cloud,
                IsFallback: false));

        var capabilityEngine = CreateCapabilityEngine(out _);
        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11434" };
        options.Targets.Add(new OllamaTargetOptions { Name = "Local", Url = "http://localhost:11434" });

        var captured = new string[1];
        var sut = CreateSizingSut(
            CaptureBodyDelegate(captured),
            tokenEstimator,
            routingDecisionService,
            options,
            capabilityEngine,
            DigestCache("digest-1"),
            enableDynamicContext: false);

        await sut.InvokeAsync(context);

        Assert.False(context.Response.Headers.ContainsKey(OllamaReverseProxyConfig.FallbackHeader));
        Assert.False(context.Response.Headers.ContainsKey(OllamaReverseProxyConfig.ContextWarningHeader));
        Assert.Equal("Cloud", context.Response.Headers[OllamaReverseProxyConfig.PublicTargetHeader]);
    }

    [Fact]
    public async Task Fallback_WhenFleetTargetFailsUncommittedAndFailsOverToCloud_RemovesFallbackHeadersInOnStarting()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen3.8-27b", "prompt": "huge prompt" }""");
        context.Response.Body = new MemoryStream();

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(200_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        // Decision was Remote (overflow fallback)
        routingDecisionService.Setup(r => r.DecideDetailedAsync(200_000, "qwen3.8-27b", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingDecisionResult(
                RoutingTarget.Remote,
                IsFallback: true,
                FallbackStrategy: "LargestCapacityOverflow",
                TargetCMax: 65536));

        var (availability, liveness) = OnlineMocks();
        var options = new OllamaRouterOptions
        {
            LocalUrl = FailoverLocalUrl,
            RemoteUrl = FailoverRemoteUrl,
            CloudOverflow = new CloudOverflowOptions { Enabled = true, DefaultModel = "cloud-model" }
        };

        var forwarder = new Mock<IHttpForwarder>();
        var sentUrls = new List<string>();
        WireForwarder(forwarder, new List<Func<HttpContext, string, ForwarderError>>
        {
            (ctx, url) => Uncommitted(ctx, url), // Remote fails uncommitted
            (ctx, url) => Uncommitted(ctx, url), // Local fails uncommitted
            (ctx, url) =>
            {
                // Cloud relay writes response, triggering OnStarting
                ctx.Response.StatusCode = 200;
                ctx.Response.WriteAsync(DoneBody).GetAwaiter().GetResult();
                return ForwarderError.None;
            }
        }, sentUrls);

        var streak = new TargetFailureStreakService();
        var sut = BuildFailoverSut(tokenEstimator, routingDecisionService, availability, liveness, options, forwarder, streak);

        await sut.InvokeAsync(context);

        Assert.Equal("Cloud", context.Response.Headers[OllamaReverseProxyConfig.PublicTargetHeader]);
        Assert.False(context.Response.Headers.ContainsKey(OllamaReverseProxyConfig.FallbackHeader));
        Assert.False(context.Response.Headers.ContainsKey(OllamaReverseProxyConfig.ContextWarningHeader));
        Assert.False(context.Response.Headers.ContainsKey(OllamaReverseProxyConfig.TelemetryContextSizeHeader));
    }

    [Theory]
    [InlineData("/api/chat")]
    [InlineData("/api/generate")]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/completions")]
    public async Task Sizing_RewritesAllFourInferenceEndpoints(string path)
    {
        var context = BuildContext(path, """{ "model": "qwen3.6-35b-a3b", "prompt": "hello" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100_000, "qwen3.6-35b-a3b", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);
        catalogClient
            .Setup(c => c.GetShowAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject { ["model_info"] = new JsonObject { ["qwen35.expert_count"] = 128 } });

        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11435" };

        var captured = new string[1];
        var sut = CreateSizingSut(
            CaptureBodyDelegate(captured),
            tokenEstimator,
            routingDecisionService,
            options,
            capabilityEngine,
            DigestCache("digest-1"));

        await sut.InvokeAsync(context);

        Assert.NotNull(captured[0]);
        using var doc = JsonDocument.Parse(captured[0]!);
        // 100k tokens on the MoE target (expert_count 128): no Modelfile num_ctx and no
        // discoverable GGUF context_length, so the two-bucket ladder (task #26) uses the
        // assumed 128k fallback as BOTH baseline and extensible limit; the 108,192 requirement
        // fits the baseline bucket, so 131,072 is injected.
        Assert.Equal(131_072, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task Sizing_Disabled_NoRewriteAndNoCatalogInteraction()
    {
        var context = BuildContext("/api/chat", """{ "model": "m", "prompt": "hi" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "m", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);

        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11435" };
        options.DynamicContext.Enabled = false;

        var captured = new string[1];
        var cache = DigestCache("digest-1");
        var sut = CreateSizingSut(
            CaptureBodyDelegate(captured),
            tokenEstimator,
            routingDecisionService,
            options,
            capabilityEngine,
            cache,
            enableDynamicContext: false);

        await sut.InvokeAsync(context);

        Assert.Equal("""{ "model": "m", "prompt": "hi" }""", captured[0]);
        Assert.False(context.Response.Headers.ContainsKey("X-OllamaRouter-Context-Size"));
        // No capability/catalog interaction when disabled.
        catalogClient.Verify(c => c.GetTagsWithDigestAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        catalogClient.Verify(c => c.GetShowAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("GET", "/api/tags")]
    [InlineData("GET", "/api/ps")]
    [InlineData("POST", "/api/embeddings")]
    public async Task Sizing_NonInferenceEndpoints_NeverRewritten(string method, string path)
    {
        const string body = """{ "model": "m", "input": "hello" }""";
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));

        var tokenEstimator = new Mock<ITokenEstimator>();
        var routingDecisionService = new Mock<IRoutingDecisionService>();

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);

        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11435" };

        var captured = new string[1];
        var cache = DigestCache("digest-1");
        var sut = CreateSizingSut(
            CaptureBodyDelegate(captured),
            tokenEstimator,
            routingDecisionService,
            options,
            capabilityEngine,
            cache);

        await sut.InvokeAsync(context);

        Assert.Equal(body, captured[0]);
        Assert.False(context.Response.Headers.ContainsKey("X-OllamaRouter-Context-Size"));
        catalogClient.Verify(c => c.GetTagsWithDigestAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        cache.Verify(c => c.GetCurrentDigestAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sizing_CloudTarget_BodyOnlyModelRewritten_NoNumCtxInjection()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);

        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11435", RemoteUrl = "http://remote:11434" };
        options.Models["llama3"] = new ModelThresholds { CloudModel = "llama3-cloud" };

        var captured = new string[1];
        var cache = DigestCache("digest-1");

        // All fleet targets are offline → the request is handed off to Cloud Overflow.
        var liveness = AllOfflineLiveness();
        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);
        RequestDelegate cloudCaptureNext = CaptureBodyDelegate(captured);
        var sut = new OllamaRoutingMiddleware(
            cloudCaptureNext,
            tokenEstimator.Object,
            routingDecisionService.Object,
            cache.Object,
            new Mock<IActivityMonitorService>().Object,
            new Mock<IActivityStatisticsService>().Object,
            new Mock<IInferenceRequestRecorder>().Object,
            availability.Object,
            liveness.Object,
            capabilityEngine,
            MsOptions.Create(options),
            NullLogger<OllamaRoutingMiddleware>.Instance,
            CreateForwarder(cloudCaptureNext).Object,
            new TargetFailureStreakService(),
            new Mock<ITargetExecutionTracker>().Object);

        await sut.InvokeAsync(context);

        Assert.NotNull(captured[0]);
        using var doc = JsonDocument.Parse(captured[0]!);
        // The cloud model rewrite still happens...
        Assert.Equal("llama3-cloud", doc.RootElement.GetProperty("model").GetString());
        // ...but the sizing transformer never touches the Cloud path.
        Assert.False(doc.RootElement.TryGetProperty("options", out _));
        Assert.False(context.Response.Headers.ContainsKey("X-OllamaRouter-Context-Size"));
        catalogClient.Verify(c => c.GetShowAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sizing_CapabilityLookupFailure_EmptyDigest_PassesThrough_RoutingSucceeds()
    {
        var context = BuildContext("/api/chat", """{ "model": "m", "prompt": "hi" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "m", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);

        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11435" };

        var captured = new string[1];
        // The instance is unreachable/empty: the catalog digest is "" → permissive pass-through.
        var sut = CreateSizingSut(
            CaptureBodyDelegate(captured),
            tokenEstimator,
            routingDecisionService,
            options,
            capabilityEngine,
            DigestCache(""));

        await sut.InvokeAsync(context);

        Assert.Equal("""{ "model": "m", "prompt": "hi" }""", captured[0]);
        Assert.False(context.Response.Headers.ContainsKey("X-OllamaRouter-Context-Size"));
        // Routing still succeeds: the request is forwarded to the decided target.
        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
        catalogClient.Verify(c => c.GetShowAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sizing_DenseLowModelfilePin_ClientNumCtx_ClampedToPreConfiguredContext()
    {
        // Task #26 dense clamp-only: a dense model whose Modelfile pre-configures num_ctx 24576
        // (a low, e.g. VRAM-tuned pin) with a client static num_ctx of 131,072 — the oversized
        // client value is clamped DOWN to the pre-configured 24,576.
        var context = BuildContext("/api/chat", """{ "model": "pinned", "prompt": "hello there", "options": { "num_ctx": 131072 } }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(3_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(3_000, "pinned", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);
        // The Modelfile pins num_ctx 24576: the pre-configured context the dense clamp targets.
        catalogClient
            .Setup(c => c.GetShowAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject
            {
                ["parameters"] = "num_ctx 24576\nnum_predict 4096",
                ["model_info"] = new JsonObject { ["general.parameter_count"] = 4_000_000_000L }
            });

        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11435" };

        var captured = new string[1];
        var sut = CreateSizingSut(
            CaptureBodyDelegate(captured),
            tokenEstimator,
            routingDecisionService,
            options,
            capabilityEngine,
            DigestCache("digest-1"));

        await sut.InvokeAsync(context);

        Assert.NotNull(captured[0]);
        using var doc = JsonDocument.Parse(captured[0]!);
        Assert.Equal(24_576, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    // ================================================================================
    // Per-target failover, streak tracking, and the uncommitted retry loop (Phase 3, task 8).
    // These drive a fully-instrumented Mock<IHttpForwarder> that returns a scripted, per-attempt
    // ForwarderError, so candidate URL ordering, body rewind, commit guards, streak increments,
    // client-abort handling, and the Cloud relay terminal step can be inspected end to end.
    // ================================================================================

    private const string FailoverLocalUrl = "http://localhost:11435";
    private const string FailoverRemoteUrl = "http://remote:11434";

    private const string FailoverBody = """{ "model": "llama3", "prompt": "Bonjour" }""";
    private const string DoneBody = """{ "model": "llama3", "done": true }""";

    private static OllamaRoutingMiddleware BuildFailoverSut(
        Mock<ITokenEstimator> tokenEstimator,
        Mock<IRoutingDecisionService> decision,
        Mock<ITargetAvailabilityService> availability,
        Mock<ITargetLivenessService> liveness,
        OllamaRouterOptions options,
        Mock<IHttpForwarder> forwarder,
        ITargetFailureStreakService streak,
        Mock<IActivityMonitorService>? activityMonitor = null)
    {
        return new OllamaRoutingMiddleware(
            _ => Task.CompletedTask,
            tokenEstimator.Object,
            decision.Object,
            new Mock<IModelCatalogCacheService>().Object,
            (activityMonitor ?? new Mock<IActivityMonitorService>()).Object,
            new Mock<IActivityStatisticsService>().Object,
            new Mock<IInferenceRequestRecorder>().Object,
            availability.Object,
            liveness.Object,
            new ModelCapabilityEngine(new Mock<IOllamaModelCatalogClient>().Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ModelCapabilityEngine>.Instance),
            MsOptions.Create(options),
            NullLogger<OllamaRoutingMiddleware>.Instance,
            forwarder.Object,
            streak,
            new Mock<ITargetExecutionTracker>().Object);
    }

    private static Mock<ITokenEstimator> Estimator()
    {
        var e = new Mock<ITokenEstimator>();
        e.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);
        return e;
    }

    private static Mock<IRoutingDecisionService> DecisionFor(RoutingTarget target)
    {
        var d = new Mock<IRoutingDecisionService>();
        // The middleware dispatch loop calls the 4-arg overload (clientRequestedNumCtx aware);
        // the 3-arg overload is kept for the legacy call paths.
        d.Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(target);
        d.Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(target);
        return d;
    }

    // Online availability + liveness (everything Online, outside the grace window).
    private static (Mock<ITargetAvailabilityService> Availability, Mock<ITargetLivenessService> Liveness) OnlineMocks()
    {
        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);
        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddHours(1));
        liveness.Setup(t => t.GetFleetTargets()).Returns(new[] { RoutingTarget.Local, RoutingTarget.Remote });
        liveness.Setup(t => t.GetEntries()).Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>());
        return (availability, liveness);
    }

    private static IForwarderErrorFeature ErrorFeature(ForwarderError error)
    {
        var feature = new Mock<IForwarderErrorFeature>();
        feature.SetupGet(f => f.Error).Returns(error);
        return feature.Object;
    }

    // Wires the forwarder so SendAsync consumes a positional script: entry i handles attempt i
    // (the last entry is reused for any attempts beyond the list). Each entry writes the response
    // side effects and returns the ForwarderError the loop should observe. A non-None error is
    // also published on context.Features (the same IForwarderErrorFeature YARP/the real forwarder
    // sets) so the middleware's finally-block classification is exercised.
    private static void WireForwarder(
        Mock<IHttpForwarder> forwarder,
        List<Func<HttpContext, string, ForwarderError>> script,
        List<string> sentUrls)
    {
        var idx = 0;
        forwarder
            .Setup(f => f.SendAsync(
                It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
                It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()))
            .Returns<HttpContext, string, HttpMessageInvoker, ForwarderRequestConfig, HttpTransformer, CancellationToken>(
                (ctx, url, _, _, _, _) =>
                {
                    sentUrls.Add(url);
                    var i = idx < script.Count ? idx : script.Count - 1;
                    var result = script[i](ctx, url);
                    idx++;
                    if (result != ForwarderError.None)
                    {
                        ctx.Features.Set<IForwarderErrorFeature>(ErrorFeature(result));
                    }
                    return new ValueTask<ForwarderError>(result);
                });
    }

    // Helpers that build the common attempt scripts.
    private static ForwarderError Uncommitted(HttpContext ctx, string url) => ForwarderError.Request;
    private static ForwarderError Ok(HttpContext ctx, string url)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.WriteAsync(DoneBody).GetAwaiter().GetResult();
        return ForwarderError.None;
    }
    private static ForwarderError OkCloud(HttpContext ctx, string url)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.WriteAsync(DoneBody).GetAwaiter().GetResult();
        return ForwarderError.None;
    }
    private static ForwarderError CloudRead(HttpContext ctx, string url, List<string> bodies)
    {
        ctx.Request.Body.Position = 0;
        using var reader = new StreamReader(ctx.Request.Body);
        bodies.Add(reader.ReadToEnd());
        ctx.Response.StatusCode = 200;
        ctx.Response.WriteAsync(DoneBody).GetAwaiter().GetResult();
        return ForwarderError.None;
    }

    private static OllamaRouterOptions TwoTargetOptions() =>
        new() { LocalUrl = FailoverLocalUrl, RemoteUrl = FailoverRemoteUrl };

    // 1. Uncommitted failover: first attempt fails before commit → body rewound → second attempt 200.
    [Fact]
    public async Task Failover_FirstAttemptUncommittedFailure_RewindsBody_FailsOverToNextTarget()
    {
        var (availability, liveness) = OnlineMocks();
        var context = BuildContext("/api/chat", FailoverBody);
        context.Response.Body = new MemoryStream();
        var streak = new TargetFailureStreakService();
        var sentUrls = new List<string>();
        var secondBodyPosition = -1;

        var forwarder = new Mock<IHttpForwarder>();
        WireForwarder(forwarder, new List<Func<HttpContext, string, ForwarderError>>
        {
            (ctx, url) => Uncommitted(ctx, url), // attempt 1: Remote (primary) fails uncommitted
            (ctx, url) =>
            {
                var stream = (MemoryStream)ctx.Request.Body;
                secondBodyPosition = (int)stream.Position; // rewind must have been applied
                return Ok(ctx, url); // attempt 2: Local serves 200
            }
        }, sentUrls);

        var sut = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Remote), availability, liveness,
            TwoTargetOptions(), forwarder, streak);
        await sut.InvokeAsync(context);

        Assert.Equal(new[] { FailoverRemoteUrl, FailoverLocalUrl }, sentUrls);
        Assert.Equal(0, secondBodyPosition); // body rewound to 0 before the second attempt
        Assert.Equal(1, streak.GetStreak(RoutingTarget.Remote));
        liveness.Verify(t => t.MarkHardFailureAsync(RoutingTarget.Remote), Times.Once);
        Assert.Equal(200, context.Response.StatusCode);
    }

    // 2. No retry after commit: committed response ending in a non-client mid-stream error →
    //    exactly one SendAsync, streak incremented, no liveness strike.
    [Fact]
    public async Task MidStreamFailure_CommittedResponse_NoRetry_StreakIncrements_NoLivenessStrike()
    {
        var (availability, liveness) = OnlineMocks();
        var context = BuildContext("/api/chat", FailoverBody);
        context.Response.Body = new MemoryStream();
        var streak = new TargetFailureStreakService();
        var sentUrls = new List<string>();

        var forwarder = new Mock<IHttpForwarder>();
        WireForwarder(forwarder, new List<Func<HttpContext, string, ForwarderError>>
        {
            (ctx, url) =>
            {
                // Commit bytes, then end in a non-client mid-stream error.
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-ndjson";
                ctx.Response.WriteAsync("""{ "model": "llama3", "done": false }\n""").GetAwaiter().GetResult();
                return ForwarderError.RequestTimedOut; // non-client error
            }
        }, sentUrls);

        var sut = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Remote), availability, liveness,
            TwoTargetOptions(), forwarder, streak);
        await sut.InvokeAsync(context);

        // Committed ⇒ no loop: exactly one attempt; streak on the served target; no liveness strike.
        Assert.Single(sentUrls);
        Assert.Equal(1, streak.GetStreak(RoutingTarget.Remote));
        liveness.Verify(t => t.MarkHardFailureAsync(It.IsAny<RoutingTarget>()), Times.Never);
    }

    // 3. Streak skip on next request: a target whose streak exceeds the threshold is excluded from
    //    the candidate list; after a success resets it, the target is offered again.
    [Fact]
    public async Task StreakExceedingThreshold_ExcludesFailedTarget_UntilSuccessResets()
    {
        var (availability, liveness) = OnlineMocks();
        var options = TwoTargetOptions();

        // Remote already above threshold (MaxConsecutiveFailures = 3 ⇒ 4).
        var streak = new TargetFailureStreakService();
        for (var i = 0; i < 4; i++)
        {
            streak.RecordFailure(RoutingTarget.Remote);
        }

        var forwarder = new Mock<IHttpForwarder>();
        var sentUrls = new List<string>();
        WireForwarder(forwarder, new List<Func<HttpContext, string, ForwarderError>> { (ctx, url) => Ok(ctx, url) }, sentUrls);

        // DecideAsync → Local. The candidate list must be [Local] only: Remote is streak-skipped.
        var sut = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Local), availability, liveness, options, forwarder, streak);
        var context = BuildContext("/api/chat", FailoverBody);
        context.Response.Body = new MemoryStream();
        await sut.InvokeAsync(context);

        Assert.DoesNotContain(FailoverRemoteUrl, sentUrls);
        Assert.Contains(FailoverLocalUrl, sentUrls);

        // After a success resets the streak, Remote is offered again on a fresh request.
        streak.RecordSuccess(RoutingTarget.Remote);
        var forwarder2 = new Mock<IHttpForwarder>();
        var sentUrls2 = new List<string>();
        WireForwarder(forwarder2, new List<Func<HttpContext, string, ForwarderError>> { (ctx, url) => Ok(ctx, url) }, sentUrls2);
        var sut2 = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Remote), availability, liveness, options, forwarder2, streak);
        var context2 = BuildContext("/api/chat", FailoverBody);
        context2.Response.Body = new MemoryStream();
        await sut2.InvokeAsync(context2);

        Assert.Contains(FailoverRemoteUrl, sentUrls2);
    }

    // 4. All-fleet-fail → Cloud relay: every fleet candidate fails uncommitted; the terminal
    //    Cloud step rewrites the model and forwards through the preferred relay.
    [Fact]
    public async Task AllFleetFail_CloudRelayUsed_WithModelRewrite_ViaPreferredRelay()
    {
        var (availability, liveness) = OnlineMocks();
        var options = TwoTargetOptions();
        options.Models["llama3"] = new ModelThresholds { CloudModel = "llama3-cloud" };
        options.CloudOverflow.PreferredRelayTarget = "Local";

        var context = BuildContext("/api/chat", FailoverBody);
        context.Response.Body = new MemoryStream();
        var streak = new TargetFailureStreakService();
        var sentUrls = new List<string>();
        var bodies = new List<string>();

        var forwarder = new Mock<IHttpForwarder>();
        WireForwarder(forwarder, new List<Func<HttpContext, string, ForwarderError>>
        {
            (ctx, url) => Uncommitted(ctx, url), // attempt 1: Remote (primary) fails uncommitted
            (ctx, url) => Uncommitted(ctx, url), // attempt 2: Local (fleet fallback) fails uncommitted
            (ctx, url) => CloudRead(ctx, url, bodies) // attempt 3: terminal Cloud relay via Local → 200
        }, sentUrls);

        var sut = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Remote), availability, liveness, options, forwarder, streak);
        await sut.InvokeAsync(context);

        Assert.Equal(3, sentUrls.Count);
        Assert.Equal(FailoverLocalUrl, sentUrls[^1]); // the cloud relay is the preferred (Local) node
        Assert.Contains("llama3-cloud", bodies[^1]); // the cloud-rewritten body carries the cloud model
    }

    // 5. Preferred relay offline → fallback node: an explicitly-configured offline preferred relay
    //    fails and the next online+enabled fleet target relays the cloud request.
    [Fact]
    public async Task PreferredRelayOffline_FallsBackToNextOnlineFleetTarget()
    {
        var (availability, liveness) = OnlineMocks();
        // The preferred relay (Local) is offline; Remote stays online.
        liveness.Setup(t => t.IsOnline(RoutingTarget.Local)).Returns(false);
        liveness.Setup(t => t.IsOnline(RoutingTarget.Remote)).Returns(true);
        var options = TwoTargetOptions();
        options.Models["llama3"] = new ModelThresholds { CloudModel = "llama3-cloud" };
        options.CloudOverflow.PreferredRelayTarget = "Local";

        var context = BuildContext("/api/chat", FailoverBody);
        context.Response.Body = new MemoryStream();
        var streak = new TargetFailureStreakService();
        var sentUrls = new List<string>();
        var bodies = new List<string>();

        var forwarder = new Mock<IHttpForwarder>();
        var remoteAttempts = 0;
        WireForwarder(forwarder, new List<Func<HttpContext, string, ForwarderError>>
        {
            // Local (offline preferred relay, and the offline fleet fallback) always fails uncommitted.
            (ctx, url) =>
            {
                if (url == FailoverLocalUrl)
                {
                    return Uncommitted(ctx, url);
                }
                remoteAttempts++;
                // First Remote attempt = fleet primary (fails uncommitted); a later Remote attempt
                // = the online cloud fallback (serves the cloud-rewritten body).
                return remoteAttempts == 1 ? Uncommitted(ctx, url) : CloudRead(ctx, url, bodies);
            }
        }, sentUrls);

        var sut = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Remote), availability, liveness, options, forwarder, streak);
        await sut.InvokeAsync(context);

        // The offline preferred relay (Local) does NOT serve; the online fallback (Remote) does.
        Assert.Equal(FailoverRemoteUrl, sentUrls[^1]);
        Assert.Contains("llama3-cloud", bodies[^1]);
    }

    // 5b. Regression lock: the default PreferredRelayTarget = "Local" relays through the
    //     Local-named target (byte-identical to the legacy MapTargetToHeader(Cloud)).
    [Fact]
    public async Task DefaultPreferredRelayLocal_RelaysThroughLocalTarget()
    {
        var (availability, liveness) = OnlineMocks();
        var options = TwoTargetOptions();
        options.Models["llama3"] = new ModelThresholds { CloudModel = "llama3-cloud" };
        // PreferredRelayTarget is left at its default ("Local").

        var context = BuildContext("/api/chat", FailoverBody);
        context.Response.Body = new MemoryStream();
        var streak = new TargetFailureStreakService();
        var sentUrls = new List<string>();
        var bodies = new List<string>();

        var forwarder = new Mock<IHttpForwarder>();
        WireForwarder(forwarder, new List<Func<HttpContext, string, ForwarderError>>
        {
            (ctx, url) => Uncommitted(ctx, url), // Remote (primary) fails
            (ctx, url) => Uncommitted(ctx, url), // Local (fleet fallback) fails
            (ctx, url) => CloudRead(ctx, url, bodies) // terminal Cloud relay via default "Local"
        }, sentUrls);

        var sut = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Remote), availability, liveness, options, forwarder, streak);
        await sut.InvokeAsync(context);

        Assert.Equal(FailoverLocalUrl, sentUrls[^1]); // cloud relayed through the Local-named target
        Assert.Contains("llama3-cloud", bodies[^1]);
    }

    // 6. Model precedence: per-model CloudModel wins over DefaultModel in the cloud-rewritten body.
    [Fact]
    public async Task ModelPrecedence_PerModelCloudModel_WinsOverDefaultModel()
    {
        var (availability, liveness) = OnlineMocks();
        var options = TwoTargetOptions();
        options.Models["llama3"] = new ModelThresholds { CloudModel = "per-model-cloud" };
        options.CloudOverflow.DefaultModel = "default-cloud";
        options.CloudOverflow.PreferredRelayTarget = "Local";

        var context = BuildContext("/api/chat", FailoverBody);
        context.Response.Body = new MemoryStream();
        var streak = new TargetFailureStreakService();
        var sentUrls = new List<string>();
        var bodies = new List<string>();

        var forwarder = new Mock<IHttpForwarder>();
        WireForwarder(forwarder, new List<Func<HttpContext, string, ForwarderError>>
        {
            (ctx, url) => Uncommitted(ctx, url), // Remote fails
            (ctx, url) => Uncommitted(ctx, url), // Local (fleet) fails
            (ctx, url) => CloudRead(ctx, url, bodies) // terminal Cloud relay
        }, sentUrls);

        var sut = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Remote), availability, liveness, options, forwarder, streak);
        await sut.InvokeAsync(context);

        Assert.Contains("per-model-cloud", bodies[^1]);
        Assert.DoesNotContain("default-cloud", bodies[^1]);
    }

    // 7. CloudOverflow.Enabled == false disables the terminal Cloud step even when a per-model
    //    CloudModel is configured: every fleet failure ends at the terminal 503 (cloud_enabled=false).
    [Fact]
    public async Task CloudDisabled_TerminalCloudStepSkipped_Returns503()
    {
        var (availability, liveness) = OnlineMocks();
        var options = TwoTargetOptions();
        options.Models["llama3"] = new ModelThresholds { CloudModel = "llama3-cloud" };
        options.CloudOverflow.Enabled = false;

        var context = BuildContext("/api/chat", FailoverBody);
        context.Response.Body = new MemoryStream();
        var streak = new TargetFailureStreakService();
        var sentUrls = new List<string>();

        var forwarder = new Mock<IHttpForwarder>();
        WireForwarder(forwarder, new List<Func<HttpContext, string, ForwarderError>>
        {
            (ctx, url) => Uncommitted(ctx, url), // Remote (primary) fails
            (ctx, url) => Uncommitted(ctx, url), // Local (fallback) fails; no cloud relay → terminal 503
        }, sentUrls);

        var sut = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Remote), availability, liveness, options, forwarder, streak);
        await sut.InvokeAsync(context);

        Assert.Equal(2, sentUrls.Count);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var bodyText = new StreamReader(context.Response.Body).ReadToEnd();
        using var doc = JsonDocument.Parse(bodyText);
        Assert.False(doc.RootElement.GetProperty("cloud_enabled").GetBoolean());
    }

    // 7b. Relay also fails: every fleet AND the cloud relay candidate fail uncommitted → the
    //     terminal 503 reports cloud_enabled: true (the Cloud step WAS attempted/available).
    [Fact]
    public async Task CloudRelayAlsoFails_Returns503WithCloudEnabledTrue()
    {
        var (availability, liveness) = OnlineMocks();
        var options = TwoTargetOptions();
        options.Models["llama3"] = new ModelThresholds { CloudModel = "llama3-cloud" };

        var context = BuildContext("/api/chat", FailoverBody);
        context.Response.Body = new MemoryStream();
        var streak = new TargetFailureStreakService();
        var sentUrls = new List<string>();

        var forwarder = new Mock<IHttpForwarder>();
        WireForwarder(forwarder, new List<Func<HttpContext, string, ForwarderError>>
        {
            (ctx, url) => Uncommitted(ctx, url), // Remote (primary) fails
            (ctx, url) => Uncommitted(ctx, url), // Local (fleet fallback) fails
            (ctx, url) => Uncommitted(ctx, url)  // cloud relay (preferred Local) fails too
        }, sentUrls);

        var sut = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Remote), availability, liveness, options, forwarder, streak);
        await sut.InvokeAsync(context);

        // The Cloud step offered both the preferred relay (Local) AND the next online node
        // (Remote) before exhausting: fleet [Remote, Local] + relay [Local, Remote].
        Assert.Equal(4, sentUrls.Count);
        Assert.Equal(FailoverLocalUrl, sentUrls[2]);
        Assert.Equal(FailoverRemoteUrl, sentUrls[3]);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var bodyText = new StreamReader(context.Response.Body).ReadToEnd();
        using var doc = JsonDocument.Parse(bodyText);
        Assert.True(doc.RootElement.GetProperty("cloud_enabled").GetBoolean());
    }

    // 8. Client abort: a client-side cancellation neither retries nor increments streaks.
    [Fact]
    public async Task ClientAbort_NeitherRetries_NorIncrementsStreaks()
    {
        var (availability, liveness) = OnlineMocks();
        var context = BuildContext("/api/chat", FailoverBody);
        context.Response.Body = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        context.RequestAborted = cts.Token;

        var streak = new TargetFailureStreakService();
        var sentUrls = new List<string>();

        var forwarder = new Mock<IHttpForwarder>();
        WireForwarder(forwarder, new List<Func<HttpContext, string, ForwarderError>>
        {
            (ctx, url) => ForwarderError.RequestCanceled // client abort → terminal, no retry
        }, sentUrls);

        var sut = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Remote), availability, liveness,
            TwoTargetOptions(), forwarder, streak);
        await sut.InvokeAsync(context);

        Assert.Single(sentUrls); // no retry
        Assert.Equal(0, streak.GetStreak(RoutingTarget.Remote)); // no streak
        liveness.Verify(t => t.MarkHardFailureAsync(It.IsAny<RoutingTarget>()), Times.Never); // no liveness strike
    }

    // 9. Pinned-failover (Q1): a pinned Remote that fails uncommitted falls through to the next
    //    online candidate; the pin is a preference, never fatal.
    [Fact]
    public async Task PinnedTarget_UncommittedFailure_FallsThroughToNextOnlineCandidate()
    {
        var (availability, liveness) = OnlineMocks();
        var context = BuildContext("/api/chat", FailoverBody);
        context.Request.Headers["X-OllamaRouter-Target"] = "Remote";
        context.Response.Body = new MemoryStream();

        var streak = new TargetFailureStreakService();
        var sentUrls = new List<string>();

        var forwarder = new Mock<IHttpForwarder>();
        WireForwarder(forwarder, new List<Func<HttpContext, string, ForwarderError>>
        {
            (ctx, url) => Uncommitted(ctx, url), // pinned Remote fails uncommitted
            (ctx, url) => Ok(ctx, url) // next online candidate (Local) serves 200
        }, sentUrls);

        var sut = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Remote), availability, liveness,
            TwoTargetOptions(), forwarder, streak);
        await sut.InvokeAsync(context);

        Assert.Equal(new[] { FailoverRemoteUrl, FailoverLocalUrl }, sentUrls);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(1, streak.GetStreak(RoutingTarget.Remote));
    }

    // ================================================================================
    // §2.6 header architecture (task #9): pinning capability gate, inbound sanitization,
    // telemetry headers.
    // ================================================================================

    // Pinning (completion pipeline): an eligible (configured + enabled + online + capable)
    // pin routes strictly to the named target and the response echoes the telemetry header.
    [Fact]
    public async Task Pin_EligibleOnlineTarget_RoutesStrictlyThere_EchoesTelemetryHeader()
    {
        var context = BuildContext("/api/chat", FailoverBody);
        context.Request.Headers["X-OllamaRouter-Target"] = "Remote";
        var forwardedUrls = new List<string>();

        var cache = new Mock<IModelCatalogCacheService>();
        cache.Setup(c => c.ModelExistsAsync(It.IsAny<string?>(), "llama3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // capable: the model exists on the pinned target

        var tokenEstimator = Estimator();
        var decision = new Mock<IRoutingDecisionService>();
        // Deliberately a different target than the pin: it must never be consulted.
        decision.Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var forwarder = new Mock<IHttpForwarder>();
        forwarder.Setup(f => f.SendAsync(
                It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
                It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()))
            .Callback<HttpContext, string, HttpMessageInvoker, ForwarderRequestConfig, HttpTransformer, CancellationToken>(
                (c, url, _, _, _, _) => forwardedUrls.Add(url))
            .Returns(new ValueTask<ForwarderError>(ForwarderError.None));

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, decision,
            modelCatalogCache: cache, forwarder: forwarder);
        await sut.InvokeAsync(context);

        // Strictly pinned: forwarded to the Remote daemon's URL, never to Local.
        Assert.Equal(new[] { "http://remote:11434" }, forwardedUrls);
        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
        decision.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Telemetry echoes the pinned (served) target.
        Assert.Equal("Remote", context.Response.Headers["X-OllamaRouter-Target"]);
    }

    // Pinning: an unconfigured pin name falls back to the normal scoring pipeline.
    [Fact]
    public async Task Pin_UnconfiguredName_FallsBackToScoring()
    {
        var context = BuildContext("/api/chat", FailoverBody);
        context.Request.Headers["X-OllamaRouter-Target"] = "ghost";

        var tokenEstimator = Estimator();
        var decision = DecisionFor(RoutingTarget.Local);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, decision);
        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
        Assert.Equal("Local", context.Response.Headers["X-OllamaRouter-Target"]);
        decision.Verify(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Pinning capability gate: a KNOWN catalog (non-empty digest) that genuinely lacks the
    // model disqualifies the pin → fall back to scoring.
    [Fact]
    public async Task Pin_ModelMissingFromKnownCatalog_FallsBackToScoring()
    {
        var context = BuildContext("/api/chat", FailoverBody);
        context.Request.Headers["X-OllamaRouter-Target"] = "Remote";

        var cache = new Mock<IModelCatalogCacheService>();
        cache.Setup(c => c.ModelExistsAsync(It.IsAny<string?>(), "llama3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        cache.Setup(c => c.GetCurrentDigestAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("digest-remote-1"); // non-empty: the catalog is KNOWN

        var tokenEstimator = Estimator();
        var decision = DecisionFor(RoutingTarget.Local);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, decision, modelCatalogCache: cache);
        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
        decision.Verify(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Pinning capability gate (permissive): ModelExistsAsync == false but the catalog digest
    // is empty (unknown/never enumerated — degraded lookup) ⇒ the pin is HONORED (task #7 Q4
    // permissive-unknown policy); scoring is never consulted.
    [Fact]
    public async Task Pin_CatalogUnknown_Permissive_HonorsPin()
    {
        var context = BuildContext("/api/chat", FailoverBody);
        context.Request.Headers["X-OllamaRouter-Target"] = "Remote";

        var cache = new Mock<IModelCatalogCacheService>();
        cache.Setup(c => c.ModelExistsAsync(It.IsAny<string?>(), "llama3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        cache.Setup(c => c.GetCurrentDigestAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(""); // empty: catalog unknown ⇒ permissive

        var tokenEstimator = Estimator();
        var decision = new Mock<IRoutingDecisionService>();

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, decision, modelCatalogCache: cache);
        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
        Assert.Equal("Remote", context.Response.Headers["X-OllamaRouter-Target"]);
        decision.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Pinning capability gate (permissive): a capability-discovery outage (ModelExistsAsync
    // throws) never blocks a valid pin ⇒ the pin is honored.
    [Fact]
    public async Task Pin_CatalogLookupThrows_Permissive_HonorsPin()
    {
        var context = BuildContext("/api/chat", FailoverBody);
        context.Request.Headers["X-OllamaRouter-Target"] = "Remote";

        var cache = new Mock<IModelCatalogCacheService>();
        cache.Setup(c => c.ModelExistsAsync(It.IsAny<string?>(), "llama3", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("catalog probe down"));

        var tokenEstimator = Estimator();
        var decision = new Mock<IRoutingDecisionService>();

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, decision, modelCatalogCache: cache);
        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
        Assert.Equal("Remote", context.Response.Headers["X-OllamaRouter-Target"]);
        decision.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Sanitization (default branch): a forged inbound X-Ollama-Target is stripped on arrival;
    // the value visible to YARP is the router's own computed default, never the forged one.
    [Fact]
    public async Task InboundInternalTargetHeader_StrippedOnArrival_DefaultPath()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/tags";
        context.Request.Headers["X-Ollama-Target"] = "Spoofed";

        var sut = CreateSut(_ => Task.CompletedTask, new Mock<ITokenEstimator>(), new Mock<IRoutingDecisionService>());
        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
    }

    // Sanitization (pinned-management branch, YARP-via-`next`): the forged internal header is
    // replaced by the router's decision captured AT the YARP layer (inside the `next` delegate),
    // so YARP route selection can never match the forged value.
    [Fact]
    public async Task InboundInternalTargetHeader_StrippedOnArrival_PinnedManagementPath()
    {
        var context = BuildContext("/api/pull", """{ "model": "llama3" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "Remote";
        context.Request.Headers["X-Ollama-Target"] = "Local"; // forged: opposite of the valid pin

        var headerAtYarpLayer = "";
        var sut = CreateSut(c => { headerAtYarpLayer = c.Request.Headers["X-Ollama-Target"].ToString(); return Task.CompletedTask; },
            new Mock<ITokenEstimator>(), new Mock<IRoutingDecisionService>());
        await sut.InvokeAsync(context);

        Assert.Equal("Remote", headerAtYarpLayer); // the valid pin, never the forged value
    }

    // Sanitization (completion branch, dispatch forwarder): the forged internal header never
    // reaches the forwarder — the header captured at SendAsync is the router's decision.
    [Fact]
    public async Task InboundInternalTargetHeader_NeverReachesForwarder_CompletionPath()
    {
        var context = BuildContext("/api/chat", FailoverBody);
        context.Request.Headers["X-Ollama-Target"] = "Local"; // forged

        var decision = DecisionFor(RoutingTarget.Remote);
        var headerAtForwarder = "";
        var forwarder = new Mock<IHttpForwarder>();
        forwarder.Setup(f => f.SendAsync(
                It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
                It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()))
            .Callback<HttpContext, string, HttpMessageInvoker, ForwarderRequestConfig, HttpTransformer, CancellationToken>(
                (c, _, _, _, _, _) =>
                {
                    headerAtForwarder = c.Request.Headers["X-Ollama-Target"].ToString();
                    c.Response.StatusCode = 200;
                    c.Response.WriteAsync(DoneBody).GetAwaiter().GetResult();
                })
            .Returns(new ValueTask<ForwarderError>(ForwarderError.None));

        var (availability, liveness) = OnlineMocks();
        var sut = BuildFailoverSut(Estimator(), decision, availability, liveness,
            TwoTargetOptions(), forwarder, new TargetFailureStreakService());
        await sut.InvokeAsync(context);

        Assert.Equal("Remote", headerAtForwarder); // the router's decision, never "Local"
    }

    // Telemetry: all four inference endpoints carry the three base telemetry headers with the
    // correct values (all four ride the same completion pipeline; /api/embeddings too).
    [Theory]
    [InlineData("/api/chat")]
    [InlineData("/api/generate")]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/completions")]
    public async Task Telemetry_HeadersPresent_AllFourInferenceEndpoints(string path)
    {
        var context = BuildContext(path, FailoverBody);

        var decision = DecisionFor(RoutingTarget.Remote);
        var sut = CreateSut(_ => Task.CompletedTask, Estimator(), decision);
        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Response.Headers["X-OllamaRouter-Target"]);
        Assert.Equal("llama3", context.Response.Headers["X-OllamaRouter-Model"]);
        Assert.Equal("100", context.Response.Headers["X-OllamaRouter-Estimated-Tokens"]);
    }

    // Telemetry (streamed): a streamed NDJSON response carries the telemetry headers — the
    // OnStarting re-assert keeps them present when the response is committed by the stream.
    [Fact]
    public async Task Telemetry_StreamedNdjsonResponse_HeadersPresent()
    {
        var context = BuildContext("/api/chat", FailoverBody);
        context.Response.Body = new MemoryStream();

        const string ndjsonResponse =
            """{ "model": "llama3", "message": { "role": "assistant", "content": "hi" }, "done": false }\n""" +
            """{ "model": "llama3", "message": { "role": "assistant", "content": "!" }, "done": true, "prompt_eval_count": 7, "eval_count": 3 }\n""";

        RequestDelegate next = c =>
        {
            c.Response.StatusCode = 200;
            c.Response.ContentType = "application/x-ndjson";
            c.Response.WriteAsync(ndjsonResponse).GetAwaiter().GetResult(); // commits the stream
            return Task.CompletedTask;
        };

        var decision = DecisionFor(RoutingTarget.Remote);
        var sut = CreateSut(next, Estimator(), decision);
        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Response.Headers["X-OllamaRouter-Target"]);
        Assert.Equal("llama3", context.Response.Headers["X-OllamaRouter-Model"]);
        Assert.Equal("100", context.Response.Headers["X-OllamaRouter-Estimated-Tokens"]);
    }

    // Telemetry failover-correctness: an UNCOMMITTED failover (the first attempt dies before
    // the response starts) means the OnStarting closure re-asserts the FINALLY-SERVED target
    // (Local), not the decided primary (Remote).
    [Fact]
    public async Task Telemetry_UncommittedFailover_ReportsServedTarget()
    {
        var (availability, liveness) = OnlineMocks();
        var context = BuildContext("/api/chat", FailoverBody);
        context.Response.Body = new MemoryStream();

        var forwarder = new Mock<IHttpForwarder>();
        // Attempt 1 (Remote, decided primary) dies uncommitted WITHOUT writing any byte.
        // Attempt 2 (Local) succeeds (ForwarderError.None) but commits NO client bytes before
        // returning, so the response is still uncommitted when the dispatch loop finishes and
        // the telemetry header reports the finally-served target (Local), not the primary.
        forwarder
            .Setup(f => f.SendAsync(
                It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
                It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()))
            .Returns<HttpContext, string, HttpMessageInvoker, ForwarderRequestConfig, HttpTransformer, CancellationToken>(
                (ctx, url, _, _, _, _) =>
                {
                    if (url == FailoverRemoteUrl)
                    {
                        return new ValueTask<ForwarderError>(ForwarderError.Request); // uncommitted
                    }
                    ctx.Response.StatusCode = 200; // Local serves (uncommitted: no body bytes yet)
                    return new ValueTask<ForwarderError>(ForwarderError.None);
                });

        var sut = BuildFailoverSut(Estimator(), DecisionFor(RoutingTarget.Remote), availability, liveness,
            TwoTargetOptions(), forwarder, new TargetFailureStreakService());
        await sut.InvokeAsync(context);

        // The telemetry header reports the finally-SERVED target (Local), not the decided
        // primary (Remote) — asserted after the response was committed by Local's body.
        Assert.Equal("Local", context.Response.Headers["X-OllamaRouter-Target"]);
        Assert.Equal("llama3", context.Response.Headers["X-OllamaRouter-Model"]);
        Assert.Equal("100", context.Response.Headers["X-OllamaRouter-Estimated-Tokens"]);
        }

        // ==================================================================
        // Session/agent sticky-routing header (X-OllamaRouter-Session-Id /
        // X-OllamaRouter-Agent-Id): extraction, response echo, lease wiring
        // ==================================================================

        [Fact]
        public async Task Completion_SessionIdHeader_EchoedInResponse_PassedToDecisionService()
        {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");
        context.Request.Headers[OllamaReverseProxyConfig.PublicSessionIdHeader] = "agent-1";

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "llama3", (int?)null, "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService,
            sessionAffinity: new SessionAffinityService(
                MsOptions.Create(new OllamaRouterOptions { LocalUrl = "http://localhost:11435", RemoteUrl = "http://remote:11434" }),
                NullLogger<SessionAffinityService>.Instance));

        await sut.InvokeAsync(context);

        // The new session-aware overload is used with the extracted session ID...
        routingDecisionService.Verify(r => r.DecideAsync(100, "llama3", (int?)null, "agent-1", It.IsAny<CancellationToken>()), Times.Once);
        // ...and the session ID is echoed back in the response telemetry.
        Assert.Equal("agent-1", context.Response.Headers[OllamaReverseProxyConfig.PublicSessionIdHeader].ToString());
        Assert.Equal("Remote", context.Response.Headers[OllamaReverseProxyConfig.PublicTargetHeader].ToString());
        }

        [Fact]
        public async Task Completion_AgentIdHeader_Alone_FallsBackToAgentId()
        {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");
        context.Request.Headers[OllamaReverseProxyConfig.PublicAgentIdHeader] = "agent-2";

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "llama3", (int?)null, "agent-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService,
            sessionAffinity: new SessionAffinityService(
                MsOptions.Create(new OllamaRouterOptions { LocalUrl = "http://localhost:11435", RemoteUrl = "http://remote:11434" }),
                NullLogger<SessionAffinityService>.Instance));

        await sut.InvokeAsync(context);

        routingDecisionService.Verify(r => r.DecideAsync(100, "llama3", (int?)null, "agent-2", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("agent-2", context.Response.Headers[OllamaReverseProxyConfig.PublicSessionIdHeader].ToString());
        }

        [Fact]
        public async Task Completion_BothSessionAndAgentHeaders_SessionIdWins()
        {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");
        context.Request.Headers[OllamaReverseProxyConfig.PublicSessionIdHeader] = "session-a";
        context.Request.Headers[OllamaReverseProxyConfig.PublicAgentIdHeader] = "agent-b";

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "llama3", (int?)null, "session-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService,
            sessionAffinity: new SessionAffinityService(
                MsOptions.Create(new OllamaRouterOptions { LocalUrl = "http://localhost:11435", RemoteUrl = "http://remote:11434" }),
                NullLogger<SessionAffinityService>.Instance));

        await sut.InvokeAsync(context);

        routingDecisionService.Verify(r => r.DecideAsync(100, "llama3", (int?)null, "session-a", It.IsAny<CancellationToken>()), Times.Once);
        routingDecisionService.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), "agent-b", It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("session-a", context.Response.Headers[OllamaReverseProxyConfig.PublicSessionIdHeader].ToString());
        }

        [Fact]
        public async Task Completion_NoSessionHeaders_UsesLegacyOverload_NoSessionResponseHeader()
        {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "llama3", (int?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        // A real affinity store IS wired — but with no session headers the middleware must
        // still use the legacy overload and emit no session response header.
        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService,
            sessionAffinity: new SessionAffinityService(
                MsOptions.Create(new OllamaRouterOptions { LocalUrl = "http://localhost:11435", RemoteUrl = "http://remote:11434" }),
                NullLogger<SessionAffinityService>.Instance));

        await sut.InvokeAsync(context);

        routingDecisionService.Verify(r => r.DecideAsync(100, "llama3", (int?)null, It.IsAny<CancellationToken>()), Times.Once);
        routingDecisionService.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(context.Response.Headers.ContainsKey(OllamaReverseProxyConfig.PublicSessionIdHeader));
        }

        [Fact]
        public async Task Completion_TooLongSessionId_Ignored_LegacyOverloadUsed()
        {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");
        context.Request.Headers[OllamaReverseProxyConfig.PublicSessionIdHeader] = new string('x', 257);

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "llama3", (int?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService,
            sessionAffinity: new SessionAffinityService(
                MsOptions.Create(new OllamaRouterOptions { LocalUrl = "http://localhost:11435", RemoteUrl = "http://remote:11434" }),
                NullLogger<SessionAffinityService>.Instance));

        await sut.InvokeAsync(context);

        // Over-256-char session IDs are ignored (bounded store): legacy overload, no echo.
        routingDecisionService.Verify(r => r.DecideAsync(100, "llama3", (int?)null, It.IsAny<CancellationToken>()), Times.Once);
        routingDecisionService.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.Is<string>(s => !string.IsNullOrWhiteSpace(s)), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(context.Response.Headers.ContainsKey(OllamaReverseProxyConfig.PublicSessionIdHeader));
        }

        [Fact]
        public async Task Completion_SessionStickyThroughMiddleware_SecondTurnReceivesFirstTurnLease()
        {
        // Integration-level stickiness: two consecutive completion requests for agent-1
        // against the same sut. The decision-service mock records the session overload's
        // sessionId argument per call; after turn 1 the REAL SessionAffinityService (wired
        // into both the middleware and observed here) holds the lease turn 1's winner set —
        // the same store the production decision service reads for the +150 bonus in turn 2.
        var affinity = new SessionAffinityService(
            MsOptions.Create(new OllamaRouterOptions { LocalUrl = "http://localhost:11435", RemoteUrl = "http://remote:11434" }),
            NullLogger<SessionAffinityService>.Instance);
        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        // Turn 1 decides Remote; turn 2 (sticky) decides Local — the real decision service
        // would have kept agent-1 on Remote via the +150 bonus; here we just prove the
        // middleware hands the lease through in both directions.
        var callIndex = 0;
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((int _, string _, int? _, string? sessionId, CancellationToken _) =>
            {
                Assert.Equal("agent-1", sessionId); // both turns carry the session ID
                return Task.FromResult(callIndex++ == 0 ? RoutingTarget.Remote : RoutingTarget.Local);
            });

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService, sessionAffinity: affinity);

        var context1 = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "turn 1" }""");
        context1.Request.Headers[OllamaReverseProxyConfig.PublicSessionIdHeader] = "agent-1";
        await sut.InvokeAsync(context1);
        Assert.Equal("Remote", context1.Response.Headers[OllamaReverseProxyConfig.PublicTargetHeader].ToString());

        // The production decision service writes the lease on the winner; simulate that
        // write through the shared store and prove the second turn's middleware path still
        // routes with the session ID (the store now pins agent-1 to Remote).
        affinity.SetAffinity("agent-1", RoutingTarget.Remote);
        Assert.True(affinity.TryGetAffinity("agent-1", out var pinned));
        Assert.Equal(RoutingTarget.Remote, pinned);

        var context2 = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "turn 2" }""");
        context2.Request.Headers[OllamaReverseProxyConfig.PublicSessionIdHeader] = "agent-1";
        await sut.InvokeAsync(context2);
        Assert.Equal("Local", context2.Response.Headers[OllamaReverseProxyConfig.PublicTargetHeader].ToString());
        routingDecisionService.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), "agent-1", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task InvokeAsync_WithRealHeuristicTokenEstimator_QwenModel_UsesQwenTunedFactors()
    {
        // 50 Unicode chars:
        // Default: 50 * 1.0 = 50 tokens
        // Qwen: 50 * 0.40 = 20 tokens
        var unicodePrompt = new string('é', 50);
        var context = BuildContext("/api/chat", $$"""{ "model": "qwen2.5:7b", "prompt": "{{unicodePrompt}}" }""");

        int capturedTokenCount = 0;
        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), "qwen2.5:7b", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<int, string, int?, CancellationToken>((tokens, _, _, _) => capturedTokenCount = tokens)
            .ReturnsAsync(RoutingTarget.Local);

        var realEstimator = new HeuristicTokenEstimator();
        var sut = CreateSut(_ => Task.CompletedTask, realEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        // Qwen estimate: 20 tokens, overhead factor: 1.0 (default in OptionsWithCloud/ThreeTargetOptions)
        Assert.Equal(20, capturedTokenCount);
    }

    [Fact]
    public async Task InvokeAsync_WithRealHeuristicTokenEstimator_GemmaModel_UsesGemmaTunedFactors()
    {
        // 50 Unicode chars:
        // Default: 50 * 1.0 = 50 tokens
        // Gemma: 50 * 0.37 = 19 tokens (Math.Ceiling(18.5) = 19)
        var unicodePrompt = new string('é', 50);
        var context = BuildContext("/api/chat", $$"""{ "model": "gemma4:12b", "prompt": "{{unicodePrompt}}" }""");

        int capturedTokenCount = 0;
        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), "gemma4:12b", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<int, string, int?, CancellationToken>((tokens, _, _, _) => capturedTokenCount = tokens)
            .ReturnsAsync(RoutingTarget.Local);

        var realEstimator = new HeuristicTokenEstimator();
        var sut = CreateSut(_ => Task.CompletedTask, realEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal(19, capturedTokenCount);
    }

    [Fact]
    public async Task InvokeAsync_WithMockModelAwareTokenEstimator_PassesModelNameToEstimator()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen3.5:32b", "prompt": "Hello world" }""");

        var modelAwareMock = new Mock<IModelAwareTokenEstimator>();
        modelAwareMock
            .Setup(e => e.EstimateTokens(It.IsAny<IReadOnlyList<string>>(), "qwen3.5:32b"))
            .Returns(77);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(77, "qwen3.5:32b", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var sut = CreateSut(_ => Task.CompletedTask, modelAwareMock.Object, routingDecisionService);

        await sut.InvokeAsync(context);

        modelAwareMock.Verify(e => e.EstimateTokens(It.IsAny<IReadOnlyList<string>>(), "qwen3.5:32b"), Times.Once);
        routingDecisionService.Verify(r => r.DecideAsync(77, "qwen3.5:32b", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_NdjsonResponse_RecordsTokenMetricsCsvWithActualTokens()
    {
        const string requestBody = """{ "model": "qwen", "messages": [ { "role": "user", "content": "hello" } ] }""";
        var context = BuildContext("/api/chat", requestBody);

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator
            .Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>()))
            .Returns(42);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), "qwen", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var requestId = Guid.NewGuid();
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.StartRequest(It.IsAny<RoutingTarget>(), "qwen", It.IsAny<int>())).Returns(requestId);

        const string ndjsonResponse =
            """{"model":"qwen","message":{"role":"assistant","content":"hi"},"done":false}""" + "\n" +
            """{"model":"qwen","message":{"role":"assistant","content":""},"done":true,"prompt_eval_count":777,"eval_count":9}""" + "\n";

        RequestDelegate next = c =>
        {
            c.Response.WriteAsync(ndjsonResponse);
            return Task.CompletedTask;
        };

        var metricsRecorder = new Mock<ITokenMetricsRecorder>();
        metricsRecorder.SetupGet(r => r.IsEnabled).Returns(true);

        var sut = CreateSut(next, tokenEstimator, routingDecisionService, activityMonitor: activityMonitor, tokenMetricsRecorder: metricsRecorder.Object);

        await sut.InvokeAsync(context);

        var expectedAggregate = HeuristicTokenEstimator.Analyze(
            OllamaRouter.Parsing.OllamaRequestParser.ParseCompletionRequest(requestBody).ContextParts);
        metricsRecorder.Verify(
            r => r.Record(It.Is<TokenMetricsCsvRecord>(rec =>
                rec.RequestId == requestId &&
                rec.Model == "qwen" &&
                rec.Target == "Local" &&
                rec.Endpoint == "/api/chat" &&
                rec.AggregateMetrics.Equals(expectedAggregate) &&
                rec.EstimatedPromptTokens == 42 &&
                rec.ActualPromptTokens == 777 &&
                rec.ActualResponseTokens == 9 &&
                rec.StatusCode == 200 &&
                rec.Success)),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_OpenAiSseResponse_RecordsTokenMetricsCsvWithUsageTokens()
    {
        const string requestBody = """{ "model": "qwen", "stream": true, "messages": [ { "role": "user", "content": "hi there" } ] }""";
        var context = BuildContext("/v1/chat/completions", requestBody);

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator
            .Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>()))
            .Returns(33);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), "qwen", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var requestId = Guid.NewGuid();
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.StartRequest(It.IsAny<RoutingTarget>(), "qwen", It.IsAny<int>())).Returns(requestId);

        const string sseResponse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{}}],\"usage\":{\"prompt_tokens\":555,\"completion_tokens\":11}}\n\n" +
            "data: [DONE]\n\n";

        RequestDelegate next = c =>
        {
            c.Response.ContentType = "text/event-stream";
            c.Response.WriteAsync(sseResponse);
            return Task.CompletedTask;
        };

        var metricsRecorder = new Mock<ITokenMetricsRecorder>();
        metricsRecorder.SetupGet(r => r.IsEnabled).Returns(true);

        var sut = CreateSut(next, tokenEstimator, routingDecisionService, activityMonitor: activityMonitor, tokenMetricsRecorder: metricsRecorder.Object);

        await sut.InvokeAsync(context);

        metricsRecorder.Verify(
            r => r.Record(It.Is<TokenMetricsCsvRecord>(rec =>
                rec.RequestId == requestId &&
                rec.Model == "qwen" &&
                rec.Target == "Remote" &&
                rec.Endpoint == "/v1/chat/completions" &&
                rec.EstimatedPromptTokens == 33 &&
                rec.ActualPromptTokens == 555 &&
                rec.ActualResponseTokens == 11 &&
                rec.StatusCode == 200 &&
                rec.Success)),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_TokenMetricsRecorderDisabled_InvokesRecordNever()
    {
        const string requestBody = """{ "model": "qwen", "messages": [ { "role": "user", "content": "hello" } ] }""";
        var context = BuildContext("/api/chat", requestBody);

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var requestId = Guid.NewGuid();
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.StartRequest(It.IsAny<RoutingTarget>(), It.IsAny<string>(), It.IsAny<int>())).Returns(requestId);

        var metricsRecorder = new Mock<ITokenMetricsRecorder>();
        metricsRecorder.SetupGet(r => r.IsEnabled).Returns(false);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService, activityMonitor: activityMonitor, tokenMetricsRecorder: metricsRecorder.Object);

        await sut.InvokeAsync(context);

        // Disabled recorder: never invoked, and the request is otherwise handled normally.
        metricsRecorder.Verify(r => r.Record(It.IsAny<TokenMetricsCsvRecord>()), Times.Never);
        activityMonitor.Verify(m => m.CompleteRequest(requestId, It.IsAny<ActivityLogEntry>()), Times.Once);
    }



    // ================================================================================
    // Location-scoped X-OllamaRouter-Target pins (task #40): bare keywords (local / remote /
    // cloud) and comma allow-lists select targets BY LOCATION when no configured target bears
    // that exact name; exact names keep precedence; the response telemetry always echoes the
    // CONCRETE target name, never the scope keyword. The scope-test fleet below is deliberately
    // named WITHOUT the scope keywords (Loop / Lan / Edge) so the headers genuinely parse as
    // location scopes rather than exact-name pins.
    // ================================================================================

    private const string ScopeLoopUrl = "http://127.0.0.1:11435";         // Loop → Local
    private const string ScopeLanUrl = "http://192.168.1.25:11435";       // Lan  → Remote
    private const string ScopeEdgeUrl = "http://edge.public.example:11435"; // Edge → Cloud class

    private static OllamaRouterOptions ScopeFleetOptions() => new()
    {
        Targets = new List<OllamaTargetOptions>
        {
            new() { Name = "Loop", Url = ScopeLoopUrl, MaxParallel = 1, Enabled = true },
            new() { Name = "Lan", Url = ScopeLanUrl, MaxParallel = 1, Enabled = true },
            new() { Name = "Edge", Url = ScopeEdgeUrl, MaxParallel = 1, Enabled = true }
        }
    };

    private static readonly HashSet<TargetLocation> LocalScopeSet = new() { TargetLocation.Local };
    private static readonly HashSet<TargetLocation> RemoteScopeSet = new() { TargetLocation.Remote };
    private static readonly HashSet<TargetLocation> LocalRemoteScopeSet = new() { TargetLocation.Local, TargetLocation.Remote };

    private static OllamaRoutingMiddleware BuildScopeSut(
        Mock<IRoutingDecisionService> decision,
        Mock<ITargetAvailabilityService>? availability = null,
        Mock<ITargetLivenessService>? liveness = null,
        ISessionAffinityService? sessionAffinity = null,
        OllamaRouterOptions? options = null)
    {
        var (defaultAvailability, defaultLiveness) = OnlineMocks();
        return new OllamaRoutingMiddleware(
            _ => Task.CompletedTask,
            Estimator().Object,
            decision.Object,
            new Mock<IModelCatalogCacheService>().Object,
            new Mock<IActivityMonitorService>().Object,
            new Mock<IActivityStatisticsService>().Object,
            new Mock<IInferenceRequestRecorder>().Object,
            (availability ?? defaultAvailability).Object,
            (liveness ?? defaultLiveness).Object,
            new ModelCapabilityEngine(new Mock<IOllamaModelCatalogClient>().Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<ModelCapabilityEngine>.Instance),
            MsOptions.Create(options ?? ScopeFleetOptions()),
            NullLogger<OllamaRoutingMiddleware>.Instance,
            CreateForwarder(_ => Task.CompletedTask).Object,
            new TargetFailureStreakService(),
            new Mock<ITargetExecutionTracker>().Object,
            contextShrinkManager: null,
            sessionAffinity,
            tokenMetricsRecorder: null);
    }

    // Content-based Moq matcher for IReadOnlySet<TargetLocation> (Moq matches reference
    // equality by default, so scope-SET arguments must be matched by content).
    private static object ScopeSetIsEqualTo(HashSet<TargetLocation> expected) =>
        It.Is<IReadOnlySet<TargetLocation>>(set => set != null && set.SetEquals(expected));

    // B1 — scope keywords (no target named that way in the fleet) route via the scoped
    // decision overload and the telemetry headers echo the CONCRETE decided target name,
    // never the keyword.
    [Fact]
    public async Task ScopePin_ScopeKeywords_RouteViaScopedDecision_EchoConcreteTarget()
    {
        foreach (var (header, scopeSet, expectedTarget) in new[]
        {
            ("local", LocalScopeSet, new RoutingTarget("Loop")),
            ("REMOTE", RemoteScopeSet, new RoutingTarget("Lan")),
            ("local,remote", LocalRemoteScopeSet, new RoutingTarget("Loop"))
        })
        {
            var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Hello" }""");
            context.Request.Headers["X-OllamaRouter-Target"] = header;

            var decision = new Mock<IRoutingDecisionService>();
            decision
                .Setup(r => r.DecideDetailedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), (IReadOnlySet<TargetLocation>)ScopeSetIsEqualTo(scopeSet), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RoutingDecisionResult(expectedTarget));

            var sut = BuildScopeSut(decision);
            await sut.InvokeAsync(context);

            // The request goes to the decided CONCRETE target — never the scope keyword.
            Assert.Equal(expectedTarget.ToString(), context.Request.Headers["X-Ollama-Target"].ToString());
            Assert.Equal(expectedTarget.ToString(), context.Response.Headers["X-OllamaRouter-Target"].ToString());
            Assert.NotEqual(header.Trim().ToLowerInvariant(), context.Response.Headers["X-OllamaRouter-Target"].ToString().ToLowerInvariant());

            // The scoped overload (6-arg) was consulted exactly once with the parsed scope.
            decision.Verify(r => r.DecideDetailedAsync(
                    It.Is<int>(t => t == 100), "llama3", It.IsAny<int?>(), It.IsAny<string?>(),
                    It.Is<IReadOnlySet<TargetLocation>>(s => !ReferenceEquals(s, null) && s.SetEquals(scopeSet)), It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    // B1 (cloud) — "cloud" alone is the EXACT Cloud pseudo-target pin (byte-identical to
    // today's behavior), not a scope: the exact pin is honored and the Cloud relay maps to
    // the relay target's name in the internal header.
    [Fact]
    public async Task ScopePin_CloudAlone_IsExactCloudPin_NotAScope()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Hello" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "cloud";

        var decision = new Mock<IRoutingDecisionService>();
        var sut = BuildScopeSut(decision);
        await sut.InvokeAsync(context);

        // Cloud relay: the internal header carries the first effective target's name; the
        // public telemetry echoes the logical Cloud target (today's exact-pinned-Cloud behavior).
        Assert.Equal("Loop", context.Request.Headers["X-Ollama-Target"].ToString());
        Assert.Equal("Cloud", context.Response.Headers["X-OllamaRouter-Target"].ToString());
        decision.Verify(r => r.DecideDetailedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<IReadOnlySet<TargetLocation>>(), It.IsAny<CancellationToken>()), Times.Never);
        decision.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // B2 — exact-name precedence: a target LITERALLY named "remote" (OrdinalIgnoreCase, here
    // "Remote") still exact-pins with the capability gate — the scope is never parsed.
    [Fact]
    public async Task ScopePin_TargetNamedRemote_ExactNameKeepsPrecedence()
    {
        // The default synthesized fleet (Local + Remote) names a target "Remote".
        var context = BuildContext("/api/chat", FailoverBody);
        context.Request.Headers["X-OllamaRouter-Target"] = "remote";

        var cache = new Mock<IModelCatalogCacheService>();
        cache.Setup(c => c.ModelExistsAsync(It.IsAny<string?>(), "llama3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // capable: the model exists on the pinned target

        var decision = new Mock<IRoutingDecisionService>();
        var forwardedUrls = new List<string>();
        var forwarder = new Mock<IHttpForwarder>();
        forwarder.Setup(f => f.SendAsync(
                It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<HttpMessageInvoker>(),
                It.IsAny<ForwarderRequestConfig>(), It.IsAny<HttpTransformer>(), It.IsAny<CancellationToken>()))
            .Callback<HttpContext, string, HttpMessageInvoker, ForwarderRequestConfig, HttpTransformer, CancellationToken>(
                (c, url, _, _, _, _) => forwardedUrls.Add(url))
            .Returns(new ValueTask<ForwarderError>(ForwarderError.None));

        var sut = CreateSut(_ => Task.CompletedTask, Estimator(), decision, modelCatalogCache: cache, forwarder: forwarder);
        await sut.InvokeAsync(context);

        // Exact-pinned to Remote (its URL), capability gate probed — NO scoped decision ran.
        Assert.Equal(new[] { "http://remote:11434" }, forwardedUrls);
        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"].ToString());
        decision.Verify(r => r.DecideDetailedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<IReadOnlySet<TargetLocation>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // B3 — "local,remote" with nothing in scope able to serve (the scoped decision throws
    // NoAvailableTargetException and the fleet is truly all offline) ⇒ the EXISTING 503
    // diagnostics fire — never the Cloud relay, never an unscooped fall-through.
    [Fact]
    public async Task ScopePin_LocalRemote_NothingInScope_Returns503_NeverCloud()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "local,remote";
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var decision = new Mock<IRoutingDecisionService>();
        decision
            .Setup(r => r.DecideDetailedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), (IReadOnlySet<TargetLocation>)ScopeSetIsEqualTo(LocalRemoteScopeSet), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NoAvailableTargetException("No enabled routing target is available to handle the request for model 'llama3'."));

        var (availability, _) = OnlineMocks();
        var liveness = AllOfflineLiveness(); // fleet truly all offline ⇒ rich 503 diagnostics

        var sut = BuildScopeSut(decision, availability: availability, liveness: liveness);
        await sut.InvokeAsync(context);

        Assert.Equal(503, context.Response.StatusCode);
        responseBody.Seek(0, SeekOrigin.Begin);
        var body = new StreamReader(responseBody).ReadToEnd();
        Assert.Contains("All fleet targets are offline", body);
        // The UNSCOPED decision must never run (no silent cloud / fleet fall-through).
        decision.Verify(r => r.DecideDetailedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        decision.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // B3 (happy half) — "local,remote" with the loopback offline: the scoped decision picks
    // the LAN target and the telemetry echoes its concrete name.
    [Fact]
    public async Task ScopePin_LocalRemote_LoopbackOffline_LanTargetChosen()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "local,remote";

        var decision = new Mock<IRoutingDecisionService>();
        decision
            .Setup(r => r.DecideDetailedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), (IReadOnlySet<TargetLocation>)ScopeSetIsEqualTo(LocalRemoteScopeSet), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingDecisionResult(new RoutingTarget("Lan")));

        var sut = BuildScopeSut(decision);
        await sut.InvokeAsync(context);

        Assert.Equal("Lan", context.Request.Headers["X-Ollama-Target"].ToString());
        Assert.Equal("Lan", context.Response.Headers["X-OllamaRouter-Target"].ToString());
    }

    // B4 — a scope that collapses to {Cloud} ("cloud,cloud") with Cloud Overflow UNAVAILABLE:
    // mirrors today's pinned-Cloud behavior — warning + unscooped dynamic routing (NOT a
    // hard 503, NOT a scoped decision).
    [Fact]
    public async Task ScopePin_CloudOnlyScope_CloudUnavailable_FallsBackToUnscopedDynamicRouting()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Hello" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "cloud,cloud"; // collapses to {Cloud}

        var decision = new Mock<IRoutingDecisionService>();
        decision
            .Setup(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingTarget("Loop"));

        // Cloud Overflow unavailable: the targets.json Cloud toggle is off (an empty
        // availability mock returns false for Cloud) while the fleet targets stay enabled,
        // so the decision result (Loop) survives the all-offline gate and gets dispatched.
        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(RoutingTarget.Cloud)).Returns(false);
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);

        var sut = BuildScopeSut(decision, availability: availability);
        await sut.InvokeAsync(context);

        // Unscooped dynamic routing ran; no 503, no scoped decision.
        Assert.Equal("Loop", context.Request.Headers["X-Ollama-Target"].ToString());
        Assert.Equal(200, context.Response.StatusCode);
        decision.Verify(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        decision.Verify(r => r.DecideDetailedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<IReadOnlySet<TargetLocation>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // B5 — pinned-management scope "remote": the FIRST enabled + routable Remote-location
    // target in declaration order wins; the response header echoes the CONCRETE name.
    [Fact]
    public async Task ScopePin_Management_FirstInScopeRoutableTarget_Wins()
    {
        var context = BuildContext("/api/pull", """{ "model": "llama3" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "remote";

        var sut = BuildScopeSut(new Mock<IRoutingDecisionService>());
        await sut.InvokeAsync(context);

        // Fleet: Loop(Local) → Lan(Remote) → Edge(Cloud-class). First in-scope Remote
        // target in declaration order is Lan; the keyword is never echoed.
        Assert.Equal("Lan", context.Request.Headers["X-Ollama-Target"].ToString());
        Assert.Equal("Lan", context.Response.Headers["X-OllamaRouter-Target"].ToString());
    }

    // B5 (fallback) — management scope "remote" with the only in-scope target offline ⇒
    // the PRIMARY target (first effective) + warning; the header echoes the concrete name.
    [Fact]
    public async Task ScopePin_Management_InScopeOffline_FallsBackToPrimary()
    {
        var context = BuildContext("/api/pull", """{ "model": "llama3" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "remote";

        var (availability, _) = OnlineMocks();
        var liveness = new Mock<ITargetLivenessService>();
        // Moq matches setups in REVERSE registration order: register the general fallback
        // FIRST, then the specific Lan-offline setup so it is consulted first.
        liveness.Setup(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        liveness.Setup(t => t.EnsureKnownAsync(new RoutingTarget("Lan"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Offline);
        liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddHours(1)); // outside grace
        liveness.Setup(t => t.GetFleetTargets()).Returns(new RoutingTarget[] { "Loop", "Lan", "Edge" });
        liveness.Setup(t => t.GetEntries())
            .Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
            {
                [new RoutingTarget("Lan")] = new(new RoutingTarget("Lan"), TargetLivenessState.Offline, "timeout", DateTimeOffset.UtcNow)
            });

        var sut = BuildScopeSut(new Mock<IRoutingDecisionService>(), availability: availability, liveness: liveness);
        await sut.InvokeAsync(context);

        // No in-scope routable target ⇒ the primary (first effective = Loop) fallback.
        Assert.Equal("Loop", context.Request.Headers["X-Ollama-Target"].ToString());
        Assert.Equal("Loop", context.Response.Headers["X-OllamaRouter-Target"].ToString());
    }

    // B6 — invalid scope tokens ("local,,remote" with an empty token, "banana") ⇒ the whole
    // header is unrecognized ⇒ the EXISTING fallbacks (dynamic for completions, primary for
    // management), never a scoped decision.
    [Fact]
    public async Task ScopePin_InvalidTokens_HeaderIgnored_ExistingFallbacks()
    {
        // completions: empty token ⇒ unscooped dynamic routing.
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Hello" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "local,,remote";
        var decision = new Mock<IRoutingDecisionService>();
        decision
            .Setup(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingTarget("Loop"));
        var sut = BuildScopeSut(decision);
        await sut.InvokeAsync(context);

        Assert.Equal("Loop", context.Request.Headers["X-Ollama-Target"].ToString());
        decision.Verify(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        decision.Verify(r => r.DecideDetailedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<IReadOnlySet<TargetLocation>>(), It.IsAny<CancellationToken>()), Times.Never);

        // completions: unknown token ⇒ unscooped dynamic routing.
        var context2 = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Hello" }""");
        context2.Request.Headers["X-OllamaRouter-Target"] = "banana";
        var decision2 = new Mock<IRoutingDecisionService>();
        decision2
            .Setup(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingTarget("Loop"));
        var sut2 = BuildScopeSut(decision2);
        await sut2.InvokeAsync(context2);
        decision2.Verify(r => r.DecideAsync(100, "llama3", It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);

        // management: invalid scope ⇒ primary target (first effective = Loop).
        var context3 = BuildContext("/api/pull", """{ "model": "llama3" }""");
        context3.Request.Headers["X-OllamaRouter-Target"] = "local,,remote";
        var sut3 = BuildScopeSut(new Mock<IRoutingDecisionService>());
        await sut3.InvokeAsync(context3);
        Assert.Equal("Loop", context3.Request.Headers["X-Ollama-Target"].ToString());
        Assert.Equal("Loop", context3.Response.Headers["X-OllamaRouter-Target"].ToString());
    }

    // B7 — an HONORED scope pin on the completion path does NOT explicitly SetAffinity from
    // the middleware (the decision service's natural final-winner lease write is the only
    // lease write — contrast with an honored EXACT pin, which does SetAffinity).
    [Fact]
    public async Task ScopePin_HonoredScope_NoExplicitSetAffinityFromMiddleware()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Hello" }""");
        context.Request.Headers["X-OllamaRouter-Target"] = "local,remote";
        context.Request.Headers["X-OllamaRouter-Session-Id"] = "session-1";

        var decision = new Mock<IRoutingDecisionService>();
        decision
            .Setup(r => r.DecideDetailedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.Is<string?>(s => s == "session-1"), (IReadOnlySet<TargetLocation>)ScopeSetIsEqualTo(LocalRemoteScopeSet), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutingDecisionResult(new RoutingTarget("Loop")));

        var affinity = new Mock<ISessionAffinityService>();
        var sut = BuildScopeSut(decision, sessionAffinity: affinity.Object);
        await sut.InvokeAsync(context);

        Assert.Equal("Loop", context.Request.Headers["X-Ollama-Target"].ToString());
        // No explicit SetAffinity from the middleware for scope pins.
        affinity.Verify(a => a.SetAffinity(It.IsAny<string>(), It.IsAny<RoutingTarget>()), Times.Never);
        // The session ID was still passed to the scoped decision (the lease write happens there).
        decision.Verify(r => r.DecideDetailedAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.Is<string?>(s => s == "session-1"),
                It.Is<IReadOnlySet<TargetLocation>>(s => !ReferenceEquals(s, null) && s.SetEquals(LocalRemoteScopeSet)), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
