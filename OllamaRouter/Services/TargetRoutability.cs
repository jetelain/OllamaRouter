using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Shared routable predicate for a single fleet target: admin-enabled, non-blank URL, and
/// online — or, during the 10 s startup grace window after the liveness prober started,
/// still unresolved (<see cref="TargetLivenessState.Unknown"/>), so a request arriving
/// before the boot probe completes is never bounced merely because probing is slow.
/// <para>
/// This is the canonical copy of the 10 s grace logic; it is also replicated verbatim in
/// <see cref="TargetExecutionTracker"/> (refresh fan-out) and <c>OllamaRoutingMiddleware</c>
/// (all-offline gate) — those copies are intentionally NOT refactored to call this helper to
/// keep the blast radius of the fleet pipeline rework minimal.
/// </para>
/// </summary>
internal static class TargetRoutability
{
    /// <summary>
    /// The 10 s startup grace window: identical to the middleware's
    /// <c>StartupGracePeriod</c> and the tracker's refresh-eligibility grace.
    /// </summary>
    internal static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Returns true when the given fleet target is currently routable per the shared
    /// predicate (enabled, non-blank URL, <c>IsOnline || (withinGrace &amp;&amp; Unknown)</c>).
    /// </summary>
    internal static bool IsRoutable(
        OllamaTargetOptions target,
        ITargetAvailabilityService availability,
        ITargetLivenessService liveness)
    {
        if (!target.Enabled || string.IsNullOrWhiteSpace(target.Url))
        {
            return false;
        }

        var routingTarget = new RoutingTarget(target.Name!);
        if (liveness.IsOnline(routingTarget))
        {
            return true;
        }

        return IsWithinStartupGrace(liveness) &&
               liveness.GetEntries().TryGetValue(routingTarget, out var entry) &&
               entry.State == TargetLivenessState.Unknown;
    }

    /// <summary>
    /// True while the liveness prober is within its startup grace window. A zero/MinValue
    /// <see cref="ITargetLivenessService.ServiceStartTimeUtc"/> (service never started) is
    /// NOT a grace window (mirrors the middleware's <c>IsWithinStartupGrace</c> exactly).
    /// </summary>
    internal static bool IsWithinStartupGrace(ITargetLivenessService liveness)
    {
        var start = liveness.ServiceStartTimeUtc;
        return start != DateTimeOffset.MinValue &&
               DateTimeOffset.UtcNow - start < StartupGracePeriod;
    }
}
