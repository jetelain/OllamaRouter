namespace OllamaRouter.Services;

/// <summary>
/// Per-target failure streak counter feeding the failover path. A streak counts consecutive
/// UNCOMMITTED transport failures (the request could not even get response headers) and
/// MID-STREAM drops (the response started, then the stream was interrupted). Complete HTTP
/// error responses (headers flushed, daemon reachable) and client aborts never feed streaks.
/// <para>
/// Targets whose streak exceeds the configured threshold
/// (<see cref="OllamaRouterOptions.MaxConsecutiveFailures"/>) are skipped as routing
/// candidates until a completed success resets the streak; the liveness re-prober (10 s)
/// brings the target back into the fleet, and the next successful request clears the streak.
/// </para>
/// </summary>
public interface ITargetFailureStreakService
{
    /// <summary>
    /// Increments the target's failure streak (both uncommitted and mid-stream failures).
    /// </summary>
    void RecordFailure(RoutingTarget target);

    /// <summary>
    /// Resets the target's failure streak to zero (a completed, non-interrupted success).
    /// </summary>
    void RecordSuccess(RoutingTarget target);

    /// <summary>
    /// Returns the current failure streak of the target (0 when no failure was recorded yet).
    /// </summary>
    int GetStreak(RoutingTarget target);

    /// <summary>
    /// True when the target's streak is ABOVE <paramref name="maxStreak"/> (i.e.
    /// <c>GetStreak(target) &gt; maxStreak</c>) and the target should be skipped as a routing
    /// candidate. <c>maxStreak &lt;= 0</c> disables skipping entirely ("set to 0 to disable").
    /// </summary>
    bool ShouldSkip(RoutingTarget target, int maxStreak);

    /// <summary>
    /// Snapshot of all tracked streaks (for diagnostics / the monitor UI later).
    /// </summary>
    IReadOnlyDictionary<RoutingTarget, int> GetSnapshot();
}
