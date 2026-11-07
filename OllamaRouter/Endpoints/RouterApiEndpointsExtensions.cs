using System.Reflection;
using OllamaRouter.Endpoints.RouterApi.ApiKeys;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Context;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Info;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Endpoints.RouterApi.Preload;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Endpoints.RouterApi.Vram;

namespace OllamaRouter.Endpoints;

/// <summary>
/// Lightweight, programmatic management API for the OllamaRouter gateway. This class is a
/// thin composer: it invokes the per-area endpoint mappers under
/// <c>OllamaRouter.Endpoints.RouterApi</c> (<see cref="RouterApi.Info.RouterInfoEndpoints"/>,
/// <see cref="RouterApi.Fleet.RouterFleetEndpoints"/>, <see cref="RouterApi.Benchmark.RouterBenchmarkEndpoints"/>,
/// <see cref="RouterApi.Models.RouterModelsEndpoints"/>, <see cref="RouterApi.Cartography.RouterCartographyEndpoints"/>,
/// <see cref="RouterApi.Vram.RouterVramEndpoints"/>, <see cref="RouterApi.Context.RouterContextEndpoints"/>,
/// <see cref="RouterApi.Preload.RouterPreloadEndpoints"/>, <see cref="RouterApi.Config.RouterConfigEndpoints"/>,
/// <see cref="RouterApi.Targets.RouterTargetsEndpoints"/>, <see cref="RouterApi.ApiKeys.RouterApiKeysEndpoints"/>)
/// in the exact original registration order, which global route matching depends on
/// (in particular the catch-all <c>/api/router/models/{*modelName}</c> vs
/// <c>POST /api/router/models/{*tail}</c> pair and the position of the
/// <c>POST /api/router/fleet/unload</c> alias).
/// </summary>
public static class RouterApiEndpointsExtensions
{
    /// <summary>
    /// Maps the full OllamaRouter management API: the 25 endpoints of the 11 per-area
    /// endpoint groups, registered in the original order.
    /// </summary>
    public static WebApplication MapOllamaRouterApiEndpoints(this WebApplication app)
    {
        app.MapRouterInfoEndpoints();
        app.MapRouterFleetEndpoints();
        app.MapRouterBenchmarkEndpoints();
        app.MapRouterModelsEndpoints();
        app.MapRouterCartographyEndpoints();
        app.MapRouterVramEndpoints();
        app.MapRouterContextEndpoints();
        app.MapRouterPreloadEndpoints();
        app.MapRouterConfigEndpoints();
        app.MapRouterTargetsEndpoints();
        app.MapRouterApiKeysEndpoints();
        return app;
    }

    /// <summary>
    /// The router version: the assembly informational version (AOT-preserved), reported by
    /// <c>GET /api/router/info</c> and reused by the OpenAPI document's <c>info.version</c>
    /// (task #51) so the two can never drift.
    /// </summary>
    internal static string GetRouterVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
