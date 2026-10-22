using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace OllamaRouter.Services;

/// <summary>
/// Caches the set of model names known to each Ollama instance (local/remote) for a short
/// duration, to avoid hammering /api/tags for every request that needs to know instance
/// availability (e.g. routing /api/show).
/// </summary>
public sealed class ModelCatalogCacheService(
    IOllamaModelCatalogClient catalogClient,
    IMemoryCache memoryCache) : IModelCatalogCacheService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OfflineCacheDuration = TimeSpan.FromSeconds(10);

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
        if (string.IsNullOrEmpty(baseUrl))
        {
            return Array.Empty<string>();
        }

        // Force-fetch (bypassing any existing cached entry) and overwrite the same
        // per-URL cache slot with the same expirations, so routing decisions that
        // consult ModelExistsAsync/HasAnyModelsAsync immediately see the refreshed names.
        var tags = await catalogClient.GetTagsAsync(baseUrl, cancellationToken);
        var names = ExtractModelNames(tags);

        memoryCache.Set(
            $"ModelCatalogCacheService:{baseUrl}",
            names,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = names.Count > 0 ? CacheDuration : OfflineCacheDuration });

        return names.ToList();
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
