using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.ContextSizing;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using static OllamaRouter.Endpoints.RouterApi.Shared.RouterApiSharedHelpers;

namespace OllamaRouter.Endpoints.RouterApi.Models;

/// <summary>
/// The model catalog endpoints of the OllamaRouter programmatic management API:
/// <list type="bullet">
/// <item><c>GET /api/router/models</c> and <c>GET /api/router/models/{modelName}</c> — the
/// zero-config model capability catalog: per-model architecture classification (Dense/Moe),
/// parameter count, native Modelfile num_ctx and the router-calculated hardware-safe context
/// ceiling (recommendedContextWindow = max CMax over the hardware-capable fleet targets;
/// cloud overflow is never a ceiling contributor, only the cloudOverflowAvailable hint).</item>
/// </list>
/// </summary>

public static class RouterModelsEndpoints
{
    /// <summary>
    /// Maps the model catalog endpoints (<c>GET /api/router/models</c>, <c>GET /api/router/models/{*modelName}</c>).
    /// </summary>
    public static WebApplication MapRouterModelsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/router/models", async (
            IOptionsMonitor<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var response = await BuildModelsListAsync(options, serviceProvider, context, cancellationToken);
            return Results.Json(response, OllamaRouterJsonSerializerContext.Default.RouterModelsResponse);
        })
        .WithName("GetRouterModels")
        .WithTags("Model Catalog & Cartography")
        .WithSummary("Zero-config model capability catalog of the merged fleet.")
        .WithDescription("Per-model architecture classification (Dense/Moe/Unknown), parameter count, native Modelfile num_ctx and "
            + "the router-calculated hardware-safe context ceiling (recommendedContextWindow = max CMax over the hardware-capable "
            + "fleet targets; cloud overflow is never a ceiling contributor, only the cloudOverflowAvailable hint). The merged "
            + "fleet model names (first-seen order) plus cloud synthesis names when Cloud overflow is available. "
            + "Read-only — no key required in default mode.");
        app.MapGet("/api/router/models/{*modelName}", async (
            IOptionsMonitor<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            HttpContext context,
            string modelName,
            CancellationToken cancellationToken) =>
        {
            modelName = Uri.UnescapeDataString(modelName).TrimStart('/');
            var detail = await BuildModelDetailAsync(options, serviceProvider, context, modelName, cancellationToken);
            if (detail is null)
            {
                return Results.Json(
                    new OllamaErrorResponse("model not found"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(detail, OllamaRouterJsonSerializerContext.Default.RouterModelDetailResponse);
        })
        .WithName("GetRouterModelDetail")
        .WithTags("Model Catalog & Cartography")
        .WithSummary("Model capability detail + per-target qualification matrix.")
        .WithDescription("Registered as the catch-all GET /api/router/models/{*modelName} route so namespaced model names with "
            + "slashes work (the segment is unescaped and trimmed of the leading '/'). Returns the summary fields plus the "
            + "per-target qualification matrix (present / capable / context ceiling / resident / in-flight; the Cloud "
            + "pseudo-target is never listed — it has no OllamaTargetOptions, and cloud-only models carry an empty matrix). "
            + "Unknown model → 404 OllamaErrorResponse. Read-only — no key required in default mode.");
        return app;
    }

    // ------------------------------------------------------------------
    //  GET /api/router/models + GET /api/router/models/{modelName}
    // ------------------------------------------------------------------

    private static async Task<RouterModelsResponse> BuildModelsListAsync(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var opt = options.CurrentValue;
        var effectiveTargets = opt.GetEffectiveTargets();
        var fleetAggregation = serviceProvider.GetService<IFleetAggregationService>();
        var catalogCache = serviceProvider.GetService<IModelCatalogCacheService>();
        var capabilityEngine = serviceProvider.GetService<ModelCapabilityEngine>();
        var availability = serviceProvider.GetService<ITargetAvailabilityService>();

        var names = await GetFleetModelNamesAsync(opt, availability, fleetAggregation, catalogCache, context, cancellationToken)
            .ConfigureAwait(false);

        // Names are independent: run the per-model discovery concurrently (the metadata calls
        // hit the 30 s single-flight capability cache).
        var summaries = await Task.WhenAll(names.Select(name => BuildModelSummaryAsync(
            name,
            opt,
            effectiveTargets,
            catalogCache,
            capabilityEngine,
            availability,
            cancellationToken))).ConfigureAwait(false);

        return new RouterModelsResponse(summaries);
    }

    private static async Task<RouterModelDetailResponse?> BuildModelDetailAsync(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        HttpContext context,
        string modelName,
        CancellationToken cancellationToken)
    {
        var opt = options.CurrentValue;
        var effectiveTargets = opt.GetEffectiveTargets();
        var fleetAggregation = serviceProvider.GetService<IFleetAggregationService>();
        var catalogCache = serviceProvider.GetService<IModelCatalogCacheService>();
        var capabilityEngine = serviceProvider.GetService<ModelCapabilityEngine>();
        var availability = serviceProvider.GetService<ITargetAvailabilityService>();
        var executionTracker = serviceProvider.GetService<ITargetExecutionTracker>();

        var names = await GetFleetModelNamesAsync(opt, availability, fleetAggregation, catalogCache, context, cancellationToken)
            .ConfigureAwait(false);
        var resolved = ResolveModelName(modelName, names);
        if (resolved is null)
        {
            if (opt.FilterNamespacedModels)
            {
                resolved = await ResolveModelOnFleetAsync(catalogCache, effectiveTargets, modelName, cancellationToken).ConfigureAwait(false);
            }

            if (resolved is null)
            {
                return null;
            }
        }

        var summary = await BuildModelSummaryAsync(
            resolved,
            opt,
            effectiveTargets,
            catalogCache,
            capabilityEngine,
            availability,
            cancellationToken).ConfigureAwait(false);

        // Cloud-only synthesis entry: no fleet target hosts it, so the target matrix is empty
        // (the Cloud pseudo-target is never listed — it has no OllamaTargetOptions).
        if (!await AnyTargetHasModelAsync(catalogCache, effectiveTargets, resolved, cancellationToken).ConfigureAwait(false))
        {
            return new RouterModelDetailResponse(
                summary.Name,
                summary.Architecture,
                summary.ParameterCount,
                summary.NativeContextWindow,
                summary.RecommendedContextWindow,
                []);
        }

        var targets = await BuildTargetCompatibilityMatrixAsync(
            resolved,
            opt,
            effectiveTargets,
            catalogCache,
            capabilityEngine,
            executionTracker,
            cancellationToken).ConfigureAwait(false);

        return new RouterModelDetailResponse(
            summary.Name,
            summary.Architecture,
            summary.ParameterCount,
            summary.NativeContextWindow,
            summary.RecommendedContextWindow,
            targets);
    }

    /// <summary>
    /// Public-facing projection of <see cref="ComputeModelSummaryAsync"/> used by
    /// <c>GET /api/router/models</c> and <c>GET /api/router/models/{modelName}</c>.
    /// </summary>
    private static async Task<RouterModelSummary> BuildModelSummaryAsync(
        string name,
        OllamaRouterOptions opt,
        IReadOnlyList<OllamaTargetOptions> effectiveTargets,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        ITargetAvailabilityService? availability,
        CancellationToken cancellationToken)
    {
        var data = await ComputeModelSummaryAsync(
            name, opt, effectiveTargets, catalogCache, capabilityEngine, availability, cancellationToken).ConfigureAwait(false);
        return data.Summary;
    }

    /// <summary>
    /// Per-target qualification matrix (one row per effective target, declaration order; the
    /// Cloud pseudo-target is NOT listed — it has no OllamaTargetOptions). Capable =
    /// hardware-capable capability AND model present on that target; the veto reasons are:
    /// model absent → "model not present in target catalog"; LargeDense on a small-class
    /// target → the exact <see cref="ModelCapabilityEngine.LargeDenseVetoReason"/> string;
    /// undiscoverable capability → no veto reason (permissive fallback, matching the routing
    /// pipeline).
    /// </summary>
    private static async Task<IReadOnlyList<RouterModelTargetCompatibility>> BuildTargetCompatibilityMatrixAsync(
        string name,
        OllamaRouterOptions opt,
        IReadOnlyList<OllamaTargetOptions> effectiveTargets,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        ITargetExecutionTracker? executionTracker,
        CancellationToken cancellationToken)
    {
        var normalizedName = RoutingDecisionService.NormalizeModelName(name);
        var pinnedArchitecture = opt.Models.TryGetValue(normalizedName, out var thresholds) ? thresholds.Architecture : null;

        // Discover primary metadata from the first target with model_info to serve as fallback.
        ModelMetadata? primaryMetadata = null;
        if (catalogCache is not null && capabilityEngine is not null)
        {
            foreach (var target in effectiveTargets)
            {
                var meta = await GetTargetMetadataAsync(target, name, catalogCache, capabilityEngine, cancellationToken).ConfigureAwait(false);
                if (meta is not null)
                {
                    if (primaryMetadata is null || (!primaryMetadata.HasModelInfo && meta.HasModelInfo))
                    {
                        primaryMetadata = meta;
                    }
                    if (primaryMetadata.HasModelInfo)
                    {
                        break;
                    }
                }
            }
        }

        return await Task.WhenAll(effectiveTargets.Select(target =>
            BuildTargetRowAsync(name, target, primaryMetadata, pinnedArchitecture, catalogCache, capabilityEngine, executionTracker, cancellationToken)))
            .ConfigureAwait(false);
    }

    private static async Task<RouterModelTargetCompatibility> BuildTargetRowAsync(
        string name,
        OllamaTargetOptions target,
        ModelMetadata? primaryMetadata,
        string? pinnedArchitecture,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        ITargetExecutionTracker? executionTracker,
        CancellationToken cancellationToken)
    {
        var qualification = await ComputeTargetQualificationAsync(
            name, target, primaryMetadata, pinnedArchitecture, catalogCache, capabilityEngine, executionTracker, cancellationToken)
            .ConfigureAwait(false);
        return qualification.ToCompatibilityRow();
    }
}
