using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

public sealed class RoutingDecisionService(
    IOptions<OllamaRouterOptions> options,
    IOllamaModelCatalogClient modelCatalogClient,
    IModelCatalogCacheService modelCatalogCacheService,
    IGpuVramProvider gpuVramProvider,
    IActivityMonitorService activityMonitor,
    ILogger<RoutingDecisionService> logger) : IRoutingDecisionService
{
    public async Task<RoutingTarget> DecideAsync(int tokenCount, string modelName, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var normalizedName = NormalizeModelName(modelName);

        // Only models explicitly configured in the options dict are allowed to run locally.
        if (settings.Models.TryGetValue(normalizedName, out var thresholds))
        {
            if (!string.IsNullOrEmpty(thresholds.CloudModel) &&
                activityMonitor.IsBusy(RoutingTarget.Local) &&
                activityMonitor.IsBusy(RoutingTarget.Remote))
            {
                logger.LogDebug("{Model}: Local and Remote are both busy, overflowing to Cloud ({CloudModel}).", normalizedName, thresholds.CloudModel);
                return RoutingTarget.Cloud;
            }

            var localUrl = settings.LocalUrl.TrimEnd('/');
            var remoteUrl = settings.RemoteUrl.TrimEnd('/');

            // Since the configuration key is tag-agnostic, the requested tag may not actually be
            // available on either (or both) targets. Check availability before deciding.
            var existsLocallyTask = modelCatalogCacheService.ModelExistsAsync(localUrl, modelName, cancellationToken);
            var existsRemotelyTask = modelCatalogCacheService.ModelExistsAsync(remoteUrl, modelName, cancellationToken);
            await Task.WhenAll(existsLocallyTask, existsRemotelyTask);
            var existsLocally = await existsLocallyTask;
            var existsRemotely = await existsRemotelyTask;

            if (existsLocally && !existsRemotely)
            {
                logger.LogDebug("{Model} is only available locally, routing to Local.", modelName);
                return RoutingTarget.Local;
            }

            if (!existsLocally && existsRemotely)
            {
                logger.LogDebug("{Model} is only available remotely, routing to Remote.", modelName);
                return RemoteOrCloudOverflow(normalizedName, thresholds.CloudModel, "the model is not available locally");
            }

            if (!existsLocally && !existsRemotely)
            {
                logger.LogDebug("{Model} is not available on either target, routing to Remote.", modelName);
                return RemoteOrCloudOverflow(normalizedName, thresholds.CloudModel, "the model is not available on either target");
            }

            // Beyond the per-model token limit, there is no need to query anything else.
            if (tokenCount > thresholds.MaxLocalTokens)
            {
                logger.LogDebug("{Model}: tokenCount {TokenCount} exceeds max {Max}, routing to Remote.", normalizedName, tokenCount, thresholds.MaxLocalTokens);
                return RemoteOrCloudOverflow(normalizedName, thresholds.CloudModel, $"tokenCount {tokenCount} exceeds max {thresholds.MaxLocalTokens}");
            }

            // If the model is already loaded locally, no need to check the available VRAM.
            if (await modelCatalogClient.IsModelLoadedLocallyAsync(localUrl, modelName, cancellationToken))
            {
                logger.LogDebug("{Model} already loaded locally.", normalizedName);
                return RoutingTarget.Local;
            }

            var freeVramMB = gpuVramProvider.GetFreeVramMB();
            if (freeVramMB >= thresholds.MinRequiredVramMB)
            {
                logger.LogDebug("{Model}: freeVram {Free}MB, minRequired {Min}MB, routing to Local.", normalizedName, freeVramMB, thresholds.MinRequiredVramMB);
                return RoutingTarget.Local;
            }

            logger.LogDebug("{Model}: freeVram {Free}MB, minRequired {Min}MB, routing to Remote.", normalizedName, freeVramMB, thresholds.MinRequiredVramMB);
            return RemoteOrCloudOverflow(normalizedName, thresholds.CloudModel, $"freeVram {freeVramMB}MB is below required {thresholds.MinRequiredVramMB}MB");
        }

        logger.LogDebug("{Model} is not configured for local routing, routing to Remote.", normalizedName);
        return RoutingTarget.Remote;
    }

    /// <summary>
    /// The Local instance cannot (or should not) handle the request. Routes to Remote, unless
    /// Remote is currently busy and a cloud equivalent is configured for the model, in which case
    /// overflows to Cloud instead of queueing behind the busy Remote instance.
    /// </summary>
    private RoutingTarget RemoteOrCloudOverflow(string normalizedName, string? cloudModel, string reason)
    {
        if (!string.IsNullOrEmpty(cloudModel) && activityMonitor.IsBusy(RoutingTarget.Remote))
        {
            logger.LogDebug("{Model}: Local cannot handle the request ({Reason}) and Remote is busy, overflowing to Cloud ({CloudModel}).", normalizedName, reason, cloudModel);
            return RoutingTarget.Cloud;
        }

        return RoutingTarget.Remote;
    }

    /// <summary>
    /// Strips the tag suffix from a full Ollama model name (e.g. "qwen3:8b" → "qwen3",
    /// "Qwen3.8-27B:latest" → "Qwen3.8-27B"), matching the convention that configuration keys
    /// are written without tag — the .NET configuration binder does not accept ":" in keys.
    /// The full name is preserved for downstream catalog queries that need the tag.
    /// </summary>
    public static string NormalizeModelName(string modelName)
    {
        if (string.IsNullOrEmpty(modelName))
            return modelName;

        var idx = modelName.IndexOf(':');
        return idx > 0 ? modelName[..idx] : modelName;
    }
}
