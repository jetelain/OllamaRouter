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

    /// <summary>
    /// Indicates whether any models are present in the /api/tags catalog of the instance
    /// located at <paramref name="baseUrl"/>. If false, the instance is considered unreachable or empty.
    /// Results are cached for a short duration.
    /// </summary>
    Task<bool> HasAnyModelsAsync(string? baseUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Force-fetches the /api/tags catalog for the instance at <paramref name="baseUrl"/>
    /// (bypassing any existing cached entry) and re-populates the same per-URL cache slot
    /// with the same 30 s / 10 s (empty) expirations, so the refreshed names are immediately
    /// visible to <see cref="ModelExistsAsync"/> and <see cref="HasAnyModelsAsync"/>.
    /// A null/empty <paramref name="baseUrl"/> is a no-op that returns an empty list.
    /// </summary>
    Task<IReadOnlyList<string>> RefreshAsync(string? baseUrl, CancellationToken cancellationToken = default);
}
