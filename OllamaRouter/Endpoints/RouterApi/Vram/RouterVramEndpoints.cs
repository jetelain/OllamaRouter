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

namespace OllamaRouter.Endpoints.RouterApi.Vram;

/// <summary>
/// The fleet VRAM reclaim endpoints of the OllamaRouter programmatic management API:
/// <list type="bullet">
/// <item><c>POST /api/router/vram/reclaim</c> and <c>POST /api/router/fleet/unload</c> (exact
/// alias, identical contract) — fleet VRAM reclaim: unloads the running models on one named
/// target (case-insensitive; unknown name → 404) or on ALL effective configured targets
/// (fail-soft per target) and optionally administratively disables the processed target(s);
/// KV-cache leases of every processed target are reset to the baseline bucket.</item>
/// </list>
/// </summary>

public static class RouterVramEndpoints
{
    /// <summary>
    /// Maps the fleet VRAM reclaim endpoints (<c>POST /api/router/vram/reclaim</c>, <c>POST /api/router/fleet/unload</c>).
    /// </summary>
    public static WebApplication MapRouterVramEndpoints(this WebApplication app)
    {
        // Canonical fleet VRAM reclaim + exact alias (identical handler, parameters and
        // response shape). Both paths unload the running models on the selected effective
        // target(s) — or ALL effective targets when no target is specified (fail-soft per
        // target; the Cloud pseudo-target is never part of the effective list).
        app.MapPost("/api/router/vram/reclaim", ReclaimVramHandler)
            .WithName("PostRouterVramReclaim")
            .WithTags("Fleet Operations")
            .WithSummary("Fleet VRAM reclaim: unload the running models on the selected target(s).")
            .WithDescription("Unloads the running models on one named target (case-insensitive; unknown name → 404) or on ALL "
                + "effective configured targets (fail-soft per target; the Cloud pseudo-target is never part of the effective "
                + "list) when no target is specified, and optionally administratively disables the processed target(s) "
                + "(disableTarget, default false); KV-cache leases of every processed target are reset to the baseline bucket. "
                + "Parameters (target, disableTarget) via query string and/or an optional JSON body: an absent/empty body is "
                + "valid, a malformed body falls through to the query values, an explicit body is authoritative per field "
                + "(body wins over query — the vram-reclaim pattern). Fail-soft: 200 with success:false + message "
                + "'no targets configured' when the effective target list is empty. No key required in default mode.");
        app.MapPost("/api/router/fleet/unload", ReclaimVramHandler)
            .WithName("PostRouterFleetUnload")
            .WithTags("Fleet Operations")
            .WithSummary("Exact alias of POST /api/router/vram/reclaim (identical handler, parameters and response shape).")
            .WithDescription("EXACT alias of POST /api/router/vram/reclaim — identical handler, parameters and response shape. "
                + "Unloads the running models on one named target (case-insensitive; unknown name → 404) or on ALL effective "
                + "configured targets (fail-soft per target; the Cloud pseudo-target is never part of the effective list) when no "
                + "target is specified, and optionally administratively disables the processed target(s) (disableTarget, default "
                + "false); KV-cache leases of every processed target are reset to the baseline bucket. Parameters (target, "
                + "disableTarget) via query string and/or an optional JSON body: an absent/empty body is valid, a malformed body "
                + "falls through to the query values, an explicit body is authoritative per field (body wins over query). "
                + "Fail-soft: 200 with success:false + message 'no targets configured' when the effective target list is empty. "
                + "No key required in default mode.");
        return app;
    }

    // ------------------------------------------------------------------
    //  POST /api/router/vram/reclaim (+ POST /api/router/fleet/unload alias)
    // ------------------------------------------------------------------

    /// <summary>
    /// Fleet VRAM reclaim handler (canonical path <c>/api/router/vram/reclaim</c>, exact
    /// alias <c>/api/router/fleet/unload</c>): unloads the running models on the selected
    /// effective target (case-insensitive name match; unknown name → 404) or on ALL
    /// effective configured targets (fail-soft per target) when no target is specified.
    /// Parameters are accepted via query string and/or an optional JSON body; body values
    /// win over query values. Optional <c>disableTarget</c> (default false) administratively
    /// disables each processed target via <see cref="ITargetAvailabilityService"/> (persisted).
    /// KV-cache leases of every processed target are reset so the next request starts at
    /// the baseline bucket. Dependency-free beyond options + catalog client (the
    /// availability service and context-shrink manager are resolved defensively).
    /// </summary>
    private static async Task<IResult> ReclaimVramHandler(
        HttpRequest request,
        IOptionsMonitor<OllamaRouterOptions> options,
        IOllamaModelCatalogClient catalogClient,
        IServiceProvider serviceProvider,
        [FromQuery] string? target,
        [FromQuery] bool? disableTarget,
        CancellationToken cancellationToken)
    {
        // Optional JSON body: a malformed body falls through to the query values (the same
        // pattern as /api/router/context/evaluate), so a bad body never 400s a request the
        // query string already satisfies.
        RouterVramReclaimRequest? body = null;
        try
        {
            body = await System.Text.Json.JsonSerializer.DeserializeAsync(
                request.Body,
                OllamaRouterJsonSerializerContext.Default.RouterVramReclaimRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed JSON body: fall through to the query values.
        }

        // Body values win over query values: an explicit body is authoritative for both fields
        // (a body with absent fields uses the record defaults: target null, disableTarget false);
        // an absent/malformed body falls back entirely to the query values.
        var effectiveTargetName = body is not null
            ? (string.IsNullOrWhiteSpace(body.Target) ? target : body.Target)
            : target;
        var effectiveDisable = body is not null
            ? body.DisableTarget
            : disableTarget ?? false;

        var effectiveTargets = options.CurrentValue.GetEffectiveTargets();
        if (effectiveTargets.Count == 0)
        {
            return Results.Json(
                new RouterVramReclaimResponse(
                    Success: false,
                    TotalUnloaded: 0,
                    Targets: [],
                    Message: "no targets configured"),
                OllamaRouterJsonSerializerContext.Default.RouterVramReclaimResponse);
        }

        // Single target: case-insensitive match against the effective fleet (D4: unknown → 404).
        IReadOnlyList<OllamaTargetOptions> selected;
        if (!string.IsNullOrWhiteSpace(effectiveTargetName))
        {
            var match = effectiveTargets.FirstOrDefault(t =>
                string.Equals(t.Name, effectiveTargetName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return Results.Json(
                    new OllamaErrorResponse($"target not found: {effectiveTargetName}"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            selected = [match];
        }
        else
        {
            // Fleet-wide: ALL effective configured targets regardless of liveness/enablement
            // (unreachable daemons fail soft per target via StopRunningModelsAsync).
            selected = effectiveTargets;
        }

        var availability = serviceProvider.GetService<ITargetAvailabilityService>();
        var shrinkManager = serviceProvider.GetService<IContextAutoShrinkManager>();

        // Concurrent per-target unload (fail-soft; results preserve the configured order).
        var results = await Task.WhenAll(selected.Select(t =>
            VramReclaimCore.ReclaimTargetAsync(
                t, effectiveDisable, catalogClient, availability, shrinkManager, cancellationToken)))
            .ConfigureAwait(false);

        return Results.Json(
            new RouterVramReclaimResponse(
                Success: true,
                TotalUnloaded: results.Sum(r => r.UnloadedModels.Count),
                Targets: results),
            OllamaRouterJsonSerializerContext.Default.RouterVramReclaimResponse);
    }
}
