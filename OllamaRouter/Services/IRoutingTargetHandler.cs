namespace OllamaRouter.Services;

/// <summary>
/// Defines a target that can participate in request routing decisions.
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
    /// Determines whether the target is currently available (enabled and not busy).
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Determines whether this target is able to process/handle the request described by <paramref name="context"/>.
    /// Must return false if the target is disabled.
    /// </summary>
    Task<bool> CanProcessAsync(RoutingContext context, CancellationToken cancellationToken = default);
}

