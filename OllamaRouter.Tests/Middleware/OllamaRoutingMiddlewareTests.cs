using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OllamaRouter.Middleware;
using OllamaRouter.Services;

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
        Mock<IRoutingDecisionService> routingDecisionService)
    {
        return new OllamaRoutingMiddleware(
            next,
            tokenEstimator.Object,
            routingDecisionService.Object,
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
}
