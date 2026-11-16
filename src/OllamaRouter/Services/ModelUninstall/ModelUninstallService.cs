using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Endpoints.RouterApi.Shared;
using OllamaRouter.Options;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Services.ModelUninstall;

/// <summary>
/// Default <see cref="IModelUninstallService"/> implementation (a registered singleton; the
/// endpoint resolves it defensively so minimal test hosts keep working). Validation is ALL
/// fleet-state, BEFORE any daemon call: (1) the fleet-wide model name check via the shared
/// <see cref="RouterApiSharedHelpers.GetFleetModelNamesAsync"/> enumeration (unknown →
/// "model not found: {model}"), (2) the effective-target roster check per requested name
/// (case-insensitive; first unknown → "target not found: {name}", ZERO daemon calls).
/// Execution is ONE fail-soft <c>DELETE {target}/api/delete</c> per selected target IN REQUEST
/// ORDER (case-insensitive duplicate names collapse onto the same canonical target); a
/// daemon 404 ("model not found") or an unreachable daemon surfaces as that target's row
/// only — the remaining targets are always processed.
/// </summary>
public sealed class ModelUninstallService : IModelUninstallService
{
    private readonly IOptionsMonitor<OllamaRouterOptions> _options;
    private readonly IOllamaModelCatalogClient _catalogClient;
    private readonly IModelCatalogCacheService? _catalogCache;
    private readonly IFleetAggregationService? _fleetAggregation;
    private readonly ITargetAvailabilityService? _availability;
    private readonly ILogger<ModelUninstallService> _logger;

    public ModelUninstallService(
        IOptionsMonitor<OllamaRouterOptions> options,
        IOllamaModelCatalogClient catalogClient,
        IModelCatalogCacheService? catalogCache,
        IFleetAggregationService? fleetAggregation,
        ITargetAvailabilityService? availability,
        ILogger<ModelUninstallService> logger)
    {
        _options = options;
        _catalogClient = catalogClient;
        _catalogCache = catalogCache;
        _fleetAggregation = fleetAggregation;
        _availability = availability;
        _logger = logger;
    }

    public async Task<ModelUninstallResult?> UninstallAsync(
        string model, IReadOnlyList<string> targetNames, CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        var effectiveTargets = options.GetEffectiveTargets();

        // ---- 1. Fleet-level model check (shared enumeration; the service has no HttpContext) ----
        var names = await RouterApiSharedHelpers.GetFleetModelNamesAsync(
            options, _availability, _fleetAggregation, _catalogCache, context: null, cancellationToken).ConfigureAwait(false);
        if (RouterApiSharedHelpers.ResolveModelName(model, names) is null)
        {
            _logger.LogInformation("Uninstall of '{Model}' rejected: model not found on the fleet", model);
            return new ModelUninstallResult(model, [], Error: $"model not found: {model}");
        }

        // ---- 2. Target-name check (ALL names validated before ANY daemon call) ----
        var selected = new List<(OllamaTargetOptions Target, string RequestedName)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in targetNames)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                continue;
            }

            // Case-insensitive duplicates collapse (the canonical target is echoed once).
            if (!seen.Add(requested))
            {
                continue;
            }

            var match = effectiveTargets.FirstOrDefault(t =>
                string.Equals(t.Name, requested, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _logger.LogInformation("Uninstall of '{Model}' rejected: unknown target '{Target}'", model, requested);
                return new ModelUninstallResult(model, [], Error: $"target not found: {requested}");
            }

            selected.Add((match, requested));
        }

        // ---- 3. Execution: ONE fail-soft DELETE /api/delete per selected target, request order ----
        var rows = new List<ModelUninstallTargetOutcome>(selected.Count);
        foreach (var (target, _) in selected)
        {
            var outcome = await _catalogClient.DeleteModelAsync(target.Url, model, cancellationToken).ConfigureAwait(false);
            rows.Add(new ModelUninstallTargetOutcome(target.Name!, outcome.Deleted, outcome.Error));
            if (!outcome.Deleted)
            {
                _logger.LogWarning("Uninstall of '{Model}' on '{Target}' failed: {Error}", model, target.Name, outcome.Error);
            }
        }

        _logger.LogInformation("Uninstall of '{Model}' finished: {Succeeded}/{Total} targets deleted",
            model, rows.Count(r => r.Success), rows.Count);
        return new ModelUninstallResult(model, rows);
    }
}
