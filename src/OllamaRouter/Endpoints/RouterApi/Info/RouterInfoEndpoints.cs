using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using static OllamaRouter.Endpoints.RouterApi.Shared.RouterApiSharedHelpers;

namespace OllamaRouter.Endpoints.RouterApi.Info;

/// <summary>
/// The identity endpoint of the OllamaRouter programmatic management API:
/// <list type="bullet">
/// <item><c>GET /api/router/info</c> — instantly differentiates OllamaRouter from a
/// standard Ollama daemon (which returns 404 for this route) and reports the router
/// version, mode, fleet summary and active features. Pure in-memory snapshot reads.</item>
/// </list>
/// </summary>

public static class RouterInfoEndpoints
{
    /// <summary>
    /// Maps <c>GET /api/router/info</c>.
    /// </summary>
    public static WebApplication MapRouterInfoEndpoints(this WebApplication app)
    {
        app.MapGet("/api/router/info", async (
            IOptionsMonitor<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
        {
            var (fleet, features) = await BuildInfoBlocksAsync(options, serviceProvider, cancellationToken);
            return Results.Json(new RouterInfoResponse(RouterApiEndpointsExtensions.GetRouterVersion(), true, fleet, features),
                OllamaRouterJsonSerializerContext.Default.RouterInfoResponse);
        })
        .WithName("GetRouterInfo")
        .WithTags("Identity & Fleet")
        .WithSummary("Report the router identity, version, fleet summary and active feature flags.")
        .WithDescription("Instantly differentiates OllamaRouter from a standard Ollama daemon (which 404s this route). "
            + "Pure in-memory snapshot read: no statistics, no history aggregation and no blocking per-target HTTP (only the awaited "
            + "local GPU CLI snapshot when a local workstation target is present). 'version' is the assembly informational version. "
            + "The fleet summary counts the Cloud pseudo-target in totalTargets/onlineTargets when cloud overflow is enabled.");
        return app;
    }

    // ------------------------------------------------------------------
    //  GET /api/router/info
    // ------------------------------------------------------------------

    private static async Task<(RouterFleetSummary Fleet, RouterFeatures Features)> BuildInfoBlocksAsync(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var opt = options.CurrentValue;
        var effectiveTargets = opt.GetEffectiveTargets();
        var liveness = serviceProvider.GetService<ITargetLivenessService>();
        var livenessEntries = liveness?.GetEntries();
        var modelCatalogCache = serviceProvider.GetService<IModelCatalogCacheService>();

        var onlineTargets = 0;
        foreach (var target in effectiveTargets)
        {
            var isOnline = livenessEntries is { } entries && entries.TryGetValue((RoutingTarget)target.Name!, out var entry)
                ? entry.State == TargetLivenessState.Online
                : // No liveness service registered: fall back to the catalog-based check
                  // (the same rule as the monitor's reduced liveness view).
                  await CatalogHasAnyModelsAsync(modelCatalogCache, target.Url, cancellationToken);
            if (isOnline)
            {
                onlineTargets++;
            }
        }

        var cloudEnabled = opt.CloudOverflow.Enabled;
        if (cloudEnabled)
        {
            onlineTargets++;
        }

        var features = new RouterFeatures(
            DynamicContext: opt.DynamicContext.Enabled,
            AutoShrink: opt.DynamicContext.Enabled,
            Failover: opt.MaxConsecutiveFailures > 0,
            AntiThrashingAffinity: true);

        return (new RouterFleetSummary(effectiveTargets.Count + 1, onlineTargets, cloudEnabled), features);
    }
}
