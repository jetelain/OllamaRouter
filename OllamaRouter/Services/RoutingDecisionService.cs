using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

public sealed class RoutingDecisionService : IRoutingDecisionService
{
    private readonly IReadOnlyList<IRoutingTargetHandler> _targets;
    private readonly IOptions<OllamaRouterOptions> _options;
    private readonly IModelCatalogCacheService _modelCatalogCacheService;
    private readonly ILogger<RoutingDecisionService> _logger;

    public RoutingDecisionService(
        IEnumerable<IRoutingTargetHandler> targets,
        IOptions<OllamaRouterOptions> options,
        IModelCatalogCacheService modelCatalogCacheService,
        ILogger<RoutingDecisionService> logger)
    {
        _targets = targets.ToList();
        _options = options;
        _modelCatalogCacheService = modelCatalogCacheService;
        _logger = logger;
    }

    public async Task<RoutingTarget> DecideAsync(int tokenCount, string modelName, CancellationToken cancellationToken = default)
    {
        var settings = _options.Value;
        var normalizedName = NormalizeModelName(modelName);
        settings.Models.TryGetValue(normalizedName, out var thresholds);

        var context = new RoutingContext(
            tokenCount,
            modelName,
            normalizedName,
            thresholds,
            _modelCatalogCacheService,
            settings.LocalUrl.TrimEnd('/'),
            settings.RemoteUrl.TrimEnd('/'),
            cancellationToken);

        // 1. Dispatch to the first target that is available and able to process the request.
        foreach (var target in _targets)
        {
            if (target.IsAvailable() && await target.CanProcessAsync(context, cancellationToken))
            {
                _logger.LogDebug("{Model}: Routing to available target {Target}.", normalizedName, target.Target);
                return target.Target;
            }
        }

        // 2. If no target is available, fallback to the first enabled target that is able to handle the request.
        // Non-overflow targets are evaluated first so that busy primary instances take precedence over overflow.
        foreach (var target in _targets.Where(t => t.IsEnabled && !t.IsOverflow))
        {
            if (await target.CanProcessAsync(context, cancellationToken))
            {
                _logger.LogDebug("{Model}: No target available, falling back to {Target}.", normalizedName, target.Target);
                return target.Target;
            }
        }

        foreach (var target in _targets.Where(t => t.IsEnabled && t.IsOverflow))
        {
            if (await target.CanProcessAsync(context, cancellationToken))
            {
                _logger.LogDebug("{Model}: No non-overflow target available, falling back to overflow target {Target}.", normalizedName, target.Target);
                return target.Target;
            }
        }

        _logger.LogWarning("{Model}: No enabled target can handle the request.", normalizedName);
        throw new NoAvailableTargetException($"No enabled routing target is available to handle the request for model '{modelName}'.");
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
