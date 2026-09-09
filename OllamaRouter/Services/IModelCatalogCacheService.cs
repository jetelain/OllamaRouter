namespace OllamaRouter.Services;

/// <summary>
/// Provides a short-lived cache of which models are known to be available on the local and
/// remote Ollama instances. Used by routing decisions that need to know instance availability
/// (e.g. /api/show) without hitting /api/tags on every request.
/// </summary>
public interface IModelCatalogCacheService
{
    /// <summary>
    /// Indicates whether the given model is present in the /api/tags catalog of the instance
    /// located at <paramref name="baseUrl"/>. Results are cached for a short duration.
    /// </summary>
    Task<bool> ModelExistsAsync(string? baseUrl, string modelName, CancellationToken cancellationToken = default);
}
