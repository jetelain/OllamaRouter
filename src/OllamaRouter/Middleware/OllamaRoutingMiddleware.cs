using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaRouter.Converters;
using OllamaRouter.Options;
using OllamaRouter.Parsing;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Clients;
using OllamaRouter.Services.ContextSizing;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
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
    IOptionsMonitor<OllamaRouterOptions> options,
    ILogger<OllamaRoutingMiddleware> logger,
    IHttpForwarder forwarder,
    ITargetFailureStreakService streakTracker,
    ITargetExecutionTracker executionTracker = null!,
    IContextAutoShrinkManager? contextShrinkManager = null,
    ISessionAffinityService? sessionAffinity = null,
    ITokenMetricsRecorder? tokenMetricsRecorder = null,
    OllamaChatRequestParser? ollamaRequestParser = null,
    OpenAIChatRequestParser? openAiRequestParser = null,
    ITargetChatClientProvider? targetChatClientProvider = null,
    ISessionUsageStore? sessionUsage = null,
    ILocalClientIdentityResolver? localClientIdentity = null)
    {
    // During the first 10 s after the liveness prober started, an unresolved (Unknown)
    // target is treated as routable rather than offline, so a request arriving before the
    // boot probe completes is never bounced to 503 merely because probing is slow.
    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(10);

    // Per-candidate request transformer for the middleware-owned dispatch loop (task #61)
    // — see the DispatchHeaderTransformer nested class below. Replaces the previous shared
    // HttpTransformer.Default so the loop can (a) strip the inbound Authorization header,
    // (b) inject the target's own provider key for OpenAI-type targets, and (c) de-duplicate
    // a /v1 destination path prefix. For Ollama targets with no key it is behaviorally
    // identical to the old static pass-through except for the Authorization strip (a client
    // key must never reach an unrelated daemon — same spirit as the inbound X-Ollama-Target strip).

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

    // HttpContext.Items key under which the dispatch loop publishes the dispatch PATH of the
    // candidate it is CURRENTLY attempting ("yarp" | "converter", task #61). The pre-dispatch
    // telemetry OnStarting closure reads it at commit time so the flushed headers carry the
    // path of the SERVED candidate, not the decided primary.
    internal const string CurrentServedDispatchModeKey = "OllamaRouter.CurrentServedDispatchMode";

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

    /// <summary>
    /// The best-effort token metrics CSV recorder (null-guarded for unit-test compatibility:
    /// when null, the middleware records no token metrics — existing unit tests that do not
    /// inject it keep the exact pre-recording behavior). Registered as a singleton in
    /// production; a no-op unless the TokenMetricsCsv export is enabled.
    /// </summary>
    private readonly ITokenMetricsRecorder? _tokenMetricsRecorder = tokenMetricsRecorder;

    /// <summary>
    /// The inbound Ollama-protocol request parser for the converter dispatch path (task #61):
    /// an Ollama client reaching a cross-protocol target (e.g. an OpenAI target) is translated
    /// canonical ChatMessage → IChatClient → Ollama NDJSON. Nullable for unit-test compatibility
    /// with the pre-converter pipeline: when null (or the client provider is null) the converter
    /// path degrades with a warning and the request skips the ineligible candidate (YARP-pure
    /// behavior is byte-identical).
    /// </summary>
    private readonly OllamaChatRequestParser? _ollamaRequestParser = ollamaRequestParser;

    /// <summary>
    /// The inbound OpenAI-protocol request parser for the converter dispatch path (task #61):
    /// an OpenAI client reaching an Ollama target (or a cloud relay daemon) is translated
    /// canonical ChatMessage → IChatClient → OpenAI SSE. Nullable (same degradation semantics as
    /// <see cref="_ollamaRequestParser"/>).
    /// </summary>
    private readonly OpenAIChatRequestParser? _openAiRequestParser = openAiRequestParser;

    /// <summary>
    /// Resolves the target's <see cref="IChatClient"/> by name for the converter dispatch path
    /// (task #61). Nullable for unit-test compatibility; when null the converter path degrades
    /// with a warning (every cross-protocol candidate is skipped as unconfigured).
    /// </summary>
    private readonly ITargetChatClientProvider? _targetChatClientProvider = targetChatClientProvider;

    /// <summary>
    /// The best-effort local client process identity resolver (task #106): for UNtagged loopback
    /// completion requests (no X-OllamaRouter-Session-Id AND no X-OllamaRouter-Agent-Id header) it
    /// attributes the request to the owning local process (<c>process:&lt;Name&gt;</c>) so local
    /// applications are visible in the activity monitor and per-session usage aggregates.
    /// NULLABLE FOR UNIT-TEST COMPATIBILITY: when null, untagged requests keep the exact
    /// pre-attribution anonymous behavior (no TCP-table lookup). Registered as a singleton in
    /// production (see <c>AddOllamaRouter</c>); display/attribution only — the sticky KV-cache
    /// lease (above) and the X-OllamaRouter-Session-Id response echo keep their
    /// explicit-headers-only semantics.
    /// </summary>
    private readonly ILocalClientIdentityResolver? _localClientIdentity = localClientIdentity;

    // One-shot guard for the converter-degradation warning (the middleware is a singleton;
    // test hosts create fresh instances so each warns exactly once).
    private int _converterDegradationWarned;

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

    // LIVE config enablement of a target (OllamaTargetOptions.Enabled), read from the current
    // options on every call so a Setup-API (task #19) enable/disable takes effect on the next
    // request without a restart. Used by the completion exact-pin gate (the pin bypasses
    // DecideAsync's own Enabled check). A name that is not an effective target cannot reach
    // this gate (TryParsePinnedTarget only matches effective names) and is treated as enabled.
    private bool IsTargetConfigEnabled(RoutingTarget target) =>
        options.CurrentValue.GetEffectiveTargets(logger)
            .FirstOrDefault(t => string.Equals(t.Name, target.Value, StringComparison.OrdinalIgnoreCase))
            is { } targetOptions
            ? targetOptions.Enabled
            : true;

    // The header value for non-inspected requests: the name of the target named "Local"
    // (case-insensitive) when enabled, otherwise the first other enabled effective target
    // (Remote in the legacy configuration, preserving the previous behavior exactly).
    private string GetDefaultHeaderValue()
    {
        var effectiveTargets = options.CurrentValue.GetEffectiveTargets(logger);
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
        var effectiveTargets = options.CurrentValue.GetEffectiveTargets(logger);
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
            var effectiveTargets = options.CurrentValue.GetEffectiveTargets(logger);
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

    // Resolves the X-OllamaRouter-Target client pin, in strict precedence order:
    // 1. BLANK header ⇒ false (existing fallback applies).
    // 2. EXACT-NAME precedence: the whole trimmed value naming the Cloud pseudo-target or any
    //    configured effective target (OrdinalIgnoreCase) ⇒ exact pin — so a target LITERALLY
    //    NAMED e.g. "remote" still exact-pins, never falls through to the location scope.
    // 3. LOCATION SCOPE: the value splits on ',' and every token (individually trimmed) must be
    //    a non-blank location keyword (local / remote / cloud, OrdinalIgnoreCase; duplicates
    //    collapse into a set) ⇒ a scope pin. ANY unknown or empty token (e.g. "local,,remote"
    //    or "banana") invalidates the whole header ⇒ parse fails (existing dynamic /
    //    pinned-management fallback, no scope).
    private bool TryParsePinnedTarget(string? headerValue, out PinnedTargetPin pin)
    {
        pin = new PinnedTargetPin(null, null);
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        var value = headerValue.Trim();

        // 2. Exact-name precedence (checked BEFORE scope parsing).
        if (string.Equals(value, RoutingTarget.Cloud.Value, StringComparison.OrdinalIgnoreCase))
        {
            pin = new PinnedTargetPin(RoutingTarget.Cloud, null);
            return true;
        }

        var exactMatch = options.CurrentValue.GetEffectiveTargets(logger)
            .FirstOrDefault(t => string.Equals(t.Name, value, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null && !string.IsNullOrWhiteSpace(exactMatch.Name))
        {
            pin = new PinnedTargetPin((RoutingTarget)exactMatch.Name!, null);
            return true;
        }

        // 3. Location-scope allow-list (single keyword or comma-separated list).
        var locations = new HashSet<TargetLocation>();
        foreach (var rawToken in value.Split(','))
        {
            if (!PinnedTargetLocationKeyword.TryMap(rawToken, out var location))
            {
                return false; // unknown/blank token: the whole header is unrecognized.
            }

            locations.Add(location);
        }

        pin = new PinnedTargetPin(null, locations);
        return true;
    }

    // Logs a location scope as a stable comma-joined keyword list (local,remote,cloud order).
    private static string FormatScope(IReadOnlySet<TargetLocation> allowedLocations)
    {
        var names = new[] { "local", "remote", "cloud" };
        return string.Join(",",
            Enumerable.Range(0, 3).Where(i => allowedLocations.Contains((TargetLocation)i)).Select(i => names[i]));
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
        return CloudRelayResolver.IsCloudOverflowAvailable(targetAvailability, options.CurrentValue, modelName);
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

    /// <summary>
    /// Client-abort detection for destination-classified MID-STREAM errors (task #96).
    /// Returns true ONLY for ForwarderError.ResponseBodyDestination where the client's
    /// request-aborted token is the evident initiator of the stream break.
    /// </summary>
    private static bool IsClientAbortedDestinationDrop(IForwarderErrorFeature forwarderError, HttpContext context)
    {
        // Deliberately scoped to ResponseBodyDestination: a request-body destination failure is
        // a genuine destination fault (the request body here is a seekable in-memory stream whose
        // reads cannot fail), and every other ForwarderError value keeps its prior semantics.
        if (forwarderError.Error != ForwarderError.ResponseBodyDestination)
        {
            return false;
        }

        // Client still connected ⇒ the destination dropped first; record the failure.
        if (!context.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        // Walk the exception chain: cancellation evidence (an OperationCanceledException in the
        // chain — the request token or YARP's linked activity token — or a 995/
        // WSA_OPERATION_ABORTED socket code) marks the client as the initiator; any genuine
        // transport-reset evidence wins over the canceled token (D2), preserving the anti-masking
        // invariant that a daemon reset propagating into the request token must still count as a
        // failure. Socket codes are matched via the SocketError enum (D1), so Windows WSA codes
        // and Linux errnos normalize into the same classification.
        var cancellationEvidence = false;
        var resetEvidence = false;
        for (Exception? ex = forwarderError.Exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is OperationCanceledException)
            {
                // Covers TaskCanceledException. The request token is already confirmed canceled
                // above; this is the cancellation that ran on our side of the socket.
                cancellationEvidence = true;
            }
            else if (ex is SocketException se)
            {
                // Peer-side reset (ECONNRESET/ECONNABORTED/network reset/not connected).
                resetEvidence |= se.SocketErrorCode is SocketError.ConnectionReset
                    or SocketError.ConnectionAborted
                    or SocketError.NetworkReset
                    or SocketError.NotConnected;
                // Aborted BY US (995/WSA_OPERATION_ABORTED): our cancellation landed on
                // in-flight I/O — the transport's way of surfacing the cancel.
                cancellationEvidence |= se.SocketErrorCode == SocketError.OperationAborted;
            }
            else if (ex is HttpRequestException)
            {
                resetEvidence = true;
            }
            else if (ex is IOException)
            {
                // An IOException wrapping a SocketException is the transport re-wrapping the
                // socket result — the inner socket code above classifies it (OperationAborted ⇒
                // cancellation evidence, reset family ⇒ reset evidence). A BARE IOException
                // (unexpected EOF, "connection reset by peer") is a genuine destination reset.
                resetEvidence |= ex.InnerException is not SocketException;
            }
        }

        // Null/unknown-shape Exception with a canceled token is conservative (record): YARP always
        // populates the exception for copy failures (D3).
        return cancellationEvidence && !resetEvidence;
    }

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

        if (sessionId!.Length > OllamaReverseProxyConfig.PublicSessionIdMaxLength)
        {
            logger.LogWarning(
                "Ignoring {Header} session ID longer than {MaxLength} characters (routing stateless).",
                sourceHeader, OllamaReverseProxyConfig.PublicSessionIdMaxLength);
            return null;
        }

        return sessionId;
    }

    // Display-only capture of the two public sticky-routing headers as INDEPENDENT raw values
    // (trimmed; empty/whitespace ⇒ null) for the monitoring UI: no 256-char cap, no logging,
    // and unconditional — monitoring must show the ids even where sticky routing itself is not
    // wired (no ISessionAffinityService). Routing/lease semantics live solely in
    // TryExtractSessionId above and are untouched.
    private static void TryExtractSessionAndAgentIds(HttpContext context, out string? sessionId, out string? agentId)
    {
        sessionId = null;
        agentId = null;

        if (context.Request.Headers.TryGetValue(OllamaReverseProxyConfig.PublicSessionIdHeader, out var sessionHeader))
        {
            var raw = sessionHeader.ToString()?.Trim();
            sessionId = string.IsNullOrEmpty(raw) ? null : raw;
        }

        if (context.Request.Headers.TryGetValue(OllamaReverseProxyConfig.PublicAgentIdHeader, out var agentHeader))
        {
            var raw = agentHeader.ToString()?.Trim();
            agentId = string.IsNullOrEmpty(raw) ? null : raw;
        }
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

    // Location-scoped decision (X-OllamaRouter-Target scope pin): the candidate set is
    // restricted to the allowed TargetLocations before Stage 1 (out-of-scope targets emit no
    // artifacts and can never win; the Cloud pseudo-target and the
    // DispatchToLargestCapacityOnOverflow escape hatch are restricted the same way, so a
    // privacy scope fails with NoAvailableTargetException instead of leaking to ollama.com).
    private async Task<RoutingDecisionResult> DecideDetailedWithSessionScopedAsync(
        int tokenCount, string modelName, int? clientNumCtx, string? sessionId,
        IReadOnlySet<TargetLocation>? allowedLocations, HttpContext context)
    {
        var result = await routingDecisionService.DecideDetailedAsync(
            tokenCount, modelName, clientNumCtx, sessionId, allowedLocations, context.RequestAborted);
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
            TryParsePinnedTarget(requestedTargetHeader.ToString(), out var pin))
        {
            if (pin.ExactTarget is { } requestedTarget)
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
            else if (pin.AllowedLocations is { } allowedLocations)
            {
                // Location-scope management routing: the first enabled + routable (online or
                // within the 10 s startup grace) target of an allowed location in declaration
                // order wins; none routable ⇒ the primary target fallback below (with warning).
                // Cloud is a relay with no management concept: a scope containing Cloud simply
                // contributes no management candidate. The response header ALWAYS echoes the
                // concrete target name, never a scope keyword.
                foreach (var t in options.CurrentValue.GetEffectiveTargets(logger))
                {
                    if (!allowedLocations.Contains(TargetLocationClassifier.Classify(t)) ||
                        string.IsNullOrWhiteSpace(t.Name))
                    {
                        continue;
                    }

                    var candidate = (RoutingTarget)t.Name!;
                    if (!targetAvailability.IsEnabled(candidate))
                    {
                        continue;
                    }

                    var state = await targetLiveness.EnsureKnownAsync(candidate, context.RequestAborted);
                    if (IsPinnedTargetRoutable(state))
                    {
                        pinned = candidate;
                        logger.LogInformation("Pinned management request with location scope '{Scope}' routed to in-scope target: {Target}", FormatScope(allowedLocations), pinned);
                        break;
                    }

                    logger.LogWarning("In-scope management target {Target} is disabled or not routable (liveness {State}); trying the next in-scope target.", candidate, state);
                }

                if (pinned is null)
                {
                    logger.LogWarning(
                        "Pinned location scope '{Scope}' has no enabled+routable target; routing the management request to the primary target.",
                        FormatScope(allowedLocations));
                }
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
        foreach (var t in options.CurrentValue.GetEffectiveTargets(logger))
        {
            if (string.Equals(t.Name, pinned.Value, StringComparison.OrdinalIgnoreCase))
            {
                pinnedOptions = t;
                break;
            }
        }

        // Non-Ollama targets with an EMPTY declared model roster are permissive (task #61):
        // the synthetic-catalog short-circuit shared with the decision service (a strict
        // roster is served by the catalog cache itself, so the lookup below still applies).
        if (NonOllamaCatalogPolicy.IsPermissive(pinnedOptions))
        {
            return true;
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
        // Inbound protocol family (task #61 dual-path dispatch): classified ONCE per request;
        // each dispatch-loop candidate is then resolved against it — same-protocol pairs take
        // the YARP fast path, cross-protocol pairs the converter path (IChatClient + encoder).
        var protocol = ClassifyRequestProtocol(endpoint);

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

        // The request's display-only raw header values for the monitor (X-OllamaRouter-Session-Id
        // and X-OllamaRouter-Agent-Id captured INDEPENDENTLY; trimmed; empty ⇒ null). Never
        // participates in routing — the merged sticky-routing id above keeps its own semantics.
        TryExtractSessionAndAgentIds(context, out var rawSessionId, out var rawAgentId);

        // Fallback display identity (task #106): ONLY when no explicit session/agent header is
        // present — explicit headers always take precedence and never trigger the TCP-table lookup.
        // Display/attribution only: the sticky-routing sessionId above and the response echo keep
        // their explicit-headers-only semantics (two windows of one IDE share a usage aggregate
        // but never a sticky KV-cache lease).
        if (rawSessionId is null && rawAgentId is null && _localClientIdentity is not null)
        {
            var processIdentity = _localClientIdentity.TryResolveClientIdentity(
                context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.LocalPort);
            if (processIdentity is not null)
            {
                rawSessionId = processIdentity;
                rawAgentId = processIdentity;
            }
        }

        // The target currently holding an in-flight accounting slot (the decided primary);
        // reassigned to the finally-served target after the dispatch loop so a request that
        // fails over Remote→Local is counted against Local, never against an exhausted candidate.
        RoutingTarget? inFlightTarget = null;

        var modelName = "unknown";
        var tokenCount = 0;
        var rawTokenCount = 0;
        string? reasoningEffort = null;
        // Unassigned-safe default: every routing branch below assigns the real decision (the
        // pin branches and the dynamic branch cover all reachable paths); the default value
        // merely satisfies definite-assignment in the branch topology.
        RoutingTarget target = default;
        RoutingDecisionResult? detailedDecision = null;
        // The request's location scope (X-OllamaRouter-Target scope pin), or null when the
        // request is unscooped (no pin, exact pin, or an invalid/unrecognized header). The
        // decision, the all-offline-gate cloud hand-off and the dispatch-loop failover must
        // all respect it: a privacy scope never leaks to an out-of-scope relay.
        IReadOnlySet<TargetLocation>? allowedLocations = null;

        try
        {
            var requestInfo = OllamaRequestParser.ParseCompletionRequest(body);
            modelName = string.IsNullOrEmpty(requestInfo.ModelName) ? "unknown" : requestInfo.ModelName;
            reasoningEffort = requestInfo.ReasoningEffort;
            rawTokenCount = tokenEstimator.EstimateTokens(requestInfo.ContextParts, modelName)
                + requestInfo.PriorContextTokenCount;
            tokenCount = (int)Math.Ceiling(rawTokenCount * options.CurrentValue.TokenEstimationOverheadFactor);
            // The client's static options.num_ctx (upper-bound preference, never a disqualifier):
            // fed to the routing pipeline's context-fit clamp (plan §2.2) and reused by the
            // dynamic context sizing rewrite below.
            var clientNumCtx = DynamicContextPayloadTransformer.ExtractClientNumCtx(body);

            if (context.Request.Headers.TryGetValue(OllamaReverseProxyConfig.PublicTargetHeader, out var requestedTargetHeader) &&
                TryParsePinnedTarget(requestedTargetHeader.ToString(), out var requestedPin))
            {
                if (requestedPin.ExactTarget is { } requestedTarget)
                {
                    // Two independent enablement gates for a pinned completion:
                    // 1. targetAvailability (the state-file targets.json administrative toggle),
                    // 2. the LIVE config Enabled flag (OllamaTargetOptions.Enabled) — checked
                    //    here (not only in the decision service) because an exact pin bypasses
                    //    DecideAsync; a Setup-API (task #19) disable must take effect on the very
                    //    next pinned request without a restart.
                    if (targetAvailability.IsEnabled(requestedTarget) && IsTargetConfigEnabled(requestedTarget))
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
                else if (requestedPin.AllowedLocations is { } pinLocations)
                {
                    // Location-scope pin: a pure candidate filter (no exact-name liveness or
                    // capability gate — the decision service's own Stage-1 checks qualify the
                    // chosen in-scope target). A {Cloud}-only scope with Cloud Overflow
                    // unavailable mirrors today's pinned-Cloud behavior: warn + unscooped
                    // dynamic routing (NOT a hard 503).
                    if (pinLocations.Count == 1 && pinLocations.Contains(TargetLocation.Cloud) &&
                        !IsCloudOverflowAvailable(modelName))
                    {
                        logger.LogWarning(
                            "Requested location scope 'cloud' cannot be honored: Cloud Overflow is unavailable; falling back to unscooped dynamic routing.");
                        allowedLocations = null;
                    }
                    else
                    {
                        allowedLocations = pinLocations;
                    }
                }

                if (allowedLocations is not null)
                {
                    // Scoped dynamic routing: refresh fleet liveness (a scope pin must not
                    // route onto a merely-stale-online target), then decide with the scope as
                    // a Stage-1 candidate filter. NO explicit SetAffinity here (Q5b): only the
                    // decision service's final-winner lease write applies.
                    await RefreshFleetLivenessAsync(context.RequestAborted);
                    detailedDecision = await DecideDetailedWithSessionScopedAsync(tokenCount, modelName, clientNumCtx, sessionId, allowedLocations, context);
                    target = detailedDecision.Target;
                }
                else if (detailedDecision is null)
                {
                    // Dynamic routing: refresh every enabled target's TTL-aware liveness verdict first
                    // (see RefreshFleetLivenessAsync) so a healthy target whose Online verdict merely
                    // lapsed between re-probe ticks is not disqualified as offline and misrouted.
                    await RefreshFleetLivenessAsync(context.RequestAborted);
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
                // A location scope that excludes Cloud must never hand off to the Cloud relay:
                // when the in-scope fleet cannot serve, the existing 503 diagnostics fire
                // instead (a privacy scope never silently leaks to ollama.com).
                if (IsCloudOverflowAvailable(modelName) &&
                    (allowedLocations is null || allowedLocations.Contains(TargetLocation.Cloud)))
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
            // Non-Ollama targets have no num_ctx semantics and no /api/tags: skip the sizing
            // pass entirely (task #61) — the body goes to the dispatch loop unmodified (no
            // wasted catalog probe either). Null options (unknown target) keeps the legacy
            // behavior byte-identical.
            var sizingTargetOptions = FindTargetOptions(target.Value);
            if (sizingTargetOptions is null || sizingTargetOptions.Type == TargetType.Ollama)
            {
                (injectedNumCtx, sizedBody) = await TryApplyDynamicContextSizingAsync(context, target, modelName, tokenCount, body);
            }
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
        // Dispatch-mode telemetry (task #61): the eager value reflects the DECIDED primary's
        // resolved path; the dispatch loop re-asserts it per candidate and the OnStarting
        // closure below re-emits the SERVED value, so a failover that changes the protocol
        // pair (e.g. YARP OpenAI target dies, converter Ollama target serves) reports the
        // path actually used.
        var decidedDispatchMode = ResolveDispatchMode(protocol, FindTargetOptions(target.Value));
        context.Items[CurrentServedDispatchModeKey] = decidedDispatchMode;
        context.Response.Headers[OllamaReverseProxyConfig.DispatchModeHeader] = decidedDispatchMode;
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
            if (context.Items[CurrentServedDispatchModeKey] is string servedDispatchMode)
            {
                context.Response.Headers[OllamaReverseProxyConfig.DispatchModeHeader] = servedDispatchMode;
            }
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

        var requestId = activityMonitor.StartRequest(target, modelName, tokenCount, reasoningEffort, rawSessionId, rawAgentId);
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
            (servedTarget, body) = await DispatchLoopAsync(context, body, target, protocol, allowedLocations);
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



            var (actualPromptTokens, actualCachedInputTokens, actualResponseTokens) = TryExtractActualTokens(capturingStream.CapturedTail);
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
            //
            // Task #96 addendum: ResponseBodyDestination is ADDITIONALLY treated as client-
            // initiated when the request token is canceled AND the exception chain evidences a
            // cancellation-side break (OperationCanceledException/TaskCanceledException, or
            // SocketException(995/WSA_OPERATION_ABORTED) — a client disconnect / client timeout
            // racing YARP's activity timeout); genuine reset evidence in the chain always wins
            // (record the failure).
            var midStreamClientAbort = isClientAborted
                && (forwarderError is null
                    || IsClientAbort(forwarderError.Error)
                    || IsClientAbortedDestinationDrop(forwarderError, context));
            if (servedTarget is { } midStreamTarget
                && forwarderError is not null
                && forwarderError.Error != ForwarderError.None
                && IsResponseCommitted(context))
            {
                if (midStreamClientAbort)
                {
                    logger.LogWarning(
                        "Target {Target} response stream canceled by client disconnect; suppressing failure streak.",
                        midStreamTarget);
                }
                else
                {
                    logger.LogWarning(forwarderError.Exception,
                        "Target {Target} dropped the response stream mid-stream ({Error}); recording failure streak.",
                        midStreamTarget, forwarderError.Error);
                    streakTracker.RecordFailure(midStreamTarget);
                }
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
                rawTokenCount,
                reasoningEffort,
                rawSessionId,
                rawAgentId,
                actualCachedInputTokens);


            activityMonitor.CompleteRequest(requestId, entry);
            activityStatistics.Add(entry);
            sessionUsage?.Record(entry);
            inferenceRequestRecorder.Record(entry, endpoint, body);

            // Token metrics CSV export (dispatch-path only — pre-dispatch 503 short-circuits
            // never reach this block). Best-effort: Record never throws, and the metrics
            // computation is gated on IsEnabled so the disabled path costs nothing beyond a
            // null/bool check. The payload is the FINAL forwarded body (post dynamic-context
            // sizing rewrite), so the recorded metrics match what was actually sent.
            if (_tokenMetricsRecorder is { IsEnabled: true } metricsRecorder)
            {
                var info = OllamaRequestParser.ParseCompletionRequest(body);
                var aggregate = HeuristicTokenEstimator.Analyze(info.ContextParts);
                metricsRecorder.Record(new TokenMetricsCsvRecord(
                    entry.Timestamp, requestId, entry.Model, entry.Target.ToString(), endpoint,
                    aggregate, info.PriorContextTokenCount, rawTokenCount,
                    actualPromptTokens, actualResponseTokens,
                    entry.ElapsedMilliseconds, entry.StatusCode, success));
            }
        }
    }

    // The middleware-owned uncommitted-retry dispatch loop (§1–§3, §6–§8), extended by the
    // task #61 dual-path engine: every candidate is resolved against the request's
    // RequestProtocol — same-protocol pairs (Ollama client ↔ Ollama target, OpenAI client ↔
    // OpenAI target) take the YARP fast path (zero-copy forward), cross-protocol pairs take
    // the converter path (request parser → target IChatClient → protocol encoder, inside the
    // SAME loop so every telemetry/booking/failover semantic applies uniformly).
    //
    // Builds the ranked candidate list — the decided primary, then the remaining
    // enabled + routable + streak-ok fleet targets in configuration order, then (when Cloud
    // Overflow is available) the terminal Cloud relay step — buffers the request body as a
    // seekable stream, and dispatches to each candidate in order. A candidate that cannot
    // serve the protocol family (OsNative — no client yet; /api/embeddings vs a non-Ollama
    // target) is skipped with NO streak/liveness side effects. An uncommitted failure (either
    // path) rewinds the body, records the failure streak, applies one best-effort liveness
    // strike, and fails over. A committed failure (response already started) is terminal: no
    // loop. Exhausting every candidate uncommitted produces the terminal 503 all-offline
    // diagnostics (cloud_enabled reports whether the Cloud relay step was available).
    private async Task<(RoutingTarget? Served, string Body)> DispatchLoopAsync(HttpContext context, string body, RoutingTarget target, RequestProtocol protocol, IReadOnlySet<TargetLocation>? allowedLocations = null)
    {
        var effectiveTargets = options.CurrentValue.GetEffectiveTargets(logger);
        var targetsByOptions = new Dictionary<string, OllamaTargetOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in effectiveTargets)
        {
            if (!string.IsNullOrWhiteSpace(t.Name))
            {
                targetsByOptions[t.Name] = t;
            }
        }

        var isCloud = target == RoutingTarget.Cloud;
        var cloudAvailable = (isCloud || IsCloudOverflowAvailable(
            OllamaRequestParser.ParseCompletionRequest(body).ModelName ?? string.Empty))
            // A location scope that excludes Cloud never takes the terminal Cloud relay step:
            // exhausting the in-scope candidates produces the existing terminal 503 instead.
            && (allowedLocations is null || allowedLocations.Contains(TargetLocation.Cloud));

        // ---- Failure-streak circuit breaker (task #94): resolved ONCE per dispatch loop. ----
        // A tripped (streak above threshold) candidate is a failover candidate while its
        // circuit is HalfOpen (cooldown elapsed); the per-attempt trial reservation below then
        // bounds the in-flight trial requests to one. A non-positive window normalizes to 0
        // (Normalize resets negatives to 60) and disables the Open phase.
        var maxFailures = options.CurrentValue.MaxConsecutiveFailures;
        var streakCooldown = TimeSpan.FromSeconds(Math.Max(0, options.CurrentValue.FailureStreakCooldownSeconds));

        // Ranked candidate list: (routing target, live target options — carrying Type,
        // ApiKey and Url; the Cloud relay entries carry the relay NODE's options).
        var candidates = new List<(RoutingTarget Target, OllamaTargetOptions Options)>();
        if (isCloud)
        {
            // All-offline gate handoff (or pinned Cloud): the fleet nodes are the relays
            // carrying the cloud-rewritten body (preferred relay first, then online fallbacks).
            AddCloudRelayCandidates(effectiveTargets, candidates);
        }
        else
        {
            // Decided primary first (always attempted, even if liveness says offline — the
            // loop itself is the probe). A resolved-but-nameless decision degrades gracefully
            // to the remaining ranked fleet candidates (no candidate ⇒ terminal 503 below).
            var primaryName = target.ToString();
            if (!string.IsNullOrWhiteSpace(primaryName) &&
                targetsByOptions.TryGetValue(primaryName, out var primaryOptions))
            {
                candidates.Add((target, primaryOptions));
            }
            // Remaining eligible fleet targets in configuration order (streak-skipped and
            // offline/disabled targets are excluded).
            foreach (var t in effectiveTargets)
            {
                if (string.IsNullOrWhiteSpace(t.Name))
                {
                    continue;
                }
                if (allowedLocations is not null &&
                    !allowedLocations.Contains(TargetLocationClassifier.Classify(t)))
                {
                    continue; // out of the request's location scope: never a failover candidate.
                }
                var candidate = (RoutingTarget)t.Name!;
                if (candidate == target)
                {
                    continue;
                }
                if (!IsRoutableNow(candidate)
                    || streakTracker.ShouldSkip(candidate, maxFailures, streakCooldown))
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(t.Url))
                {
                    candidates.Add((candidate, t));
                }
            }
            // Terminal Cloud relay step (Q1: a pinned/decided fleet target that fails
            // uncommitted falls through to Cloud before the terminal 503).
            if (cloudAvailable)
            {
                AddCloudRelayCandidates(effectiveTargets, candidates);
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
        foreach (var (candidate, targetOptions) in candidates)
        {
            if (candidate == RoutingTarget.Cloud && !cloudBodySwapped)
            {
                // Cloud terminal step: swap in the cloud-rewritten body (per-model CloudModel
                // precedence over CloudOverflow.DefaultModel) and never restore it afterwards.
                var requestedModel = OllamaRequestParser.ParseCompletionRequest(body).ModelName ?? string.Empty;
                var cloudModel = CloudRelayResolver.ResolveCloudModel(options.CurrentValue, requestedModel);
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

            // Protocol-eligibility gate (task #61): a candidate that CANNOT serve this
            // request's protocol family is skipped WITHOUT streak or liveness side effects —
            // the target is healthy, it simply cannot take this request (OsNative has no
            // client implementation until tasks #63/#64; no embeddings converter exists, so
            // /api/embeddings is Ollama-only). Failover continues to the next candidate.
            if (!IsDispatchEligible(protocol, targetOptions.Type))
            {
                logger.LogWarning(
                    "Candidate {Target} (type {Type}) is ineligible for {Endpoint}; skipping (no streak, no liveness effect).",
                    candidate.ToString(), targetOptions.Type, context.Request.Path.Value);
                continue;
            }

            // ---- Half-open trial reservation (task #94): the single in-flight trial slot is
            // owned by the DISPATCH attempt (not the decision — a decision may name a
            // half-open target the loop never reaches). A tripped candidate that passed the
            // cooldown-aware gate is HalfOpen (or flipped there between list build and
            // attempt): exactly one in-flight trial may reach it. A busy slot skips this
            // candidate with NO streak/liveness side effects (mirrors the protocol-ineligible
            // skip). The lease is released after the outcome on every path below. ----
            var trialLeased = false;
            if (maxFailures > 0 && streakTracker.GetStreak(candidate) > maxFailures)
            {
                if (!streakTracker.TryReserveTrial(candidate, maxFailures, streakCooldown))
                {
                    logger.LogInformation(
                        "Target {Target} half-open trial already in flight; skipping.",
                        candidate.ToString());
                    continue;
                }
                trialLeased = true;
            }

            lastAttempted = candidate;
            // Publish the candidate AND the dispatch path being attempted so a successful
            // fallback's committed response (headers flushed mid-attempt → OnStarting) reports
            // THIS target and THIS path (yarp | converter).
            context.Items[CurrentServedTargetKey] = candidate;
            var useConverter = ShouldUseConverterPath(protocol, targetOptions.Type) && ConverterAvailable(protocol);
            if (!useConverter && ShouldUseConverterPath(protocol, targetOptions.Type) &&
                System.Threading.Interlocked.Exchange(ref _converterDegradationWarned, 1) == 0)
            {
                // The dispatch matrix wants the converter path but its dependencies are not wired
                // (test hosts / degraded configuration): one warning, then the byte-identical
                // YARP fast path for every such request (spec §6 constructor note).
                logger.LogWarning(
                    "The converter dispatch path is not configured; cross-protocol requests ({Protocol} to a {Type} target) fall back to the YARP fast path.",
                    protocol, targetOptions.Type);
            }
            context.Items[CurrentServedDispatchModeKey] = useConverter ? "converter" : "yarp";
            if (!context.Response.HasStarted)
            {
                context.Response.Headers[OllamaReverseProxyConfig.DispatchModeHeader] = (string)context.Items[CurrentServedDispatchModeKey]!;
            }

            var outcome = useConverter
                ? await TryConverterDispatchAsync(context, bodyBytes, protocol, targetOptions, context.RequestAborted)
                : await TryYarpDispatchAsync(context, targetOptions);

            if (outcome == ConverterOutcome.FallbackToYarp)
            {
                // Defensive degradation (dependency vanished mid-flight, client not resolvable,
                // or body unparseable) BEFORE any byte was committed: retry this same candidate
                // through the YARP fast path — and re-assert the served-path telemetry so the
                // flushed Dispatch-Mode header reports the path ACTUALLY taken (yarp).
                logger.LogWarning(
                    "Converter dispatch to {Target} ({Protocol}) could not be applied; degrading to the YARP fast path for this candidate.",
                    targetOptions.Name, protocol);
                context.Items[CurrentServedDispatchModeKey] = "yarp";
                if (!context.Response.HasStarted)
                {
                    context.Response.Headers[OllamaReverseProxyConfig.DispatchModeHeader] = "yarp";
                }
                outcome = await TryYarpDispatchAsync(context, targetOptions);
            }

            // The dispatch outcome for this attempt is determined (terminal return or the
            // uncommitted fail-over below): release the single-trial slot so the circuit can
            // be re-reserved. Idempotent — the RecordFailure/RecordSuccess safety nets
            // double-release harmlessly (task #94).
            if (trialLeased)
            {
                streakTracker.ReleaseTrial(candidate);
                trialLeased = false;
            }

            switch (outcome)
            {
                case ConverterOutcome.Served:
                    // Committed (or error-free): this candidate produced the response. A
                    // completed success resets the target's failure streak; a complete HTTP
                    // error response from an ALIVE daemon (4xx — headers flushed, transport
                    // reliable) does NOT, per Q4 (converter parity: upstream HTTP errors never
                    // reach this outcome — they surface as UpstreamHttpError below).
                    if (context.Response.StatusCode is >= 200 and < 400)
                    {
                        streakTracker.RecordSuccess(candidate);
                    }
                    return (candidate, body);

                case ConverterOutcome.ClientAborted:
                    // Client abort (client disconnected / request canceled): terminal, never
                    // retry, never strike the streak, never feed liveness (§7).
                    logger.LogWarning(
                        "Target {Target} aborted by the client; not failing over.", candidate.ToString());
                    return (candidate, body);

                case ConverterOutcome.UpstreamHttpError:
                case ConverterOutcome.MidStreamFailure:
                    // Terminal committed-equivalent failures: an upstream HTTP error mirrors
                    // the YARP "complete HTTP error from an alive daemon" rule (never a
                    // streak); a mid-stream drop carries IForwarderErrorFeature so the
                    // post-dispatch mid-stream streak classification fires uniformly.
                    return (candidate, body);
            }

            // Uncommitted failure (TransportFailure) — identical semantics for both dispatch
            // paths: rewind (next iteration), strike the failure streak, and apply one
            // best-effort liveness strike through the existing hysteresis (Q2).
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
                "Target {Target} failed uncommitted; failing over.", candidate.ToString());
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
        // relay); the options carry the relay NODE's identity (Url/Type/ApiKey), so a
        // cross-protocol request reaching the terminal Cloud step takes the converter branch
        // against the relay daemon (e.g. OpenAI client → OllamaChatClient → SSE) with no
        // special-casing (task #61).
        private void AddCloudRelayCandidates(
            IReadOnlyList<OllamaTargetOptions> effectiveTargets,
            List<(RoutingTarget Target, OllamaTargetOptions Options)> candidates)
        {
            var preferred = options.CurrentValue.CloudOverflow.PreferredRelayTarget;
            var preferredEntry = effectiveTargets
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Name) &&
                                      string.Equals(t.Name, preferred, StringComparison.OrdinalIgnoreCase));
            var preferredName = preferredEntry is not null
                && targetAvailability.IsEnabled((RoutingTarget)preferredEntry.Name!)
                ? preferredEntry.Name!
                : null;
            if (preferredName is not null && !string.IsNullOrWhiteSpace(preferredEntry!.Url))
            {
                candidates.Add((RoutingTarget.Cloud, preferredEntry));
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
                if (!string.IsNullOrWhiteSpace(t.Url))
                {
                    candidates.Add((RoutingTarget.Cloud, t));
                }
            }
        }

    // ─── Task #61: dual-path (YARP / converter) dispatch engine ───────────────────────────────

    /// <summary>
    /// The inbound wire protocol family of a completion-class request. Classified once per
    /// request by the middleware and resolved per candidate against the target's
    /// <see cref="TargetType"/> into a dispatch path (YARP fast path vs converter path).
    /// </summary>
    internal enum RequestProtocol
    {
        /// <summary>POST /api/chat or POST /api/generate (Ollama NDJSON wire).</summary>
        OllamaChat,
        /// <summary>POST /v1/chat/completions (OpenAI SSE wire).</summary>
        OpenAIChat,
        /// <summary>POST /v1/completions (legacy completions; same-protocol family only, NEVER translated).</summary>
        OpenAICompletions,
        /// <summary>POST /api/embeddings (Ollama-only in v1; no embeddings converter exists).</summary>
        OllamaEmbeddings
    }

    /// <summary>
    /// Maps the inbound path to its protocol family. Unknown completion-class paths degrade to
    /// the Ollama family (every Ollama target stays eligible — the legacy behavior).
    /// </summary>
    internal static RequestProtocol ClassifyRequestProtocol(string? path) => path switch
    {
        "/api/chat" => RequestProtocol.OllamaChat,
        "/api/generate" => RequestProtocol.OllamaChat,
        "/v1/chat/completions" => RequestProtocol.OpenAIChat,
        "/v1/completions" => RequestProtocol.OpenAICompletions,
        "/api/embeddings" => RequestProtocol.OllamaEmbeddings,
        _ => RequestProtocol.OllamaChat
    };

    /// <summary>
    /// Whether the candidate needs protocol translation for the request's protocol family
    /// (the converter path): an Ollama client reaching an OpenAI target, or an OpenAI client
    /// reaching an Ollama target (incl. cloud relay daemons). Same-protocol pairs and the
    /// never-translated endpoints (/v1/completions, /api/embeddings) take the YARP fast path.
    /// </summary>
    internal static bool ShouldUseConverterPath(RequestProtocol protocol, TargetType targetType) => protocol switch
    {
        RequestProtocol.OllamaChat => targetType == TargetType.OpenAI,
        RequestProtocol.OpenAIChat => targetType == TargetType.Ollama,
        _ => false
    };

    /// <summary>
    /// Whether the candidate can serve the request's protocol family AT ALL: OsNative targets
    /// are never dispatch candidates (no client implementation until tasks #63/#64), and
    /// /api/embeddings is Ollama-only (no embeddings converter in v1). Ineligible candidates
    /// are skipped without streak or liveness side effects.
    /// </summary>
    internal static bool IsDispatchEligible(RequestProtocol protocol, TargetType targetType) => (protocol, targetType) switch
    {
        (_, TargetType.OsNative) => false,
        (RequestProtocol.OllamaEmbeddings, not TargetType.Ollama) => false,
        _ => true
    };

    /// <summary>
    /// The dispatch-mode telemetry value ("yarp" | "converter") for a target (unknown targets
    /// default to the Ollama type — the config binder's enum default).
    /// </summary>
    internal static string ResolveDispatchMode(RequestProtocol protocol, OllamaTargetOptions? options)
        => ShouldUseConverterPath(protocol, options?.Type ?? TargetType.Ollama) ? "converter" : "yarp";

    /// <summary>
    /// Whether the converter dispatch path can run at all for the request's protocol family
    /// (task #61 §6): the client provider AND the matching inbound parser must be wired. When
    /// false, cross-protocol candidates degrade to the YARP fast path (byte-identical
    /// pure-Ollama behavior on converter-less hosts — existing unit-test harnesses).
    /// </summary>
    private bool ConverterAvailable(RequestProtocol protocol)
        => _targetChatClientProvider is not null &&
           (IChatRequestConverter?)(protocol == RequestProtocol.OllamaChat ? _ollamaRequestParser : _openAiRequestParser) is not null;

    /// <summary>
    /// Looks up the live options of a named target (case-insensitive); null for unknown names
    /// (incl. the Cloud pseudo-target). Used for the dynamic-sizing early return (non-Ollama
    /// targets have no num_ctx semantics) and the dispatch-mode telemetry.
    /// </summary>
    private OllamaTargetOptions? FindTargetOptions(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var t in options.CurrentValue.GetEffectiveTargets(logger))
        {
            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }
        }

        return null;
    }

    // Outcome of a single dispatch attempt (YARP or converter); the loop maps each outcome to
    // the SAME streak/liveness/failover semantics for both paths (task #61 failover parity).
    private enum ConverterOutcome
    {
        /// <summary>Response complete and accepted — return the candidate as served.</summary>
        Served,
        /// <summary>Complete HTTP error from an alive upstream (committed-equivalent) — terminal, NO streak.</summary>
        UpstreamHttpError,
        /// <summary>Cancelled by the client — terminal, never retry, never streak.</summary>
        ClientAborted,
        /// <summary>Bytes were committed then the stream broke — terminal + IForwarderErrorFeature.</summary>
        MidStreamFailure,
        /// <summary>Failed before the first byte — rewind, streak, liveness strike, fail over.</summary>
        TransportFailure,
        /// <summary>
        /// The converter path could not take this candidate (dependency not wired, client not
        /// resolvable, body unparseable) — BEFORE any byte was written; the loop degrades this
        /// candidate to the YARP fast path (spec: converter-less hosts keep the byte-identical
        /// pure-Ollama behavior).
        /// </summary>
        FallbackToYarp
    }

    // The YARP fast-path attempt (same-protocol pair): zero-copy forward through the shared
    // proxy client. The per-candidate DispatchHeaderTransformer strips the inbound
    // Authorization header, injects the target's provider key (OpenAI targets) and
    // de-duplicates a /v1 destination path prefix.
    private async Task<ConverterOutcome> TryYarpDispatchAsync(HttpContext context, OllamaTargetOptions targetOptions)
    {
        var transformer = new DispatchHeaderTransformer(targetOptions.Type, targetOptions.ApiKey);
        var error = await forwarder
            .SendAsync(context, targetOptions.Url!, ProxyClient, ForwarderRequestConfig, transformer, context.RequestAborted)
            .AsTask();

        if (error == ForwarderError.None)
        {
            return ConverterOutcome.Served;
        }

        if (IsResponseCommitted(context))
        {
            // Mid-stream committed failure: YARP already set IForwarderErrorFeature, so the
            // post-dispatch mid-stream streak classification fires exactly as before.
            return ConverterOutcome.MidStreamFailure;
        }

        if (IsClientAbort(error) || context.RequestAborted.IsCancellationRequested)
        {
            return ConverterOutcome.ClientAborted;
        }

        return ConverterOutcome.TransportFailure;
    }

    // The converter dispatch attempt (cross-protocol pair), executed inside the dispatch loop
    // so every existing failover/telemetry/booking semantic applies uniformly (task #61):
    //   1. rewind the buffered body, select the request parser by INBOUND protocol, parse to
    //      canonical (IList<ChatMessage>, ChatOptions, ModelName);
    //   2. resolve the target's IChatClient (null ⇒ YARP degradation);
    //   3. set the response Content-Type BEFORE the first byte; the per-attempt encoder writes
    //      into a StreamPipeWriter over the response capturing stream installed loop-upstream
    //      (token-tail extraction, IsResponseComplete and TryExtractActualTokens work on both
    //      encoders' terminal frames: NDJSON done:true + counts / SSE usage + [DONE]);
    //   4. streaming: GetStreamingResponseAsync → WriteStreamChunkAsync per update (usage is
    //      carried as UsageContent inside update.Contents); non-streaming: GetResponseAsync →
    //      WriteNonStreamingResponseAsync; the terminal frame carries model/duration/finish;
    //   5. failure semantics (parity with the YARP branch): upstream HTTP error ⇒ terminal
    //      committed-equivalent (status + protocol-shaped error body, NO streak); transport
    //      failure before the first byte ⇒ uncommitted (rewind/strike/failover); failure
    //      after the first byte ⇒ terminal + IForwarderErrorFeature.Exception; client abort
    //      ⇒ terminal, never retry, never streak.
    private async Task<ConverterOutcome> TryConverterDispatchAsync(HttpContext context, byte[] bodyBytes, RequestProtocol protocol, OllamaTargetOptions targetOptions, CancellationToken ct)
    {
        IChatRequestConverter parser = protocol == RequestProtocol.OllamaChat ? _ollamaRequestParser! : _openAiRequestParser!;
        if (parser is null || _targetChatClientProvider is null)
        {
            logger.LogWarning(
                "The converter dispatch path is not configured (missing parser/client provider); target {Target} degrades to the YARP fast path for {Endpoint}.",
                targetOptions.Name, context.Request.Path.Value);
            return ConverterOutcome.FallbackToYarp;
        }

        if (!_targetChatClientProvider.TryResolve(targetOptions.Name!, out var client) || client is null)
        {
            logger.LogWarning(
                "No chat client available for target {Target} (type {Type}); degrading to the YARP fast path.",
                targetOptions.Name, targetOptions.Type);
            return ConverterOutcome.FallbackToYarp;
        }

        var isStreaming = ExtractStreamFlag(bodyBytes, protocol);
        IChatResponseEncoder encoder = protocol == RequestProtocol.OllamaChat
            ? new OllamaNdjsonResponseEncoder()
            : new OpenAiSseResponseEncoder();

        IList<ChatMessage> messages;
        ChatOptions requestOptions;
        string modelName;
        try
        {
            (messages, requestOptions, modelName) = await parser.ParseChatRequestAsync(new MemoryStream(bodyBytes), ct);
        }
        catch (OperationCanceledException)
        {
            throw; // client abort: the outer handler classifies it.
        }
        catch (Exception ex)
        {
            // A structurally valid body should have parsed (the decision pipeline already saw it);
            // a parse failure here degrades to the YARP fast path (uncommitted — nothing was
            // written), which answers the request the way a native same-protocol forward would.
            logger.LogWarning(ex, "Failed to parse the {Protocol} request body for converter dispatch to {Target}; degrading to the YARP fast path.", protocol, targetOptions.Name);
            return ConverterOutcome.FallbackToYarp;
        }

        // The target's IChatClient resolves the model from ChatOptions.ModelId (or its own
        // default — none here): the INBOUND request's model name is the fleet-consistent one
        // (the routing catalog already qualified it against this target).
        if (string.IsNullOrWhiteSpace(modelName))
        {
            modelName = string.Empty;
        }
        requestOptions.ModelId = modelName;

        // Content type BEFORE the first byte (the terminal error path may still set the status
        // while uncommitted — headers are not yet flushed). A non-streaming OpenAI-family
        // response is a plain JSON object, not an SSE stream: reporting it as
        // text/event-stream would make the post-dispatch completeness check wait for a
        // [DONE] marker that never arrives and misreport a complete response as a 502.
        context.Response.ContentType = protocol == RequestProtocol.OpenAIChat && !isStreaming
            ? "application/json"
            : encoder.ContentType;
        // Clear a 502 left by a PREVIOUS candidate's failed YARP attempt (ResetUncommittedResponse
        // restores 200 only on its own path); the capturing stream is already installed loop-upstream.
        ResetUncommittedResponse(context);

        var capturing = context.Items[CapturingStreamKey] as ResponseCapturingStream
            ?? new ResponseCapturingStream(context.Response.Body);
        var watch = Stopwatch.StartNew();
        try
        {
            if (isStreaming)
            {
                // .NET 10 replaced StreamPipeWriter with the PipeWriter.Create factory; LeaveOpen keeps
                // the middleware's capturing stream alive (it owns/restores the response body).
                var writer = PipeWriter.Create(capturing, new StreamPipeWriterOptions(leaveOpen: true));
                UsageDetails? usage = null;
                ChatFinishReason? finishReason = null;
                await foreach (var update in client.GetStreamingResponseAsync(messages, requestOptions, ct))
                {
                    if (update.FinishReason is { } reason)
                    {
                        finishReason = reason;
                    }
                    foreach (var content in update.Contents)
                    {
                        if (content is UsageContent usageContent)
                        {
                            usage = usageContent.Details; // carried inside the update — not re-fetched.
                        }
                    }
                    await encoder.WriteStreamChunkAsync(writer, update, modelName, ct);
                }
                await encoder.WriteStreamEndAsync(writer, modelName, usage, watch.Elapsed, finishReason, ct);
                await writer.CompleteAsync();
            }
            else
            {
                var response = await client.GetResponseAsync(messages, requestOptions, ct);
                await encoder.WriteNonStreamingResponseAsync(capturing, response, modelName, watch.Elapsed, ct);
                await capturing.FlushAsync(ct);
            }

            return ConverterOutcome.Served;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client abort: terminal, never retry, never streak (existing isClientAborted flow
            // in the post-dispatch block reads IForwarderErrorFeature).
            logger.LogWarning(
                "Converter dispatch to {Target} was cancelled by the client before completion.", targetOptions.Name);
            context.Features.Set<IForwarderErrorFeature>(new ConverterForwarderErrorFeature(ForwarderError.RequestCanceled, null));
            return ConverterOutcome.ClientAborted;
        }
        catch (Exception ex) when (ex is OpenAiHttpException or OllamaHttpException)
        {
            var upstreamStatus = (ex as OpenAiHttpException)?.StatusCode ?? (ex as OllamaHttpException)?.StatusCode;
            if (upstreamStatus is not null && !IsResponseCommitted(context))
            {
                // Upstream HTTP error (404 model-not-found, 401, 429, …): the provider ANSWERED —
                // committed-equivalent, mirroring the YARP "complete HTTP error from an alive
                // daemon" rule. Terminal with the upstream status + a protocol-shaped error body;
                // NO streak, NO liveness strike (transport was reliable).
                context.Response.StatusCode = (int)upstreamStatus.Value;
                await WriteProtocolShapedErrorBodyAsync(context, protocol, ex.Message);
                return ConverterOutcome.UpstreamHttpError;
            }

            // Same exception type but no status (transport-level, e.g. connection refused for
            // an OpenAiHttpException) or bytes already committed (mid-stream break).
            if (IsResponseCommitted(context))
            {
                context.Features.Set<IForwarderErrorFeature>(new ConverterForwarderErrorFeature(ForwarderError.ResponseBodyDestination, ex));
                logger.LogWarning(ex, "Converter stream to {Target} broke mid-stream ({Message}).", targetOptions.Name, ex.Message);
                return ConverterOutcome.MidStreamFailure;
            }

            logger.LogWarning(ex, "Converter transport failure to {Target} before the first byte; failing over.", targetOptions.Name);
            return ConverterOutcome.TransportFailure;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            if (IsResponseCommitted(context))
            {
                context.Features.Set<IForwarderErrorFeature>(new ConverterForwarderErrorFeature(ForwarderError.ResponseBodyDestination, ex));
                logger.LogWarning(ex, "Converter stream to {Target} broke mid-stream ({Message}).", targetOptions.Name, ex.Message);
                return ConverterOutcome.MidStreamFailure;
            }

            logger.LogWarning(ex, "Converter transport failure to {Target} before the first byte; failing over.", targetOptions.Name);
            return ConverterOutcome.TransportFailure;
        }
        catch (Exception ex)
        {
            // Safety net for unexpected encoder/client faults: classify by commit state so the
            // loop's uniform semantics always apply (never an unhandled 500 mid-failover).
            if (IsResponseCommitted(context))
            {
                context.Features.Set<IForwarderErrorFeature>(new ConverterForwarderErrorFeature(ForwarderError.ResponseBodyDestination, ex));
                logger.LogError(ex, "Unexpected converter failure to {Target} after the first byte; marking mid-stream.", targetOptions.Name);
                return ConverterOutcome.MidStreamFailure;
            }

            logger.LogError(ex, "Unexpected converter failure to {Target} before the first byte; failing over.", targetOptions.Name);
            return ConverterOutcome.TransportFailure;
        }
    }

    /// <summary>
    /// The request's <c>stream</c> flag from the raw buffered body (top-level property only).
    /// Default: true for the Ollama endpoints (/api/chat, /api/generate), false for the OpenAI
    /// endpoint (/v1/chat/completions) — matching the protocol defaults (the task #59 parser
    /// contract itself is NOT extended for this flag).
    /// </summary>
    internal static bool ExtractStreamFlag(byte[] body, RequestProtocol protocol)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("stream", out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }
        catch (JsonException)
        {
            // Malformed body — the protocol default below applies (the parser will report it).
        }

        return protocol switch
        {
            RequestProtocol.OpenAIChat => false,
            _ => true
        };
    }

    // Writes the protocol-shaped error body for a converter-path upstream HTTP error
    // (task #61): Ollama {"error": "..."} for the Ollama-family endpoints, the OpenAI
    // {"error": {"message", "type", "code"}} envelope for the OpenAI-family endpoints.
    private static async Task WriteProtocolShapedErrorBodyAsync(HttpContext context, RequestProtocol protocol, string message)
    {
        context.Response.ContentType = protocol == RequestProtocol.OllamaChat
            ? "application/x-ndjson"
            : "application/json";
        var payload = protocol == RequestProtocol.OllamaChat
            ? JsonSerializer.Serialize(new OllamaErrorResponse(message), OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse)
            : JsonSerializer.Serialize(
                new OpenAiErrorResponseDto
                {
                    Error = new OpenAiErrorDetailDto { Message = message, Type = "upstream_error" }
                },
                OllamaRouterJsonSerializerContext.Default.OpenAiErrorResponseDto);
        await context.Response.WriteAsync(payload);
    }

    // YARP 2.3.0's ForwarderErrorFeature class is INTERNAL (only IForwarderErrorFeature is
    // public), so the converter dispatch path publishes its classification through this tiny
    // public implementation — the post-dispatch block reads IForwarderErrorFeature.Error
    // (mid-stream streaks fire for any Error != None that is not a client-abort value) and
    // consults its Exception for the task #96 ResponseBodyDestination client-drop
    // classification (IsClientAbortedDestinationDrop).
    private sealed class ConverterForwarderErrorFeature(ForwarderError error, Exception? exception) : IForwarderErrorFeature
    {
        public ForwarderError Error { get; } = error;
        public Exception? Exception { get; } = exception;
    }

    // Per-candidate YARP request transformer (task #61). YARP 2.3.0's HttpTransformer base
    // implementation copies every inbound request header into the outgoing message (skipping
    // protocol headers); this override then (a) REMOVES the Authorization header (a client key
    // must never reach an unrelated daemon — same spirit as the inbound X-Ollama-Target strip),
    // (b) re-adds Authorization: Bearer {ApiKey} for OpenAI-type targets (the same-protocol
    // fast path is the only auth-injecting path in the router), and (c) rewrites the request
    // URI so a /v1-prefixed base address does not duplicate the segment (base
    // http://h:8000/v1 + request /v1/chat/completions ⇒ forwarded /v1/chat/completions once).
    // YARP composes the RequestUri AFTER this callback when it is still null, so setting it
    // here is honored; leaving it null keeps the default composition.
    private sealed class DispatchHeaderTransformer(TargetType targetType, string? apiKey) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            proxyRequest.Headers.Authorization = null;
            if (targetType == TargetType.OpenAI && !string.IsNullOrWhiteSpace(apiKey))
            {
                proxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            RewritePathPrefix(httpContext, proxyRequest, destinationPrefix);
        }

        private static void RewritePathPrefix(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix)
        {
            var prefixPath = new Uri(destinationPrefix, UriKind.Absolute).AbsolutePath.TrimEnd('/');
            if (prefixPath.Length == 0)
            {
                return; // scheme://host[:port] only — YARP's default composition is already correct.
            }

            var inboundPath = httpContext.Request.Path.Value ?? "/";
            if (inboundPath == prefixPath || inboundPath.StartsWith(prefixPath + "/", StringComparison.Ordinal))
            {
                // The inbound path already carries the destination's path prefix: strip it so the
                // composed URI does not duplicate the segment (…/v1/v1/chat/completions).
                var stripped = inboundPath[prefixPath.Length..]; // "" or "/chat/completions"
                proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(
                    destinationPrefix,
                    stripped.Length == 0 ? "/" : stripped,
                    httpContext.Request.QueryString);
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

    private static (int? PromptTokens, int? CachedInputTokens, int? ResponseTokens) TryExtractActualTokens(string capturedTail)
    {
        if (string.IsNullOrWhiteSpace(capturedTail))
        {
            return (null, null, null);
        }

        // The last NDJSON line of /api/chat and /api/generate responses carries "prompt_eval_count"
        // (total prompt size, cached tokens INCLUSIVE per Ollama docs), "prompt_eval_cached_count"
        // (absent ⇒ 0 cached) and "eval_count". OpenAI-compatible (SSE) responses instead carry a
        // "usage" object with "prompt_tokens" (cached INCLUSIVE),
        // "prompt_tokens_details.cached_tokens" (absent ⇒ 0 cached) and "completion_tokens" in the
        // final "data:" block before "data: [DONE]".
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
                int? cachedInputTokens = null;
                int? responseTokens = null;

                if (root.TryGetProperty("prompt_eval_count", out var promptEvalCount) && promptEvalCount.TryGetInt32(out var promptCount))
                {
                    promptTokens = promptCount;
                }

                if (root.TryGetProperty("prompt_eval_cached_count", out var promptEvalCachedCount) && promptEvalCachedCount.TryGetInt32(out var cachedCount))
                {
                    cachedInputTokens = cachedCount;
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

                    if (cachedInputTokens is null &&
                        usage.TryGetProperty("prompt_tokens_details", out var promptTokensDetails) &&
                        promptTokensDetails.ValueKind == JsonValueKind.Object &&
                        promptTokensDetails.TryGetProperty("cached_tokens", out var cachedTokens) &&
                        cachedTokens.TryGetInt32(out var cachedTokensValue))
                    {
                        cachedInputTokens = cachedTokensValue;
                    }
                }

                if (promptTokens is not null || responseTokens is not null)
                {
                    // A line that yields prompt/response counts is the terminal usage frame:
                    // an ABSENT cached count means the target saw 0 cached tokens.
                    cachedInputTokens ??= 0;
                    // Defensively clamp cached ≤ total (cached is a subset of the total input).
                    if (promptTokens is not null && cachedInputTokens > promptTokens)
                    {
                        cachedInputTokens = promptTokens;
                    }

                    return (promptTokens, cachedInputTokens, responseTokens);
                }
            }
            catch (JsonException)
            {
                // Not a complete/valid JSON line (e.g. truncated tail); ignore and keep looking.
            }
        }

        return (null, null, null);
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
        var dynamicOptions = options.CurrentValue.DynamicContext;
        if (!dynamicOptions.Enabled)
        {
            return (null, null);
        }

        var normalizedName = RoutingDecisionService.NormalizeModelName(modelName);
        OllamaTargetOptions? targetOptions = null;
        foreach (var t in options.CurrentValue.GetEffectiveTargets(logger))
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
            options.CurrentValue.Models.TryGetValue(normalizedName, out thresholds);

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
            var margin = Math.Max(0, options.CurrentValue.ContextFitMarginTokens);
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
