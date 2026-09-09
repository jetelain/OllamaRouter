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

    public async Task<bool> ModelExistsAsync(string? baseUrl, string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(modelName))
        {
            return false;
        }

        var names = await GetModelNamesAsync(baseUrl, cancellationToken);

        return names.Contains(modelName);
    }

    private Task<HashSet<string>> GetModelNamesAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var cacheKey = $"ModelCatalogCacheService:{baseUrl}";

        return memoryCache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            var tags = await catalogClient.GetTagsAsync(baseUrl, cancellationToken);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in tags)
            {
                if (model?["name"]?.ToString() is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }

            return names;
        })!;
    }
}
