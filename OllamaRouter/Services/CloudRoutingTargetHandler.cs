namespace OllamaRouter.Services;

/// <summary>
/// Handles overflow routing logic and availability for the ollama.com cloud target.
/// </summary>
public sealed class CloudRoutingTargetHandler(
    ITargetAvailabilityService targetAvailability,
    ILogger<CloudRoutingTargetHandler>? logger = null) : IRoutingTargetHandler
{
    public RoutingTarget Target => RoutingTarget.Cloud;

    public bool IsOverflow => true;

    public bool IsEnabled => targetAvailability.IsEnabled(RoutingTarget.Cloud);

    /// <summary>
    /// Determines whether the cloud target is currently available.
    /// Since Cloud serves as an overflow target that handles concurrent requests without
    /// single-GPU concurrency constraints, it is always available as long as it is enabled.
    /// </summary>
    public bool IsAvailable() =>  IsEnabled;

    public Task<bool> CanProcessAsync(RoutingContext context, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            logger?.LogDebug("{Model}: Cloud target is disabled.", context.NormalizedModelName);
            return Task.FromResult(false);
        }

        var hasCloudModel = !string.IsNullOrEmpty(context.Thresholds?.CloudModel);
        if (!hasCloudModel)
        {
            logger?.LogDebug("{Model}: No CloudModel configured for overflow.", context.NormalizedModelName);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }
}

