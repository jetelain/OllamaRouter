using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Endpoints;

/// <summary>
/// Shared per-target unload core used by the fleet VRAM endpoints
/// (<c>POST /api/router/vram/reclaim</c> / <c>POST /api/router/fleet/unload</c>) and the
/// legacy monitor endpoints (<c>POST /monitor/reclaim-vram</c>, <c>POST /api/models/unload</c>):
/// unloads every running model on one target (fail-soft), optionally administratively
/// disables the target, and resets its KV-cache context leases.
/// </summary>
internal static class VramReclaimCore
{
    /// <summary>
    /// Unloads all running models on a single target (fail-soft: unreachable daemon ⇒
    /// empty list), applies the optional administrative availability update, and resets
    /// the target's KV-cache leases so the next request starts at the baseline bucket.
    /// </summary>
    internal static async Task<RouterVramReclaimTargetResult> ReclaimTargetAsync(
        OllamaTargetOptions target,
        bool disableTarget,
        IOllamaModelCatalogClient catalogClient,
        ITargetAvailabilityService? availability,
        IContextAutoShrinkManager? shrinkManager,
        CancellationToken cancellationToken)
    {
        // Fail-soft by contract: StopRunningModelsAsync never throws (empty list on failure).
        var unloaded = await catalogClient.StopRunningModelsAsync(target.Url, cancellationToken)
            .ConfigureAwait(false);

        if (disableTarget)
        {
            availability?.Update(
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    [target.Name!] = false
                });
        }

        // The model was physically evicted: any retained expanded lease is stale.
        shrinkManager?.ResetTargetLeases(new RoutingTarget(target.Name!));

        return new RouterVramReclaimTargetResult(target.Name!, unloaded, disableTarget);
    }
}
