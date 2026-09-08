using System.Text.Json.Nodes;

namespace OllamaRouter.Services;

/// <summary>
/// HTTP implementation of <see cref="IOllamaModelCatalogClient"/>. Failed network calls are
/// caught and treated as empty catalogs / model not loaded, so as not to block routing when
/// one of the instances is unavailable.
/// </summary>
public sealed class OllamaModelCatalogClient(IHttpClientFactory httpClientFactory) : IOllamaModelCatalogClient
{
    public async Task<JsonArray> GetMergedTagsAsync(string? localUrl, string? remoteUrl, CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient();

        var localTask = FetchArraySafeAsync(client, BuildUrl(localUrl, "/api/tags"), "models", cancellationToken);
        var remoteTask = FetchArraySafeAsync(client, BuildUrl(remoteUrl, "/api/tags"), "models", cancellationToken);
        await Task.WhenAll(localTask, remoteTask);

        return MergeByKey(remoteTask.Result, localTask.Result, "name");
    }

    public async Task<JsonArray> GetMergedOpenAIModelsAsync(string? localUrl, string? remoteUrl, CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient();

        var localTask = FetchArraySafeAsync(client, BuildUrl(localUrl, "/v1/models"), "data", cancellationToken);
        var remoteTask = FetchArraySafeAsync(client, BuildUrl(remoteUrl, "/v1/models"), "data", cancellationToken);
        await Task.WhenAll(localTask, remoteTask);

        return MergeByKey(remoteTask.Result, localTask.Result, "id");
    }

    public async Task<JsonArray> GetMergedRunningModelsAsync(string? localUrl, string? remoteUrl, CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient();

        var localTask = FetchArraySafeAsync(client, BuildUrl(localUrl, "/api/ps"), "models", cancellationToken);
        var remoteTask = FetchArraySafeAsync(client, BuildUrl(remoteUrl, "/api/ps"), "models", cancellationToken);
        await Task.WhenAll(localTask, remoteTask);

        return MergeByKey(remoteTask.Result, localTask.Result, "name");
    }

    public async Task<bool> IsModelLoadedLocallyAsync(string localUrl, string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(modelName))
        {
            return false;
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            var response = await client.GetStringAsync($"{localUrl}/api/ps", cancellationToken);
            var json = JsonNode.Parse(response);
            var models = json?["models"]?.AsArray();

            if (models is null)
            {
                return false;
            }

            foreach (var model in models)
            {
                if (model?["name"]?.ToString() == modelName)
                {
                    return true;
                }
            }
        }
        catch
        {
            // If the local API does not respond, assume the model is not loaded.
        }

        return false;
    }

    private static string BuildUrl(string? baseUrl, string path) => $"{baseUrl}{path}";

    private static async Task<JsonArray> FetchArraySafeAsync(HttpClient client, string url, string arrayPropertyName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetStringAsync(url, cancellationToken);
            var json = JsonNode.Parse(response);
            return json?[arrayPropertyName]?.AsArray() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Merges two JSON model arrays, giving priority to entries from the second
    /// array (usually local) in case of a matching key.
    /// </summary>
    public static JsonArray MergeByKey(JsonArray lowerPriority, JsonArray higherPriority, string keyProperty)
    {
        var merged = new Dictionary<string, JsonNode>();

        foreach (var model in lowerPriority)
        {
            if (model?[keyProperty] is { } key)
            {
                merged[key.ToString()] = model;
            }
        }

        foreach (var model in higherPriority)
        {
            if (model?[keyProperty] is { } key)
            {
                merged[key.ToString()] = model;
            }
        }

        return new JsonArray(merged.Values.Select(n => n.DeepClone()).ToArray());
    }
}
