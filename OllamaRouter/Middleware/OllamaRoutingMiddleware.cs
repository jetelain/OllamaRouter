using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Parsing;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using Yarp.ReverseProxy.Forwarder;

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
    IInferenceRequestRecorder inferenceRequestRecorder,
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
            context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = GetDefaultHeaderValue();
            await next(context);
        }
    }

    // The header value for non-inspected requests: the name of the target named "Local"
    // (case-insensitive) when enabled, otherwise the first other enabled effective target
    // (Remote in the legacy configuration, preserving the previous behavior exactly).
    private string GetDefaultHeaderValue()
    {
        var effectiveTargets = options.Value.GetEffectiveTargets(logger);
        if (effectiveTargets.Count == 0)
        {
            return OllamaReverseProxyConfig.LocalTarget;
        }

        var local = effectiveTargets.FirstOrDefault(t => string.Equals(t.Name, OllamaReverseProxyConfig.LocalTarget, StringComparison.OrdinalIgnoreCase));
        if (local is not null && targetAvailability.IsEnabled(local.Name))
        {
            return local.Name!;
        }

        var fallback = effectiveTargets.FirstOrDefault(t => t.Name is not null && targetAvailability.IsEnabled(t.Name));
        return fallback?.Name ?? effectiveTargets[0].Name!;
    }

    // Last-resort fallback when request inspection fails: prefer the "Remote" named target,
    // then the "Local" named target, then any other enabled effective target — mirroring
    // the legacy Remote-then-Local preference. Returns null when no target is enabled.
    private string? GetFallbackHeaderValue()
    {
        var effectiveTargets = options.Value.GetEffectiveTargets(logger);
        return effectiveTargets.FirstOrDefault(t => string.Equals(t.Name, OllamaReverseProxyConfig.RemoteTarget, StringComparison.OrdinalIgnoreCase)
                && targetAvailability.IsEnabled(t.Name))?.Name
            ?? effectiveTargets.FirstOrDefault(t => string.Equals(t.Name, OllamaReverseProxyConfig.LocalTarget, StringComparison.OrdinalIgnoreCase)
                && targetAvailability.IsEnabled(t.Name))?.Name
            ?? effectiveTargets.FirstOrDefault(t => t.Name is not null && targetAvailability.IsEnabled(t.Name))?.Name;
    }

    // Cloud traffic is relayed through the "local" instance (signed in to ollama.com); the
    // X-Ollama-Target header therefore carries the relay target's name: a target named "Local"
    // (case-insensitive) when present, otherwise the first effective target. Any other target
    // is mapped to its own name.
    private string MapTargetToHeader(RoutingTarget target)
    {
        if (target == RoutingTarget.Cloud)
        {
            var effectiveTargets = options.Value.GetEffectiveTargets(logger);
            var relay = effectiveTargets.FirstOrDefault(t => string.Equals(t.Name, OllamaReverseProxyConfig.LocalTarget, StringComparison.OrdinalIgnoreCase))
                ?? (effectiveTargets.Count > 0 ? effectiveTargets[0] : null);
            if (relay is null)
            {
                logger.LogWarning("Cloud target requested but no effective targets are configured; falling back to '{LocalTarget}'.", OllamaReverseProxyConfig.LocalTarget);
                return OllamaReverseProxyConfig.LocalTarget;
            }

            logger.LogInformation("Cloud overflow request is relayed through target {RelayTarget}.", relay.Name);
            return relay.Name!;
        }

        return target.ToString();
    }

    // Case-insensitive lookup of the pinned target over the configured fleet targets plus the
    // Cloud pseudo-target; unknown names do not match (the request falls back to dynamic routing).
    private bool TryParsePinnedTarget(string? headerValue, out RoutingTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        if (string.Equals(headerValue, RoutingTarget.Cloud.Value, StringComparison.OrdinalIgnoreCase))
        {
            target = RoutingTarget.Cloud;
            return true;
        }

        var match = options.Value.GetEffectiveTargets(logger)
            .FirstOrDefault(t => string.Equals(t.Name, headerValue, StringComparison.OrdinalIgnoreCase));
        if (match is null || string.IsNullOrWhiteSpace(match.Name))
        {
            return false;
        }

        target = (RoutingTarget)match.Name!;
        return true;
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
        var endpoint = context.Request.Path.Value ?? "/";

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

            if (context.Request.Headers.TryGetValue("X-OllamaRouter-Target", out var requestedTargetHeader) &&
                TryParsePinnedTarget(requestedTargetHeader.ToString(), out var requestedTarget))
            {
                if (targetAvailability.IsEnabled(requestedTarget))
                {
                    target = requestedTarget;
                    logger.LogInformation("Routing target explicitly pinned via request header: {Target}", target);
                }
                else
                {
                    logger.LogWarning("Requested target {Target} is disabled. Falling back to dynamic routing.", requestedTarget);
                    target = await routingDecisionService.DecideAsync(tokenCount, modelName, context.RequestAborted);
                }
            }
            else
            {
                target = await routingDecisionService.DecideAsync(tokenCount, modelName, context.RequestAborted);
            }

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
            var fallback = GetFallbackHeaderValue();
            if (fallback is null)
            {
                logger.LogWarning("No enabled routing target available for fallback.");
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new OllamaErrorResponse("No enabled routing target is available."), OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse));
                return;
            }

            target = (RoutingTarget)fallback;
        }

        context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = MapTargetToHeader(target);

        context.Response.Headers["X-OllamaRouter-Target"] = target.ToString();
        context.Response.Headers["X-OllamaRouter-Model"] = modelName;
        context.Response.Headers["X-OllamaRouter-Estimated-Tokens"] = tokenCount.ToString();

        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-OllamaRouter-Target"] = target.ToString();
            context.Response.Headers["X-OllamaRouter-Model"] = modelName;
            context.Response.Headers["X-OllamaRouter-Estimated-Tokens"] = tokenCount.ToString();
            return Task.CompletedTask;
        });

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
                body = rewrittenBody; // Record the payload actually sent to the cloud target.
            }
        }

        var originalBody = context.Response.Body;
        var capturingStream = new ResponseCapturingStream(originalBody);
        context.Response.Body = capturingStream;

        var requestId = activityMonitor.StartRequest(target, modelName, tokenCount);
        var stopwatch = Stopwatch.StartNew();
        var success = false;
        var isClientAborted = false;

        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            isClientAborted = true;
        }
        finally
        {
            stopwatch.Stop();
            context.Response.Body = originalBody;

            isClientAborted = isClientAborted || context.RequestAborted.IsCancellationRequested;

            var forwarderError = context.Features.Get<IForwarderErrorFeature>();
            if (forwarderError is not null && forwarderError.Error is ForwarderError.RequestCanceled
                                                                   or ForwarderError.ResponseBodyCanceled
                                                                   or ForwarderError.ResponseBodyClient
                                                                   or ForwarderError.RequestBodyCanceled
                                                                   or ForwarderError.RequestBodyClient)
            {
                isClientAborted = true;
            }

            var (actualPromptTokens, actualResponseTokens) = TryExtractActualTokens(capturingStream.CapturedTail);
            var isStreamCompleted = IsResponseComplete(context, capturingStream.CapturedTail, actualResponseTokens);

            var isInterrupted = !isStreamCompleted;
            success = context.Response.StatusCode < 400 && !isInterrupted;

            var recordedStatusCode = context.Response.StatusCode;
            if (context.Response.StatusCode < 400 && isInterrupted)
            {
                recordedStatusCode = isClientAborted ? 499 : StatusCodes.Status502BadGateway;
            }

            var entry = new ActivityLogEntry(
                DateTimeOffset.UtcNow,
                target,
                modelName,
                tokenCount,
                actualPromptTokens,
                actualResponseTokens,
                stopwatch.ElapsedMilliseconds,
                recordedStatusCode,
                success,
                rawTokenCount);

            activityMonitor.CompleteRequest(requestId, entry);
            activityStatistics.Add(entry);
            inferenceRequestRecorder.Record(entry, endpoint, body);
        }
    }

    private static bool IsResponseComplete(HttpContext context, string capturedTail, int? actualResponseTokens)
    {
        if (actualResponseTokens is not null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(capturedTail))
        {
            return false;
        }

        var contentType = context.Response.ContentType ?? "";

        bool isSse = contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
            || capturedTail.Contains("data:", StringComparison.Ordinal);

        if (isSse)
        {
            return capturedTail.Contains("[DONE]", StringComparison.Ordinal);
        }

        bool isNdjson = contentType.Contains("application/x-ndjson", StringComparison.OrdinalIgnoreCase)
            || capturedTail.Contains("\"done\":false", StringComparison.Ordinal)
            || capturedTail.Contains("\"done\": false", StringComparison.Ordinal);

        if (isNdjson)
        {
            return capturedTail.Contains("\"done\":true", StringComparison.Ordinal) ||
                   capturedTail.Contains("\"done\": true", StringComparison.Ordinal);
        }

        return capturedTail.TrimEnd().EndsWith('}');
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
