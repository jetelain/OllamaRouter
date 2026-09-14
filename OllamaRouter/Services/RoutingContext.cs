using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Encapsulates request parameters and shared state (e.g. cached catalog lookups) for a single routing decision.
/// </summary>
public sealed class RoutingContext
{
    private readonly IModelCatalogCacheService _modelCatalogCache;
    private readonly string _localUrl;
    private readonly string _remoteUrl;
    private Task<bool>? _existsLocallyTask;
    private Task<bool>? _existsRemotelyTask;

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
        CancellationToken cancellationToken = default)
    {
        TokenCount = tokenCount;
        ModelName = modelName;
        NormalizedModelName = normalizedModelName;
        Thresholds = thresholds;
        _modelCatalogCache = modelCatalogCache;
        _localUrl = localUrl;
        _remoteUrl = remoteUrl;

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
}

