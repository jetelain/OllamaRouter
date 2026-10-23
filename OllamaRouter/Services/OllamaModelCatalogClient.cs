using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OllamaRouter.Services;

/// <summary>
/// HTTP implementation of <see cref="IOllamaModelCatalogClient"/>. Failed network calls are
/// caught and treated as empty catalogs / model not loaded, so as not to block routing when
/// one of the instances is unavailable.
/// </summary>
public sealed class OllamaModelCatalogClient(HttpClient httpClient) : IOllamaModelCatalogClient
{
    public async Task<JsonArray> GetMergedTagsAsync(string? localUrl, string? remoteUrl, CancellationToken cancellationToken = default)
    {
        var localTask = GetTagsAsync(localUrl, cancellationToken);
        var remoteTask = GetTagsAsync(remoteUrl, cancellationToken);
        await Task.WhenAll(localTask, remoteTask);

        return MergeByKey(localTask.Result, remoteTask.Result, "name");
    }

    public async Task<JsonArray> GetTagsAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        var (tags, _) = await GetTagsWithDigestAsync(baseUrl, cancellationToken);
        return tags;
    }

    public async Task<(JsonArray Tags, string Digest)> GetTagsWithDigestAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return ([], string.Empty);
        }

        try
        {
            var raw = await httpClient.GetStringAsync(BuildUrl(baseUrl, "/api/tags"), cancellationToken);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
            var json = JsonNode.Parse(raw);
            return (json?["models"]?.AsArray() ?? [], digest);
        }
        catch
        {
            return ([], string.Empty);
        }
    }

    public async Task<JsonObject?> GetShowAsync(string? baseUrl, string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        try
        {
            var payload = new JsonObject { ["model"] = modelName };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(BuildUrl(baseUrl, "/api/show"), content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonNode.Parse(raw) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    public async Task<JsonArray> GetMergedOpenAIModelsAsync(string? localUrl, string? remoteUrl, CancellationToken cancellationToken = default)
    {
        var localTask = FetchArraySafeAsync(BuildUrl(localUrl, "/v1/models"), "data", cancellationToken);
        var remoteTask = FetchArraySafeAsync(BuildUrl(remoteUrl, "/v1/models"), "data", cancellationToken);
        await Task.WhenAll(localTask, remoteTask);

        return MergeByKey(localTask.Result, remoteTask.Result, "id");
    }

    public async Task<JsonArray> GetMergedRunningModelsAsync(string? localUrl, string? remoteUrl, CancellationToken cancellationToken = default)
    {
        var localTask = FetchArraySafeAsync(BuildUrl(localUrl, "/api/ps"), "models", cancellationToken);
        var remoteTask = FetchArraySafeAsync(BuildUrl(remoteUrl, "/api/ps"), "models", cancellationToken);
        await Task.WhenAll(localTask, remoteTask);

        return MergeByKey(localTask.Result, remoteTask.Result, "name");
    }

    public async Task<bool> IsModelLoadedLocallyAsync(string localUrl, string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(modelName))
        {
            return false;
        }

        var runningModels = await GetRunningModelNamesAsync(localUrl, cancellationToken);
        return runningModels.Any(m => string.Equals(m, modelName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<string>> GetRunningModelNamesAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return [];
        }

        var models = await FetchArraySafeAsync(BuildUrl(baseUrl, "/api/ps"), "models", cancellationToken);
        var names = new List<string>();
        foreach (var model in models)
        {
            var name = model?["name"]?.ToString() ?? model?["model"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names.Distinct().ToList();
    }

    public async Task<bool> UnloadModelAsync(string? baseUrl, string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        try
        {
            var payload = new JsonObject
            {
                ["model"] = modelName,
                ["keep_alive"] = 0,
                ["stream"] = false
            };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(BuildUrl(baseUrl, "/api/generate"), content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> StopRunningModelsAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        var runningModels = await GetRunningModelNamesAsync(baseUrl, cancellationToken);
        if (runningModels.Count == 0)
        {
            return [];
        }

        var unloaded = new List<string>();
        foreach (var model in runningModels)
        {
            if (await UnloadModelAsync(baseUrl, model, cancellationToken))
            {
                unloaded.Add(model);
            }
        }

        return unloaded;
    }

    public async Task<(JsonArray? Array, bool Succeeded)> FetchArrayBoundedAsync(string? baseUrl, string path, string arrayPropertyName, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (null, false);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (budget > TimeSpan.Zero)
        {
            linkedCts.CancelAfter(budget);
        }

        try
        {
            var response = await httpClient.GetStringAsync(BuildUrl(baseUrl, path), linkedCts.Token);
            var json = JsonNode.Parse(response);
            var array = json?[arrayPropertyName]?.AsArray();
            // A successful HTTP response without the expected array property (e.g. an Ollama
            // error body) is a failed fetch: surface (null, false) so the caller falls back to
            // the cached catalog instead of treating a missing array as an empty one.
            return (array, array is not null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller itself was cancelled (e.g. client disconnect); propagate.
            throw;
        }
        catch
        {
            // Budget exceeded (per-target timeout) or transport/parse failure: surface as a
            // failed fetch so the aggregation layer falls back to the cached catalog.
            return (null, false);
        }
    }

    /// <summary>
    /// Merges the given JSON arrays in argument order WITHOUT deduping entries that repeat
    /// across arrays (unlike <see cref="MergeByKey"/>): an entry from every array is appended,
    /// preserving per-array order. Used for /api/ps so the same model loaded on two daemons
    /// appears twice (disambiguated by pid + the injected router_target field).
    /// Entries lacking the key property are still appended (they carry their own identity,
    /// e.g. a pid).
    /// </summary>
    public static JsonArray MergeUnion(params JsonArray[] arrays)
    {
        var merged = new List<JsonNode>();
        foreach (var array in arrays)
        {
            if (array is null)
            {
                continue;
            }

            foreach (var entry in array)
            {
                if (entry is not null)
                {
                    merged.Add(entry.DeepClone());
                }
            }
        }

        return new JsonArray(merged.ToArray());
    }

    private static string BuildUrl(string? baseUrl, string path) => $"{baseUrl?.TrimEnd('/')}{path}";

    private async Task<JsonArray> FetchArraySafeAsync(string url, string arrayPropertyName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetStringAsync(url, cancellationToken);
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
