using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Endpoints;

/// <summary>
/// Lightweight, dependency-free monitoring UI: shows the multi-target fleet (per-target
/// liveness, active model, in-flight requests, hardware profile, and context allocation) plus
/// requests currently in progress and the recent request history with model, estimated and
/// actual tokens, and elapsed time. It reads in-memory state from
/// <see cref="IActivityMonitorService"/> and aggregated token statistics (persisted, current day
/// and last 7 days) from <see cref="IActivityStatisticsService"/>, so it has no impact on VRAM.
/// </summary>
public static partial class MonitorEndpointsExtensions
{
    public static WebApplication MapOllamaMonitorEndpoints(this WebApplication app)
    {
        app.MapGet("/monitor/api", async (
            IActivityMonitorService activityMonitor,
            IActivityStatisticsService activityStatistics,
            IOptions<OllamaRouterOptions> options,
            ITargetAvailabilityService targetAvailability,
            IModelCatalogCacheService modelCatalogCache,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
        {
            // Optional enrichment services: null-tolerant so minimal (test) apps that do not
            // register them keep working with the reduced (catalog-only) liveness view.
            var monitorResponse = await BuildMonitorStateResponseAsync(
                activityMonitor,
                activityStatistics,
                options,
                targetAvailability,
                modelCatalogCache,
                cancellationToken,
                includeOverhead: true,
                liveness: serviceProvider.GetService<ITargetLivenessService>(),
                executionTracker: serviceProvider.GetService<ITargetExecutionTracker>(),
                contextShrinkManager: serviceProvider.GetService<IContextAutoShrinkManager>(),
                fleetHardware: serviceProvider.GetService<IFleetHardwareService>());

            return Results.Json(monitorResponse, OllamaRouterJsonSerializerContext.Default.MonitorStateResponse);
        });

        app.MapPost("/targets", (TargetsUpdateRequest request, ITargetAvailabilityService targetAvailability) =>
        {
            var flags = request.AsFlags();
            targetAvailability.Update(flags);
            return Results.Ok(new TargetsUpdateResponse(flags));
        });

        app.MapPost("/monitor/reclaim-vram", async (
            [FromQuery] bool? disableTarget,
            IOllamaModelCatalogClient catalogClient,
            ITargetAvailabilityService targetAvailability,
            IOptions<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
            await ReclaimLocalLegacyAsync(
                disableTarget ?? true, catalogClient, targetAvailability, options,
                serviceProvider.GetService<IContextAutoShrinkManager>(), cancellationToken));

        app.MapPost("/api/models/unload", async (
            [FromQuery] bool? disableTarget,
            IOllamaModelCatalogClient catalogClient,
            ITargetAvailabilityService targetAvailability,
            IOptions<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
            await ReclaimLocalLegacyAsync(
                disableTarget ?? false, catalogClient, targetAvailability, options,
                serviceProvider.GetService<IContextAutoShrinkManager>(), cancellationToken));

        app.MapGet("/monitor", () => Results.Content(MonitorPageHtml, "text/html"));

        return app;
    }

    /// <summary>
    /// Shared legacy per-"Local"-target reclaim core for <c>POST /monitor/reclaim-vram</c>
    /// (default <c>disableTarget: true</c>) and <c>POST /api/models/unload</c> (default
    /// <c>false</c>): keeps the exact legacy behavior — "Local" is disabled FIRST (even when
    /// <c>LocalUrl</c> is unconfigured, in which case the response is
    /// <c>success: false</c> + "Local URL is not configured.") and the unload is routed
    /// through the shared per-target <see cref="VramReclaimCore"/>, preserving the legacy
    /// <see cref="ReclaimVramResponse"/> shape byte-for-byte.
    /// </summary>
    private static async Task<IResult> ReclaimLocalLegacyAsync(
        bool shouldDisable,
        IOllamaModelCatalogClient catalogClient,
        ITargetAvailabilityService targetAvailability,
        IOptions<OllamaRouterOptions> options,
        IContextAutoShrinkManager? shrinkManager,
        CancellationToken cancellationToken)
    {
        if (shouldDisable)
        {
            DisableLocalTarget(targetAvailability);
        }

        var effectiveTargets = options.Value.GetEffectiveTargets();
        var localTarget = effectiveTargets.FirstOrDefault(t => string.Equals(t.Name, "Local", StringComparison.OrdinalIgnoreCase))
            ?? effectiveTargets.FirstOrDefault(t => OllamaProcessLauncher.IsLoopback(t.Url));

        var localUrl = localTarget?.Url?.TrimEnd('/');
#pragma warning disable CS0618
        if (string.IsNullOrWhiteSpace(localUrl))
        {
            localUrl = options.Value.LocalUrl?.TrimEnd('/');
        }
#pragma warning restore CS0618

        if (string.IsNullOrWhiteSpace(localUrl))
        {
            return Results.Ok(new ReclaimVramResponse(
                Success: false,
                Message: "Local URL is not configured.",
                Count: 0,
                UnloadedModels: Array.Empty<string>(),
                LocalDisabled: shouldDisable));
        }

        var result = await VramReclaimCore.ReclaimTargetAsync(
            localTarget ?? new OllamaTargetOptions { Name = "Local", Url = localUrl },
            false, // Already disabled above (legacy pre-disable ordering).
            catalogClient,
            targetAvailability,
            shrinkManager,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(new ReclaimVramResponse(
            Success: true,
            Message: null,
            Count: result.UnloadedModels.Count,
            UnloadedModels: result.UnloadedModels,
            LocalDisabled: shouldDisable));
    }

    // "Reclaim" behavior: disable the target named "Local" (case-insensitive); when no such
    // target exists in the fleet the availability service ignores the unknown name (skip).
    private static void DisableLocalTarget(ITargetAvailabilityService targetAvailability)
    {
        targetAvailability.Update(
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["Local"] = false
            });
    }
}
