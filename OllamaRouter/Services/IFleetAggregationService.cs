using System.Text.Json.Nodes;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Parallel, budgeted aggregation of the fleet's catalog endpoints (GET /api/tags, /v1/models,
/// /api/ps) and model-directed resolution of /api/show.
/// <para>
/// Every online (or startup-grace Unknown) enabled target is queried in parallel
/// (<c>Task.WhenAll</c>) with a strict per-target budget (<see cref="AggregationOptions.PerTargetTimeoutMs"/>).
/// A target that misses the budget contributes its last cached catalog (stale-while-revalidate)
/// instead of blocking the response, so an unresponsive/asleep daemon can never hang the IDE.
/// </para>
/// </summary>
public interface IFleetAggregationService
{
    /// <summary>
    /// The effective fleet targets that participate in the live fan-out: enabled targets that are
    /// currently online (TTL-aware), or still Unknown within the 10 s startup grace window. The
    /// Cloud pseudo-target is never a fan-out member (it is relay-only).
    /// </summary>
    IReadOnlyList<OllamaTargetOptions> GetFanOutTargets();

    /// <summary>
    /// Returns the merged /api/tags catalog (Ollama shape, keyed by <c>name</c>) of the whole
    /// fleet, in effective-target (priority) order. Live fetches are write-through cached;
    /// slow/offline targets fall back to their cached catalog (or contribute nothing when none
    /// is cached). <paramref name="cloudSyntheses"/> (when non-null) are appended when not
    /// already present (case-insensitive <c>name</c>/<c>model</c> match).
    /// </summary>
    Task<JsonArray> GetTagsFleetAsync(IReadOnlyList<JsonNode>? cloudSyntheses, HttpContext? context = null);

    /// <summary>
    /// Returns the merged /v1/models data array (OpenAI shape, keyed by <c>id</c>) of the whole
    /// fleet, in effective-target (priority) order. Same budget and stale-while-revalidate
    /// semantics as <see cref="GetTagsFleetAsync"/>; <paramref name="cloudSyntheses"/> are
    /// appended when not already present (case-insensitive <c>id</c> match).
    /// </summary>
    Task<JsonArray> GetOpenAIModelsFleetAsync(IReadOnlyList<JsonNode>? cloudSyntheses, HttpContext? context = null);

    /// <summary>
    /// Returns the merged /api/ps array of the whole fleet: the union of every online target's
    /// running models (no cross-target dedup, so the same model loaded on two daemons appears
    /// twice). Each entry is annotated with the non-standard <c>router_target</c> field carrying
    /// the target name, so per-target <c>size_vram</c> is distinguishable. Same budget and
    /// stale-while-revalidate semantics as <see cref="GetTagsFleetAsync"/>.
    /// </summary>
    Task<JsonArray> GetPsFleetAsync(HttpContext? context = null);

    /// <summary>
    /// Model-directed /api/show: returns the parsed /api/show response for
    /// <paramref name="modelName"/> served by the chosen target, or null when no target could
    /// serve it (the endpoint then answers 502). Target selection order:
    /// (1) the given <paramref name="pinned"/> target when valid, enabled and online/grace —
    /// a pin fetches exactly that target (no fleet-wide cache, no fan-out, ever);
    /// (2) otherwise the unpinned fleet-wide strategy (task #43): all online (fan-out-eligible)
    /// targets whose cached catalog contains the model are scanned in parallel, each fetch
    /// bounded by the per-target budget, and the hoster declaring the largest <c>num_ctx</c>
    /// wins (tie → earlier effective-target (config) order; absent/unparseable <c>num_ctx</c>
    /// ⇒ score 0; no hoster produced a payload ⇒ the first hoster's stale per-target cache,
    /// else 502); a single hoster keeps the legacy single-fetch behavior;
    /// (3) when no hoster is known at all, the primary target ("Local" when present+enabled,
    /// else the first enabled effective target, else the first effective target) is fetched
    /// directly. Every fetched payload is write-through cached 30 s under
    /// <c>FleetAggregation:show:{targetName}:{model}</c>, and the fleet-wide winner is
    /// additionally cached 30 s under <c>FleetAggregation:show-best:{model}</c>; a hit of
    /// either cache performs no network I/O at all. Pinned requests never touch the best
    /// cache.
    /// </summary>
    Task<JsonObject?> GetShowFleetAsync(string modelName, RoutingTarget? pinned, HttpContext? context = null);
}
