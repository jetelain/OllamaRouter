using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Services;
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
        Mock<IActivityMonitorService>? activityMonitor = null)
    {
        return new OllamaRoutingMiddleware(
            next,
            tokenEstimator.Object,
            routingDecisionService.Object,
            (modelCatalogCache ?? new Mock<IModelCatalogCacheService>()).Object,
            (activityMonitor ?? new Mock<IActivityMonitorService>()).Object,
            MsOptions.Create(new OllamaRouterOptions { LocalUrl = "http://localhost:11435", RemoteUrl = "http://remote:11434" }),
            NullLogger<OllamaRoutingMiddleware>.Instance);
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
        tokenEstimator.Verify(t => t.EstimateTokens(It.IsAny<string>()), Times.Never);
        routingDecisionService.Verify(r => r.DecideAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_InterceptedPath_PassesTokenCountAndModelName_ToRoutingDecisionService()
    {
        var context = BuildContext("/api/chat", """{ "model": "llama3", "prompt": "Bonjour" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<string>())).Returns(100);

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
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<string>())).Returns(50);

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
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<string>())).Throws(new InvalidOperationException("boom"));

        var routingDecisionService = new Mock<IRoutingDecisionService>();

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService);

        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
    }

    [Fact]
    public async Task InvokeAsync_GenerateRequestWithPriorContext_AddsPriorContextTokenCount()
    {
        var context = BuildContext("/api/generate", """{ "model": "llama3", "prompt": "Continue", "context": [1, 2, 3, 4, 5] }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<string>())).Returns(100);

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
    public async Task InvokeAsync_ShowRequest_ModelExistsRemotely_SetsHeaderToRemote()
    {
        var context = BuildContext("/api/show", """{ "model": "llama3" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        var routingDecisionService = new Mock<IRoutingDecisionService>();

        var modelCatalogCache = new Mock<IModelCatalogCacheService>();
        modelCatalogCache
            .Setup(c => c.ModelExistsAsync("http://remote:11434", "llama3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService, modelCatalogCache);

        await sut.InvokeAsync(context);

        Assert.Equal("Remote", context.Request.Headers["X-Ollama-Target"]);
    }

    [Fact]
    public async Task InvokeAsync_ShowRequest_ModelNotFoundRemotely_SetsHeaderToLocal()
    {
        var context = BuildContext("/api/show", """{ "model": "local-only" }""");

        var tokenEstimator = new Mock<ITokenEstimator>();
        var routingDecisionService = new Mock<IRoutingDecisionService>();

        var modelCatalogCache = new Mock<IModelCatalogCacheService>();
        modelCatalogCache
            .Setup(c => c.ModelExistsAsync("http://remote:11434", "local-only", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateSut(_ => Task.CompletedTask, tokenEstimator, routingDecisionService, modelCatalogCache);

        await sut.InvokeAsync(context);

        Assert.Equal("Local", context.Request.Headers["X-Ollama-Target"]);
    }

    [Fact]
    public async Task InvokeAsync_OpenAIStyleSseResponse_ExtractsUsageFromLastDataBlock()
    {
        var context = BuildContext("/v1/chat/completions", """{ "model": "qwen", "stream": true }""");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<string>())).Returns(100);

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
    public async Task InvokeAsync_OllamaStyleNdjsonResponse_ExtractsTokenCountsFromLastLine()
    {
        var context = BuildContext("/api/chat", """{ "model": "qwen", "stream": true }""");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var tokenEstimator = new Mock<ITokenEstimator>();
        tokenEstimator.Setup(t => t.EstimateTokens(It.IsAny<string>())).Returns(100);

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
}
