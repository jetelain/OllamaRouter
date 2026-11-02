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
    IOptionsMonitor<OllamaRouterOptions> options,
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

    // Fleet-wide (unpinned) best-hoster cache for /api/show (task #43): a hit within the
    // window performs zero network I/O of any kind (no /api/show, no /api/tags refresh).
    private static readonly TimeSpan ShowBestCacheDuration = TimeSpan.FromSeconds(30);

    // White-box-testable record of the fleet-wide /api/show winner (task #43). The payload
    // alone would suffice functionally; TargetName/NumCtx exist for the extended log line
    // and test assertions (internal + InternalsVisibleTo, not private).
    internal sealed record FleetShowBestEntry(string TargetName, int NumCtx, JsonObject Payload);


    // Hidden property used to carry the source target across the merge; renamed to the
    // public "router_target" on /api/ps entries (the only invented response field).
    private const string SourceTargetProperty = "__router_source_target";

    public IReadOnlyList<OllamaTargetOptions> GetFanOutTargets()
    {
        var withinGrace = IsWithinStartupGrace();
        var entries = liveness.GetEntries();
        return options.CurrentValue.GetEffectiveTargets(logger)
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
        var (hosters, direct) = await ResolveShowTargetsAsync(modelName, pinned, context);
        if (direct is not null)
        {
            // (1) Honored pin, or (3) primary fallback: the legacy single-fetch path. Pinned
            // requests never read or write the fleet best cache and never fan out.
            return await FetchShowForTargetAsync(direct, modelName, context);
        }

        if (hosters.Count == 0)
        {
            logger.LogWarning("No online fleet target can serve /api/show for {ModelName}.", modelName);
            return null;
        }

        if (hosters.Count == 1)
        {
            // Single hoster: preserve the exact legacy single-fetch behavior (no extra
            // traffic, no fleet best cache interaction).
            return await FetchShowForTargetAsync(hosters[0], modelName, context);
        }

        // (2) Unpinned fleet-wide strategy (task #43): every online hoster declaring the
        // model is scanned in parallel; the largest declared num_ctx wins.
        return await GetShowFleetWideAsync(hosters, modelName, context);
    }

    /// <summary>
    /// Legacy single-fetch path (pinned target, single hoster, primary fallback): per-target
    /// cache read → budgeted fetch → write-through → log → stale-cache fallback → null. It
    /// never consults the fleet best cache, so the pinned path and the single-hoster path stay
    /// byte-for-byte on the pre-task-#43 behavior and traffic profile.
    /// </summary>
    private async Task<JsonObject?> FetchShowForTargetAsync(OllamaTargetOptions target, string modelName, HttpContext? context)
    {
        var name = target.Name!;
        var cacheKey = $"FleetAggregation:show:{name}:{modelName}";
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
            var result = await catalogClient.GetShowAsync(target.Url, modelName, linkedCts.Token);
            if (result is not null)
            {
                memoryCache.Set(cacheKey, result.DeepClone(), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ShowCacheDuration });
                logger.LogInformation("/api/show for {ModelName} served by {Target}.", modelName, name);
                return result;
            }
        }
        catch (OperationCanceledException) when (context?.RequestAborted.IsCancellationRequested == true)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fetching /api/show for {ModelName} from {Target} failed within the {Budget} budget.", modelName, name, budget);
        }

        // The 500 ms budget expired or the fetch failed: serve the (possibly stale) cached
        // value when one exists, otherwise let the endpoint answer 502.
        return memoryCache.TryGetValue<JsonObject>(cacheKey, out var staleShow) ? (JsonObject?)staleShow.DeepClone() : null;
    }

    /// <summary>
    /// Unpinned fleet-wide resolution (task #43): best-cache read (zero network on hit) →
    /// parallel, budgeted fan-out of POST /api/show to every hoster (one slow/down daemon
    /// never blocks the response) → per-target write-through → winner selection by largest
    /// declared num_ctx (tie → earlier effective-target (config) order; absent ⇒ 0) →
    /// best-cache write. When no hoster produced a payload, the first hoster's stale
    /// per-target cache is served (stale-while-revalidate), else null (the endpoint answers
    /// 502).
    /// </summary>
    private async Task<JsonObject?> GetShowFleetWideAsync(IReadOnlyList<OllamaTargetOptions> hosters, string modelName, HttpContext? context)
    {
        // Best-cache read first: a hit within the 30 s window performs zero network I/O of
        // any kind (the name-set lookups of the caller's hoster enumeration are warm from
        // the same window, so no /api/tags refresh is triggered either).
        var bestKey = $"FleetAggregation:show-best:{modelName}";
        if (memoryCache.TryGetValue<FleetShowBestEntry>(bestKey, out var best) && best is not null)
        {
            return (JsonObject?)best.Payload.DeepClone();
        }

        var budget = GetPerTargetBudget();
        var requestCt = context?.RequestAborted ?? default;

        // Start every hoster in parallel, each under its own per-target budget CTS (mirrors
        // the AggregateAsync conventions). One slow/down daemon never holds up the WhenAll.
        var slots = hosters.Select(target => (target, Task: FetchShowSlotAsync(target, modelName, budget, requestCt))).ToList();
        await Task.WhenAll(slots.Select(s => s.Task));

        // Winner selection in hoster (config) order: the first payload-bearing hoster is the
        // provisional winner; a later hoster replaces it only on a strictly-greater num_ctx
        // (absent/unparseable ⇒ 0, so any explicit declaration wins). Only hosts with a null
        // payload (failed/budget-missed) are skipped, so an all-absent fleet degenerates to
        // the earliest config-order hoster (legacy behavior preserved).
        (OllamaTargetOptions Target, JsonObject Payload, int NumCtx)? winner = null;
        foreach (var (target, task) in slots)
        {
            var payload = await task;
            if (payload is null)
            {
                continue;
            }

            var score = ModelMetadataParser.ParseNumCtx(payload) ?? 0;
            if (winner is null || score > winner.Value.NumCtx)
            {
                winner = (target, payload, score);
            }

            // Write-through: every fetched payload is cached under the existing per-target
            // key, so a subsequent pinned request to any scanned hoster (or a single-hoster
            // re-resolution) is served from cache with no traffic.
            memoryCache.Set(
                $"FleetAggregation:show:{target.Name}:{modelName}",
                payload.DeepClone(),
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ShowCacheDuration });
        }

        if (winner is not null)
        {
            memoryCache.Set(
                bestKey,
                new FleetShowBestEntry(winner.Value.Target.Name!, winner.Value.NumCtx, (JsonObject)winner.Value.Payload.DeepClone()),
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ShowBestCacheDuration });
            logger.LogInformation(
                "/api/show for {ModelName} served by {Target} (num_ctx={NumCtx}, scanned {Count} hosters).",
                modelName,
                winner.Value.Target.Name!,
                winner.Value.NumCtx > 0 ? winner.Value.NumCtx.ToString() : "unknown",
                hosters.Count);
            return winner.Value.Payload;
        }

        // No hoster produced a payload: serve the first (highest-priority) hoster's existing
        // per-target cached payload when one exists (today's stale-while-revalidate),
        // otherwise let the endpoint answer 502.
        var firstKey = $"FleetAggregation:show:{hosters[0].Name}:{modelName}";
        if (memoryCache.TryGetValue<JsonObject>(firstKey, out var staleFirst) && staleFirst is not null)
        {
            return (JsonObject?)staleFirst.DeepClone();
        }

        // The client itself aborted the request and no winner could be produced: terminate
        // the aborted request instead of answering it.
        if (requestCt.IsCancellationRequested)
        {
            throw new OperationCanceledException(requestCt);
        }

        return null;
    }

    /// <summary>
    /// One fan-out slot of the fleet-wide /api/show scan: a budgeted GetShowAsync (already
    /// fail-soft: null on timeout/error) whose exceptions are swallowed per slot so a single
    /// slow/down daemon never blocks the response. A client-side abort propagates.
    /// </summary>
    private async Task<JsonObject?> FetchShowSlotAsync(OllamaTargetOptions target, string modelName, TimeSpan budget, CancellationToken requestCt)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(requestCt);
        if (budget > TimeSpan.Zero)
        {
            linkedCts.CancelAfter(budget);
        }

        try
        {
            return await catalogClient.GetShowAsync(target.Url, modelName, linkedCts.Token);
        }
        catch (OperationCanceledException) when (requestCt.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fetching /api/show for {ModelName} from {Target} failed within the {Budget} budget.", modelName, target.Name, budget);
            return null;
        }
    }

    /// <summary>
    /// Resolves the /api/show routing target(s): (1) an honored pin →
    /// <c>([], pinned)</c>; (2) otherwise the ordered list of online (fan-out-eligible)
    /// effective targets whose cached catalog contains the model → <c>(hosters, null)</c>;
    /// (3) otherwise the primary target → <c>([], primary)</c> ("Local" when present+enabled,
    /// else the first enabled effective target, else the first effective target). An empty
    /// tuple means no target at all.
    /// </summary>
    private async Task<(IReadOnlyList<OllamaTargetOptions> Hosters, OllamaTargetOptions? Direct)> ResolveShowTargetsAsync(string modelName, RoutingTarget? pinned, HttpContext? context)
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
                return ([], pinnedTarget);
            }

            logger.LogWarning("Pinned target {Target} for /api/show is not routable (offline/disabled); falling back to model-directed selection.", pinned.Value);
        }

        // (2) Every online target whose cached catalog contains the model, in effective-target
        // (config) order (the name-set lookup is itself 30 s cached and doubles as the
        // winner tie-break order).
        var hosters = new List<OllamaTargetOptions>();
        foreach (var fanOut in GetFanOutTargets())
        {
            if (await catalogCache.ModelExistsAsync(fanOut.Url, modelName, context?.RequestAborted ?? default))
            {
                hosters.Add(fanOut);
            }
        }

        if (hosters.Count > 0)
        {
            return (hosters, null);
        }

        // (3) Primary target: "Local" (case-insensitive) when present+enabled, else the first
        // enabled effective target, else the first effective target (the exact legacy default).
        var effective = options.CurrentValue.GetEffectiveTargets(logger);
        var local = effective.FirstOrDefault(t => string.Equals(t.Name, OllamaReverseProxyConfig.LocalTarget, StringComparison.OrdinalIgnoreCase));
        var primary = (local is not null && local.Enabled)
            ? local
            : effective.FirstOrDefault(t => t.Enabled) ?? effective.FirstOrDefault();
        return ([], primary);
    }

    private bool TryGetTarget(string name, out OllamaTargetOptions target)
    {
        target = options.CurrentValue.GetEffectiveTargets(logger)
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
        var ms = options.CurrentValue.Aggregation?.PerTargetTimeoutMs ?? 500;
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
        var effective = options.CurrentValue.GetEffectiveTargets(logger);

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
