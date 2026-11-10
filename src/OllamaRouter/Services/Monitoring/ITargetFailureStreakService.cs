namespace OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;

/// <summary>
/// Derived circuit-breaker state of a target's failure streak (task #94), evaluated on read
/// from the streak count, the last-failure timestamp and the configured cooldown window:
/// </summary>
public enum FailureStreakCircuitState
{
    /// <summary>
    /// The streak is within the threshold (or skipping is disabled): the target is fully routable.
    /// </summary>
    Closed = 0,

    /// <summary>
    /// The streak is above the threshold and the cooldown window has not elapsed yet: the target
    /// is skipped as a routing candidate.
    /// </summary>
    Open = 1,

    /// <summary>
    /// The streak is above the threshold and the cooldown window has elapsed: at most one
    /// in-flight trial request may reach the target (single-trial lease); a success closes the
    /// circuit, a failure re-arms the cooldown.
    /// </summary>
    HalfOpen = 2
}

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
/// <para>
/// Task #94 extends the counter into a circuit breaker: a tripped target stays skipped only
/// for <c>OllamaRouterOptions.FailureStreakCooldownSeconds</c> (circuit Open), after which it
/// is HalfOpen and a single trial request (single-trial lease) is allowed through — a success
/// closes the circuit, a failure re-arms the cooldown — so a failing target can no longer be
/// quarantined permanently.
/// </para>
/// </summary>
public interface ITargetFailureStreakService
{
    /// <summary>
    /// Increments the target's failure streak (both uncommitted and mid-stream failures),
    /// stamps the last-failure timestamp (re-arming the circuit-breaker cooldown) and clears
    /// any half-open trial lease (idempotent safety net).
    /// </summary>
    void RecordFailure(RoutingTarget target);

    /// <summary>
    /// Resets the target's failure streak to zero (a completed, non-interrupted success),
    /// clears the last-failure timestamp and releases any half-open trial lease (circuit
    /// Closed).
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
    /// Legacy overload: retains its exact pre-task-#94 semantics for source compatibility
    /// (no cooldown awareness, no lease awareness).
    /// </summary>
    bool ShouldSkip(RoutingTarget target, int maxStreak);

    /// <summary>
    /// Cooldown-aware gate (task #94): Open (tripped, cooldown not elapsed) ⇒ skip;
    /// HalfOpen (tripped, cooldown elapsed) ⇒ skip iff a trial lease is currently held;
    /// Closed ⇒ never skip. A <c>cooldown &lt;= TimeSpan.Zero</c> disables the Open window
    /// (a tripped target is immediately half-open).
    /// </summary>
    bool ShouldSkip(RoutingTarget target, int maxStreak, TimeSpan cooldown);

    /// <summary>
    /// Derived circuit state for the target under the given threshold and cooldown window.
    /// </summary>
    FailureStreakCircuitState GetCircuitState(RoutingTarget target, int maxStreak, TimeSpan cooldown);

    /// <summary>
    /// Atomically claims the single in-flight half-open trial slot. True only when the circuit
    /// is HalfOpen (streak above a positive threshold, cooldown elapsed) AND no trial is in
    /// flight; sets the lease on success. False when Closed, Open, or another trial is already
    /// in flight.
    /// </summary>
    bool TryReserveTrial(RoutingTarget target, int maxStreak, TimeSpan cooldown);

    /// <summary>
    /// Idempotently releases the in-flight trial slot (no-op when nothing is leased).
    /// </summary>
    void ReleaseTrial(RoutingTarget target);

    /// <summary>
    /// Resets the streak to 0 and clears the last-failure timestamp (no-op when the target is
    /// untracked). Used by the liveness rejoin reconciliation
    /// (<see cref="OllamaRouterOptions.ResetStreakOnLivenessRecovery"/>).
    /// </summary>
    void ResetStreak(RoutingTarget target);

    /// <summary>
    /// Timestamp of the last recorded failure, or null when none / already healed.
    /// </summary>
    DateTimeOffset? GetLastFailureUtc(RoutingTarget target);

    /// <summary>
    /// Snapshot of all tracked streaks (for diagnostics / the monitor UI later).
    /// </summary>
    IReadOnlyDictionary<RoutingTarget, int> GetSnapshot();
}
