using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

public sealed class RoutingDecisionService : IRoutingDecisionService
{
    private readonly List<IRoutingTargetHandler> _targets;
    private readonly IOptions<OllamaRouterOptions> _options;
    private readonly IModelCatalogCacheService _modelCatalogCacheService;
    private readonly IActivityMonitorService? _activityMonitor;
    private readonly ModelCapabilityEngine? _capabilityEngine;
    private readonly ILogger<RoutingDecisionService> _logger;
    private readonly ITargetFailureStreakService _streakTracker;

    public RoutingDecisionService(
        IEnumerable<IRoutingTargetHandler> targets,
        IOptions<OllamaRouterOptions> options,
        IModelCatalogCacheService modelCatalogCacheService,
        ILogger<RoutingDecisionService> logger)
        : this(targets, options, modelCatalogCacheService, null, logger, null, null)
    {
    }

    public RoutingDecisionService(
        IEnumerable<IRoutingTargetHandler> targets,
        IOptions<OllamaRouterOptions> options,
        IModelCatalogCacheService modelCatalogCacheService,
        IActivityMonitorService? activityMonitor,
        ILogger<RoutingDecisionService> logger)
        : this(targets, options, modelCatalogCacheService, activityMonitor, logger, null, null)
    {
    }

    public RoutingDecisionService(
        IEnumerable<IRoutingTargetHandler> targets,
        IOptions<OllamaRouterOptions> options,
        IModelCatalogCacheService modelCatalogCacheService,
        IActivityMonitorService? activityMonitor,
        ILogger<RoutingDecisionService> logger,
        ModelCapabilityEngine? capabilityEngine,
        ITargetFailureStreakService? streakTracker = null)
    {
        _targets = targets.ToList();
        _options = options;
        _modelCatalogCacheService = modelCatalogCacheService;
        _activityMonitor = activityMonitor;
        _capabilityEngine = capabilityEngine;
        _logger = logger;
        // A per-instance default keeps the delegating constructors and existing tests
        // source-compatible; Program.cs injects the shared singleton via the factory
        // registration so all decision requests share one streak table.
        _streakTracker = streakTracker ?? new TargetFailureStreakService();
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
            cancellationToken,
            _capabilityEngine,
            settings.GetEffectiveTargets(_logger));

        var primaryTarget = await TrySelectTargetAsync(context, _targets, cancellationToken);
        if (primaryTarget is null)
        {
            _logger.LogWarning("{Model}: No enabled target can handle the request.", normalizedName);
            throw new NoAvailableTargetException($"No enabled routing target is available to handle the request for model '{modelName}'.");
        }

        var maxFailures = settings.MaxConsecutiveFailures;
        if (maxFailures > 0 && _streakTracker.ShouldSkip(primaryTarget.Target, maxFailures))
        {
            var primaryIndex = _targets.IndexOf(primaryTarget);
            if (primaryIndex >= 0 && primaryIndex < _targets.Count - 1)
            {
                // The decided target is above the failure-streak threshold: walk the remaining
                // handlers in configuration order (after the primary's index), skipping any that
                // are also above threshold, and reuse the standard selection over the survivors.
                var subsequentCandidates = _targets
                    .Skip(primaryIndex + 1)
                    .Where(t => !_streakTracker.ShouldSkip(t.Target, maxFailures));

                var nextTarget = await TrySelectTargetAsync(context, subsequentCandidates, cancellationToken);
                if (nextTarget is not null)
                {
                    _logger.LogInformation(
                        "{Model}: Target {FailedTarget} has a failure streak of {Streak} above threshold {MaxFailures}. Switching to next target {NewTarget}.",
                        normalizedName, primaryTarget.Target, _streakTracker.GetStreak(primaryTarget.Target), maxFailures, nextTarget.Target);
                    return nextTarget.Target;
                }

                _logger.LogWarning(
                    "{Model}: Target {FailedTarget} has a failure streak of {Streak} above threshold {MaxFailures}, but no subsequent target is enabled or can process the request. Keeping {FailedTarget}.",
                    normalizedName, primaryTarget.Target, _streakTracker.GetStreak(primaryTarget.Target), maxFailures, primaryTarget.Target);
            }
        }

        _logger.LogDebug("{Model}: Routing to target {Target}.", normalizedName, primaryTarget.Target);
        return primaryTarget.Target;
    }

    private static async Task<IRoutingTargetHandler?> TrySelectTargetAsync(
        RoutingContext context,
        IEnumerable<IRoutingTargetHandler> candidates,
        CancellationToken cancellationToken)
    {
        var list = candidates.ToList();

        // 1. Dispatch to the first target that is available and able to process the request.
        foreach (var target in list)
        {
            if (target.IsAvailable() && await target.CanProcessAsync(context, cancellationToken))
            {
                return target;
            }
        }

        // 2. If no target is available, fallback to the first enabled target that is able to handle the request.
        // Non-overflow targets are evaluated first so that busy primary instances take precedence over overflow.
        foreach (var target in list.Where(t => t.IsEnabled && !t.IsOverflow))
        {
            if (await target.CanProcessAsync(context, cancellationToken))
            {
                return target;
            }
        }

        foreach (var target in list.Where(t => t.IsEnabled && t.IsOverflow))
        {
            if (await target.CanProcessAsync(context, cancellationToken))
            {
                return target;
            }
        }

        return null;
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
