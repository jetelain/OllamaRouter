using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Encapsulates request parameters and shared state (e.g. cached catalog lookups) for a single routing decision.
/// </summary>
public sealed class RoutingContext
{
    private readonly IModelCatalogCacheService _modelCatalogCache;
    private readonly ModelCapabilityEngine? _capabilityEngine;
    private readonly string _localUrl;
    private readonly string _remoteUrl;
    private readonly IReadOnlyDictionary<string, OllamaTargetOptions>? _targetByName;
    private Task<bool>? _existsLocallyTask;
    private Task<bool>? _existsRemotelyTask;
    private readonly Dictionary<RoutingTarget, Task<TargetModelCapability?>> _capabilityTasks = new();

    public int TokenCount { get; }
    public string ModelName { get; }
    public string NormalizedModelName { get; }
    public ModelThresholds? Thresholds { get; }

    public RoutingContext(
        int tokenCount,
        string modelName,
        string normalizedModelName,
        ModelThresholds? thresholds,
        IModelCatalogCacheService modelCatalogCache,
        string localUrl,
        string remoteUrl,
        CancellationToken cancellationToken = default,
        ModelCapabilityEngine? capabilityEngine = null,
        IReadOnlyList<OllamaTargetOptions>? targets = null)
    {
        TokenCount = tokenCount;
        ModelName = modelName;
        NormalizedModelName = normalizedModelName;
        Thresholds = thresholds;
        _modelCatalogCache = modelCatalogCache;
        _capabilityEngine = capabilityEngine;
        _localUrl = localUrl;
        _remoteUrl = remoteUrl;
        _targetByName = targets is { Count: > 0 }
            ? targets.Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .ToDictionary(t => t.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
            : null;

        // If the model is configured, eagerly start catalog checks concurrently.
        if (thresholds is not null)
        {
            _existsLocallyTask = modelCatalogCache.ModelExistsAsync(_localUrl, modelName, cancellationToken);
            _existsRemotelyTask = modelCatalogCache.ModelExistsAsync(_remoteUrl, modelName, cancellationToken);
        }
    }

    /// <summary>
    /// Returns whether the model exists in the local Ollama catalog.
    /// </summary>
    public Task<bool> ExistsLocallyAsync(CancellationToken cancellationToken = default)
    {
        return _existsLocallyTask ??= _modelCatalogCache.ModelExistsAsync(_localUrl, ModelName, cancellationToken);
    }

    /// <summary>
    /// Returns whether the model exists in the remote Ollama catalog.
    /// </summary>
    public Task<bool> ExistsRemotelyAsync(CancellationToken cancellationToken = default)
    {
        return _existsRemotelyTask ??= _modelCatalogCache.ModelExistsAsync(_remoteUrl, ModelName, cancellationToken);
    }

    /// <summary>
    /// Discovers (or returns the cached) per-target capability for this request's model:
    /// architecture classification plus the C_default/C_max context ceilings. Returns null
    /// when no capability engine is configured or the target has no discoverable model
    /// metadata (unknown URL, empty catalog digest, or unreachable target).
    /// </summary>
    public Task<TargetModelCapability?> GetTargetCapabilityAsync(RoutingTarget target, string targetUrl, CancellationToken cancellationToken = default)
    {
        if (_capabilityEngine is null || string.IsNullOrEmpty(targetUrl))
        {
            return Task.FromResult<TargetModelCapability?>(null);
        }

        if (!_capabilityTasks.TryGetValue(target, out var task))
        {
            task = ComputeTargetCapabilityAsync(target, targetUrl, cancellationToken);
            _capabilityTasks[target] = task;
        }

        return task;
    }

    private async Task<TargetModelCapability?> ComputeTargetCapabilityAsync(RoutingTarget target, string targetUrl, CancellationToken cancellationToken)
    {
        var engine = _capabilityEngine!;
        try
        {
            var digest = await _modelCatalogCache.GetCurrentDigestAsync(targetUrl, cancellationToken);
            if (string.IsNullOrEmpty(digest))
            {
                return null;
            }

            var metadata = await engine.GetMetadataAsync(targetUrl, ModelName, digest, cancellationToken);

            // Resolve the target's VRAM budget when known (unknown target ⇒ unknown budget).
            var targetOptions = _targetByName is { } byName && byName.TryGetValue(target.Value, out var resolved)
                ? resolved
                : new OllamaTargetOptions { Name = target.Value, Url = targetUrl };

            return engine.DeriveCapability(metadata, target.Value, targetOptions, Thresholds?.Architecture);
        }
        catch
        {
            return null;
        }
    }
}
