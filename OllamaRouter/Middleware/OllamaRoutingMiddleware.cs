using OllamaRouter.Parsing;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Services;

namespace OllamaRouter.Middleware;

/// <summary>
/// Inspects chat/generate requests to dynamically decide whether they should be routed to
/// the local or remote Ollama instance, by setting the <c>X-Ollama-Target</c> header consumed
/// by the YARP configuration.
/// </summary>
public sealed class OllamaRoutingMiddleware(
    RequestDelegate next,
    ITokenEstimator tokenEstimator,
    IRoutingDecisionService routingDecisionService,
    ILogger<OllamaRoutingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsInterceptedRequest(context))
        {
            await RouteInterceptedRequestAsync(context);
        }
        else
        {
            // Default (pull, push, delete...): target the local instance.
            context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = OllamaReverseProxyConfig.LocalTarget;
        }

        await next(context);
    }

    private static bool IsInterceptedRequest(HttpContext context)
    {
        return context.Request.Method == HttpMethods.Post &&
               (context.Request.Path.StartsWithSegments("/api/chat") ||
                context.Request.Path.StartsWithSegments("/api/generate") ||
                context.Request.Path.StartsWithSegments("/v1/chat/completions") ||
                context.Request.Path.StartsWithSegments("/v1/completions"));
    }

    private async Task RouteInterceptedRequestAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;

        try
        {
            var contextText = OllamaRequestParser.ExtractContextText(body);
            var modelName = OllamaRequestParser.ExtractModelName(body);
            int tokenCount = tokenEstimator.EstimateTokens(contextText);

            var target = await routingDecisionService.DecideAsync(tokenCount, modelName, context.RequestAborted);

            logger.LogInformation("{ModelName} - Tokens: {TokenCount} => {Target}", modelName, tokenCount, target);

            context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = target == RoutingTarget.Local
                ? OllamaReverseProxyConfig.LocalTarget
                : OllamaReverseProxyConfig.RemoteTarget;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while inspecting the request. Falling back to Remote routing.");
            context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = OllamaReverseProxyConfig.RemoteTarget;
        }
    }
}
