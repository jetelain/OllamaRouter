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
    ITargetLivenessService targetLiveness,
    IOptions<OllamaRouterOptions> options,
    ILogger<OllamaRoutingMiddleware> logger)
{
    // During the first 10 s after the liveness prober started, an unresolved (Unknown)
    // target is treated as routable rather than offline, so a request arriving before the
    // boot probe completes is never bounced to 503 merely because probing is slow.
    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(10);

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsCompletionRequest(context))
        {
            // The completion pipeline performs the forwarding (or a 503 short-circuit) itself.
            await RouteAndTrackCompletionRequestAsync(context);
        }
        else if (IsPinnedManagementRequest(context))
        {
            // Pinned class (pull/delete/create/copy): X-OllamaRouter-Target honored when valid +
            // enabled + online/grace, otherwise the primary target (the exact legacy default).
            // YARP forwards, so streams (pull progress) work unchanged.
            await RoutePinnedManagementRequestAsync(context);
            await next(context);
        }
        else
        {
            // Default (everything else, incl. the short-circuited hybrid GET endpoints whose
            // response never reaches YARP): target the local instance if enabled, otherwise remote.
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

    // A pinned target is routable when its liveness is Online, or — during the 10 s startup
    // grace window — still Unknown (the boot probe may simply not have completed yet). After
    // the grace window an unresolved Unknown is treated as Offline.
    private bool IsPinnedTargetRoutable(TargetLivenessState state)
    {
        if (state == TargetLivenessState.Online)
        {
            return true;
        }

        return state == TargetLivenessState.Unknown &&
               DateTimeOffset.UtcNow - targetLiveness.ServiceStartTimeUtc < StartupGracePeriod;
    }

    // All-offline predicate: no ENABLED fleet target is routable. A target is routable when
    // the TTL-aware IsOnline is true, OR — during the 10 s startup grace window — when its
    // raw state is still Unknown (the boot probe has not definitively resolved it yet, so we
    // do not bounce a request to 503 merely because probing is slow). A TTL-expired stale
    // Online entry is NOT routable (so the 10 s re-prober keeps trying to bring it back).
    private bool IsFleetAllOffline()
    {
        var fleetTargets = targetLiveness.GetFleetTargets();
        if (fleetTargets.Count == 0)
        {
            return true;
        }

        var withinGrace = IsWithinStartupGrace();
        var entries = targetLiveness.GetEntries();
        return !fleetTargets.Any(t =>
            targetAvailability.IsEnabled(t) &&
            (targetLiveness.IsOnline(t) ||
             (withinGrace && entries.TryGetValue(t, out var entry) && entry.State == TargetLivenessState.Unknown)));
    }

    // True while the liveness prober is within its 10 s startup grace window. A zero/MinValue
    // ServiceStartTimeUtc (service never started) is NOT a grace window.
    private bool IsWithinStartupGrace()
    {
        var start = targetLiveness.ServiceStartTimeUtc;
        return start != DateTimeOffset.MinValue &&
               DateTimeOffset.UtcNow - start < StartupGracePeriod;
    }

    // Cloud Overflow availability for this request (mirrors CloudRoutingTargetHandler's
    // predicate): the model has a configured CloudModel AND the Cloud target is enabled.
    private bool IsCloudOverflowAvailable(string modelName)
    {
        if (!targetAvailability.IsEnabled(RoutingTarget.Cloud))
        {
            return false;
        }

        var normalizedName = RoutingDecisionService.NormalizeModelName(modelName);
        return options.Value.Models.TryGetValue(normalizedName, out var thresholds) &&
               !string.IsNullOrEmpty(thresholds.CloudModel);
    }

    // Diagnostics payload for the all-offline 503: enumerates the ENABLED fleet targets
    // in fleet order with their raw (non-TTL) state lowercased and the last probe error.
    // cloud_enabled is false by construction (the 503 path is only taken when Cloud is NOT
    // available). The request is never forwarded and no activity statistics are recorded.
    private AllOfflineDiagnostics BuildAllOfflineDiagnostics()
    {
        var entries = targetLiveness.GetEntries();
        var targets = targetLiveness.GetFleetTargets()
            .Where(t => targetAvailability.IsEnabled(t))
            .Select(t =>
            {
                var entry = entries.TryGetValue(t, out var found) ? found : default;
                return new OfflineTargetInfo(
                    t.ToString(),
                    entry is null ? "unknown" : entry.State.ToString().ToLowerInvariant(),
                    entry?.LastError);
            })
            .ToList();

        return new AllOfflineDiagnostics(
            "All fleet targets are offline or disabled.",
            targets,
            CloudEnabled: false);
    }

    private static bool IsCompletionRequest(HttpContext context)
    {
        return context.Request.Method == HttpMethods.Post &&
               (context.Request.Path.StartsWithSegments("/api/chat") ||
                context.Request.Path.StartsWithSegments("/api/generate") ||
                context.Request.Path.StartsWithSegments("/v1/chat/completions") ||
                context.Request.Path.StartsWithSegments("/v1/completions") ||
                context.Request.Path.StartsWithSegments("/api/embeddings"));
    }

    // The pinned-management class: model lifecycle endpoints that are directed at a single
    // daemon (the X-OllamaRouter-Target pin, or the primary target when absent).
    private static bool IsPinnedManagementRequest(HttpContext context)
    {
        return (context.Request.Method == HttpMethods.Post &&
                (context.Request.Path.StartsWithSegments("/api/pull") ||
                 context.Request.Path.StartsWithSegments("/api/create") ||
                 context.Request.Path.StartsWithSegments("/api/copy"))) ||
               (context.Request.Method == HttpMethods.Delete &&
                context.Request.Path.StartsWithSegments("/api/delete"));
    }

    // Pinned-management routing: honor the X-OllamaRouter-Target pin when it names a real,
    // enabled, online/grace target; otherwise fall back to the primary target
    // (GetDefaultHeaderValue — "Local" when present+enabled, else first enabled effective
    // target, else first effective target). The served target is echoed in the response
    // telemetry header X-OllamaRouter-Target. Unlike completions, there is no token parsing,
    // no dynamic DecideAsync and no all-offline gate: a pinned lifecycle request always goes
    // somewhere (YARP will report 502/504 if that daemon is unreachable).
    private async Task RoutePinnedManagementRequestAsync(HttpContext context)
    {
        RoutingTarget? pinned = null;
        if (context.Request.Headers.TryGetValue("X-OllamaRouter-Target", out var requestedTargetHeader) &&
            TryParsePinnedTarget(requestedTargetHeader.ToString(), out var requestedTarget))
        {
            if (targetAvailability.IsEnabled(requestedTarget))
            {
                var pinnedState = requestedTarget == RoutingTarget.Cloud
                    ? TargetLivenessState.Online
                    : await targetLiveness.EnsureKnownAsync(requestedTarget, context.RequestAborted);

                if (IsPinnedTargetRoutable(pinnedState))
                {
                    pinned = requestedTarget;
                    logger.LogInformation("Pinned management request routed to pinned target: {Target}", pinned);
                }
                else
                {
                    logger.LogWarning("Pinned target {Target} is offline (liveness {State}); routing the management request to the primary target.", requestedTarget, pinnedState);
                }
            }
            else
            {
                logger.LogWarning("Pinned target {Target} is disabled; routing the management request to the primary target.", requestedTarget);
            }
        }

        var served = pinned is not null ? pinned : (RoutingTarget)GetDefaultHeaderValue();
        context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = served.ToString();

        // Response telemetry header (mirrors the completion path: set eagerly so it is visible
        // before the response starts, and re-asserted in OnStarting so streamed pull progress
        // responses carry it too).
        context.Response.Headers["X-OllamaRouter-Target"] = served.ToString();
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-OllamaRouter-Target"] = served.ToString();
            return Task.CompletedTask;
        });
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
                    // Pinned-target liveness gate: never forward to a dead daemon. Cloud is a
                    // relay (never probed); fleet targets are probed on demand (bounded at
                    // the 1.5 s probe timeout) before being trusted.
                    var pinnedState = requestedTarget == RoutingTarget.Cloud
                        ? TargetLivenessState.Online
                        : await targetLiveness.EnsureKnownAsync(requestedTarget, context.RequestAborted);

                    if (IsPinnedTargetRoutable(pinnedState))
                    {
                        target = requestedTarget;
                        logger.LogInformation("Routing target explicitly pinned via request header: {Target}", target);
                    }
                    else
                    {
                        logger.LogWarning("Requested target {Target} is offline (liveness {State}). Falling back to dynamic routing.", requestedTarget, pinnedState);
                        target = await routingDecisionService.DecideAsync(tokenCount, modelName, context.RequestAborted);
                    }
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

        // All-offline gate (completion requests only): runs AFTER the pinned/dynamic decision
        // is resolved (and a final on-demand probe of the chosen fleet target, bounded at the
        // 1.5 s probe timeout) and BEFORE the target header, response telemetry headers and
        // activity statistics — the 503 path performs no forwarding and records nothing.
        // Cloud is a relay and is never gated by liveness (the Q4-A resolution: hand off to
        // Cloud whenever Cloud is available, regardless of relay-target liveness).
        if (target != RoutingTarget.Cloud)
        {
            await targetLiveness.EnsureKnownAsync(target, context.RequestAborted);

            if (IsFleetAllOffline())
            {
                if (IsCloudOverflowAvailable(modelName))
                {
                    logger.LogWarning("All fleet targets are offline; handing {ModelName} off to Cloud Overflow.", modelName);
                    target = RoutingTarget.Cloud;
                }
                else
                {
                    logger.LogWarning("All fleet targets are offline or disabled and Cloud Overflow is unavailable; returning 503 for {ModelName}.", modelName);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(
                        BuildAllOfflineDiagnostics(),
                        OllamaRouterJsonSerializerContext.Default.AllOfflineDiagnostics));
                    return;
                }
            }
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

/// <summary>
/// Diagnostics payload returned as <c>503 Service Unavailable</c> when all enabled
/// fleet targets are offline/unknown and Cloud Overflow is not available for the
/// request. The <c>error</c> field mirrors <see cref="OllamaErrorResponse"/> so clients
/// that parse <c>error</c> keep working.
/// </summary>
public sealed record AllOfflineDiagnostics(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("targets")] IReadOnlyList<OfflineTargetInfo> Targets,
    [property: JsonPropertyName("cloud_enabled")] bool CloudEnabled);

/// <summary>
/// Per-target diagnostics for the all-offline payload: the raw (non-TTL) liveness
/// state, lowercased, plus the most recent probe error (null for Unknown).
/// </summary>
public sealed record OfflineTargetInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("last_error")] string? LastError);
