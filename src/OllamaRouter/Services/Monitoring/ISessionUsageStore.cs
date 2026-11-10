using OllamaRouter.Services.Routing;

namespace OllamaRouter.Services.Monitoring;

/// <summary>
/// Accumulated raw counters of one <see cref="RoutingTarget"/> for one session (task #85).
/// Session totals are always derived sums over the per-target map — the per-target counters
/// are the only stored counters.
/// </summary>
/// <param name="Requests">Completed SUCCESSFUL requests with daemon-reported usage tokens.</param>
/// <param name="InputTokens">Total prompt/input tokens (cached tokens INCLUSIVE).</param>
/// <param name="CachedInputTokens">Cached prompt tokens (a subset of <paramref name="InputTokens"/>).</param>
/// <param name="OutputTokens">Total response/completion tokens.</param>
/// <param name="ElapsedMilliseconds">Cumulative GPU time in milliseconds (every completed
/// SUCCESSFUL request contributes, with or without usage tokens — electricity flows).</param>
public sealed record SessionUsageTotals(
    int Requests,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    long ElapsedMilliseconds);

/// <summary>
/// Read-only snapshot of one stored session (task #85). The list returned by
/// <see cref="ISessionUsageStore.GetSessions"/> is in most-recently-active order; the snapshot
/// itself never carries ordering.
/// </summary>
/// <param name="SessionId">The public session ID (first-seen casing is preserved).</param>
/// <param name="LastSeenUtc">Timestamp of the last recorded completion for this session.</param>
/// <param name="ByTarget">Per-target accumulated counters (only targets that received a
/// recorded completion for this session).</param>
public sealed record SessionUsageSnapshot(
    string SessionId,
    DateTimeOffset LastSeenUtc,
    IReadOnlyDictionary<RoutingTarget, SessionUsageTotals> ByTarget);

/// <summary>
/// Bounded in-memory store of per-session usage aggregates keyed by the public session ID
/// (task #85). The store holds RAW COUNTERS ONLY (exactly like the daily
/// <see cref="IActivityStatisticsService"/>) — cost figures (cloud value, electricity, savings)
/// are computed at READ time by the endpoints from the currently configured pricing, so a
/// pricing change re-prices every stored session immediately with zero store changes.
/// <para>
/// In-memory only (nothing is persisted), LRU-bounded by
/// <c>OllamaRouterOptions.SessionUsageMaxSessions</c> (default 1000, live). Requests without a
/// session ID are never stored; only completed requests are recorded, and only
/// <see cref="Record"/> refreshes recency — reads never influence eviction.
/// </para>
/// </summary>
public interface ISessionUsageStore
{
    /// <summary>
    /// Records the completed request's contribution to the session identified by
    /// <see cref="ActivityLogEntry.SessionId"/> (completions only; mirroring the
    /// <see cref="IActivityStatisticsService.Add"/> counting semantics: failed requests
    /// contribute nothing; a success without daemon-reported usage contributes elapsed time
    /// only). No-op for null/empty session IDs and for IDs longer than 256 characters.
    /// </summary>
    void Record(ActivityLogEntry entry);

    /// <summary>
    /// Returns the stored sessions, most-recently-active first. Read-only: this call never
    /// refreshes recency (eviction is driven by request traffic only).
    /// </summary>
    IReadOnlyList<SessionUsageSnapshot> GetSessions();

    /// <summary>
    /// Returns the snapshot of the session with the given ID (case-insensitive) or null when
    /// no such session is stored. Read-only: this call never refreshes recency.
    /// </summary>
    SessionUsageSnapshot? TryGetSession(string sessionId);

    /// <summary>The number of sessions currently stored (bounded by the configured cap).</summary>
    int Count { get; }
}
