namespace OllamaRouter.Services;

/// <summary>
/// The liveness state of a single fleet target as seen by the
/// <see cref="ITargetLivenessService"/> state machine.
/// </summary>
public enum TargetLivenessState
{
    /// <summary>No probe has completed yet for this target.</summary>
    Unknown,

    /// <summary>A recent probe connected successfully; the target is routable.</summary>
    Online,

    /// <summary>The target is unreachable (hard transport failure or repeated probe timeouts).</summary>
    Offline
}

/// <summary>
/// Immutable snapshot of a single target's liveness state, as exposed by
/// <see cref="ITargetLivenessService.GetEntries"/> and
/// <see cref="ITargetLivenessService.GetSnapshot"/>.
/// <para>
/// The <see cref="State"/> value is the raw state-machine state (NOT the
/// TTL-aware <see cref="ITargetLivenessService.IsOnline"/> predicate), so that
/// diagnostics payloads can honestly report a TTL-expired "online" entry as such.
/// </para>
/// </summary>
public sealed record TargetLivenessEntry(
    RoutingTarget Target,
    TargetLivenessState State,
    string? LastError,
    DateTimeOffset LastProbeAt,
    long? LastProbeLatencyMs = null);

/// <summary>
/// Immutable snapshot of the liveness state of the entire fleet, for diagnostics
/// (the 503 all-offline payload) and future monitor UI / TUI display.
/// </summary>
public sealed record TargetLivenessSnapshot(IReadOnlyList<TargetLivenessEntry> Entries);

/// <summary>
/// Multi-tier, hysteresis-aware liveness prober for the fleet targets.
/// <para>
/// Probes each effective fleet target (the Cloud pseudo-target is never probed) with
/// <c>GET {Url}/</c> and maintains a per-target state machine
/// (<see cref="TargetLivenessState"/>): a hard transport failure (socket error,
/// connection refused, host unreachable) trips the target to
/// <see cref="TargetLivenessState.Offline"/> immediately; a probe timeout requires
/// 2 consecutive strikes before an Online/Unknown target goes Offline (hysteresis);
/// a single successful probe returns the target to
/// <see cref="TargetLivenessState.Online"/> and (re)starts the short
/// <see cref="ITargetLivenessService.IsOnline"/> cache TTL (3 s).
/// </para>
/// <para>
/// The service is the ONLY component that mutates liveness state: the background
/// re-prober (an <see cref="IHostedService"/> timer probing Offline/Unknown targets
/// every 10 s) and the middleware's on-demand
/// <see cref="EnsureKnownAsync"/> gate. YARP forwarding, routing decision
/// handlers and the statistics <c>finally</c> block never
/// call into it, which is what guarantees that a streaming/generation timeout mid
/// request can never trip the breaker.
/// </para>
/// </summary>
public interface ITargetLivenessService
{
    /// <summary>
    /// Fast, TTL-aware "is this target currently safe to route to" predicate:
    /// true iff the state machine says Online AND the last successful probe is
    /// within the 3 s cache TTL. A target whose cache expired is stale and must be
    /// re-probed via <see cref="EnsureKnownAsync"/> before being trusted again.
    /// </summary>
    bool IsOnline(RoutingTarget target);

    /// <summary>
    /// Ensures the liveness state of <paramref name="target"/> is known and fresh:
    /// returns immediately when Online within TTL, otherwise runs (or coalesces
    /// onto, when a probe for the same target is already in flight) one probe
    /// bounded by the probe <c>HttpClient</c> timeout (1.5 s) and applies the
    /// result. Concurrent callers for the same target share a single in-flight
    /// probe, so no request can be blocked longer than one probe.
    /// </summary>
    Task<TargetLivenessState> EnsureKnownAsync(RoutingTarget target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies exactly ONE best-effort liveness strike to <paramref name="target"/> after an
    /// UNCOMMITTED transport failure observed by the dispatch loop (the request could not even
    /// reach response headers, e.g. connection refused / socket error). The strike flows
    /// through the same hysteresis code path as a hard-transport probe failure
    /// (<c>ApplyProbeResult(..., ProbeOutcome.HardError)</c>), so the target trips to
    /// <see cref="TargetLivenessState.Offline"/> immediately and the 10 s background re-prober
    /// remains the recovery path. Mid-stream failures NEVER call this (inference isolation).
    /// Never triggers a catalog refresh. Never throws.
    /// </summary>
    Task MarkHardFailureAsync(RoutingTarget target);

    /// <summary>
    /// Per-target raw state-machine state (no TTL applied) plus the most recent
    /// probe error, for diagnostics payloads and the future monitor UI.
    /// </summary>
    TargetLivenessSnapshot GetSnapshot();

    /// <summary>
    /// Same data as <see cref="GetSnapshot"/>, exposed as a dictionary for
    /// convenience; entries use the raw (non-TTL-aware) state.
    /// </summary>
    IReadOnlyDictionary<RoutingTarget, TargetLivenessEntry> GetEntries();

    /// <summary>
    /// When the hosted-service <c>StartAsync</c> ran (i.e. when the background
    /// probe + re-prober actually started). Captured in <c>StartAsync</c> rather
    /// than at construction because DI may build the singleton before hosted
    /// services start; the middleware uses this to measure its 10 s startup grace
    /// window.
    /// </summary>
    DateTimeOffset ServiceStartTimeUtc { get; }

    /// <summary>
    /// The effective fleet targets (in configuration order) that are subject to
    /// liveness probing — the Cloud pseudo-target is excluded (it is a relay, never
    /// probed). Used by the middleware's all-offline gate and diagnostics payload.
    /// </summary>
    IReadOnlyList<RoutingTarget> GetFleetTargets();
}
