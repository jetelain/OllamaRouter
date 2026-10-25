using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Multi-tier, hysteresis-aware liveness prober for the fleet targets (see
/// <see cref="ITargetLivenessService"/>). Implements <see cref="IHostedService"/>
/// so it runs an asynchronous fleet probe at boot and a background timer that
/// re-probes every Offline/Unknown target every <c>reprobeInterval</c> (default
/// 10 s), letting sleeping/roaming machines rejoin the routable fleet within one
/// tick — without restarting the router. An Offline/Unknown → Online transition
/// fires an asynchronous catalog refresh
/// (<see cref="IModelCatalogCacheService.RefreshAsync"/>, fire-and-forget bounded
/// to 4 s) so a rejoined node's /api/tags names are immediately visible to routing
/// decisions.
/// <para>
/// State transitions are applied through the single <see cref="ApplyProbeResult"/>
/// code path shared by on-demand probes and the background re-prober. The probe
/// transport is the injected <see cref="HttpClient"/> (in production the
/// "LivenessProbe" named client with a 1.5 s timeout and 10 s pooled-connection
/// lifetime); it is <b>not</b> disposed by this service because
/// <see cref="IHttpClientFactory"/>-created clients must not be disposed — only
/// the cancellation source is disposed in <see cref="StopAsync"/>.
/// </para>
/// </summary>
public sealed class TargetLivenessService(
    IOptions<OllamaRouterOptions> options,
    IModelCatalogCacheService modelCatalogCache,
    HttpClient probeHttpClient,
    ILogger<TargetLivenessService> logger,
    TimeSpan? reprobeInterval = null,
    TimeSpan? onlineCacheTtl = null) : ITargetLivenessService, IHostedService
{
    /// <summary>Default: how long a successful probe's "Online" verdict is trusted before the next on-demand re-probe.</summary>
    private static readonly TimeSpan DefaultOnlineCacheTtl = TimeSpan.FromSeconds(3);

    /// <summary>Consecutive probe timeouts required before an Online/Unknown target is marked Offline (hysteresis).</summary>
    private const int TimeoutStrikesToTrip = 2;

    /// <summary>How long a fire-and-forget catalog refresh on rejoin may run before being abandoned.</summary>
    private static readonly TimeSpan CatalogRefreshBound = TimeSpan.FromSeconds(4);

    private readonly TimeSpan _reprobeInterval = reprobeInterval ?? TimeSpan.FromSeconds(10);
    private readonly TimeSpan _onlineCacheTtl = onlineCacheTtl ?? DefaultOnlineCacheTtl;

    private readonly ConcurrentDictionary<RoutingTarget, TargetSlot> _slots = new();
    private readonly ConcurrentDictionary<RoutingTarget, Task<TargetLivenessState>> _inFlightProbes = new();

    private CancellationTokenSource? _cts;
    private Task? _reprobeLoopTask;

    public DateTimeOffset ServiceStartTimeUtc { get; private set; } = DateTimeOffset.MinValue;

    // ------------------------------------------------------------------
    //  ITargetLivenessService
    // ------------------------------------------------------------------

    public bool IsOnline(RoutingTarget target)
    {
        if (!_slots.TryGetValue(target, out var slot))
        {
            return false;
        }

        lock (slot)
        {
            return slot.State == TargetLivenessState.Online &&
                   DateTimeOffset.UtcNow - slot.CachedAt <= _onlineCacheTtl;
        }
    }

    public async Task<TargetLivenessState> EnsureKnownAsync(RoutingTarget target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var slot = _slots.GetOrAdd(target, _ => new TargetSlot(target));

        if (slot.IsOnlineWithinTtl(DateTimeOffset.UtcNow, _onlineCacheTtl))
        {
            return TargetLivenessState.Online;
        }

        // Coalesce: N concurrent callers for the same target share one in-flight probe,
        // so the request is blocked for at most a single probe duration (the HttpClient
        // timeout). Probes always run on a background thread (Task.Run) so the probe's
        // own timeout can apply even on a synchronous (thread-pool-starved) stack, and
        // a caller cancellation never cancels the shared probe mid-flight for others.
        var probeTask = _inFlightProbes.GetOrAdd(target, _ => Task.Run(async () =>
        {
            try
            {
                var outcome = await ProbeOnceAsync(target, CancellationToken.None);
                ApplyProbeResult(target, slot, outcome, source: "on-demand");
                return slot.ReadState();
            }
            finally
            {
                Task<TargetLivenessState>? removed;
                _inFlightProbes.TryRemove(target, out removed);
            }
        }, CancellationToken.None));

        return await probeTask;
    }

    // One best-effort strike after an uncommitted transport failure from the dispatch loop.
    // Applies a single HardError through the shared hysteresis path (immediate Offline in any
    // state) and deliberately does NOT trigger the rejoin catalog refresh (a hard failure is
    // never a rejoin) and never throws.
    public Task MarkHardFailureAsync(RoutingTarget target)
    {
        var slot = _slots.GetOrAdd(target, _ => new TargetSlot(target));
        ApplyProbeResult(target, slot, ProbeOutcome.HardError, source: "uncommitted-transport-failure");
        return Task.CompletedTask;
    }

    public TargetLivenessSnapshot GetSnapshot() => new(GetEntries().Values.ToList());

    public IReadOnlyDictionary<RoutingTarget, TargetLivenessEntry> GetEntries()
    {
        var result = new Dictionary<RoutingTarget, TargetLivenessEntry>();
        foreach (var (target, slot) in _slots)
        {
            result[target] = slot.ToEntry();
        }

        // Ensure fleet targets that were never probed still show up as Unknown.
        foreach (var target in GetFleetTargets())
        {
            result.TryAdd(target, new TargetLivenessEntry(target, TargetLivenessState.Unknown, null, DateTimeOffset.MinValue));
        }

        return result;
    }

    public IReadOnlyList<RoutingTarget> GetFleetTargets()
    {
        // Cloud is never part of GetEffectiveTargets(); the defensive filter keeps
        // the guarantee explicit (Cloud is a relay and is never probed).
        return options.Value.GetEffectiveTargets(logger)
            .Where(t => !string.IsNullOrWhiteSpace(t.Name) && !string.IsNullOrWhiteSpace(t.Url))
            .Select(t => (RoutingTarget)t.Name!)
            .DistinctBy(t => t.Value ?? "")
            .Where(t => t != RoutingTarget.Cloud)
            .ToList();
    }

    // ------------------------------------------------------------------
    //  IHostedService: boot probe + background re-prober
    // ------------------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ServiceStartTimeUtc = DateTimeOffset.UtcNow;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Asynchronous fleet probe at boot (non-blocking: app.Run() is never delayed).
        _ = Task.Run(() => ProbeFleetAsync(GetFleetTargets(), token, source: "boot"), CancellationToken.None);

        _reprobeLoopTask = Task.Run(() => RunReprobeLoopAsync(token), CancellationToken.None);

        logger.LogInformation("Target liveness prober started (re-probe interval: {Interval}).", _reprobeInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }
        }

        if (_reprobeLoopTask is not null)
        {
            try
            {
                await _reprobeLoopTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown raced with the timer; the loop exits via its own token.
            }
            catch (TimeoutException)
            {
                // The loop was stuck in a probe (bounded by the HttpClient timeout);
                // give up on waiting — the process is going down anyway.
            }
        }

        _cts?.Dispose();
    }

    private async Task RunReprobeLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_reprobeInterval);
        while (true)
        {
            try
            {
                await timer.WaitForNextTickAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                // Re-probe Offline targets (and any still Unknown, e.g. whose boot probe
                // was a blackholed timeout-strike that never resolved); healthy Online
                // targets are validated on demand by the middleware within the TTL.
                var targets = GetFleetTargets()
                    .Where(t => !IsOnline(t))
                    .ToList();
                if (targets.Count > 0)
                {
                    await ProbeFleetAsync(targets, token, source: "re-prober");
                }
            }
            catch (Exception ex)
            {
                // The timer itself must never throw (a probe failure is a liveness
                // signal, not a crash).
                logger.LogDebug(ex, "Background re-probe tick failed; continuing.");
            }
        }
    }

    // ------------------------------------------------------------------
    //  Probe machinery
    // ------------------------------------------------------------------

    private async Task ProbeFleetAsync(IReadOnlyList<RoutingTarget> targets, CancellationToken link, string source)
    {
        // Probe every selected target in parallel; each probe is individually
        // bounded by the HttpClient timeout so one blackholed target cannot stall
        // the whole tick.
        await Task.WhenAll(targets.Select(t => ProbeAndApplyAsync(t, link, source)));
    }

    private async Task ProbeAndApplyAsync(RoutingTarget target, CancellationToken link, string source)
    {
        var slot = _slots.GetOrAdd(target, _ => new TargetSlot(target));
        var outcome = await ProbeOnceAsync(target, link);
        var previous = ApplyProbeResult(target, slot, outcome, source);
        if (previous != TargetLivenessState.Online && slot.ReadState() == TargetLivenessState.Online)
        {
            // Rejoin: an Offline/Unknown target just became Online → refresh its catalog.
            TriggerCatalogRefreshAsync(target);
        }
    }

    /// <summary>
    /// Performs one <c>GET {Url}/</c> probe and classifies the outcome into a
    /// <see cref="ProbeOutcome"/>. Bounded by the injected <see cref="HttpClient"/>
    /// timeout (1.5 s in production); never throws — every failure maps to an
    /// outcome so the probe loop and the middleware gate can rely on it.
    /// </summary>
    private async Task<ProbeOutcome> ProbeOnceAsync(RoutingTarget target, CancellationToken cancellationToken)
    {
        var url = GetTargetUrl(target);
        if (url is null)
        {
            // Unconfigured URL: treat as a connection failure (Offline), never an error.
            return ProbeOutcome.HardError;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.RelativeOrAbsolute).AbsoluteUri);
            // ResponseHeadersRead: only the connection/headers matter for liveness;
            // the body is never buffered.
            using var response = await probeHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return ProbeOutcome.Success;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or
                                   SocketException)
        {
            return Classify(ex, cancellationToken);
        }
    }

    private string? GetTargetUrl(RoutingTarget target) =>
        options.Value.GetEffectiveTargets(logger)
            .FirstOrDefault(t => string.Equals(t.Name, target.Value, StringComparison.OrdinalIgnoreCase))
            ?.Url;

    /// <summary>
    /// Applies one probe outcome to the target's slot and returns the previous
    /// state. Single code path for on-demand probes and the background re-prober:
    /// Success → Online (resets the timeout counter, refreshes the TTL);
    /// Timeout → 2-strike hysteresis; HardError → immediate Offline;
    /// Canceled → state unchanged.
    /// </summary>
    private TargetLivenessState ApplyProbeResult(RoutingTarget target, TargetSlot slot, ProbeOutcome outcome, string source)
    {
        if (outcome == ProbeOutcome.Canceled)
        {
            // Request/shutdown cancellation is not a liveness signal.
            return slot.ReadState();
        }

        TargetLivenessState previous;
        TargetLivenessState next;

        lock (slot)
        {
            previous = slot.State;
            slot.LastProbeAt = DateTimeOffset.UtcNow;

            switch (outcome)
            {
                case ProbeOutcome.Success:
                    next = TargetLivenessState.Online;
                    slot.ConsecutiveTimeouts = 0;
                    slot.CachedAt = DateTimeOffset.UtcNow;
                    slot.LastError = null;
                    break;

                case ProbeOutcome.Timeout:
                    slot.ConsecutiveTimeouts++;
                    slot.LastError = "timeout";
                    // Hysteresis: an Online/Unknown target trips only after 2
                    // consecutive timeouts; an already-Offline target stays Offline.
                    next = slot.ConsecutiveTimeouts >= TimeoutStrikesToTrip
                        ? TargetLivenessState.Offline
                        : slot.State;
                    break;

                case ProbeOutcome.HardError:
                default:
                    // A transport-level failure (socket error, connection refused,
                    // host unreachable, DNS failure) trips immediately, in any state.
                    next = TargetLivenessState.Offline;
                    slot.ConsecutiveTimeouts = 0;
                    slot.LastError = "connection error";
                    break;
            }

            if (next != slot.State)
            {
                slot.State = next;
                slot.LastStateChange = DateTimeOffset.UtcNow;
            }
        }

        LogTransition(target, source, previous, next, outcome);
        return previous;
    }

    private void LogTransition(RoutingTarget target, string source, TargetLivenessState previous, TargetLivenessState next, ProbeOutcome outcome)
    {
        if (previous == next)
        {
            logger.LogDebug("Liveness probe ({Source}) for {Target}: {Outcome}, state remains {State}.", source, target, outcome, next);
            return;
        }

        if (next == TargetLivenessState.Offline)
        {
            logger.LogInformation("Liveness probe ({Source}) for {Target}: {Outcome} → target marked Offline.", source, target, outcome);
        }
        else
        {
            logger.LogInformation("Liveness probe ({Source}) for {Target}: {Outcome} → target marked Online (rejoined the fleet).", source, target, outcome);
        }
    }

    private void TriggerCatalogRefreshAsync(RoutingTarget target)
    {
        var url = GetTargetUrl(target);
        if (url is null)
        {
            return;
        }

        // Fire-and-forget, bounded to 4 s so a slow /api/tags cannot pile up
        // unbounded work; failures are swallowed (the normal 30 s cache path
        // will pick the catalog up on its own eventually).
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(CatalogRefreshBound);
            try
            {
                var names = await modelCatalogCache.RefreshAsync(url, cts.Token).ConfigureAwait(false);
                logger.LogInformation("Catalog for rejoined target {Target} refreshed ({Count} models).", target, names.Count);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Catalog refresh for rejoined target {Target} failed (ignored).", target);
            }
        }, CancellationToken.None);
    }

    private static ProbeOutcome Classify(Exception exception, CancellationToken externalToken)
    {
        if (exception is OperationCanceledException oce && !oce.CancellationToken.IsCancellationRequested)
        {
            // The HttpClient's own timeout fired (no token set anywhere): a slow
            // but connected daemon → a probe timeout (hysteresis strike).
            return ProbeOutcome.Timeout;
        }

        // Cancellation from the external token (shutdown/tick cancel) is not a
        // liveness signal.
        if (exception is OperationCanceledException)
        {
            return externalToken.IsCancellationRequested ? ProbeOutcome.Canceled : ProbeOutcome.Timeout;
        }

        // HttpRequestException (including one wrapping SocketException / a DNS
        // failure) or a bare SocketException: a connection-establishment failure
        // → trip immediately.
        return ProbeOutcome.HardError;
    }

    // ------------------------------------------------------------------
    //  Per-target slot
    // ------------------------------------------------------------------

    private enum ProbeOutcome { Success, Timeout, HardError, Canceled }

    /// <summary>Mutable per-target state, guarded by the slot's own lock.</summary>
    private sealed class TargetSlot(RoutingTarget target)
    {
        public RoutingTarget Target { get; } = target;

        public TargetLivenessState State { get; set; } = TargetLivenessState.Unknown;
        public int ConsecutiveTimeouts { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset CachedAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastProbeAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastStateChange { get; set; } = DateTimeOffset.MinValue;

        public bool IsOnlineWithinTtl(DateTimeOffset now, TimeSpan ttl) =>
            State == TargetLivenessState.Online && now - CachedAt <= ttl;

        public TargetLivenessState ReadState() => State;

        public TargetLivenessEntry ToEntry() => new(Target, State, LastError, LastProbeAt);
    }
}
