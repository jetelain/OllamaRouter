using Microsoft.Extensions.Logging;

namespace OllamaRouter.Services;

/// <summary>
/// Handles routing logic and availability for the remote Ollama instance.
/// </summary>
public sealed class RemoteRoutingTargetHandler(
    IActivityMonitorService activityMonitor,
    ITargetAvailabilityService targetAvailability,
    ILogger<RemoteRoutingTargetHandler>? logger = null) : IRoutingTargetHandler
{
    public RoutingTarget Target => RoutingTarget.Remote;

    public bool IsOverflow => false;

    public bool IsEnabled => targetAvailability.IsEnabled(RoutingTarget.Remote);

    public bool IsAvailable()
    {
        return IsEnabled && !activityMonitor.IsBusy(RoutingTarget.Remote);
    }

    public async Task<bool> CanProcessAsync(RoutingContext context, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            logger?.LogDebug("{Model}: Remote target is disabled.", context.ModelName);
            return false;
        }

        // Models without explicit local configuration are always eligible for remote processing.
        if (context.Thresholds is null)
        {
            return true;
        }

        var existsLocally = await context.ExistsLocallyAsync(cancellationToken);
        var existsRemotely = await context.ExistsRemotelyAsync(cancellationToken);

        // If the model is exclusively available locally, Remote cannot process it.
        if (existsLocally && !existsRemotely)
        {
            logger?.LogDebug("{Model} is only available locally, cannot route to Remote.", context.ModelName);
            return false;
        }

        return true;
    }
}

