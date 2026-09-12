namespace OllamaRouter.Services;

public interface IActivityStatisticsService
{
    /// <summary>
    /// Records the token usage of a request log entry toward the daily statistics.
    /// </summary>
    void Add(ActivityLogEntry activityLogEntry);

    /// <summary>
    /// Returns a snapshot of the aggregated token usage: per target and overall total, for the
    /// current day and for the last 7 days (today plus the 6 previous days). Statistics are
    /// persisted to disk, so they survive process restarts.
    /// </summary>
    ActivityStatisticsSnapshot GetSnapshot();
}

/// <summary>
/// Aggregated usage for a routing target (or for all targets combined, when used as a total).
/// </summary>
public sealed record ActivityStatisticsTotals(int Requests, int InputTokens, int OutputTokens);

/// <summary>
/// Read-only snapshot of the persisted activity statistics.
/// </summary>
public sealed record ActivityStatisticsSnapshot(
    IReadOnlyDictionary<RoutingTarget, ActivityStatisticsTotals> Today,
    ActivityStatisticsTotals TodayTotal,
    IReadOnlyDictionary<RoutingTarget, ActivityStatisticsTotals> Last7Days,
    ActivityStatisticsTotals Last7DaysTotal);
