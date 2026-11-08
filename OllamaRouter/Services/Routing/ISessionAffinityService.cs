namespace OllamaRouter.Services.Routing;

/// <summary>
/// Sticky session/agent affinity store (multi-agent KV-cache affinity): pins a logical
/// caller session (e.g. an orchestrator agent) to the fleet target that served its previous
/// turn, so consecutive turns reuse the target's prompt-prefix KV cache instead of paying a
/// 10–30 s prompt re-evaluation penalty on every turn.
/// <para>
/// Session IDs are opaque strings (case-insensitive keys) and leases are fleet-target only
/// (the Cloud pseudo-target is a relay and is NEVER leased). Leases expire after
/// <c>OllamaRouterOptions.SessionAffinityTtlMinutes</c> (default 30) of inactivity — a
/// sliding window refreshed on every <see cref="TryGetAffinity"/> hit /
/// <see cref="SetAffinity"/> — with a bounded lazy sweep (no background timer, so the
/// in-process test host stays deterministic).
/// </para>
/// <para>
/// Active-session reservation window (task #77): a session is "active on" a target while its
/// lease exists and its sliding last-access timestamp is within the caller-supplied window
/// (typically <c>OllamaRouterOptions.SessionReservationWindowSeconds</c>, default 30 s).
/// The read-only reservation queries below (<see cref="GetActiveSessionCount"/>,
/// <see cref="IsTargetReservedByOtherSession"/>) let the routing pipeline recognize a
/// recently-active target as reserved/contended for a DIFFERENT arriving session without
/// extending the leaser's sliding TTL.
/// </para>
/// </summary>
public interface ISessionAffinityService
{
    /// <summary>
    /// Returns the pinned target for <paramref name="sessionId"/> when a live lease exists.
    /// A hit refreshes the sliding TTL; an absent or TTL-expired lease returns false (the
    /// expired entry is removed lazily).
    /// </summary>
    bool TryGetAffinity(string sessionId, out RoutingTarget target);

    /// <summary>
    /// Pins (or re-pins) <paramref name="sessionId"/> to <paramref name="target"/>,
    /// refreshing the sliding TTL.
    /// </summary>
    void SetAffinity(string sessionId, RoutingTarget target);

    /// <summary>
    /// Removes the lease for a single session (e.g. the caller explicitly ended the session).
    /// </summary>
    void RemoveAffinity(string sessionId);

    /// <summary>
    /// Removes every lease pinned to <paramref name="target"/> (case-insensitive name match),
    /// called when the target goes offline or fails uncommitted, so other sessions pinned to
    /// the dying target are released immediately rather than waiting for the TTL.
    /// </summary>
    void InvalidateTarget(RoutingTarget target);

    /// <summary>
    /// Read-only active-session occupancy query (task #77 reservation window): the number of
    /// DISTINCT sessions with a live lease on <paramref name="target"/> (case-insensitive
    /// name match) whose last access is within <paramref name="window"/> of the current clock.
    /// A non-positive <paramref name="window"/> returns 0 (the window is disabled). The query
    /// never refreshes the sliding TTL nor mutates the lease table (evaluating candidates for
    /// one session must never extend another session's reservation). The Cloud pseudo-target
    /// can never be reserved (it is never leased).
    /// </summary>
    int GetActiveSessionCount(RoutingTarget target, TimeSpan window);

    /// <summary>
    /// Read-only reservation query (task #77): whether <paramref name="target"/> is currently
    /// reserved by ANOTHER active session — i.e. it holds a live lease for some session whose
    /// last access is within <paramref name="window"/> of the current clock, EXCLUDING the
    /// request's own lease (<paramref name="currentSessionId"/>, case-insensitive ID match,
    /// so a session never contends with itself and sticky affinity is preserved on its own
    /// target). A null/blank <paramref name="currentSessionId"/> means any active lease on the
    /// target counts as "other"; a non-positive <paramref name="window"/> returns false.
    /// The query never refreshes the sliding TTL nor mutates the lease table.
    /// </summary>
    bool IsTargetReservedByOtherSession(RoutingTarget target, string? currentSessionId, TimeSpan window);
}
