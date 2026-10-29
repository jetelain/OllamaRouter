using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Parsing;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using OllamaRouter.Transforms;
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
    ModelCapabilityEngine capabilityEngine,
    IOptions<OllamaRouterOptions> options,
    ILogger<OllamaRoutingMiddleware> logger,
    IHttpForwarder forwarder,
    ITargetFailureStreakService streakTracker,
    ITargetExecutionTracker executionTracker = null!,
    IContextAutoShrinkManager? contextShrinkManager = null,
    ISessionAffinityService? sessionAffinity = null)
    {
    // During the first 10 s after the liveness prober started, an unresolved (Unknown)
    // target is treated as routable rather than offline, so a request arriving before the
    // boot probe completes is never bounced to 503 merely because probing is slow.
    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(10);

    // Pass-through transformer for the middleware-owned dispatch loop. HttpTransformer.Default
    // (the public YARP 2.3.0 instance) is pure pass-through (headers/body flow untouched), so
    // the already-set request headers (incl. X-Ollama-Target) reach the destination unchanged.
    private static readonly HttpTransformer PassThroughTransformer = HttpTransformer.Default;

    // Per-request forwarding config: a 15 min activity timeout matching the existing YARP
    // cluster config (OllamaReverseProxyConfig.ActivityTimeout). The dedicated proxy client
    // below disables its client-level timeout so long generations survive this window rather
    // than being killed by SocketsHttpHandler's default 100 s.
    private static readonly ForwarderRequestConfig ForwarderRequestConfig = new() { ActivityTimeout = TimeSpan.FromMinutes(15) };

    // Dedicated proxy client for the dispatch loop: a SocketsHttpHandler with a 10 s
    // pooled-connection lifetime (mirrors the LivenessProbe client so sleeping-then-woken
    // machines with a changed IP do not keep hitting dead pooled sockets). No client-level
    // timeout is imposed — an HttpMessageInvoker has none (the 100 s default is an HttpClient
    // concept) — so long generations survive until the per-request 15 min
    // ForwarderRequestConfig.ActivityTimeout above. Shared (thread-safe) across requests; the
    // middleware is instantiated once.
    private static readonly HttpMessageInvoker ProxyClient = new(
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(10) });

    // HttpContext.Items key under which the response capturing stream is stored so the
    // dispatch loop can reach its commit guard (HasWritten) even after the response body has
    // been re-assigned (YARP's DisableBuffering swaps it) or in the test host, where the
    // capturing stream IS the response body.
    internal const string CapturingStreamKey = "OllamaRouter.CapturingStream";

    // HttpContext.Items key under which the dispatch loop publishes the target it is CURRENTLY
    // attempting to forward. The pre-dispatch telemetry OnStarting closure reads it at the
    // moment the response commits (see the failover-telemetry note above) so a successful
    // fallback's flushed headers carry the served target, not the decided primary.
    internal const string CurrentServedTargetKey = "OllamaRouter.CurrentServedTarget";

    /// <summary>
    /// The Auto-Shrink Manager (Phase 3, item 2): consulted after the stateless sizing decision
    /// to apply the KV-cache-hysteresis lease (retain an expanded bucket across the cooldown /
    /// idle window, inject a shrink when a trigger fires). Nullable for unit-test compatibility
    /// with the pre-lease pipeline: when null, the middleware keeps the exact stateless sizing
    /// behavior (no lease, no <c>X-OllamaRouter-Context-Mode</c> header, no lease resets).
    /// Registered as a singleton in production (see <c>Program.cs</c>); the execution tracker
    /// (model-eviction signal) and this middleware (offline/online liveness transitions) are the
    /// two components that invalidate leases.
    /// </summary>
    private readonly IContextAutoShrinkManager? _contextShrinkManager = contextShrinkManager;

    /// <summary>
    /// The multi-agent session/agent sticky-routing lease store (null-guarded for unit-test
    /// compatibility with the pre-affinity pipeline: when null, the middleware extracts no
    /// session lease — the request always uses the stateless DecideAsync overloads, no
    /// X-OllamaRouter-Session-Id response header is emitted, and no failover/liveness
    /// invalidation happens).
    /// </summary>
    private readonly ISessionAffinityService? _sessionAffinity = sessionAffinity;

    // Last-seen raw liveness state per target, used to detect offline/online TRANSITIONS so
    // the Auto-Shrink Manager's leases are reset exactly when a daemon (re)loads (its KV cache
    // is unknown at that point and no stale expanded bucket may be assigned to it).
    private readonly ConcurrentDictionary<RoutingTarget, TargetLivenessState> _lastLivenessState = new();

    public async Task InvokeAsync(HttpContext context)
    {
        // Inbound sanitization (security rule §2.6): the X-Ollama-Target header is the
        // INTERNAL YARP cluster selector. A client-supplied value must NEVER reach YARP
        // route selection or the dispatch forwarder (cluster spoofing), so it is stripped on
        // arrival; every branch below then assigns the router's own computed value.
        if (context.Request.Headers.Remove(OllamaReverseProxyConfig.TargetHeader, out var forgedTarget))
        {
            logger.LogWarning(
                "Stripped inbound {Header} header (value: '{Value}'): the internal cluster selector is set by the router and never honored from clients.",
                OllamaReverseProxyConfig.TargetHeader, forgedTarget.ToString());
        }

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

    // Cloud Overflow availability for this request (shared with CloudRoutingTargetHandler
    // via CloudRelayResolver): the targets.json Cloud toggle AND the static
    // CloudOverflow.Enabled switch AND a resolvable cloud model (per-model CloudModel wins
    // over CloudOverflow.DefaultModel).
    private bool IsCloudOverflowAvailable(string modelName)
    {
        return CloudRelayResolver.IsCloudOverflowAvailable(targetAvailability, options.Value, modelName);
    }

    // Whether the given fleet target can receive a request right now: enabled in
    // targets.json AND (online, or still Unknown within the 10 s startup grace window).
    // Mirrors IsPinnedTargetRoutable / IsFleetAllOffline; does NOT force a new probe (the
    // dispatch loop itself is the probe).
    private bool IsRoutableNow(RoutingTarget target)
    {
        if (!targetAvailability.IsEnabled(target))
        {
            return false;
        }

        if (targetLiveness.IsOnline(target))
        {
            return true;
        }

        var entries = targetLiveness.GetEntries();
        return IsWithinStartupGrace() &&
               entries is not null &&
               entries.TryGetValue(target, out var entry) &&
               entry.State == TargetLivenessState.Unknown;
    }

    /// <summary>
    /// Refreshes the liveness of every ENABLED fleet target whose TTL-aware
    /// <see cref="ITargetLivenessService.IsOnline"/> verdict is currently false, by running one
    /// on-demand probe per such target in parallel (bounded by the 3.5 s probe timeout; a probe failure
    /// leaves the prior state). Called BEFORE a dynamic routing decision so a continuously
    /// healthy target whose Online verdict merely LAPSED (raw state still Online) between the
    /// background re-prober's last tick and this decision is not misrouted: without this, the
    /// decision reads a stale <c>IsOnline == false</c>, disqualifies the target as offline, and
    /// silently routes its hot model to a lower-scoring fallback (task #12 hot-affinity defect).
    /// Targets already Online within TTL are untouched (a no-op probe would be wasted work), and
    /// each target's probe is best-effort so one slow/blackholed daemon cannot stall the whole
    /// decision (the per-target probe timeout bounds it).
    /// </summary>
    private async Task RefreshFleetLivenessAsync(CancellationToken cancellationToken)
    {
        var targetsToProbe = targetLiveness.GetFleetTargets()
            .Where(t => targetAvailability.IsEnabled(t) && !targetLiveness.IsOnline(t))
            .ToList();

        if (targetsToProbe.Count == 0)
        {
            return;
        }

        await Task.WhenAll(targetsToProbe.Select(async fleetTarget =>
        {
            try
            {
                await targetLiveness.EnsureKnownAsync(fleetTarget, cancellationToken);
            }
            catch
            {
                // Best-effort: a probe failure leaves the prior (offline/unknown) state, which is
                // itself the correct liveness signal for the decision (the target stays unroutable
                // and the decision fails over).
            }
        }));
    }

    // Whether any response bytes have already been committed to the client: the real
    // HttpResponse.HasStarted flag, OR (in the DefaultHttpContext test host, where HasStarted
    // never flips after a write) the first-write marker on the capturing stream installed by
    // this middleware.
    private static bool IsResponseCommitted(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            return true;
        }

        return context.Items[CapturingStreamKey] is ResponseCapturingStream { HasWritten: true };
        }

        // Reset an UNCOMMITTED response so the next dispatch-loop candidate can be forwarded
        // (called only after an uncommitted failure — no byte reached the client). YARP's
        // SendAsync throws InvalidOperationException when RequestUtilities.IsResponseSet is true
        // (StatusCode != 200 || HasStarted); its failure path sets StatusCode = 502 WITHOUT
        // flushing (HasStarted stays false). Left in place, the NEXT candidate's forward throws
        // before it can fail over, so the client sees a bare 500 instead of a completed response
        // from a healthy target. The ONLY thing that blocks IsResponseSet is a non-200 status,
        // so restore it to 200 WITHOUT clearing the pre-dispatch telemetry headers (which in the
        // test host are not re-asserted, since the response may never commit before the test
        // reads them). IForwarderErrorFeature is set on context.Features by YARP before SendAsync
        // returns and is deliberately preserved: the post-dispatch error classification
        // (client-abort / mid-stream) reads it after the loop.
        private static void ResetUncommittedResponse(HttpContext context)
        {
            if (IsResponseCommitted(context))
            {
                return; // Bytes already flushed: never mutate a committed response.
            }
            context.Response.StatusCode = StatusCodes.Status200OK;
        }

    // The client-abort classification (binding): the five client-side ForwarderError values (or a
    // canceled request token) mean the client went away. Client aborts never retry, never feed the
    // failure streak, and never apply a liveness strike.
    private static bool IsClientAbort(ForwarderError error)
        => error is ForwarderError.RequestCanceled
            or ForwarderError.ResponseBodyCanceled
            or ForwarderError.ResponseBodyClient
            or ForwarderError.RequestBodyCanceled
            or ForwarderError.RequestBodyClient;

    // Session/agent sticky-routing extraction: X-OllamaRouter-Session-Id first, then the
    // X-OllamaRouter-Agent-Id fallback (whitespace trimmed; empty ⇒ absent). The ID is opaque
    // to the router (any string); values longer than 256 chars are ignored with a warning so
    // a hostile header can never bloat the in-memory lease store. Returns null when no
    // (valid) session/agent ID is present.
    private string? TryExtractSessionId(HttpContext context)
    {
        string? sessionId = null;
        var sourceHeader = OllamaReverseProxyConfig.PublicSessionIdHeader;
        if (context.Request.Headers.TryGetValue(OllamaReverseProxyConfig.PublicSessionIdHeader, out var sessionHeader))
        {
            sessionId = sessionHeader.ToString()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(sessionId) &&
            context.Request.Headers.TryGetValue(OllamaReverseProxyConfig.PublicAgentIdHeader, out var agentHeader))
        {
            sessionId = agentHeader.ToString()?.Trim();
            sourceHeader = OllamaReverseProxyConfig.PublicAgentIdHeader;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        if (sessionId!.Length > 256)
        {
            logger.LogWarning(
                "Ignoring {Header} session ID longer than 256 characters (routing stateless).",
                sourceHeader);
            return null;
        }

        return sessionId;
    }

    private Task<RoutingTarget> DecideWithSessionAsync(
        int tokenCount, string modelName, int? clientNumCtx, string? sessionId, HttpContext context)
    {
        return sessionId is null
            ? routingDecisionService.DecideAsync(tokenCount, modelName, clientNumCtx, context.RequestAborted)
            : routingDecisionService.DecideAsync(tokenCount, modelName, clientNumCtx, sessionId, context.RequestAborted);
    }

    private async Task<RoutingDecisionResult> DecideDetailedWithSessionAsync(
        int tokenCount, string modelName, int? clientNumCtx, string? sessionId, HttpContext context)
    {
        var result = await routingDecisionService.DecideDetailedAsync(tokenCount, modelName, clientNumCtx, sessionId, context.RequestAborted);
        if (result != null)
        {
            return result;
        }

        var decidedTarget = await DecideWithSessionAsync(tokenCount, modelName, clientNumCtx, sessionId, context);
        return new RoutingDecisionResult(decidedTarget);
    }

    // Diagnostics payload for the all-offline 503: enumerates the ENABLED fleet targets
    // in fleet order with their raw (non-TTL) state lowercased and the last probe error.
    // <paramref name="cloudEnabled"/> is false on the pre-dispatch gate (Cloud was NOT
    // available) and true on the dispatch-loop terminal path (every candidate died at
    // runtime AFTER the Cloud relay step was attempted/available).
    private AllOfflineDiagnostics BuildAllOfflineDiagnostics(bool cloudEnabled)
    {
        var entries = targetLiveness.GetEntries() ?? new Dictionary<RoutingTarget, TargetLivenessEntry>();
        var targets = (targetLiveness.GetFleetTargets() ?? Array.Empty<RoutingTarget>())
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
            CloudEnabled: cloudEnabled);
    }

    // Auto-Shrink lease invalidation on liveness TRANSITIONS: the KV cache of a daemon that
    // went offline (or came back online and reloaded its models) is unknown, so no stale
    // expanded bucket may be assigned to a freshly loaded model. The transition is detected
    // against the last state this middleware observed for the target; a null first observation
    // is NOT a transition (the prober may simply have resolved the target for the first time).
    private void ResetLeasesOnLivenessTransition(RoutingTarget target, TargetLivenessState newState)
    {
        if (target == RoutingTarget.Cloud)
        {
            return;
        }

        if (_lastLivenessState.TryGetValue(target, out var previous) && previous != newState)
        {
            if (newState == TargetLivenessState.Offline || previous == TargetLivenessState.Offline)
            {
                logger.LogInformation("Target {Target} liveness transitioned {Previous} -> {New}; resetting its auto-shrink context leases.", target.Value, previous, newState);
                _contextShrinkManager?.ResetTargetLeases(target);
                if (newState == TargetLivenessState.Offline)
                {
                    // The KV cache of a target that just went offline is gone: release every
                    // session/agent lease pinned to it immediately (the decision service's
                    // next turn re-binds the session to a live target) rather than waiting
                    // for the lease TTL to lapse.
                    _sessionAffinity?.InvalidateTarget(target);
                }
            }
        }

        _lastLivenessState[target] = newState;
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
        if (context.Request.Headers.TryGetValue(OllamaReverseProxyConfig.PublicTargetHeader, out var requestedTargetHeader) &&
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

        var served = pinned ?? (RoutingTarget)GetDefaultHeaderValue();
        context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = served.ToString();

        // Response telemetry header (mirrors the completion path: set eagerly so it is visible
        // before the response starts, and re-asserted in OnStarting so streamed pull progress
        // responses carry it too).
        context.Response.Headers[OllamaReverseProxyConfig.PublicTargetHeader] = served.ToString();
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[OllamaReverseProxyConfig.PublicTargetHeader] = served.ToString();
            return Task.CompletedTask;
        });
    }

    // Pin capability gate (completion pipeline only): the requested model must exist in the
    // pinned target's catalog. A KNOWN catalog (non-empty digest) that genuinely lacks the
    // model disqualifies the pin (the caller falls back to scoring). A degraded lookup
    // (empty/unknown digest, or the probe throwing) is PERMISSIVE: the pin is honored, so a
    // capability-discovery outage never blocks a valid pin (task #7 Q4 permissive-unknown
    // policy). Cloud is a relay and never probed, so it is exempt.
    private async Task<bool> IsPinnedTargetCapableAsync(RoutingTarget pinned, string modelName, HttpContext context)
    {
        if (pinned == RoutingTarget.Cloud)
        {
            return true;
        }

        OllamaTargetOptions? pinnedOptions = null;
        foreach (var t in options.Value.GetEffectiveTargets(logger))
        {
            if (string.Equals(t.Name, pinned.Value, StringComparison.OrdinalIgnoreCase))
            {
                pinnedOptions = t;
                break;
            }
        }

        var pinnedUrl = pinnedOptions?.Url;
        if (string.IsNullOrWhiteSpace(pinnedUrl) || string.Equals(modelName, "unknown", StringComparison.Ordinal))
        {
            return true; // nothing to check against: permissive.
        }

        try
        {
            var exists = await modelCatalogCache.ModelExistsAsync(pinnedUrl, modelName, context.RequestAborted);
            if (exists)
            {
                return true;
            }

            var digest = await modelCatalogCache.GetCurrentDigestAsync(pinnedUrl, context.RequestAborted);
            return string.IsNullOrEmpty(digest); // empty digest ⇒ catalog unknown ⇒ permissive.
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Pin capability probe failed for {Model} on {Target}; honoring the pin (permissive).", modelName, pinned.Value);
            return true;
        }
    }

    private async Task RouteAndTrackCompletionRequestAsync(HttpContext context)
    {
        var body = await ReadBodyAsync(context.Request);
        var endpoint = context.Request.Path.Value ?? "/";

        // Detach the request body from the client's (network) content stream immediately: the
        // dispatch loop buffers and forwards the body ASYNCHRONOUSLY, and the client is free to
        // dispose its own request content (HttpClient.Dispose on the request) as soon as
        // SendAsync returns. Reading/rewinding the original network stream after that disposal
        // would surface as an ObjectDisposedException, so the body is held in an in-memory
        // stream from the first byte onward. The dynamic-context sizing step below may replace
        // it with a rewritten (but still in-memory) stream.
        var initialBodyBytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(initialBodyBytes);
        context.Request.ContentLength = initialBodyBytes.Length;
        context.Request.Body.Position = 0;

        // The target that actually produced the response (may differ from the decided primary
        // after an uncommitted failover), or the last attempted one on terminal failure.
        RoutingTarget? servedTarget = null;

        // The request's session/agent sticky-routing ID (X-OllamaRouter-Session-Id with the
        // X-OllamaRouter-Agent-Id fallback); null when absent. Used for the Stage-2
        // SessionAffinityBonus + lease rebinding and echoed back in the response telemetry.
        var sessionId = _sessionAffinity is not null ? TryExtractSessionId(context) : null;

        // The target currently holding an in-flight accounting slot (the decided primary);
        // reassigned to the finally-served target after the dispatch loop so a request that
        // fails over Remote→Local is counted against Local, never against an exhausted candidate.
        RoutingTarget? inFlightTarget = null;

        var modelName = "unknown";
        var tokenCount = 0;
        var rawTokenCount = 0;
        RoutingTarget target;
        RoutingDecisionResult? detailedDecision = null;

        try
        {
            var requestInfo = OllamaRequestParser.ParseCompletionRequest(body);
            modelName = string.IsNullOrEmpty(requestInfo.ModelName) ? "unknown" : requestInfo.ModelName;
            rawTokenCount = tokenEstimator.EstimateTokens(requestInfo.ContextParts, modelName)
                + requestInfo.PriorContextTokenCount;
            tokenCount = (int)Math.Ceiling(rawTokenCount * options.Value.TokenEstimationOverheadFactor);
            // The client's static options.num_ctx (upper-bound preference, never a disqualifier):
            // fed to the routing pipeline's context-fit clamp (plan §2.2) and reused by the
            // dynamic context sizing rewrite below.
            var clientNumCtx = DynamicContextPayloadTransformer.ExtractClientNumCtx(body);

            if (context.Request.Headers.TryGetValue(OllamaReverseProxyConfig.PublicTargetHeader, out var requestedTargetHeader) &&
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
                        // Pinned-target capability gate: the model must exist on the target.
                        // A known catalog lacking the model falls back to scoring; a degraded
                        // lookup (empty digest / probe failure) is permissive (honored).
                        if (await IsPinnedTargetCapableAsync(requestedTarget, modelName, context))
                        {
                            target = requestedTarget;
                            detailedDecision = new RoutingDecisionResult(target);
                            logger.LogInformation("Routing target explicitly pinned via request header: {Target}", target);
                            // Session stickiness (Q1 resolution): an HONORED pin explicitly
                            // sets/refreshes the session lease to the pinned target, so the
                            // caller's subsequent NON-pinned turns stay sticky on it. (Cloud
                            // pins are never leased: SetAffinity ignores the pseudo-target.)
                            if (sessionId is not null)
                            {
                                _sessionAffinity?.SetAffinity(sessionId, target);
                            }
                        }
                        else
                        {
                            logger.LogWarning("Pinned target {Target} does not host model {Model}; falling back to dynamic routing.", requestedTarget, modelName);
                            detailedDecision = await DecideDetailedWithSessionAsync(tokenCount, modelName, clientNumCtx, sessionId, context);
                            target = detailedDecision.Target;
                        }
                    }
                    else
                    {
                        logger.LogWarning("Requested target {Target} is offline (liveness {State}). Falling back to dynamic routing.", requestedTarget, pinnedState);
                        detailedDecision = await DecideDetailedWithSessionAsync(tokenCount, modelName, clientNumCtx, sessionId, context);
                        target = detailedDecision.Target;
                    }
                }
                else
                {
                    logger.LogWarning("Requested target {Target} is disabled. Falling back to dynamic routing.", requestedTarget);
                    detailedDecision = await DecideDetailedWithSessionAsync(tokenCount, modelName, clientNumCtx, sessionId, context);
                    target = detailedDecision.Target;
                }
            }
            else
            {
                // Dynamic routing: refresh every enabled target's TTL-aware liveness verdict first
                // (see RefreshFleetLivenessAsync) so a healthy target whose Online verdict merely
                // lapsed between re-probe ticks is not disqualified as offline and misrouted.
                await RefreshFleetLivenessAsync(context.RequestAborted);
                detailedDecision = await DecideDetailedWithSessionAsync(tokenCount, modelName, clientNumCtx, sessionId, context);
                target = detailedDecision.Target;
            }

            logger.LogInformation("{ModelName} - Tokens: {TokenCount} => {Target}", modelName, tokenCount, target);
        }
        catch (NoAvailableTargetException ex)
        {
            logger.LogWarning(ex, "No target could handle the request: {Message}", ex.Message);

            // All-fleet-dead fast path: the routability check inside DecideAsync disqualifies
            // every OFFLINE target (stage 2), so when the WHOLE fleet is offline the decision
            // throws BEFORE the post-decision all-offline gate below can run. Handle the
            // all-dead case HERE so the client gets the rich diagnostics payload (targets[] with
            // per-target states) instead of a bare 503. (When Cloud Overflow is available it is
            // already a ranked candidate in DecideAsync and is returned as the decision — this
            // throw only fires when Cloud is disabled/unavailable, so the cloudEnabled flag here
            // is false by construction.)
            // Force a fresh on-demand probe of each enabled target (the post-decision gate's
            // EnsureKnownAsync pattern) so the all-dead verdict reflects a hard kill even when
            // the background re-prober has not yet caught up; then emit the rich diagnostics.
            foreach (var fleetTarget in targetLiveness.GetFleetTargets())
            {
                if (!targetAvailability.IsEnabled(fleetTarget))
                {
                    continue;
                }

                try
                {
                    await targetLiveness.EnsureKnownAsync(fleetTarget, context.RequestAborted);
                }
                catch
                {
                    // Best-effort: a probe failure leaves the prior state (still an offline signal).
                }
            }

            if (IsFleetAllOffline())
            {
                logger.LogWarning("All fleet targets are offline or disabled and Cloud Overflow is unavailable; returning 503 with diagnostics for {ModelName}.", modelName);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(
                    BuildAllOfflineDiagnostics(cloudEnabled: false),
                    OllamaRouterJsonSerializerContext.Default.AllOfflineDiagnostics));
                return;
            }

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
            var preDispatchState = await targetLiveness.EnsureKnownAsync(target, context.RequestAborted);
            ResetLeasesOnLivenessTransition(target, preDispatchState);

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
                        BuildAllOfflineDiagnostics(cloudEnabled: false),
                        OllamaRouterJsonSerializerContext.Default.AllOfflineDiagnostics));
                    return;
                }
            }
        }

        context.Request.Headers[OllamaReverseProxyConfig.TargetHeader] = MapTargetToHeader(target);

        int? injectedNumCtx = null;
        string? sizedBody = null;

        if (detailedDecision is { IsFallback: true, TargetCMax: not null } && target != RoutingTarget.Cloud && detailedDecision.Target == target)
        {
            var cMax = detailedDecision.TargetCMax.Value;
            var warning = $"Requested {tokenCount} exceeds capacity {cMax}; clamped to ceiling.";
            context.Response.Headers[OllamaReverseProxyConfig.FallbackHeader] = detailedDecision.FallbackStrategy ?? "LargestCapacityOverflow";
            context.Response.Headers[OllamaReverseProxyConfig.ContextWarningHeader] = warning;
            context.Items[OllamaReverseProxyConfig.FallbackHeader] = detailedDecision.FallbackStrategy ?? "LargestCapacityOverflow";
            context.Items[OllamaReverseProxyConfig.ContextWarningHeader] = warning;

            var (rewrittenBody, injectedNum) = DynamicContextPayloadTransformer.ApplyExplicitNumCtx(body, cMax);
            injectedNumCtx = injectedNum ?? cMax;
            sizedBody = rewrittenBody;
            var rewrittenBytes = Encoding.UTF8.GetBytes(rewrittenBody);
            context.Request.Body = new MemoryStream(rewrittenBytes);
            context.Request.ContentLength = rewrittenBytes.Length;
            context.Request.Body.Position = 0;
        }
        else if (target != RoutingTarget.Cloud)
        {
            (injectedNumCtx, sizedBody) = await TryApplyDynamicContextSizingAsync(context, target, modelName, tokenCount, body);
        }

        var contextModeHeader = context.Response.Headers.ContainsKey(OllamaReverseProxyConfig.TelemetryContextModeHeader)
            ? context.Response.Headers[OllamaReverseProxyConfig.TelemetryContextModeHeader].ToString()
            : null;
        if (sizedBody is not null)
        {
            body = sizedBody; // Record the payload actually sent to the target.
        }

        // Response telemetry headers: the target header tracks the FINALLY-SERVED target —
        // after an uncommitted failover the dispatch loop may have served a different target
        // than the decided primary, so `telemetryTarget` is updated below when the response
        // had not yet been committed. `telemetryTarget` (not `target`) is captured by the
        // OnStarting closure so streamed responses (NDJSON/SSE) re-assert the served target
        // before the first body byte.
        var telemetryTarget = target;
        // Share the CURRENTLY-ATTEMPTED target with the OnStarting closure below. The dispatch
        // loop updates this slot before each candidate's forward, so when a successful fallback
        // commits the response MID-loop (YARP flushes headers → OnStarting fires inside that
        // SendAsync), the closure reads the SERVED target — not the decided primary captured at
        // registration. Without this, a streamed uncommitted-failover response carries the stale
        // primary header (the outer post-dispatch update is skipped once the response committed).
        context.Items[CurrentServedTargetKey] = target;
        context.Response.Headers[OllamaReverseProxyConfig.PublicTargetHeader] = telemetryTarget.ToString();
        context.Response.Headers[OllamaReverseProxyConfig.TelemetryModelHeader] = modelName;
        context.Response.Headers[OllamaReverseProxyConfig.TelemetryEstimatedTokensHeader] = tokenCount.ToString();
        if (sessionId is not null)
        {
            // Echo the recognized session/agent ID back (telemetry + client bookkeeping), both
            // eagerly and re-asserted in the OnStarting closure so streamed NDJSON/SSE
            // responses carry it too.
            context.Response.Headers[OllamaReverseProxyConfig.PublicSessionIdHeader] = sessionId;
        }
        if (injectedNumCtx.HasValue)
        {
            context.Response.Headers[OllamaReverseProxyConfig.TelemetryContextSizeHeader] = injectedNumCtx.Value.ToString();
        }

        context.Response.OnStarting(() =>
        {
            var effectiveServed = context.Items[CurrentServedTargetKey] as RoutingTarget? ?? telemetryTarget;
            context.Response.Headers[OllamaReverseProxyConfig.PublicTargetHeader] = effectiveServed.ToString();
            context.Response.Headers[OllamaReverseProxyConfig.TelemetryModelHeader] = modelName;
            context.Response.Headers[OllamaReverseProxyConfig.TelemetryEstimatedTokensHeader] = tokenCount.ToString();
            if (sessionId is not null)
            {
                context.Response.Headers[OllamaReverseProxyConfig.PublicSessionIdHeader] = sessionId;
            }
            if (effectiveServed == RoutingTarget.Cloud)
            {
                context.Response.Headers.Remove(OllamaReverseProxyConfig.FallbackHeader);
                context.Response.Headers.Remove(OllamaReverseProxyConfig.ContextWarningHeader);
                context.Response.Headers.Remove(OllamaReverseProxyConfig.TelemetryContextSizeHeader);
                context.Response.Headers.Remove(OllamaReverseProxyConfig.TelemetryContextModeHeader);
            }
            else
            {
                if (injectedNumCtx.HasValue)
                {
                    context.Response.Headers[OllamaReverseProxyConfig.TelemetryContextSizeHeader] = injectedNumCtx.Value.ToString();
                }
                if (contextModeHeader is not null)
                {
                    context.Response.Headers[OllamaReverseProxyConfig.TelemetryContextModeHeader] = contextModeHeader;
                }
                if (context.Items[OllamaReverseProxyConfig.FallbackHeader] is string fallback)
                {
                    context.Response.Headers[OllamaReverseProxyConfig.FallbackHeader] = fallback;
                }
                if (context.Items[OllamaReverseProxyConfig.ContextWarningHeader] is string contextWarning)
                {
                    context.Response.Headers[OllamaReverseProxyConfig.ContextWarningHeader] = contextWarning;
                }
            }

            return Task.CompletedTask;
        });

        var originalBody = context.Response.Body;
        var capturingStream = new ResponseCapturingStream(originalBody);
        context.Response.Body = capturingStream;
        // Expose the capturing stream so IsResponseCommitted can read its commit guard even
        // after the response body has been re-assigned (YARP's DisableBuffering) or in the
        // test host, where the capturing stream is the response body.
        context.Items[CapturingStreamKey] = capturingStream;

        // In-flight accounting (Cloud is a relay and is never counted): exactly one
        // increment per real dispatch; the matching decrement lives in the finally block
        // below, keyed on the finally-served target.
        if (target != RoutingTarget.Cloud)
        {
            inFlightTarget = target;
            executionTracker.IncrementInFlight(target);
        }

        var requestId = activityMonitor.StartRequest(target, modelName, tokenCount);
        var stopwatch = Stopwatch.StartNew();
        var success = false;
        var isClientAborted = false;

        try
        {
            // Middleware-owned dispatch loop (Phase 3): forward the buffered body to the
            // decided primary and, on uncommitted failure, rewind the body and fail over to
            // the next eligible candidate (then the terminal Cloud relay step). Replaces the
            // previous `await next(context)` YARP hand-off for completion requests; the
            // forwarder sets the same IForwarderErrorFeature on context.Features that YARP
            // did, so the response-completeness / token-extraction logic below is unchanged.
            (servedTarget, body) = await DispatchLoopAsync(context, body, target);
            if (servedTarget is not null && servedTarget != target && !context.Response.HasStarted)
            {
                // Telemetry failover-correctness: the dispatch loop served a DIFFERENT target
                // than the decided primary (an uncommitted failover — the first attempt died
                // before any byte was written) and the response had not been committed yet,
                // so the telemetry target header reports the served target. The OnStarting
                // closure reads the updated telemetryTarget when it fires; if the response
                // WAS committed, mid-stream failover is impossible (HTTP forbids switching
                // targets) and the decided-target value stands (already flushed).
                telemetryTarget = servedTarget.Value;
                context.Response.Headers[OllamaReverseProxyConfig.PublicTargetHeader] = telemetryTarget.ToString();
                if (telemetryTarget == RoutingTarget.Cloud)
                {
                    context.Response.Headers.Remove(OllamaReverseProxyConfig.FallbackHeader);
                    context.Response.Headers.Remove(OllamaReverseProxyConfig.ContextWarningHeader);
                    context.Response.Headers.Remove(OllamaReverseProxyConfig.TelemetryContextSizeHeader);
                    context.Response.Headers.Remove(OllamaReverseProxyConfig.TelemetryContextModeHeader);
                }
            }
            if (servedTarget is not null && servedTarget != inFlightTarget)
            {
                // Reassign in-flight accounting to the finally-served target (binding §7):
                // a failover Remote→Local is counted against Local, never against the
                // exhausted candidate that produced the uncommitted failure.
                if (inFlightTarget is { } inFlight)
                {
                    executionTracker.DecrementInFlight(inFlight);
                }
                if (servedTarget != RoutingTarget.Cloud)
                {
                    executionTracker.IncrementInFlight(servedTarget.Value);
                    inFlightTarget = servedTarget;
                }
                else
                {
                    inFlightTarget = null; // Cloud is a relay; never counted in-flight.
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            isClientAborted = true;
            servedTarget ??= target;
        }
        finally
        {
            stopwatch.Stop();
            context.Response.Body = originalBody;

            // In-flight accounting is keyed on the finally-served target (or null if the
            // request was served through the Cloud relay, which is never counted).
            if (inFlightTarget is { } inFlightDecrement)
            {
                executionTracker.DecrementInFlight(inFlightDecrement);
            }

            isClientAborted = isClientAborted || context.RequestAborted.IsCancellationRequested;

            var forwarderError = context.Features.Get<IForwarderErrorFeature>();
            if (forwarderError is not null && IsClientAbort(forwarderError.Error))
            {
                isClientAborted = true;
            }



            var (actualPromptTokens, actualResponseTokens) = TryExtractActualTokens(capturingStream.CapturedTail);
            var isStreamCompleted = IsResponseComplete(context, capturingStream.CapturedTail, actualResponseTokens);

            var isInterrupted = !isStreamCompleted;
            success = context.Response.StatusCode < 400 && !isInterrupted;

            // Mid-stream failure (§7): the response had already started (bytes committed) and
            // then ended in a forwarder error. Do NOT loop — increment the served target's
            // failure streak so the next request avoids a flapping target. Client aborts
            // (isClientAborted) never feed streaks, and a complete HTTP error response from an
            // alive daemon (headers flushed, stream intact) never does either (Q4: streaks
            // track transport reliability only — only a transport-level uncommitted failure or
            // a mid-stream drop counts).
            //
            // The client-abort gate below keys off the authoritative ForwarderError value
            // rather than the request token: a destination-side drop (ForwarderError =
            // ResponseBody/RequestBody Destination) is the daemon resetting the connection
            // mid-stream, and that same reset can propagate to cancel the router's request
            // token, which would otherwise misclassify the daemon drop as a client abort and
            // suppress the streak. Only a genuinely client-initiated cancellation suppresses it.
            var midStreamClientAbort = isClientAborted && (forwarderError is null || IsClientAbort(forwarderError.Error));
            if (servedTarget is { } midStreamTarget
                && !midStreamClientAbort
                && forwarderError is not null
                && forwarderError.Error != ForwarderError.None
                && IsResponseCommitted(context))
            {
                streakTracker.RecordFailure(midStreamTarget);
            }

            var recordedStatusCode = context.Response.StatusCode;
            if (context.Response.StatusCode < 400 && isInterrupted)
            {
                recordedStatusCode = isClientAborted ? 499 : StatusCodes.Status502BadGateway;
            }

            var entry = new ActivityLogEntry(
                DateTimeOffset.UtcNow,
                servedTarget ?? target,
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

    // The middleware-owned uncommitted-retry dispatch loop (§1–§3, §6–§8).
    //
    // Builds the ranked candidate list — the decided primary, then the remaining
    // enabled + routable + streak-ok fleet targets in configuration order, then (when Cloud
    // Overflow is available) the terminal Cloud relay step — buffers the request body as a
    // seekable stream, and forwards to each candidate in order. An uncommitted failure
    // (ForwarderError != None and no response bytes committed) rewinds the body, records the
    // failure streak, applies one best-effort liveness strike, and fails over. A committed
    // failure (response already started) is terminal: no loop. Exhausting every candidate
    // uncommitted produces the terminal 503 all-offline diagnostics (cloud_enabled reports
    // whether the Cloud relay step was available).
    private async Task<(RoutingTarget? Served, string Body)> DispatchLoopAsync(HttpContext context, string body, RoutingTarget target)
    {
        var effectiveTargets = options.Value.GetEffectiveTargets(logger);
        var targetsByName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in effectiveTargets)
        {
            if (!string.IsNullOrWhiteSpace(t.Name))
            {
                targetsByName[t.Name] = t.Url;
            }
        }

        var isCloud = target == RoutingTarget.Cloud;
        var cloudAvailable = isCloud || IsCloudOverflowAvailable(
            OllamaRequestParser.ParseCompletionRequest(body).ModelName ?? string.Empty);

        // Ranked candidate list: (routing target, destination URL).
        var candidates = new List<(RoutingTarget Target, string Url)>();
        if (isCloud)
        {
            // All-offline gate handoff (or pinned Cloud): the fleet nodes are the relays
            // carrying the cloud-rewritten body (preferred relay first, then online fallbacks).
            AddCloudRelayCandidates(effectiveTargets, targetsByName, candidates);
        }
        else
        {
            // Decided primary first (always attempted, even if liveness says offline — the
            // loop itself is the probe). A resolved-but-nameless decision degrades gracefully
            // to the remaining ranked fleet candidates (no candidate ⇒ terminal 503 below).
            var primaryName = target.ToString();
            if (!string.IsNullOrWhiteSpace(primaryName)
                && targetsByName.TryGetValue(primaryName, out var primaryUrl)
                && !string.IsNullOrWhiteSpace(primaryUrl))
            {
                candidates.Add((target, primaryUrl!));
            }
            // Remaining eligible fleet targets in configuration order (streak-skipped and
            // offline/disabled targets are excluded).
            foreach (var t in effectiveTargets)
            {
                if (string.IsNullOrWhiteSpace(t.Name))
                {
                    continue;
                }
                var candidate = (RoutingTarget)t.Name!;
                if (candidate == target)
                {
                    continue;
                }
                if (!IsRoutableNow(candidate)
                    || streakTracker.ShouldSkip(candidate, options.Value.MaxConsecutiveFailures))
                {
                    continue;
                }
                if (targetsByName.TryGetValue(candidate.ToString(), out var url) && !string.IsNullOrWhiteSpace(url))
                {
                    candidates.Add((candidate, url!));
                }
            }
            // Terminal Cloud relay step (Q1: a pinned/decided fleet target that fails
            // uncommitted falls through to Cloud before the terminal 503).
            if (cloudAvailable)
            {
                AddCloudRelayCandidates(effectiveTargets, targetsByName, candidates);
            }
        }

        // Buffer the (possibly dynamically-sized) body as a seekable stream; it is rewound to
        // position 0 before each attempt. The cloud-rewritten bytes replace it only when the
        // terminal Cloud step begins (binding §6).
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var bodyStream = new MemoryStream(bodyBytes);
        context.Request.Body = bodyStream;
        context.Request.ContentLength = bodyStream.Length;
        bodyStream.Position = 0;

        var lastAttempted = target;
        var cloudBodySwapped = false;
        foreach (var (candidate, url) in candidates)
        {
            if (candidate == RoutingTarget.Cloud && !cloudBodySwapped)
            {
                // Cloud terminal step: swap in the cloud-rewritten body (per-model CloudModel
                // precedence over CloudOverflow.DefaultModel) and never restore it afterwards.
                var requestedModel = OllamaRequestParser.ParseCompletionRequest(body).ModelName ?? string.Empty;
                var cloudModel = CloudRelayResolver.ResolveCloudModel(options.Value, requestedModel);
                if (!string.IsNullOrEmpty(cloudModel))
                {
                    body = OllamaRequestParser.ReplaceModelName(body, cloudModel);
                    bodyBytes = Encoding.UTF8.GetBytes(body);
                    bodyStream = new MemoryStream(bodyBytes);
                    context.Request.Body = bodyStream;
                    context.Request.ContentLength = bodyBytes.Length;
                }
                cloudBodySwapped = true;
            }

            if (bodyStream.CanSeek)
            {
                bodyStream.Position = 0; // rewind before every attempt (acceptance criterion).
            }
            lastAttempted = candidate;
            // Publish the candidate being attempted so a successful fallback's committed
            // response (headers flushed inside SendAsync → OnStarting) reports THIS target.
            context.Items[CurrentServedTargetKey] = candidate;
            var error = await forwarder
                .SendAsync(context, url, ProxyClient, ForwarderRequestConfig, PassThroughTransformer, context.RequestAborted)
                .AsTask();

            if (error == ForwarderError.None || IsResponseCommitted(context))
            {
                // Committed (or error-free): this candidate produced the response. A completed
                // success resets the target's failure streak; a complete HTTP error response from
                // an ALIVE daemon (4xx — headers flushed, transport reliable) does NOT, per Q4.
                if (error == ForwarderError.None && context.Response.StatusCode is >= 200 and < 400)
                {
                    streakTracker.RecordSuccess(candidate);
                }
                return (candidate, body);
            }

            // Client abort (client disconnected / request canceled): terminal, never retry,
            // never strike the streak, never feed liveness (§7 / client-abort binding).
            if (IsClientAbort(error) || context.RequestAborted.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Target {Target} aborted by the client ({Error}); not failing over.", candidate.ToString(), error);
                return (candidate, body);
            }

            // Uncommitted failure: rewind (next iteration), strike the failure streak, and
            // apply one best-effort liveness strike through the existing hysteresis (Q2).
            streakTracker.RecordFailure(candidate);
            // Release every session/agent lease pinned to the dying target immediately (the
            // decision service's next turn re-binds those sessions to live targets) rather
            // than waiting for the lease TTL or the liveness prober. Null-guarded, best-effort.
            _sessionAffinity?.InvalidateTarget(candidate);
            ResetUncommittedResponse(context);
            try
            {
                await targetLiveness.MarkHardFailureAsync(candidate);
            }
            catch
            {
                // Best-effort: never block failover on liveness bookkeeping.
            }
            logger.LogWarning(
                "Target {Target} failed uncommitted ({Error}); failing over.", candidate.ToString(), error);
        }

            // Every candidate failed uncommitted (dead at runtime): terminal 503 with diagnostics.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";
            var diagnostics = BuildAllOfflineDiagnostics(cloudEnabled: cloudAvailable);
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                diagnostics,
                OllamaRouterJsonSerializerContext.Default.AllOfflineDiagnostics));
            return (lastAttempted, body);
        }

        // Appends the Cloud relay candidates (§6): the preferred relay (when it names an existing,
        // enabled effective target — tried first even when offline, per the Q3 regression lock), then
        // the remaining enabled + routable fleet nodes in configuration order. All candidates carry
        // the logical Cloud routing target (telemetry and in-flight accounting treat them as the
        // relay); the destination URL is the fleet node's URL.
        private void AddCloudRelayCandidates(
            IReadOnlyList<OllamaTargetOptions> effectiveTargets,
            Dictionary<string, string?> targetsByName,
            List<(RoutingTarget Target, string Url)> candidates)
        {
            var preferred = options.Value.CloudOverflow.PreferredRelayTarget;
            var preferredEntry = effectiveTargets
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Name) &&
                                      string.Equals(t.Name, preferred, StringComparison.OrdinalIgnoreCase));
            var preferredName = preferredEntry is not null
                && targetAvailability.IsEnabled((RoutingTarget)preferredEntry.Name!)
                ? preferredEntry.Name!
                : null;
            if (preferredName is not null && targetsByName.TryGetValue(preferredName, out var preferredUrl)
                && !string.IsNullOrWhiteSpace(preferredUrl))
            {
                candidates.Add((RoutingTarget.Cloud, preferredUrl!));
                logger.LogDebug("Cloud relay resolved to preferred target {Target}.", preferredName);
            }
            else if (preferredEntry is not null)
            {
                logger.LogWarning(
                    "Preferred cloud relay {Target} is disabled; falling back to online fleet nodes.", preferred);
            }

            foreach (var t in effectiveTargets)
            {
                if (string.IsNullOrWhiteSpace(t.Name))
                {
                    continue;
                }
                if (preferredName is not null && string.Equals(t.Name, preferredName, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // already added as the preferred relay.
                }
                var candidate = (RoutingTarget)t.Name!;
                if (!targetAvailability.IsEnabled(candidate) || !IsRoutableNow(candidate))
                {
                    continue;
                }
                if (targetsByName.TryGetValue(candidate.ToString(), out var url) && !string.IsNullOrWhiteSpace(url))
                {
                    candidates.Add((RoutingTarget.Cloud, url!));
                }
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

    /// <summary>
    /// Resolves the decided fleet target's derived capability, computes the dynamic context
    /// sizing decision for this request, and rewrites the request body with the selected
    /// bucket. Returns (injected <c>num_ctx</c>, rewritten body) when a rewrite happened,
    /// otherwise (null, null) (disabled, unresolvable URL/digest, capability discovery
    /// failure, or not hardware capable) - the permissive pass-through never blocks the
    /// request.
    /// </summary>
    private async Task<(int? InjectedNumCtx, string? RewrittenBody)> TryApplyDynamicContextSizingAsync(
        HttpContext context,
        RoutingTarget target,
        string modelName,
        int tokenCount,
        string body)
    {
        var dynamicOptions = options.Value.DynamicContext;
        if (!dynamicOptions.Enabled)
        {
            return (null, null);
        }

        var normalizedName = RoutingDecisionService.NormalizeModelName(modelName);
        OllamaTargetOptions? targetOptions = null;
        foreach (var t in options.Value.GetEffectiveTargets(logger))
        {
            if (string.Equals(t.Name, target.Value, StringComparison.OrdinalIgnoreCase))
            {
                targetOptions = t;
                break;
            }
        }

        var targetUrl = targetOptions?.Url;

        if (targetOptions is null || string.IsNullOrWhiteSpace(targetUrl) || string.Equals(modelName, "unknown", StringComparison.Ordinal))
        {
            return (null, null);
        }

        try
        {
            var digest = await modelCatalogCache.GetCurrentDigestAsync(targetUrl, context.RequestAborted);
            if (string.IsNullOrEmpty(digest))
            {
                return (null, null);
            }

            var metadata = await capabilityEngine.GetMetadataAsync(targetUrl, modelName, digest, context.RequestAborted);

            ModelThresholds? thresholds = null;
            options.Value.Models.TryGetValue(normalizedName, out thresholds);

            var capability = capabilityEngine.DeriveCapability(metadata, target.Value, targetOptions, thresholds?.Architecture);
            if (!DynamicContextSizingEngine.IsHardwareSuitable(capability))
            {
                // Stage-1 hard veto (e.g. LargeDense on < 12 GB): never size/rewrite for a
                // hardware-incapable target (the veto ignores activity state entirely).
                logger.LogInformation(
                    "Dynamic context sizing skipped: {Model} is not hardware-suitable on target {Target}.",
                    modelName, target.Value);
                return (null, null);
            }

            var clientNumCtx = DynamicContextPayloadTransformer.ExtractClientNumCtx(body);
            var margin = Math.Max(0, options.Value.ContextFitMarginTokens);
            var decision = DynamicContextSizingEngine.Decide(
                dynamicOptions,
                capability,
                tokenCount,
                clientNumCtx,
                margin,
                thresholds?.MaxContextTokens);

            // Dense clamp-only pass-through (task #26): the Auto-Shrink Manager is skipped
            // entirely (dense is never bucketed or leased — a dense KV refill costs 300 s+).
            // Clamped → rewrite options.num_ctx down to the pre-configured baseline exactly
            // once; PassThrough → the payload is byte-identical, no mode header.
            if (decision.Mode is ContextSizingMode.Clamped or ContextSizingMode.PassThrough)
            {
                if (decision.Mode == ContextSizingMode.Clamped)
                {
                    var (clampedBody, clampedInjected) = DynamicContextPayloadTransformer.ApplyExplicitNumCtx(body, decision.InjectedNumCtx);
                    if (clampedInjected is null)
                    {
                        // Unparseable payload: permissive pass-through (never break the body).
                        return (null, null);
                    }

                    var clampedBytes = System.Text.Encoding.UTF8.GetBytes(clampedBody);
                    context.Request.Body = new MemoryStream(clampedBytes);
                    context.Request.ContentLength = clampedBytes.Length;
                    context.Request.Body.Position = 0; // Ready for the forwarder (and the recorder) to read.

                    context.Response.Headers[OllamaReverseProxyConfig.TelemetryContextModeHeader] = decision.Mode.ToString();
                    logger.LogInformation(
                        "Dynamic context sizing (dense clamp-only): {Model} on {Target} - client num_ctx {ClientNumCtx} clamped down to the pre-configured context {Injected}.",
                        modelName,
                        target.Value,
                        clientNumCtx?.ToString() ?? "none",
                        clampedInjected.Value);
                    return (clampedInjected, clampedBody);
                }

                return (null, null);
            }

            // MoE two-bucket ladder + Auto-Shrink Manager (Phase 3, item 2): apply the
            // KV-cache-hysteresis lease on top of the stateless engine decision - it may
            // RETAIN an expanded bucket (KV prefix cache preserved) or fire a shrink back
            // to the pre-configured baseline bucket. When the manager is absent (null,
            // pre-lease test pipeline) the engine decision stands.
            int injectedValue = decision.InjectedNumCtx;
            LeaseBucketState? leaseState = null;
            if (_contextShrinkManager is { } shrinkManager)
            {
                var leaseDecision = shrinkManager.Decide(target, normalizedName, decision, dynamicOptions, tokenCount, DateTimeOffset.UtcNow);
                injectedValue = leaseDecision.InjectedNumCtx;
                leaseState = leaseDecision.State;
                if (leaseDecision.State is LeaseBucketState.FreshExpand or LeaseBucketState.ShrunkToBaseline)
                {
                    logger.LogInformation(
                        "Auto-shrink lease {State}: {Model} on {Target} - injected bucket {Injected} (engine wanted {EngineBucket}; trigger: {Trigger}).",
                        leaseDecision.State,
                        modelName,
                        target.Value,
                        injectedValue,
                        decision.InjectedNumCtx,
                        leaseDecision.Trigger?.ToString() ?? "n/a");
                }
            }

            var (rewritten, injected) = DynamicContextPayloadTransformer.Apply(body, injectedValue, dynamicOptions);
            if (injected is null)
            {
                return (null, null);
            }

            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(rewritten);
            context.Request.Body = new MemoryStream(bodyBytes);
            context.Request.ContentLength = bodyBytes.Length;
            context.Request.Body.Position = 0; // Ready for the forwarder (and the recorder) to read.

            // Informational response header: the superset mode vocabulary (task #26) — the
            // lease state when the Auto-Shrink Manager participates (Baseline/FreshExpand/
            // RetainedExpanded/ShrunkToBaseline), otherwise the engine mode (Baseline for
            // the MoE baseline bucket; Expanded when the engine expanded the context).
            var mode = leaseState is { } leaseMode ? leaseMode.ToString() : decision.Mode.ToString();
            context.Response.Headers[OllamaReverseProxyConfig.TelemetryContextModeHeader] = mode;

            logger.LogInformation(
                "Dynamic context sizing: {Model} on {Target} - {Tokens} estimated tokens, client num_ctx {ClientNumCtx}, injected bucket {Injected} (mode {Mode}, baseline {Baseline}, ceiling {Ceiling}, clamped {Clamped}).",
                modelName,
                target.Value,
                tokenCount,
                clientNumCtx?.ToString() ?? "none",
                injected.Value,
                mode,
                decision.BaselineBucket,
                decision.EffectiveCeiling,
                decision.ClientRequestedClamped);

            return (injected, rewritten);
        }
        catch (Exception ex)
        {
            // Permissive pass-through: a sizing miss (network, parse, anything) never blocks
            // inference - the request is forwarded with its original body.
            logger.LogDebug(ex, "Dynamic context sizing failed for {Model} on {Target}; forwarding the request unchanged.", modelName, target.Value);
            return (null, null);
        }
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
