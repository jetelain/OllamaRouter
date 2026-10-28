namespace OllamaRouter.Services;

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
}
