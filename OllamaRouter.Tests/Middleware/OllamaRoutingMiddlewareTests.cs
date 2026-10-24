using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
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
        ModelCapabilityEngine? capabilityEngine = null)
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
                    [RoutingTarget.Local] = new TargetLivenessEntry(RoutingTarget.Local, TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
                    [RoutingTarget.Remote] = new TargetLivenessEntry(RoutingTarget.Remote, TargetLivenessState.Online, null, DateTimeOffset.UtcNow)
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
            tokenEstimator.Object,
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
            new Mock<ITargetExecutionTracker>().Object);
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
        routingDecisionService.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_InterceptedPath_PassesTokenCountAndModelName_ToRoutingDecisionService()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "llama3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
        routingDecisionService.Verify(r => r.DecideAsync(100, "llama3", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_RoutingDecisionReturnsLocal_SetsHeaderToLocal()
    {
        var context = BuildContext("/api/generate", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(50);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(50, "llama3", It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(105, "llama3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
        routingDecisionService.Verify(r => r.DecideAsync(105, "llama3", It.IsAny<CancellationToken>()), Times.Once);
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
        routingDecisionService.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
            .Setup(r => r.DecideAsync(100, "llama3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Remote);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
        routingDecisionService.Verify(r => r.DecideAsync(100, "llama3", It.IsAny<CancellationToken>()), Times.Once);
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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(100, "llama3", It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(100, "qwen", It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(1420, "qwen2.5-coder:32b", It.IsAny<CancellationToken>()))
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
        routingDecisionService.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
            .Setup(r => r.DecideAsync(50, "llama3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var targetAvailability = new Mock<ITargetAvailabilityService>();
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Remote)).Returns(false);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Local)).Returns(true);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService, targetAvailability: targetAvailability);

        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Response.Headers["X-OllamaRouter-Target"]);
        // Because Remote was disabled, it fell back to dynamic routing
        routingDecisionService.Verify(r => r.DecideAsync(50, "llama3", It.IsAny<CancellationToken>()), Times.Once);
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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var targetAvailability = new Mock<ITargetAvailabilityService>();
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Local)).Returns(true);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Remote)).Returns(true);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Cloud)).Returns(false);

        var activityMonitor = new Mock<IActivityMonitorService>();

        var (options, optionsWrapper) = OptionsWithCloud(cloudModel: null);
        var liveness = AllOfflineLiveness();

        var nextCalled = false;
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
            new Mock<ITargetExecutionTracker>().Object);

        await sut.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(nextCalled);
        Assert.False(context.Request.Headers.ContainsKey("X-Ollama-Target"));
        activityMonitor.Verify(m => m.StartRequest(It.IsAny<RoutingTarget>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);

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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        var sut = new OllamaRoutingMiddleware(
            c =>
            {
                nextCalled = true;
                capturedTargetHeader = c.Request.Headers["X-Ollama-Target"].ToString();
                using var reader = new StreamReader(c.Request.Body);
                var body = reader.ReadToEnd();
                using var doc = JsonDocument.Parse(body);
                capturedModel = doc.RootElement.GetProperty("model").GetString();
                return Task.CompletedTask;
            },
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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.DecideAsync(50, "llama3", It.IsAny<CancellationToken>()))
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
        routingDecisionService.Verify(r => r.DecideAsync(50, "llama3", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartupUnknownWithinGraceWindow_IsRoutable_Not503()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        var sut = new OllamaRoutingMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        // The liveness service is consulted only up front (for the pinned/chosen gate),
        // never after the streamed failure — the breaker is inference-isolated.
        liveness.Verify(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()), Times.Exactly(1));
        liveness.Verify(t => t.IsOnline(It.IsAny<RoutingTarget>()), Times.Once);
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
        Mock<IActivityMonitorService>? activityMonitor = null)
    {
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
    public async Task Sizing_Moe200kPrompt_96GBTarget_ModelfilePin221184_Injects221184_AndEmitsContextSizeHeader()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen3.6-35b-a3b", "messages": [ { "role": "user", "content": "a long prompt" } ] }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(200_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(200_000, "qwen3.6-35b-a3b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);
        // 35B-A3B MoE: expert_count present; the Modelfile pins num_ctx 221184 (hierarchy
        // level 1), capping the effective ceiling at the 221,184 bucket.
        catalogClient
            .Setup(c => c.GetShowAsync("http://localhost:11435", "qwen3.6-35b-a3b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject
            {
                ["parameters"] = "num_ctx 221184\nnum_predict 4096",
                ["model_info"] = new JsonObject { ["qwen35.expert_count"] = 128 }
            });

        var options = new OllamaRouterOptions { LocalUrl = "http://localhost:11435" };
        options.Targets.Add(new OllamaTargetOptions { Name = "Local", Url = "http://localhost:11435", HardwareHint = "96gb" });

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
    }

    [Fact]
    public async Task Sizing_DenseDesktop2kPrompt_Client131072_ClampedTo49152_RoutingUnchanged()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen3.8-27b", "messages": [ { "role": "user", "content": "hi" } ], "options": { "num_ctx": 131072 } }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(2_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(2_000, "qwen3.8-27b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);
        // 27B dense: parameter count above the 14B threshold, no experts.
        catalogClient
            .Setup(c => c.GetShowAsync("http://localhost:11435", "qwen3.8-27b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject
            {
                ["model_info"] = new JsonObject { ["general.parameter_count"] = 27_800_000_000L }
            });

        // Legacy configuration: the synthesized "Local" target carries HardwareHint "16gb".
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
        // The large static client value is clamped to the 48k desktop ceiling — never disqualified.
        Assert.Equal(49_152, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal("49152", context.Response.Headers["X-OllamaRouter-Context-Size"]);
        // Routing is unchanged: still the Desktop-equivalent 16 GB target.
        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
        Assert.Equal("Local", context.Response.Headers["X-OllamaRouter-Target"]);
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
            .Setup(r => r.DecideAsync(100_000, "qwen3.6-35b-a3b", It.IsAny<CancellationToken>()))
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
        // 100k tokens on the 16 GB MoE target: the 184,320 baseline is selected, then the
        // hardware CMax (49,152 for the 16gb class) clamps the injected value.
        Assert.Equal(49_152, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task Sizing_Disabled_NoRewriteAndNoCatalogInteraction()
    {
        var context = BuildContext("/api/chat", """{ "model": "m", "prompt": "hi" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(100);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(100, "m", It.IsAny<CancellationToken>()))
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
            cache);

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
            .Setup(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        var sut = new OllamaRoutingMiddleware(
            CaptureBodyDelegate(captured),
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
            .Setup(r => r.DecideAsync(100, "m", It.IsAny<CancellationToken>()))
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
    public async Task Sizing_ModelfileNumCtxBelowBucket_ClampsInjectedValueToModelfile()
    {
        var context = BuildContext("/api/chat", """{ "model": "pinned", "prompt": "hello there" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(3_000);

        var routingDecisionService = new Mock<IRoutingDecisionService>();
        routingDecisionService
            .Setup(r => r.DecideAsync(3_000, "pinned", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoutingTarget.Local);

        var capabilityEngine = CreateCapabilityEngine(out var catalogClient);
        // The Modelfile pins num_ctx 24576: hierarchy level 1 caps CMax below any bucket.
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
}
