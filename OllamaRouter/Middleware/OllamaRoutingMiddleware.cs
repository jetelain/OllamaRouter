using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Parsing;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Services;

namespace OllamaRouter.Middleware;

/// <summary>
/// Inspects chat/generate/show requests to dynamically decide whether they should be routed to
/// the local or remote Ollama instance, by setting the <c>X-Ollama-Target</c> header consumed
/// by the YARP configuration.
/// </summary>
public sealed class OllamaRoutingMiddleware(
    RequestDelegate next,
    ITokenEstimator tokenEstimator,
    IRoutingDecisionService routingDecisionService,
    IModelCatalogCacheService modelCatalogCache,
    IOptions<OllamaRouterOptions> options,
    ILogger<OllamaRoutingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsCompletionRequest(context))
        {
            await RouteCompletionRequestAsync(context);
        }
        else if (IsShowRequest(context))
        {
            await RouteShowRequestAsync(context);
        }
        else
        {
            // Default (pull, push, delete...): target the local instance.
            context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = OllamaReverseProxyConfig.LocalTarget;
        }

        await next(context);
    }

    private static bool IsCompletionRequest(HttpContext context)
    {
        return context.Request.Method == HttpMethods.Post &&
               (context.Request.Path.StartsWithSegments("/api/chat") ||
                context.Request.Path.StartsWithSegments("/api/generate") ||
                context.Request.Path.StartsWithSegments("/v1/chat/completions") ||
                context.Request.Path.StartsWithSegments("/v1/completions"));
    }

    private static bool IsShowRequest(HttpContext context)
    {
        return context.Request.Method == HttpMethods.Post &&
               context.Request.Path.StartsWithSegments("/api/show");
    }

    private async Task RouteCompletionRequestAsync(HttpContext context)
    {
        var body = await ReadBodyAsync(context.Request);

        try
        {
            var contextText = OllamaRequestParser.ExtractContextText(body);
            var modelName = OllamaRequestParser.ExtractModelName(body);
            int tokenCount = tokenEstimator.EstimateTokens(contextText)
                + OllamaRequestParser.ExtractPriorContextTokenCount(body);

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

    private async Task RouteShowRequestAsync(HttpContext context)
    {
        var body = await ReadBodyAsync(context.Request);

        try
        {
            var modelName = OllamaRequestParser.ExtractModelName(body);

            // Prefer the remote instance when the model is known to be available there, since it
            // typically advertises a larger context window; otherwise fall back to the local instance.
            var existsRemotely = await modelCatalogCache.ModelExistsAsync(options.Value.RemoteUrl, modelName, context.RequestAborted);

            logger.LogInformation("{ModelName} - /api/show => {Target}", modelName, existsRemotely ? "Remote" : "Local");

            context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = existsRemotely
                ? OllamaReverseProxyConfig.RemoteTarget
                : OllamaReverseProxyConfig.LocalTarget;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while inspecting the /api/show request. Falling back to Remote routing.");
            context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = OllamaReverseProxyConfig.RemoteTarget;
        }
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return body;
    }
}
