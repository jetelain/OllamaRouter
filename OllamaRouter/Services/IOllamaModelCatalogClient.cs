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
}
