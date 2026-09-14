using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Handles routing logic and availability for the local Ollama instance.
/// </summary>
public sealed class LocalRoutingTargetHandler(
    IOptions<OllamaRouterOptions> options,
    IOllamaModelCatalogClient modelCatalogClient,
    IGpuVramProvider gpuVramProvider,
    IActivityMonitorService activityMonitor,
    ITargetAvailabilityService targetAvailability,
    ILogger<LocalRoutingTargetHandler>? logger = null) : IRoutingTargetHandler
{
    public RoutingTarget Target => RoutingTarget.Local;

    public bool IsOverflow => false;

    public bool IsEnabled => targetAvailability.IsEnabled(RoutingTarget.Local);

    public bool IsAvailable()
    {
        return IsEnabled && !activityMonitor.IsBusy(RoutingTarget.Local);
    }

    public async Task<bool> CanProcessAsync(RoutingContext context, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            logger?.LogDebug("{Model}: Local target is disabled.", context.NormalizedModelName);
            return false;
        }

        // Only models explicitly configured in the options dict are allowed to run locally.
        if (context.Thresholds is null)
        {
            logger?.LogDebug("{Model} is not configured for local routing.", context.NormalizedModelName);
            return false;
        }

        var existsLocally = await context.ExistsLocallyAsync(cancellationToken);
        if (!existsLocally)
        {
            logger?.LogDebug("{Model} is not available locally.", context.ModelName);
            return false;
        }

        var existsRemotely = await context.ExistsRemotelyAsync(cancellationToken);
        if (!existsRemotely)
        {
            logger?.LogDebug("{Model} is only available locally.", context.ModelName);
            return true;
        }

        if (context.TokenCount > context.Thresholds.MaxLocalTokens)
        {
            logger?.LogDebug("{Model}: tokenCount {TokenCount} exceeds max {Max}.", context.NormalizedModelName, context.TokenCount, context.Thresholds.MaxLocalTokens);
            return false;
        }

        var localUrl = options.Value.LocalUrl.TrimEnd('/');
        if (await modelCatalogClient.IsModelLoadedLocallyAsync(localUrl, context.ModelName, cancellationToken))
        {
            logger?.LogDebug("{Model} already loaded locally.", context.NormalizedModelName);
            return true;
        }

        var freeVramMB = gpuVramProvider.GetFreeVramMB();
        if (freeVramMB >= context.Thresholds.MinRequiredVramMB)
        {
            logger?.LogDebug("{Model}: freeVram {Free}MB, minRequired {Min}MB.", context.NormalizedModelName, freeVramMB, context.Thresholds.MinRequiredVramMB);
            return true;
        }

        logger?.LogDebug("{Model}: freeVram {Free}MB is below required {Min}MB.", context.NormalizedModelName, freeVramMB, context.Thresholds.MinRequiredVramMB);
        return false;
    }
}

