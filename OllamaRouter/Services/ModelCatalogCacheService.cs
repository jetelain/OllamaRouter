using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace OllamaRouter.Services;

/// <summary>
/// Caches the set of model names known to each Ollama instance (local/remote) for a short
/// duration, to avoid hammering /api/tags for every request that needs to know instance
/// availability (e.g. routing /api/show).
/// <para>
/// Also tracks a per-instance digest (SHA-256 of the raw /api/tags body) so that the digest
/// keyed model capability cache (see <see cref="ModelCapabilityEngine"/>) can be invalidated
/// when the catalog of a single instance changes: on digest change only the capability cache
/// keys recorded for that instance are removed.
/// </para>
/// </summary>
public sealed class ModelCatalogCacheService(
    IOllamaModelCatalogClient catalogClient,
    IMemoryCache memoryCache,
    ILogger<ModelCatalogCacheService>? logger = null) : IModelCatalogCacheService
{
    internal const string CapabilityKeyPrefix = "ModelCapabilityEngine:";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OfflineCacheDuration = TimeSpan.FromSeconds(10);

    // Per-instance capability cache key registry: baseUrl → (digest, recorded capability keys).
    // On digest change the recorded keys of the previous digest are removed from the shared
    // memory cache so only the changed instance's capabilities are invalidated.
    private readonly ConcurrentDictionary<string, (string Digest, HashSet<string> Keys)> _capabilityKeys =
        new(StringComparer.Ordinal);

    public async Task<bool> ModelExistsAsync(string? baseUrl, string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(modelName))
        {
            return false;
        }

        var names = await GetModelNamesAsync(baseUrl, cancellationToken);

        return names.Contains(modelName);
    }

    public async Task<bool> HasAnyModelsAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(baseUrl))
        {
            return false;
        }

        var names = await GetModelNamesAsync(baseUrl, cancellationToken);

        return names.Count > 0;
    }

    public async Task<IReadOnlyList<string>> RefreshAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        var (names, _) = await RefreshWithDigestAsync(baseUrl, cancellationToken);
        return names;
    }

    public async Task<(IReadOnlyList<string> Names, string Digest)> RefreshWithDigestAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(baseUrl))
        {
            return ([], string.Empty);
        }

        var (tags, digest) = await catalogClient.GetTagsWithDigestAsync(baseUrl, cancellationToken);
        var names = ExtractModelNames(tags);
        var result = (names.ToList(), digest);
        RefreshDigestAsync(baseUrl, result);
        return result;
    }

    public Task<string> GetCurrentDigestAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(baseUrl))
        {
            return Task.FromResult(string.Empty);
        }

        // Serve the digest of the most recent refresh when one is known (non-empty);
        // otherwise (unknown / target was empty or unreachable) trigger a refresh.
        if (_capabilityKeys.TryGetValue(baseUrl, out var cached) && cached.Digest.Length > 0)
        {
            return Task.FromResult(cached.Digest);
        }

        return RefreshWithDigestAsync(baseUrl, cancellationToken).ContinueWith(
            t => t.Result.Digest,
            TaskScheduler.Default);
    }

    public string CurrentDigest(string? baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl))
        {
            return string.Empty;
        }

        return _capabilityKeys.TryGetValue(baseUrl, out var entry) ? entry.Digest : string.Empty;
    }

    /// <summary>
    /// Records capability cache keys owned by the capability engine for a given instance so
    /// that a digest change can invalidate them. Keys recorded under a digest different from
    /// the current one are discarded (the previous digest's keys were already removed).
    /// </summary>
    public void RegisterCapabilityCacheKeys(string baseUrl, IEnumerable<string> keys, string digest)
    {
        if (string.IsNullOrEmpty(baseUrl) || keys is null)
        {
            return;
        }

        _capabilityKeys.AddOrUpdate(
            baseUrl,
            _ => (digest, new HashSet<string>(keys, StringComparer.Ordinal)),
            (_, existing) => existing.Digest == digest
                ? (existing.Digest, Union(existing.Keys, keys))
                : (digest, new HashSet<string>(keys, StringComparer.Ordinal)));
    }

    private static HashSet<string> Union(HashSet<string> existing, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            existing.Add(key);
        }

        return existing;
    }

    private void RefreshDigestAsync(string baseUrl, (IReadOnlyList<string> Names, string Digest) result)
    {
        if (_capabilityKeys.TryGetValue(baseUrl, out var previous) &&
            previous.Digest.Length > 0 &&
            previous.Digest != result.Digest)
        {
            // Digest transition: only this instance's previously recorded capability keys
            // are removed; every other target's capability entries stay untouched.
            foreach (var key in previous.Keys)
            {
                memoryCache.Remove(key);
            }

            logger?.LogDebug(
                "Catalog digest for {BaseUrl} changed to {Digest}; removed {Count} stale capability cache keys.",
                baseUrl, result.Digest, previous.Keys.Count);
        }

        // Record the current digest with a cleared key set: only keys recorded under the
        // current digest will be removed on the next transition.
        _capabilityKeys[baseUrl] = (result.Digest, new HashSet<string>(StringComparer.Ordinal));

        // Force-repopulate the per-URL name cache slot (same expirations as before) so the
        // refreshed names are immediately visible to ModelExistsAsync/HasAnyModelsAsync.
        memoryCache.Set(
            $"ModelCatalogCacheService:{baseUrl}",
            new HashSet<string>(result.Names, StringComparer.OrdinalIgnoreCase),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = result.Names.Count > 0 ? CacheDuration : OfflineCacheDuration });
    }

    private Task<HashSet<string>> GetModelNamesAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var cacheKey = $"ModelCatalogCacheService:{baseUrl}";

        return memoryCache.GetOrCreateAsync(cacheKey, async entry =>
        {
            var names = ExtractModelNames(await catalogClient.GetTagsAsync(baseUrl, cancellationToken));
            entry.AbsoluteExpirationRelativeToNow = names.Count > 0 ? CacheDuration : OfflineCacheDuration;
            return names;
        })!;
    }

    private static HashSet<string> ExtractModelNames(JsonArray tags)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in tags)
        {
            if (model?["name"]?.ToString() is { Length: > 0 } name)
            {
                names.Add(name);
            }
        }

        return names;
    }
}
