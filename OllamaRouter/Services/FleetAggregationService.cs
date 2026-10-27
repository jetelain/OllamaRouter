using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.ReverseProxy;

namespace OllamaRouter.Services;

/// <summary>
/// Default implementation of <see cref="IFleetAggregationService"/>: parallel, budgeted
/// fan-out over the online fleet with per-target stale-while-revalidate fallback, plus
/// model-directed /api/show resolution with a 30 s in-memory response cache.
/// </summary>
public sealed class FleetAggregationService(
    IOptions<OllamaRouterOptions> options,
    IOllamaModelCatalogClient catalogClient,
    IModelCatalogCacheService catalogCache,
    ITargetLivenessService liveness,
    ITargetAvailabilityService targetAvailability,
    IMemoryCache memoryCache,
    ILogger<FleetAggregationService> logger) : IFleetAggregationService
{
    // During the first 10 s after the liveness prober started, an unresolved (Unknown)
    // target is treated as fan-out-eligible (identical to the middleware's routable
    // definition) so aggregation never disagrees with routing.
    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ShowCacheDuration = TimeSpan.FromSeconds(30);

    // Hidden property used to carry the source target across the merge; renamed to the
    // public "router_target" on /api/ps entries (the only invented response field).
    private const string SourceTargetProperty = "__router_source_target";

    public IReadOnlyList<OllamaTargetOptions> GetFanOutTargets()
    {
        var withinGrace = IsWithinStartupGrace();
        var entries = liveness.GetEntries();
        return options.Value.GetEffectiveTargets(logger)
            .Where(t => t.Enabled &&
                        !string.Equals(t.Name, RoutingTarget.Cloud.Value, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(t.Url) &&
                        (liveness.IsOnline((RoutingTarget)t.Name!, ITargetLivenessService.DefaultTolerantCacheTtl) ||
                         (withinGrace &&
                          entries.TryGetValue((RoutingTarget)t.Name!, out var entry) &&
                          entry.State == TargetLivenessState.Unknown)))
            .ToList();
    }

    public async Task<JsonArray> GetTagsFleetAsync(IReadOnlyList<JsonNode>? cloudSyntheses, HttpContext? context = null)
    {
        var merged = await AggregateAsync(context,
            liveFetcher: (url, budget, ct) => catalogClient.FetchArrayBoundedAsync(url, "/api/tags", "models", budget, ct),
            staleFetcher: url => catalogCache.GetStaleTagsAsync(url),
            writeThrough: (url, array) => catalogCache.SetStaleTags(url, array),
            dedupeKey: "name");
        StripSourceMarker(merged);
        return AppendCloudSyntheses(merged, cloudSyntheses, keySelectors: ["name", "model"]);
    }

    public async Task<JsonArray> GetOpenAIModelsFleetAsync(IReadOnlyList<JsonNode>? cloudSyntheses, HttpContext? context = null)
    {
        var merged = await AggregateAsync(context,
            liveFetcher: (url, budget, ct) => catalogClient.FetchArrayBoundedAsync(url, "/v1/models", "data", budget, ct),
            staleFetcher: url => catalogCache.GetStaleV1ModelsAsync(url),
            writeThrough: (url, array) => catalogCache.SetStaleV1Models(url, array),
            dedupeKey: "id");
        StripSourceMarker(merged);
        return AppendCloudSyntheses(merged, cloudSyntheses, keySelectors: ["id"]);
    }

    public async Task<JsonArray> GetPsFleetAsync(HttpContext? context = null)
    {
        var merged = await AggregateAsync(context,
            liveFetcher: (url, budget, ct) => catalogClient.FetchArrayBoundedAsync(url, "/api/ps", "models", budget, ct),
            staleFetcher: url => catalogCache.GetStalePsAsync(url),
            writeThrough: (url, array) => catalogCache.SetStalePs(url, array),
            // No cross-target dedupe: the same model loaded on two daemons appears twice.
            dedupeKey: null);

        // The ONLY invented field in the fleet API surface: it lets a client tell which daemon
        // owns each entry so per-target size_vram is meaningful (approved architecture).
        foreach (var entry in merged)
        {
            if (entry is JsonObject obj)
            {
                obj["router_target"] = obj[SourceTargetProperty]?.GetValue<string>() ?? "";
                obj.Remove(SourceTargetProperty);
            }
        }

        return merged;
    }

    // Removes the internal source-target marker so /api/tags and /v1/models entries keep the
    // pristine Ollama / OpenAI shapes (IDE clients are strict about those; the ONLY invented
    // response field in the whole fleet surface is the /api/ps router_target above).
    private static void StripSourceMarker(JsonArray entries)
    {
        foreach (var entry in entries)
        {
            if (entry is JsonObject obj)
            {
                obj.Remove(SourceTargetProperty);
            }
        }
    }

    public async Task<JsonObject?> GetShowFleetAsync(string modelName, RoutingTarget? pinned, HttpContext? context = null)
    {
        var (target, url) = await ResolveShowTargetAsync(modelName, pinned, context);
        if (target is null || url is null)
        {
            logger.LogWarning("No online fleet target can serve /api/show for {ModelName}.", modelName);
            return null;
        }

        var cacheKey = $"FleetAggregation:show:{target.Name}:{modelName}";
        var cached = memoryCache.TryGetValue<JsonObject>(cacheKey, out var showCache) ? showCache : null;
        if (cached is not null)
        {
            // Cache hit: no network I/O at all.
            return (JsonObject?)cached.DeepClone();
        }

        var budget = GetPerTargetBudget();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context?.RequestAborted ?? default);
        if (budget > TimeSpan.Zero)
        {
            linkedCts.CancelAfter(budget);
        }

        try
        {
            var result = await catalogClient.GetShowAsync(url, modelName, linkedCts.Token);
            if (result is not null)
            {
                memoryCache.Set(cacheKey, result.DeepClone(), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ShowCacheDuration });
                logger.LogInformation("/api/show for {ModelName} served by {Target}.", modelName, target.Name);
                return result;
            }
        }
        catch (OperationCanceledException) when (context?.RequestAborted.IsCancellationRequested == true)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fetching /api/show for {ModelName} from {Target} failed within the {Budget} budget.", modelName, target.Name, budget);
        }

        // The 500 ms budget expired or the fetch failed: serve the (possibly stale) cached
        // value when one exists, otherwise let the endpoint answer 502.
        return memoryCache.TryGetValue<JsonObject>(cacheKey, out var staleShow) ? (JsonObject?)staleShow.DeepClone() : null;
    }

    private async Task<(OllamaTargetOptions? Target, string? Url)> ResolveShowTargetAsync(string modelName, RoutingTarget? pinned, HttpContext? context)
    {
        // (1) Pin honored when it names a real, enabled, online/grace effective target.
        if (pinned is not null &&
            pinned != RoutingTarget.Cloud &&
            !string.IsNullOrWhiteSpace(pinned.Value) &&
            targetAvailability.IsEnabled(pinned.Value) &&
            TryGetTarget(pinned.Value, out var pinnedTarget))
        {
            if (IsTargetRoutable(pinned.Value))
            {
                return (pinnedTarget, pinnedTarget.Url);
            }

            logger.LogWarning("Pinned target {Target} for /api/show is not routable (offline/disabled); falling back to model-directed selection.", pinned.Value);
        }

        // (2) Highest-priority (earliest effective-target order) online target whose cached
        // catalog contains the model (the name-set lookup is itself 30 s cached).
        foreach (var fanOut in GetFanOutTargets())
        {
            if (await catalogCache.ModelExistsAsync(fanOut.Url, modelName, context?.RequestAborted ?? default))
            {
                return (fanOut, fanOut.Url);
            }
        }

        // (3) Primary target: "Local" (case-insensitive) when present+enabled, else the first
        // enabled effective target, else the first effective target (the exact legacy default).
        var effective = options.Value.GetEffectiveTargets(logger);
        var local = effective.FirstOrDefault(t => string.Equals(t.Name, OllamaReverseProxyConfig.LocalTarget, StringComparison.OrdinalIgnoreCase));
        var primary = (local is not null && local.Enabled)
            ? local
            : effective.FirstOrDefault(t => t.Enabled) ?? effective.FirstOrDefault();
        if (primary is null)
        {
            return (null, null);
        }

        return (primary, primary.Url);
    }

    private bool TryGetTarget(string name, out OllamaTargetOptions target)
    {
        target = options.Value.GetEffectiveTargets(logger)
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))!;
        return target is not null && !string.IsNullOrWhiteSpace(target.Name);
    }

    // Same routable definition as the middleware: TTL-aware Online, or Unknown within the
    // 10 s startup grace window.
    private bool IsTargetRoutable(RoutingTarget target)
    {
        if (liveness.IsOnline(target, ITargetLivenessService.DefaultTolerantCacheTtl))
        {
            return true;
        }

        return IsWithinStartupGrace() &&
               liveness.GetEntries().TryGetValue(target, out var entry) &&
               entry.State == TargetLivenessState.Unknown;
    }

    private bool IsWithinStartupGrace()
    {
        var start = liveness.ServiceStartTimeUtc;
        return start != DateTimeOffset.MinValue &&
               DateTimeOffset.UtcNow - start < StartupGracePeriod;
    }

    private TimeSpan GetPerTargetBudget()
    {
        var ms = options.Value.Aggregation?.PerTargetTimeoutMs ?? 500;
        return ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromMilliseconds(500);
    }

    private async Task<JsonArray> AggregateAsync(
        HttpContext? context,
        Func<string, TimeSpan, CancellationToken, Task<(JsonArray? Array, bool Succeeded)>> liveFetcher,
        Func<string, Task<JsonArray?>> staleFetcher,
        Action<string, JsonArray> writeThrough,
        string? dedupeKey)
    {
        var fanOut = GetFanOutTargets();
        var effective = options.Value.GetEffectiveTargets(logger);

        // Online/grace targets are fetched live (in parallel, each under its own per-target
        // budget); every other enabled target is offline and contributes its cached catalog
        // (stale-while-revalidate) without any network call.
        var slots = effective
            .Where(t => t.Enabled &&
                        !string.Equals(t.Name, RoutingTarget.Cloud.Value, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(t.Url))
            .Select(t => (
                Target: t.Name!,
                Url: t.Url!.TrimEnd('/'),
                Live: fanOut.Any(f => string.Equals(f.Name, t.Name, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        var budget = GetPerTargetBudget();
        var requestCt = context?.RequestAborted ?? default;

        // Start every slot in parallel: live slots under their own per-target budget CTS,
        // stale slots as plain cache reads. One slow daemon can never hold up the WhenAll.
        var results = slots.Select(slot => (slot, Task: slot.Live
            ? FetchLiveAsync(slot, budget, requestCt, liveFetcher)
            : FetchStaleAsync(slot, staleFetcher))).ToList();
        await Task.WhenAll(results.Select(r => r.Task));

        // Merge in effective-target (priority) order.
        var merged = new JsonArray();
        foreach (var (slot, task) in results)
        {
            var array = await task;
            if (array is not null)
            {
                MergeInto(merged, array, slot.Target, dedupeKey);
            }
        }

        return merged;

        async Task<JsonArray?> FetchLiveAsync((string Target, string Url, bool Live) slot, TimeSpan targetBudget, CancellationToken ct, Func<string, TimeSpan, CancellationToken, Task<(JsonArray? Array, bool Succeeded)>> fetcher)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (targetBudget > TimeSpan.Zero)
            {
                linkedCts.CancelAfter(targetBudget);
            }

            (JsonArray? Array, bool Succeeded) result;
            try
            {
                result = await fetcher(slot.Url, targetBudget, linkedCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Aggregated fetch of {Target} failed; falling back to its cached catalog.", slot.Target);
                result = (null, false);
            }

            if (result.Succeeded)
            {
                writeThrough(slot.Url, result.Array!);
                return result.Array;
            }

            // Missed the budget or errored: serve the cached catalog (stale-while-revalidate).
            var stale = await FetchStaleAsync(slot, staleFetcher);
            if (stale is not null)
            {
                logger.LogInformation("Target {Target} missed the aggregation budget; serving its cached catalog.", slot.Target);
            }

            return stale;
        }

        async Task<JsonArray?> FetchStaleAsync((string Target, string Url, bool Live) slot, Func<string, Task<JsonArray?>> fetcher)
        {
            try
            {
                return await fetcher(slot.Url);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reading the cached catalog of {Target} failed; it contributes nothing to this response.", slot.Target);
                return null;
            }
        }
    }

    private static void MergeInto(JsonArray destination, JsonArray source, string targetName, string? dedupeKey)
    {
        foreach (var entry in source)
        {
            if (entry is not JsonObject obj)
            {
                continue;
            }

            if (dedupeKey is not null)
            {
                var key = obj[dedupeKey]?.GetValue<string>();
                if (key is null)
                {
                    continue;
                }

                // First-encountered (earlier / higher-priority target) wins on duplicates.
                if (destination.Any(e => e is JsonObject existing &&
                        string.Equals(existing[dedupeKey]?.GetValue<string>(), key, StringComparison.Ordinal)))
                {
                    continue;
                }
            }

            obj[SourceTargetProperty] = targetName;
            destination.Add(obj.DeepClone());
        }
    }

    private static JsonArray AppendCloudSyntheses(JsonArray merged, IReadOnlyList<JsonNode>? cloudSyntheses, string[] keySelectors)
    {
        if (cloudSyntheses is null)
        {
            return merged;
        }

        foreach (var synthesis in cloudSyntheses)
        {
            if (synthesis is not JsonObject obj)
            {
                continue;
            }

            var present = merged.Any(m =>
                m is JsonObject existing &&
                keySelectors.Any(key =>
                    string.Equals(existing[key]?.GetValue<string>(), obj[key]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)));

            if (!present)
            {
                merged.Add(obj.DeepClone());
            }
        }

        return merged;
    }
}
