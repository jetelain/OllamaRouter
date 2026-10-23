using System.Text.Json.Nodes;

namespace OllamaRouter.Services;

/// <summary>
/// Fetches and merges Ollama model catalogs (local + remote), and allows checking whether a
/// given model is currently loaded in memory locally.
/// </summary>
public interface IOllamaModelCatalogClient
{
    /// <summary>
    /// Merges the models exposed via /api/tags by the local and remote instances.
    /// Local models take priority in case of duplicates.
    /// </summary>
    Task<JsonArray> GetMergedTagsAsync(string? localUrl, string? remoteUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges the models exposed via /v1/models (OpenAI format) by the local and remote instances.
    /// Local models take priority in case of duplicates.
    /// </summary>
    Task<JsonArray> GetMergedOpenAIModelsAsync(string? localUrl, string? remoteUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges the currently running models exposed via /api/ps by the local and remote instances.
    /// Local models take priority in case of duplicates.
    /// </summary>
    Task<JsonArray> GetMergedRunningModelsAsync(string? localUrl, string? remoteUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Indicates whether the given model already appears loaded (/api/ps) on the local instance.
    /// </summary>
    Task<bool> IsModelLoadedLocallyAsync(string localUrl, string modelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the raw list of models exposed via /api/tags by a single instance (no merging).
    /// Returns an empty array if the instance is unreachable or not configured.
    /// </summary>
    Task<JsonArray> GetTagsAsync(string? baseUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the raw list of models exposed via /api/tags by a single instance (no merging)
    /// together with a SHA-256 hex digest of the raw response body. The digest identifies the
    /// exact catalog contents so digest-keyed capability caches can be invalidated when the
    /// catalog of this instance changes. Returns (empty array, "") for an empty/unreachable
    /// instance.
    /// </summary>
    Task<(JsonArray Tags, string Digest)> GetTagsWithDigestAsync(string? baseUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inspects a single model on the specified instance via POST /api/show and returns the
    /// parsed response (parameters block, Modelfile, details, model_info). Returns null when
    /// the instance is unconfigured/unreachable, the request fails, or the response is not
    /// valid JSON (fail-soft, matching <see cref="GetTagsAsync"/>).
    /// </summary>
    Task<JsonObject?> GetShowAsync(string? baseUrl, string modelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the list of currently running model names on the specified instance via /api/ps.
    /// </summary>
    Task<IReadOnlyList<string>> GetRunningModelNamesAsync(string? baseUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests the specified instance to unload a model immediately by setting keep_alive to 0 via /api/generate.
    /// </summary>
    Task<bool> UnloadModelAsync(string? baseUrl, string modelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unloads all currently loaded models on the specified instance to free VRAM.
    /// Returns the list of model names that were unloaded.
    /// </summary>
    Task<IReadOnlyList<string>> StopRunningModelsAsync(string? baseUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a JSON array endpoint of a single instance with an explicit per-target time
    /// budget (linked cancellation on top of the client's own timeout). Parses the
    /// <paramref name="arrayPropertyName"/> root property ("models" or "data") and returns
    /// (null, false) on timeout, error, or unconfigured URL — as distinct from a genuine
    /// empty catalog, which returns ([], true). Used by the parallel fleet aggregation so a
    /// slow target can fall back to its cached catalog instead of blocking the response.
    /// </summary>
    Task<(JsonArray? Array, bool Succeeded)> FetchArrayBoundedAsync(string? baseUrl, string path, string arrayPropertyName, TimeSpan budget, CancellationToken cancellationToken = default);
}
