using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;

namespace OllamaRouter.Services.Catalog;

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
    ILogger<ModelCatalogCacheService>? logger = null,
    IOptionsMonitor<OllamaRouterOptions>? routerOptions = null) : IModelCatalogCacheService
{
    internal const string CapabilityKeyPrefix = "ModelCapabilityEngine:";

    private const string StaleSlotPrefix = "FleetAggregation:";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OfflineCacheDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleSlotDuration = TimeSpan.FromSeconds(60);

    // Optional live-options reference (task #61): enables the SYNTHETIC catalog for
    // non-Ollama targets (declared Models roster instead of /api/tags). Null-tolerant so
    // every pre-fleet unit-test construction keeps the exact /api/tags behavior.
    private readonly IOptionsMonitor<OllamaRouterOptions>? _routerOptions = routerOptions;

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

        // Synthetic catalog (task #61): a URL belonging to a non-Ollama effective target is
        // served from the target's declared Models roster — never a /api/tags probe (the
        // daemon does not speak it). The digest is a stable SHA-256 over the sorted names so
        // roster edits (rebind) are change-detected exactly like a real catalog change.
        if (TryGetSyntheticNames(baseUrl, out var syntheticNames))
        {
            var syntheticDigest = ComputeSyntheticDigest(syntheticNames);
            var syntheticResult = (syntheticNames.ToList(), syntheticDigest);
            RefreshDigestAsync(baseUrl, syntheticResult);
            return syntheticResult;
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

    public Task<JsonArray?> GetStaleTagsAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult<JsonArray?>(null);
        }

        return GetStaleSlotAsync(StaleSlotKey("tags", url));
    }

    public void SetStaleTags(string url, JsonArray tags)
    {
        SetStaleSlot(StaleSlotKey("tags", url), tags);
    }

    public Task<JsonArray?> GetStaleV1ModelsAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult<JsonArray?>(null);
        }

        return GetStaleSlotAsync(StaleSlotKey("v1models", url));
    }

    public void SetStaleV1Models(string url, JsonArray data)
    {
        SetStaleSlot(StaleSlotKey("v1models", url), data);
    }

    public Task<JsonArray?> GetStalePsAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult<JsonArray?>(null);
        }

        return GetStaleSlotAsync(StaleSlotKey("ps", url));
    }

    public void SetStalePs(string url, JsonArray models)
    {
        SetStaleSlot(StaleSlotKey("ps", url), models);
    }

    // Stale-while-revalidate slot for the unified fleet endpoints: populated only on a
    // successful live fetch (write-through) and never cleared by a failed fetch, so a
    // slow/asleep daemon keeps serving its last known catalog until the 60 s TTL lapses.
    private Task<JsonArray?> GetStaleSlotAsync(string cacheKey)
    {
        // GetAsync (no factory): a slot is created only by Set* (live success), so a
        // null miss must NOT create a persistent null entry.
        return Task.FromResult(memoryCache.TryGetValue<JsonArray>(cacheKey, out var slot) ? (JsonArray?)slot.DeepClone() : null);
    }

    private void SetStaleSlot(string cacheKey, JsonArray value)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        memoryCache.Set(cacheKey, value.DeepClone(), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = StaleSlotDuration });
    }

    private static string StaleSlotKey(string kind, string url) => $"{StaleSlotPrefix}{kind}:{url}";

    private Task<HashSet<string>> GetModelNamesAsync(string baseUrl, CancellationToken cancellationToken)
    {
        // Synthetic catalog (task #61): non-Ollama targets resolve their model names from the
        // live declared roster on EVERY call (cheap in-memory walk, no cache, no network) so a
        // Setup-API roster edit is visible on the very next decision (no TTL staleness).
        if (TryGetSyntheticNames(baseUrl, out var synthetic))
        {
            return Task.FromResult(synthetic);
        }

        var cacheKey = $"ModelCatalogCacheService:{baseUrl}";

        return memoryCache.GetOrCreateAsync(cacheKey, async entry =>
        {
            var names = ExtractModelNames(await catalogClient.GetTagsAsync(baseUrl, cancellationToken));
            entry.AbsoluteExpirationRelativeToNow = names.Count > 0 ? CacheDuration : OfflineCacheDuration;
            return names;
        })!;
    }

    // Resolves the SYNTHETIC model roster of a non-Ollama effective target whose URL matches
    // <paramref name="baseUrl"/> (case-insensitive, trailing-slash tolerant). Returns false for
    // Ollama targets, unresolvable URLs and hosts without the optional options reference (the
    // exact pre-fleet /api/tags path applies). Blank roster names are skipped (documented).
    private bool TryGetSyntheticNames(string baseUrl, out HashSet<string> names)
    {
        names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_routerOptions is null || string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        foreach (var target in _routerOptions.CurrentValue.GetEffectiveTargets())
        {
            if (target.Type == TargetType.Ollama)
            {
                continue;
            }

            if (!UrlsMatch(target.Url, baseUrl))
            {
                continue;
            }

            names = ExtractSyntheticNames(target);
            return true;
        }

        return false;
    }

    private static bool UrlsMatch(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
           string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> ExtractSyntheticNames(OllamaTargetOptions target)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (target.Models is null)
        {
            return result;
        }

        foreach (var model in target.Models)
        {
            if (!string.IsNullOrWhiteSpace(model.Name))
            {
                result.Add(model.Name.Trim());
            }
        }

        return result;
    }

    /// <summary>
    /// Stable digest of a synthetic roster: SHA-256 (lowercase hex) of the comma-joined,
    /// case-insensitively sorted distinct names. Empty roster ⇒ SHA-256 of the empty string —
    /// any roster edit (add/remove/rename) changes the digest (change detection on rebind).
    /// </summary>
    internal static string ComputeSyntheticDigest(IReadOnlyCollection<string> names)
    {
        var joined = string.Join(",",
            names.Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
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
