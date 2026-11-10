using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Services.Monitoring;

/// <summary>
/// Bounded in-memory implementation of <see cref="ISessionUsageStore"/> (task #85): a
/// single-gate case-insensitive dictionary plus an intrusive doubly-linked list (head =
/// most-recently-active, tail = least-recently-active). Only <see cref="Record"/> moves
/// entries, so "most-recently-active" is driven by request traffic ONLY — list/detail reads
/// never influence eviction (this is why the BCL <c>IntrusiveLruCache</c> is NOT used: its
/// <c>TryGetValue</c> marks the found entry most-recently-used). In-memory only: no
/// persistence, no background tasks, no I/O, no timers — AOT-safe.
/// </summary>
public sealed class SessionUsageStore : ISessionUsageStore
{
    // Maximum length of a stored session ID (mirrors the sticky-routing 256-char cap in the
    // routing middleware; the display-only header extraction has no cap, so the store enforces
    // it — a hostile oversized header can never bloat the store).
    private const int MaxSessionIdLength = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionUsageEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMonitor<OllamaRouterOptions> _options;
    private readonly ILogger<SessionUsageStore>? _logger;

    // Intrusive LRU list ends (head = most-recently-active, tail = least-recently-active).
    private SessionUsageEntry? _head;
    private SessionUsageEntry? _tail;

    public SessionUsageStore(IOptionsMonitor<OllamaRouterOptions> options, ILogger<SessionUsageStore>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Record(ActivityLogEntry entry)
    {
        var sessionId = entry.SessionId;
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length > MaxSessionIdLength)
        {
            return;
        }

        // Failed completions contribute NOTHING (no entry, no counters, no elapsed time) —
        // the exact IActivityStatisticsService.Add semantics (task #85, Q2 = A).
        if (!entry.Success)
        {
            return;
        }

        var elapsedMs = Math.Max(0.0, (double)entry.ElapsedMilliseconds);
        var actualOutput = entry.ActualResponseTokens;

        int inputTokens = 0;
        int cachedInputTokens = 0;
        if (actualOutput is not null)
        {
            // Prompt count falls back to the estimate; cached is a subset of the total input
            // (a missing cached count counts as 0, a corrupt cached > input is clamped) —
            // same as the daily statistics.
            inputTokens = entry.ActualPromptTokens ?? entry.EstimatedPromptTokens;
            cachedInputTokens = Math.Clamp(entry.CachedInputTokens ?? 0, 0, inputTokens);
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var session))
            {
                // Live cap (OllamaRouter:SessionUsageMaxSessions): read on every Record; a
                // shrunk cap evicts down to the new bound. A NEW session beyond the cap evicts
                // the LEAST-recently-active entries (never the just-recorded one).
                while (_entries.Count >= _options.CurrentValue.SessionUsageMaxSessions && _tail is { } evicted)
                {
                    Unlink(evicted);
                    _entries.Remove(evicted.SessionId);
                    _logger?.LogDebug("Evicted session usage aggregate for {SessionId} (session usage cap reached).", evicted.SessionId);
                }

                session = new SessionUsageEntry(sessionId)
                {
                    LastSeenUtc = entry.Timestamp,
                };
                _entries[sessionId] = session;
            }

            var counters = session.ByTarget.TryGetValue(entry.Target, out var current)
                ? current
                : session.ByTarget[entry.Target] = new();
            counters.ElapsedMilliseconds += (long)elapsedMs;
            if (actualOutput is not null)
            {
                counters.Requests++;
                counters.InputTokens += inputTokens;
                counters.CachedInputTokens += cachedInputTokens;
                counters.OutputTokens += actualOutput.Value;
            }

            session.LastSeenUtc = entry.Timestamp;
            MoveToHead(session);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<SessionUsageSnapshot> GetSessions()
    {
        lock (_gate)
        {
            var snapshots = new List<SessionUsageSnapshot>(_entries.Count);
            for (var current = _head; current is not null; current = current.Next)
            {
                snapshots.Add(SnapshotOf(current));
            }
            return snapshots;
        }
    }

    /// <inheritdoc/>
    public SessionUsageSnapshot? TryGetSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(sessionId, out var session) ? SnapshotOf(session) : null;
        }
    }

    /// <inheritdoc/>
    public int Count
    {
        get { lock (_gate) { return _entries.Count; } }
    }

    // Builds a read-only snapshot of one entry (must be called with _gate held).
    private static SessionUsageSnapshot SnapshotOf(SessionUsageEntry session)
    {
        var byTarget = new Dictionary<RoutingTarget, SessionUsageTotals>(session.ByTarget.Count);
        foreach (var (target, counters) in session.ByTarget)
        {
            byTarget[target] = new SessionUsageTotals(
                counters.Requests,
                counters.InputTokens,
                counters.CachedInputTokens,
                counters.OutputTokens,
                counters.ElapsedMilliseconds);
        }
        return new SessionUsageSnapshot(session.SessionId, session.LastSeenUtc, byTarget);
    }

    // Moves an existing entry to the list head (must be called with _gate held). A not-yet-
    // linked (just-created) entry is inserted directly — Unlinking a null-pointer node would
    // misread the NULL ends as "this node is head/tail" and clobber the list ends.
    private void MoveToHead(SessionUsageEntry session)
    {
        var alreadyLinked = ReferenceEquals(session, _head) || session.Prev is not null || session.Next is not null;
        if (alreadyLinked)
        {
            Unlink(session);
        }

        session.Prev = null;
        session.Next = _head;
        if (_head is not null)
        {
            _head.Prev = session;
        }
        _head = session;
        _tail ??= session;
    }

    // Removes an entry from the list WITHOUT removing it from the dictionary (the caller
    // decides; must be called with _gate held).
    private void Unlink(SessionUsageEntry session)
    {
        if (session.Prev is { } prev)
        {
            prev.Next = session.Next;
        }
        else
        {
            _head = session.Next;
        }

        if (session.Next is { } next)
        {
            next.Prev = session.Prev;
        }
        else
        {
            _tail = session.Prev;
        }

        session.Prev = null;
        session.Next = null;
    }

    /// <summary>One stored session: the key, the LRU pointers and the per-target counters.</summary>
    private sealed class SessionUsageEntry(string sessionId)
    {
        public string SessionId { get; } = sessionId;

        public DateTimeOffset LastSeenUtc { get; set; }

        internal Dictionary<RoutingTarget, Counters> ByTarget { get; } = new();

        public SessionUsageEntry? Prev { get; set; }

        public SessionUsageEntry? Next { get; set; }

        /// <summary>Mutable per-target counters (single-threaded under the store gate).</summary>
        internal sealed class Counters
        {
            public int Requests;
            public int InputTokens;
            public int CachedInputTokens;
            public int OutputTokens;
            public long ElapsedMilliseconds;
        }
    }
}
