using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Parsing;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Serialization;
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
    IActivityMonitorService activityMonitor,
    IActivityStatisticsService activityStatistics,
    ITargetAvailabilityService targetAvailability,
    IOptions<OllamaRouterOptions> options,
    ILogger<OllamaRoutingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsCompletionRequest(context))
        {
            await RouteAndTrackCompletionRequestAsync(context);
        }
        else if (IsShowRequest(context))
        {
            await RouteShowRequestAsync(context);
            await next(context);
        }
        else
        {
            // Default (pull, push, delete...): target the local instance if enabled, otherwise remote.
            context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = targetAvailability.IsEnabled(RoutingTarget.Local)
                ? OllamaReverseProxyConfig.LocalTarget
                : OllamaReverseProxyConfig.RemoteTarget;
            await next(context);
        }
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

    private async Task RouteAndTrackCompletionRequestAsync(HttpContext context)
    {
        var body = await ReadBodyAsync(context.Request);

        var modelName = "unknown";
        var tokenCount = 0;
        var rawTokenCount = 0;
        RoutingTarget target;

        try
        {
            var requestInfo = OllamaRequestParser.ParseCompletionRequest(body);
            modelName = string.IsNullOrEmpty(requestInfo.ModelName) ? "unknown" : requestInfo.ModelName;
            rawTokenCount = tokenEstimator.EstimateTokens(requestInfo.ContextParts)
                + requestInfo.PriorContextTokenCount;
            tokenCount = (int)Math.Ceiling(rawTokenCount * options.Value.TokenEstimationOverheadFactor);

            target = await routingDecisionService.DecideAsync(tokenCount, modelName, context.RequestAborted);

            logger.LogInformation("{ModelName} - Tokens: {TokenCount} => {Target}", modelName, tokenCount, target);
        }
        catch (NoAvailableTargetException ex)
        {
            logger.LogWarning(ex, "No target could handle the request: {Message}", ex.Message);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new OllamaErrorResponse(ex.Message), OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse));
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while inspecting the request. Falling back to default routing.");
            var fallback = targetAvailability.IsEnabled(RoutingTarget.Remote)
                ? RoutingTarget.Remote
                : targetAvailability.IsEnabled(RoutingTarget.Local)
                    ? RoutingTarget.Local
                    : (RoutingTarget?)null;

            if (fallback is null)
            {
                logger.LogWarning("No enabled routing target available for fallback.");
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new OllamaErrorResponse("No enabled routing target is available."), OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse));
                return;
            }

            target = fallback.Value;
        }

        context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = target switch
        {
            RoutingTarget.Local => OllamaReverseProxyConfig.LocalTarget,
            RoutingTarget.Cloud => OllamaReverseProxyConfig.LocalTarget,
            _ => OllamaReverseProxyConfig.RemoteTarget
        };

        if (target == RoutingTarget.Cloud)
        {
            var normalizedName = RoutingDecisionService.NormalizeModelName(modelName);
            if (options.Value.Models.TryGetValue(normalizedName, out var cloudThresholds) &&
                !string.IsNullOrEmpty(cloudThresholds.CloudModel))
            {
                var rewrittenBody = OllamaRequestParser.ReplaceModelName(body, cloudThresholds.CloudModel);
                var bodyBytes = System.Text.Encoding.UTF8.GetBytes(rewrittenBody);
                context.Request.Body = new MemoryStream(bodyBytes);
                context.Request.ContentLength = bodyBytes.Length;
            }
        }

        var originalBody = context.Response.Body;
        var capturingStream = new ResponseCapturingStream(originalBody);
        context.Response.Body = capturingStream;

        var requestId = activityMonitor.StartRequest(target, modelName, tokenCount);
        var stopwatch = Stopwatch.StartNew();
        var success = false;

        try
        {
            await next(context);
            success = context.Response.StatusCode < 400;
        }
        finally
        {
            stopwatch.Stop();
            context.Response.Body = originalBody;

            var (actualPromptTokens, actualResponseTokens) = TryExtractActualTokens(capturingStream.CapturedTail);

            var entry = new ActivityLogEntry(
                DateTimeOffset.UtcNow,
                target,
                modelName,
                tokenCount,
                actualPromptTokens,
                actualResponseTokens,
                stopwatch.ElapsedMilliseconds,
                context.Response.StatusCode,
                success,
                rawTokenCount);

            activityMonitor.CompleteRequest(requestId, entry);
            activityStatistics.Add(entry);
        }
    }

    private static (int? PromptTokens, int? ResponseTokens) TryExtractActualTokens(string capturedTail)
    {
        if (string.IsNullOrWhiteSpace(capturedTail))
        {
            return (null, null);
        }

        // The last NDJSON line of /api/chat and /api/generate responses carries "prompt_eval_count"
        // and "eval_count". OpenAI-compatible (SSE) responses instead carry a "usage" object with
        // "prompt_tokens" and "completion_tokens" in the final "data:" block before "data: [DONE]".
        foreach (var line in capturedTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            var json = line;
            if (json.StartsWith("data:", StringComparison.Ordinal))
            {
                // OpenAI-compatible SSE responses prefix each payload with "data:".
                json = json["data:".Length..].TrimStart();
            }

            if (json.Length == 0 || json[0] != '{')
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                int? promptTokens = null;
                int? responseTokens = null;

                if (root.TryGetProperty("prompt_eval_count", out var promptEvalCount) && promptEvalCount.TryGetInt32(out var promptCount))
                {
                    promptTokens = promptCount;
                }

                if (root.TryGetProperty("eval_count", out var evalCount) && evalCount.TryGetInt32(out var count))
                {
                    responseTokens = count;
                }

                if (root.TryGetProperty("usage", out var usage))
                {
                    if (promptTokens is null && usage.TryGetProperty("prompt_tokens", out var promptTokensProp) && promptTokensProp.TryGetInt32(out var promptTokensValue))
                    {
                        promptTokens = promptTokensValue;
                    }

                    if (responseTokens is null && usage.TryGetProperty("completion_tokens", out var completionTokens) && completionTokens.TryGetInt32(out var completionCount))
                    {
                        responseTokens = completionCount;
                    }
                }

                if (promptTokens is not null || responseTokens is not null)
                {
                    return (promptTokens, responseTokens);
                }
            }
            catch (JsonException)
            {
                // Not a complete/valid JSON line (e.g. truncated tail); ignore and keep looking.
            }
        }

        return (null, null);
    }

    private async Task RouteShowRequestAsync(HttpContext context)
    {
        var body = await ReadBodyAsync(context.Request);

        try
        {
            var modelName = OllamaRequestParser.ExtractModelName(body);

            var remoteEnabled = targetAvailability.IsEnabled(RoutingTarget.Remote);
            var localEnabled = targetAvailability.IsEnabled(RoutingTarget.Local);

            // Prefer the remote instance when enabled and the model is known to be available there;
            // otherwise fall back to local if enabled, or remote if it is the only enabled target.
            var existsRemotely = remoteEnabled && await modelCatalogCache.ModelExistsAsync(options.Value.RemoteUrl, modelName, context.RequestAborted);

            var target = (existsRemotely || (remoteEnabled && !localEnabled))
                ? OllamaReverseProxyConfig.RemoteTarget
                : OllamaReverseProxyConfig.LocalTarget;

            logger.LogInformation("{ModelName} - /api/show => {Target}", modelName, target);

            context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = target;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while inspecting the /api/show request. Falling back to default routing.");
            context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = targetAvailability.IsEnabled(RoutingTarget.Remote)
                ? OllamaReverseProxyConfig.RemoteTarget
                : OllamaReverseProxyConfig.LocalTarget;
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

public sealed record OllamaErrorResponse(
    [property: JsonPropertyName("error")] string Error);
