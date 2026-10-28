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
    private readonly ITargetExecutionTracker? _executionTracker;
    private readonly Dictionary<string, Task<ModelMetadata?>> _metadataTasks = new(StringComparer.OrdinalIgnoreCase);
    private Task<bool>? _existsLocallyTask;
    private Task<bool>? _existsRemotelyTask;
    private readonly Dictionary<RoutingTarget, Task<TargetModelCapability?>> _capabilityTasks = new();

    public int TokenCount { get; }
    public string ModelName { get; }
    public string NormalizedModelName { get; }
    public ModelThresholds? Thresholds { get; }

    /// <summary>
    /// The client's static <c>options.num_ctx</c> from the request body (an upper-bound
    /// preference, never a disqualifier), used by the routing pipeline's context-fit clamp.
    /// </summary>
    public int? ClientRequestedNumCtx { get; }

    /// <summary>
    /// The opaque session/agent identifier carried by the request's
    /// <c>X-OllamaRouter-Session-Id</c> / <c>X-OllamaRouter-Agent-Id</c> header (null when
    /// absent): used by the routing pipeline's Stage-2 SessionAffinityBonus (+150) to keep
    /// a multi-agent caller's consecutive turns on the same target (KV-cache stickiness).
    /// </summary>
    public string? SessionId { get; }

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
        IReadOnlyList<OllamaTargetOptions>? targets = null,
        ITargetExecutionTracker? executionTracker = null,
        int? clientRequestedNumCtx = null,
        string? sessionId = null)
    {
        TokenCount = tokenCount;
        ModelName = modelName;
        NormalizedModelName = normalizedModelName;
        Thresholds = thresholds;
        ClientRequestedNumCtx = clientRequestedNumCtx;
        SessionId = sessionId;
        _modelCatalogCache = modelCatalogCache;
        _capabilityEngine = capabilityEngine;
        _executionTracker = executionTracker;
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
    /// Synchronous, non-blocking snapshot of the candidate target's execution state from the
    /// execution tracker (loaded models, in-flight count, VRAM totals). Returns null for the
    /// Cloud pseudo-target or when no tracker is wired (unit-test pipelines); the routing
    /// pipeline treats a null snapshot as "never observed" (empty models, 0 in-flight, stale).
    /// </summary>
    public TargetExecutionState? GetExecutionState(RoutingTarget target)
    {
        if (_executionTracker is null || target == RoutingTarget.Cloud)
        {
            return null;
        }

        return _executionTracker.GetState(target);
    }

    /// <summary>
    /// Returns the configured <see cref="OllamaTargetOptions"/> for the given fleet target
    /// (case-insensitive name match), or null for unknown targets and the Cloud pseudo-target.
    /// </summary>
    public OllamaTargetOptions? GetTargetOptions(RoutingTarget target)
    {
        if (target == RoutingTarget.Cloud || _targetByName is null)
        {
            return null;
        }

        return _targetByName.TryGetValue(target.Value, out var resolved) ? resolved : null;
    }

    /// <summary>
    /// Returns the cached (per-context) <see cref="ModelMetadata"/> for this request's model on
    /// the given target URL, starting the fetch lazily (single-flight per URL). Returns null
    /// when no capability engine is wired, the URL is blank, or no catalog digest is
    /// discoverable — the degraded "unknown" metadata case of the permissive Stage-1 fallback.
    /// </summary>
    public Task<ModelMetadata?> GetModelMetadataAsync(string targetUrl, CancellationToken cancellationToken = default)
    {
        if (_capabilityEngine is null || string.IsNullOrEmpty(targetUrl))
        {
            return Task.FromResult<ModelMetadata?>(null);
        }

        if (!_metadataTasks.TryGetValue(targetUrl, out var task))
        {
            task = ComputeMetadataAsync(targetUrl, cancellationToken);
            _metadataTasks[targetUrl] = task;
        }

        return task;
    }

    private async Task<ModelMetadata?> ComputeMetadataAsync(string targetUrl, CancellationToken cancellationToken)
    {
        try
        {
            var digest = await _modelCatalogCache.GetCurrentDigestAsync(targetUrl, cancellationToken);
            if (string.IsNullOrEmpty(digest))
            {
                return null;
            }

            return await _capabilityEngine!.GetMetadataAsync(targetUrl, ModelName, digest, cancellationToken);
        }
        catch
        {
            return null;
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
