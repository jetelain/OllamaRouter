using System.Text.Json;
using System.Text.Json.Serialization;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Services.Monitoring;

/// <summary>
/// Tracks token usage per <see cref="RoutingTarget"/> and calendar day, persisted to JSON so that
/// counters survive process restarts. See <see cref="GetSnapshot"/> for the data shape exposed
/// to callers.
/// </summary>
public class ActivityStatisticsService : IActivityStatisticsService
{
    // Statistics are saved when the day rolls over and on application shutdown; this periodic
    // save additionally limits data loss to at most SaveInterval of token counts if the process
    // exits unexpectedly (crash, kill) while keeping the write frequency low.
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    // Latest day bucket; when set, this is the last element of all.
    private ActivityStatisticsDate? today;

    // Guards the structural state (all, today, isLoaded). The per-target counters are mutated
    // with Interlocked, so the lock only protects reads/writes of the whole structure.
    private readonly object gate = new();

    // All day buckets (oldest first), loaded from disk once.
    private readonly List<ActivityStatisticsDate> all = new();
    private readonly string statsFile;
    // Fleet targets (configured targets + the Cloud pseudo-target) that new day buckets are
    // pre-populated with, and that aggregation zero-fills.
    private readonly IReadOnlyList<RoutingTarget> targets;
    private bool isLoaded;
    // TickCount64 of the last successful save; used for throttling by SaveThrottled.
    private long lastSaveAt;
    private readonly ILogger<ActivityStatisticsService> logger;

    public ActivityStatisticsService(
        string statsFile,
        IReadOnlyList<RoutingTarget> targets,
        IHostApplicationLifetime lifetime,
        ILogger<ActivityStatisticsService> logger)
    {
        this.statsFile = statsFile;
        this.targets = targets
            .Where(target => !string.IsNullOrWhiteSpace(target.Value))
            .GroupBy(target => target.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        this.logger = logger;

        lifetime.ApplicationStopped.Register(Save);
    }

    /// <summary>
    /// Records the entry's contribution toward the daily statistics: every COMPLETED SUCCESSFUL
    /// request accumulates its wall-clock <see cref="ActivityLogEntry.ElapsedMilliseconds"/>
    /// (defensively clamped to >= 0) because the node consumes electricity whether or not the
    /// daemon reported usage tokens (task #84); the request/token counters are additionally
    /// recorded only when the router reported actual response tokens (estimated outputs are
    /// never counted; failed requests never count). The prompt count falls back to the
    /// estimate when the router did not report actual prompt tokens.
    /// </summary>
    public void Add(ActivityLogEntry activityLogEntry)
    {
        if (!activityLogEntry.Success)
        {
            return;
        }

        var elapsedMs = Math.Max(0.0, (double)activityLogEntry.ElapsedMilliseconds);
        var actualOutput = activityLogEntry.ActualResponseTokens;

        if (actualOutput is null)
        {
            // Electricity flows even when the daemon reports no usage (task #84); the
            // request/token counters stay untouched (pre-existing semantics for
            // usage-less entries).
            AddElapsed(activityLogEntry.Target, elapsedMs);
            return;
        }

        var actualInput = activityLogEntry.ActualPromptTokens ?? activityLogEntry.EstimatedPromptTokens;

        // Cached is a subset of the total input tokens; a missing cached count counts as 0,
        // and a corrupt cached > input value is clamped to the final input total.
        var cachedInput = Math.Clamp(activityLogEntry.CachedInputTokens ?? 0, 0, actualInput);

        Add(target: activityLogEntry.Target, inputTokens: actualInput, outputTokens: actualOutput.Value, cachedInputTokens: cachedInput, elapsedMs: elapsedMs);
    }

    // Adds ONLY the elapsed time of a completed successful request without usage counters
    // (task #84); thread-safe like <see cref="Add"/>.
    private void AddElapsed(RoutingTarget target, double elapsedMs)
    {
        var current = GetToday()[target];
        Interlocked.Add(ref current.TotalElapsedMilliseconds, (long)elapsedMs);

        SaveThrottled();
    }

    // Increments today's counters; all counter reads/writes go through Interlocked, so this
    // is thread-safe even though multiple request-handling threads call it concurrently.
    private void Add(RoutingTarget target, int inputTokens, int outputTokens, int cachedInputTokens, double elapsedMs)
    {
        var current = GetToday()[target];
        Interlocked.Increment(ref current.TotalRequests);
        Interlocked.Add(ref current.TotalInputTokens, inputTokens);
        Interlocked.Add(ref current.TotalOutputTokens, outputTokens);
        Interlocked.Add(ref current.TotalCachedInputTokens, cachedInputTokens);
        Interlocked.Add(ref current.TotalElapsedMilliseconds, (long)elapsedMs);

        SaveThrottled();
    }

    // Lock-free throttled save: the first caller that wins the CompareExchange on lastSaveAt
    // triggers an async Save; every other caller within SaveInterval returns immediately.
    private void SaveThrottled()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref lastSaveAt);
        if (now - last < SaveInterval.TotalMilliseconds ||
            Interlocked.CompareExchange(ref lastSaveAt, now, last) != last)
        {
            return;
        }

        Task.Run(Save);
    }

    /// <inheritdoc/>
    public ActivityStatisticsSnapshot GetSnapshot()
    {
        lock (gate)
        {
            if (!isLoaded)
            {
                Load();
            }

            var todayDate = DateTime.Now.Date;
            var last7Days = all.Where(d => d.Date >= todayDate.AddDays(-6)).ToList();

            var last7ByTarget = AggregateByTarget(last7Days);
            var todayByTarget = AggregateByTarget(last7Days.Where(d => d.Date == todayDate));

            return new ActivityStatisticsSnapshot(
                Today: todayByTarget,
                TodayTotal: Total(todayByTarget),
                Last7Days: last7ByTarget,
                Last7DaysTotal: Total(last7ByTarget));
        }
    }

    // Sums the given day buckets into a per-target totals dictionary; every fleet target
    // appears in the result (zero-filled when a target never received traffic).
    private Dictionary<RoutingTarget, ActivityStatisticsTotals> AggregateByTarget(IEnumerable<ActivityStatisticsDate> days)
    {
        var totals = targets
            .ToDictionary(target => target, _ => (Requests: 0, InputTokens: 0, OutputTokens: 0, CachedInputTokens: 0, ElapsedMilliseconds: 0.0));

        foreach (var day in days)
        {
            foreach (var (target, entry) in day.Statistics)
            {
                if (!totals.TryGetValue(target, out var current))
                {
                    continue;
                }

                totals[target] = (
                    current.Requests + entry.TotalRequests,
                    current.InputTokens + entry.TotalInputTokens,
                    current.OutputTokens + entry.TotalOutputTokens,
                    current.CachedInputTokens + entry.TotalCachedInputTokens,
                    current.ElapsedMilliseconds + entry.TotalElapsedMilliseconds);
            }
        }

        return totals.ToDictionary(
            pair => pair.Key,
            pair => new ActivityStatisticsTotals(
                pair.Value.Requests,
                pair.Value.InputTokens,
                pair.Value.OutputTokens,
                pair.Value.CachedInputTokens,
                pair.Value.ElapsedMilliseconds));
    }

    // Combines a per-target dictionary into a single overall total.
    private static ActivityStatisticsTotals Total(IReadOnlyDictionary<RoutingTarget, ActivityStatisticsTotals> byTarget)
    {
        return new ActivityStatisticsTotals(
            byTarget.Values.Sum(t => t.Requests),
            byTarget.Values.Sum(t => t.InputTokens),
            byTarget.Values.Sum(t => t.OutputTokens),
            byTarget.Values.Sum(t => t.CachedInputTokens),
            byTarget.Values.Sum(t => t.ElapsedMilliseconds));
    }

    // Returns today's per-target counters, creating a new day bucket under gate when the
    // calendar day has rolled over. Double-checked locking keeps the fast path on the hot
    // request path lock-free. When a new bucket is added it is persisted asynchronously so the
    // day boundary survives a crash.
    private Dictionary<RoutingTarget, ActivityStatisticsEntry> GetToday()
    {
        var date = DateTime.Now.Date;
        if (today == null || today.Date != date)
        {
            lock (gate)
            {
                if (!isLoaded)
                {
                    Load();
                }
                if (today == null || today.Date != date)
                {
                    var statistics = new Dictionary<RoutingTarget, ActivityStatisticsEntry>();
                    foreach (var target in targets)
                    {
                        statistics[target] = new ActivityStatisticsEntry();
                    }

                    today = new ActivityStatisticsDate()
                    {
                        Date = date,
                        // A new day bucket is pre-populated with one entry per fleet target.
                        Statistics = statistics
                    };
                    all.Add(today);

                    Task.Run(Save);
                }
            }
        }
        return today.Statistics;
    }

    // Loads persisted day buckets once (must be called with gate held). A missing or corrupt
    // file is logged and treated as "no history".
    private void Load()
    {
        isLoaded = true;
        if (File.Exists(statsFile))
        {
            try
            {
                var json = File.ReadAllText(statsFile);
                var loaded = JsonSerializer.Deserialize(json, OllamaRouterJsonSerializerContext.Default.ListActivityStatisticsDate);
                if (loaded != null)
                {
                    all.AddRange(loaded);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error loading activity statistics");
            }
            today = all.LastOrDefault();
        }
    }

    /// <summary>
    /// Persists all day buckets to the stats file (creating the directory when necessary).
    /// Registered on application shutdown, fired on day rollover, and called periodically by
    /// <see cref="SaveThrottled"/>; I/O errors are logged, not thrown.
    /// </summary>
    public void Save()
    {
        lock (gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(statsFile);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(statsFile, JsonSerializer.Serialize(all, OllamaRouterJsonSerializerContext.Default.ListActivityStatisticsDate));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error saving activity statistics");
            }
        }
    }

}

/// <summary>
/// Raw counters for a single RoutingTarget within one day.
/// </summary>
public class ActivityStatisticsEntry
{
    // Counters are Interlocked-mutated public fields; JsonInclude makes them (de)serializable
    // without having to set IncludeFields on the serializer options.
    [JsonInclude]
    public int TotalInputTokens;
    [JsonInclude]
    public int TotalOutputTokens;
    [JsonInclude]
    public int TotalRequests;
    // Total cached prompt tokens across all requests for this target on this day (a subset of
    // TotalInputTokens; absent in legacy persisted files ⇒ defaults to 0 on load).
    [JsonInclude]
    public int TotalCachedInputTokens;
    // Cumulative GPU time (milliseconds) of completed SUCCESSFUL requests for this target on
    // this day (task #84; absent in legacy persisted files ⇒ defaults to 0 on load). Kept as
    // long so the counter stays lock-free (Interlocked.Add) and exact for millisecond values.
    [JsonInclude]
    public long TotalElapsedMilliseconds;
}

/// <summary>
/// One calendar day of statistics; new buckets are pre-populated by the service with one entry
/// per fleet target (configured targets + the Cloud pseudo-target).
/// </summary>
public class ActivityStatisticsDate
{
    public DateTime Date { get; set; }

    public Dictionary<RoutingTarget, ActivityStatisticsEntry> Statistics { get; set; } = new();
}
