namespace OllamaRouter.Services;

/// <summary>
/// Defines a routing target that can participate in request routing decisions.
/// <para>
/// Reserved for terminal fallback targets only: since the fleet-wide two-stage scoring
/// pipeline (Phase 3, item 3), fleet targets are evaluated directly by the
/// <see cref="RoutingDecisionService"/> over the configured <c>Targets</c> list, and the only
/// remaining implementation is the Cloud pseudo-target (<c>CloudRoutingTargetHandler</c>),
/// which the pipeline treats as a fixed score-0 candidate appended after all fleet targets.
/// </para>
/// </summary>
public interface IRoutingTargetHandler
{
    /// <summary>
    /// The target destination (e.g. Local, Remote, Cloud).
    /// </summary>
    RoutingTarget Target { get; }

    /// <summary>
    /// Indicates whether this target serves as an overflow target (e.g. Cloud).
    /// </summary>
    bool IsOverflow { get; }

    /// <summary>
    /// Indicates whether this target is currently enabled. A disabled target must never handle requests.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Determines whether the target is currently available (enabled).
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Determines whether this target is able to process/handle the request described by <paramref name="context"/>.
    /// Must return false if the target is disabled.
    /// </summary>
    Task<bool> CanProcessAsync(RoutingContext context, CancellationToken cancellationToken = default);
}
