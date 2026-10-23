using System.Text.Json.Nodes;

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

    /// <summary>
    /// Force-fetches the /api/tags catalog for the instance at <paramref name="baseUrl"/>
    /// (bypassing any existing cached entry), re-populates the per-URL name cache slot, and
    /// returns the fetched model names together with the SHA-256 hex digest of the raw
    /// response body. When the digest changes, only this target's previously recorded
    /// capability cache keys (see ModelCapabilityEngine) are invalidated. A null/empty
    /// <paramref name="baseUrl"/> is a no-op that returns ([], ""); an unreachable instance
    /// returns ([], "").
    /// </summary>
    Task<(IReadOnlyList<string> Names, string Digest)> RefreshWithDigestAsync(string? baseUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the digest of the most recent /api/tags refresh for the instance at
    /// <paramref name="baseUrl"/>, triggering a refresh when none is known yet.
    /// Returns "" for a null/empty base URL or when the instance is unreachable/empty.
    /// </summary>
    Task<string> GetCurrentDigestAsync(string? baseUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous accessor for the digest of the most recent /api/tags refresh of the
    /// instance at <paramref name="baseUrl"/> ("" when unknown or empty).
    /// </summary>
    string CurrentDigest(string? baseUrl);

    /// <summary>
    /// Records capability cache keys owned by <see cref="ModelCapabilityEngine"/> for a given
    /// instance so that a catalog digest change can invalidate them. Keys recorded under a
    /// digest different from the current one are discarded.
    /// </summary>
    void RegisterCapabilityCacheKeys(string baseUrl, IEnumerable<string> keys, string digest);

    /// <summary>
    /// Returns the last successfully fetched raw /api/tags array of the instance at
    /// <paramref name="url"/> (full tag entries, not just names) for stale-while-revalidate
    /// aggregation, or null when none is cached (yet). The slot expires after 60 s and is
    /// refreshed only on a successful live fetch.
    /// </summary>
    Task<JsonArray?> GetStaleTagsAsync(string url);

    /// <summary>
    /// Writes the freshly fetched raw /api/tags array into the per-URL stale slot
    /// (write-through, 60 s TTL). Only call on a successful live fetch.
    /// </summary>
    void SetStaleTags(string url, JsonArray tags);

    /// <summary>
    /// Returns the last successfully fetched raw /v1/models data array of the instance at
    /// <paramref name="url"/> for stale-while-revalidate aggregation, or null when none is
    /// cached (yet).
    /// </summary>
    Task<JsonArray?> GetStaleV1ModelsAsync(string url);

    /// <summary>
    /// Writes the freshly fetched raw /v1/models data array into the per-URL stale slot
    /// (write-through, 60 s TTL). Only call on a successful live fetch.
    /// </summary>
    void SetStaleV1Models(string url, JsonArray data);

    /// <summary>
    /// Returns the last successfully fetched raw /api/ps models array of the instance at
    /// <paramref name="url"/> for stale-while-revalidate aggregation, or null when none is
    /// cached (yet).
    /// </summary>
    Task<JsonArray?> GetStalePsAsync(string url);

    /// <summary>
    /// Writes the freshly fetched raw /api/ps models array into the per-URL stale slot
    /// (write-through, 60 s TTL). Only call on a successful live fetch.
    /// </summary>
    void SetStalePs(string url, JsonArray models);
}
