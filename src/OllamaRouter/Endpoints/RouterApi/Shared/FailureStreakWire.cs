using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Endpoints.RouterApi.Shared;

/// <summary>
/// The shared failure-streak wire/read-model helper (task #95): maps the task #94
/// <see cref="ITargetFailureStreakService"/> circuit-breaker state onto the JSON wire values
/// (<c>failureStreak</c> / <c>circuitState</c> / <c>cooldownRemainingSeconds</c>) used by the
/// fleet, cartography and monitor builders — a single source of truth so the three surfaces
/// can never drift.
/// <para>
/// Wire vocabulary: <c>Closed → "Closed"</c>, <c>Open → "Cooldown"</c> (tripped + cooldown
/// window active — the breaker's Open state, expressed in operator terms) and
/// <c>HalfOpen → "HalfOpen"</c>. <c>cooldownRemainingSeconds</c> is present (non-null) ONLY
/// while in "Cooldown"; "Closed" and "HalfOpen" rows omit it.
/// </para>
/// </summary>
internal static class FailureStreakWire
{
    /// <summary>
    /// Maps the circuit-breaker enum onto the wire string
    /// ("Closed"/"Cooldown"/"HalfOpen" — "Cooldown" = the breaker's Open state).
    /// </summary>
    internal static string ToStateString(FailureStreakCircuitState state) => state switch
    {
        FailureStreakCircuitState.Open => "Cooldown",
        FailureStreakCircuitState.HalfOpen => "HalfOpen",
        _ => "Closed"
    };

    /// <summary>
    /// Composes the failure-streak read model of one target from the live (singleton) streak
    /// service: <c>Streak</c> = the live count (0 when untracked / service absent), <c>State</c>
    /// = the wire string under the configured threshold/cooldown ("Closed" when the service is
    /// absent) and <c>CooldownRemainingSeconds</c> = the remaining cooldown window (non-null
    /// ONLY while in "Cooldown", clamped to a minimum of 0.0, from the service's last-failure
    /// timestamp against the wall clock).
    /// </summary>
    internal static (int Streak, string State, double? CooldownRemainingSeconds) Describe(
        ITargetFailureStreakService? streakService,
        RoutingTarget target,
        int maxConsecutiveFailures,
        int cooldownSeconds)
    {
        if (streakService is null)
        {
            return (0, "Closed", null);
        }

        var streak = streakService.GetStreak(target);
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, cooldownSeconds));
        var state = ToStateString(streakService.GetCircuitState(target, maxConsecutiveFailures, cooldown));

        double? cooldownRemainingSeconds = null;
        if (state == "Cooldown" && streakService.GetLastFailureUtc(target) is { } lastFailureUtc)
        {
            cooldownRemainingSeconds = Math.Max(0.0, (cooldown - (DateTimeOffset.UtcNow - lastFailureUtc)).TotalSeconds);
        }

        return (streak, state, cooldownRemainingSeconds);
    }
}
