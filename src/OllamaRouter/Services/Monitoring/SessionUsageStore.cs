using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Services.Monitoring;

/// <summary>
/// Bounded implementation of <see cref="ISessionUsageStore"/> (task #85): a single-gate
/// case-insensitive dictionary plus an intrusive doubly-linked list (head = most-recently-
/// active, tail = least-recently-active). Only <see cref="Record"/> moves entries, so
/// "most-recently-active" is driven by request traffic ONLY — list/detail reads never
/// influence eviction (this is why the BCL <c>IntrusiveLruCache</c> is NOT used: its
/// <c>TryGetValue</c> marks the found entry most-recently-used).
/// <para>
/// Persistence (task #105): when constructed with a <c>stateFile</c> path, the raw counters
/// are lazily loaded from that file on first access (fail open: a missing/corrupt file
/// starts the store empty) and persisted best-effort — throttled background saves (at most
/// one every 30 seconds, atomic same-directory temp-file write) plus a synchronous flush on
/// <see cref="IHostApplicationLifetime.ApplicationStopped"/>. Cost figures are NEVER persisted
/// (raw counters only); they are computed at read time so pricing changes re-price persisted
/// sessions immediately. When constructed WITHOUT a <c>stateFile</c> (the unit-test default),
/// the store is strictly in-memory: no file I/O, no background tasks — AOT-safe.
/// </para>
/// </summary>
public sealed class SessionUsageStore : ISessionUsageStore
{
    // Maximum length of a stored session ID (mirrors the sticky-routing 256-char cap in the
    // routing middleware; the display-only header extraction has no cap, so the store enforces
    // it — a hostile oversized header can never bloat the store).
    private const int MaxSessionIdLength = 256;

    // Records are saved at most this often (throttled background saves; a graceful shutdown
    // flush covers the rest), limiting data loss on a crash while keeping write frequency low.
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionUsageEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMonitor<OllamaRouterOptions> _options;
    private readonly ILogger<SessionUsageStore>? _logger;
    private readonly string? _stateFile;
    private bool _loaded;
    // TickCount64 of the last scheduled save; used for throttling by SaveThrottled.
    private long _lastSaveAt;

    // Intrusive LRU list ends (head = most-recently-active, tail = least-recently-active).
    private SessionUsageEntry? _head;
    private SessionUsageEntry? _tail;

    public SessionUsageStore(
        IOptionsMonitor<OllamaRouterOptions> options,
        ILogger<SessionUsageStore>? logger = null,
        string? stateFile = null,
        IHostApplicationLifetime? lifetime = null)
    {
        _options = options;
        _logger = logger;
        _stateFile = stateFile;
        if (stateFile is not null && lifetime is not null)
        {
            // Graceful shutdown (ollamarouter stop / /api/router/admin/shutdown) flushes all
            // pending session statistics to disk synchronously.
            lifetime.ApplicationStopped.Register(Save);
        }
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
            EnsureLoaded();

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

        SaveThrottled();
    }

    /// <inheritdoc/>
    public IReadOnlyList<SessionUsageSnapshot> GetSessions()
    {
        lock (_gate)
        {
            EnsureLoaded();

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
            EnsureLoaded();
            return _entries.TryGetValue(sessionId, out var session) ? SnapshotOf(session) : null;
        }
    }

    /// <inheritdoc/>
    public int Count
    {
        get { lock (_gate) { EnsureLoaded(); return _entries.Count; } }
    }

    /// <summary>
    /// Persists all sessions (most-recently-active first, raw counters only) to the state file
    /// (creating the directory when necessary) via an atomic same-directory temp-file write.
    /// No-op when the store was constructed in-memory only. Registered on application
    /// shutdown, called periodically by <see cref="SaveThrottled"/>; I/O errors are logged,
    /// not thrown.
    /// </summary>
    public void Save()
    {
        if (_stateFile is null)
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoaded();

            // The JSON array order IS the LRU order (element 0 = head = most-recently-active);
            // the gate is held for the write (same as ActivityStatisticsService.Save — the file
            // is small: at most the session cap × a small entry, and saves are throttled).
            var entries = new List<SessionUsageStateEntry>(_entries.Count);
            for (var current = _head; current is not null; current = current.Next)
            {
                var byTarget = new Dictionary<RoutingTarget, SessionUsageStateCounters>(current.ByTarget.Count);
                foreach (var (target, counters) in current.ByTarget)
                {
                    byTarget[target] = new SessionUsageStateCounters
                    {
                        Requests = counters.Requests,
                        InputTokens = counters.InputTokens,
                        CachedInputTokens = counters.CachedInputTokens,
                        OutputTokens = counters.OutputTokens,
                        ElapsedMilliseconds = counters.ElapsedMilliseconds
                    };
                }

                entries.Add(new SessionUsageStateEntry
                {
                    SessionId = current.SessionId,
                    LastSeenUtc = current.LastSeenUtc,
                    ByTarget = byTarget
                });
            }

            try
            {
                var directory = Path.GetDirectoryName(_stateFile);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                var tempPath = Path.Combine(directory ?? ".", $"session-usage.json.tmp-{Guid.NewGuid():N}");
                try
                {
                    File.WriteAllText(
                        tempPath,
                        JsonSerializer.Serialize(entries, OllamaRouterJsonSerializerContext.Default.ListSessionUsageStateEntry));
                    File.Move(tempPath, _stateFile, overwrite: true); // atomic on the same volume
                }
                finally
                {
                    // Best-effort cleanup so a failed write never leaves a temp file behind.
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); }
                        catch { /* best effort */ }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error saving session usage");
            }
        }
    }

    // Lock-free throttled save: the first caller that wins the CompareExchange on _lastSaveAt
    // triggers an async Save; every other caller within SaveInterval returns immediately.
    // No-op when the store is in-memory only (also the no-Task.Run guarantee for unit tests).
    private void SaveThrottled()
    {
        if (_stateFile is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastSaveAt);
        if (now - last < SaveInterval.TotalMilliseconds ||
            Interlocked.CompareExchange(ref _lastSaveAt, now, last) != last)
        {
            return;
        }

        Task.Run(Save);
    }

    // Loads the persisted sessions once (must be called with _gate held). A missing file is a
    // silent first run; a corrupt/unreadable file is logged and treated as an empty store
    // (fail open — never thrown, never blocks startup).
    private void EnsureLoaded()
    {
        if (_stateFile is null || _loaded)
        {
            return;
        }
        _loaded = true;
        if (!File.Exists(_stateFile))
        {
            return; // first run: nothing persisted yet.
        }

        try
        {
            var json = File.ReadAllText(_stateFile);
            var loaded = JsonSerializer.Deserialize(json, OllamaRouterJsonSerializerContext.Default.ListSessionUsageStateEntry);
            if (loaded is null)
            {
                return;
            }

            // Rebuild the LRU list IN ARRAY ORDER (element 0 = head = most-recently-active),
            // validating each entry: skip empty/oversized IDs, keep the FIRST case-duplicate
            // (the MRU occurrence), truncate to the live cap (most-recently-active prefix)
            // and clamp hand-edited negative counters to 0 (torn/hand-edited files are
            // tolerated best-effort).
            var cap = _options.CurrentValue.SessionUsageMaxSessions;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<SessionUsageEntry>();
            foreach (var entry in loaded)
            {
                if (_entries.Count >= cap)
                {
                    break;
                }
                if (string.IsNullOrEmpty(entry.SessionId) || entry.SessionId.Length > MaxSessionIdLength)
                {
                    continue;
                }
                if (!seen.Add(entry.SessionId))
                {
                    continue; // case-duplicate; the earlier (more recently active) entry wins
                }

                var session = new SessionUsageEntry(entry.SessionId)
                {
                    LastSeenUtc = entry.LastSeenUtc
                };
                foreach (var (target, counters) in entry.ByTarget ?? new Dictionary<RoutingTarget, SessionUsageStateCounters>())
                {
                    session.ByTarget[target] = new SessionUsageEntry.Counters
                    {
                        Requests = Math.Max(0, counters.Requests),
                        InputTokens = Math.Max(0, counters.InputTokens),
                        CachedInputTokens = Math.Max(0, counters.CachedInputTokens),
                        OutputTokens = Math.Max(0, counters.OutputTokens),
                        ElapsedMilliseconds = Math.Max(0, counters.ElapsedMilliseconds)
                    };
                }

                _entries[entry.SessionId] = session;
                ordered.Add(session);
            }

            // Link the intrusive list head → tail in (most-recently-active-first) array order.
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Prev = i > 0 ? ordered[i - 1] : null;
                ordered[i].Next = i + 1 < ordered.Count ? ordered[i + 1] : null;
            }
            _head = ordered.Count > 0 ? ordered[0] : null;
            _tail = ordered.Count > 0 ? ordered[^1] : null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load persisted session usage from {Path}; starting with an empty store.", _stateFile);
        }
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

/// <summary>
/// One persisted session (task #105). The FILE-LEVEL array order is the most-recently-active-
/// first (LRU) order; the entry itself carries no ordering. Cost figures are NOT persisted
/// (raw counters only) — they are computed at read time from the current Pricing.
/// </summary>
public sealed class SessionUsageStateEntry
{
    public string SessionId { get; set; } = string.Empty;

    public DateTimeOffset LastSeenUtc { get; set; }

    public Dictionary<RoutingTarget, SessionUsageStateCounters> ByTarget { get; set; } = new();
}

/// <summary>
/// Raw per-target counters (task #105; exact mirror of the store's internal counters).
/// <c>ElapsedMilliseconds</c> is <c>long</c> end-to-end for an exact round-trip (the wire
/// DTO renders it as <c>double</c> unchanged).
/// </summary>
public sealed class SessionUsageStateCounters
{
    [JsonInclude] public int Requests;

    [JsonInclude] public int InputTokens;

    [JsonInclude] public int CachedInputTokens;

    [JsonInclude] public int OutputTokens;

    [JsonInclude] public long ElapsedMilliseconds;
}
