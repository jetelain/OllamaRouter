using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

public sealed class RoutingDecisionService(
    IOptions<OllamaRouterOptions> options,
    IOllamaModelCatalogClient modelCatalogClient,
    IGpuVramProvider gpuVramProvider,
    ILogger<RoutingDecisionService> logger) : IRoutingDecisionService
{
    public async Task<RoutingTarget> DecideAsync(int tokenCount, string modelName, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var normalizedName = NormalizeModelName(modelName);

        // Only models explicitly configured in the options dict are allowed to run locally.
        if (settings.Models.TryGetValue(normalizedName, out var thresholds))
        {
            // Beyond the per-model token limit, there is no need to query anything else.
            if (tokenCount > thresholds.MaxLocalTokens)
            {
                logger.LogDebug("{Model}: tokenCount {TokenCount} exceeds max {Max}, routing to Remote.", normalizedName, tokenCount, thresholds.MaxLocalTokens);
                return RoutingTarget.Remote;
            }

            var localUrl = settings.LocalUrl.TrimEnd('/');

            // If the model is already loaded locally, no need to check the available VRAM.
            if (await modelCatalogClient.IsModelLoadedLocallyAsync(localUrl, modelName, cancellationToken))
            {
                logger.LogDebug("{Model} already loaded locally.", normalizedName);
                return RoutingTarget.Local;
            }

            var freeVramMB = gpuVramProvider.GetFreeVramMB();
            var target = freeVramMB >= thresholds.MinRequiredVramMB
                ? RoutingTarget.Local
                : RoutingTarget.Remote;
            logger.LogDebug("{Model}: freeVram {Free}MB, minRequired {Min}MB, routing to {Target}.",
                normalizedName, freeVramMB, thresholds.MinRequiredVramMB, target);
            return target;
        }

        logger.LogDebug("{Model} is not configured for local routing, routing to Remote.", normalizedName);
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
