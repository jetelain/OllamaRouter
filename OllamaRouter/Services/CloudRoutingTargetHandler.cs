using Microsoft.Extensions.Logging;

namespace OllamaRouter.Services;

/// <summary>
/// Handles overflow routing logic and availability for the ollama.com cloud target.
/// </summary>
public sealed class CloudRoutingTargetHandler(
    IActivityMonitorService activityMonitor,
    ITargetAvailabilityService targetAvailability,
    ILogger<CloudRoutingTargetHandler>? logger = null) : IRoutingTargetHandler
{
    public RoutingTarget Target => RoutingTarget.Cloud;

    public bool IsOverflow => true;

    public bool IsEnabled => targetAvailability.IsEnabled(RoutingTarget.Cloud);

    public bool IsAvailable()
    {
        return IsEnabled && !activityMonitor.IsBusy(RoutingTarget.Cloud);
    }

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

