using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Middleware;
using OllamaRouter.Serialization;
using OllamaRouter.Services.ModelUninstall;

namespace OllamaRouter.Endpoints.RouterApi.Models;

/// <summary>
/// The model uninstall endpoint of the OllamaRouter programmatic management API (task
/// #126, report item #7): <c>POST /api/router/models/uninstall</c> permanently deletes a
/// model from one or more fleet nodes — the destructive counterpart of the model pre-load
/// (task #44). The router issues ONE fail-soft <c>DELETE {target}/api/delete</c> per selected
/// target daemon directly (never through the routing middleware / YARP); a failing target
/// (daemon 404 "model not found", unreachable node) never aborts the remaining ones. After
/// a successful delete the model disappears from the daemon's /api/tags and /api/ps, so the
/// digest-keyed capability cache and the 2 s /api/ps execution tracker invalidate naturally
/// (the desktop App's cartography poll — and its immediate refresh after the dialog — keep
/// the matrix consistent; deleted cells read "not present").
/// <para>
/// ROUTE NOTE (literal beats catch-all): the dedicated LITERAL route
/// <c>/api/router/models/uninstall</c> coexists with the <c>GET /api/router/models/{*modelName}</c>
/// model-detail and <c>POST /api/router/models/{*tail}</c> preload catch-alls — ASP.NET
/// Core route scoring always prefers the literal segment, the same mechanism the existing
/// <c>GET /api/router/models</c> vs <c>/{*modelName}</c> pair uses (and a trailing
/// <c>/uninstall</c> sniff on the POST catch-all is impossible for a namespaced model literally
/// named ".../uninstall", so the literal route is the unambiguous contract). Namespaced
/// model names need no URI escaping here (the name travels in the JSON body).
/// </para>
/// </summary>
public static class RouterModelUninstallEndpoints
{
    /// <summary>
    /// Maps the model uninstall endpoint (<c>POST /api/router/models/uninstall</c>).
    /// </summary>
    public static WebApplication MapRouterModelUninstallEndpoints(this WebApplication app)
    {
        app.MapPost("/api/router/models/uninstall", UninstallModelHandler)
            .WithName("PostRouterModelUninstall")
            .WithTags("Fleet Operations")
            .WithSummary("Router-owned model uninstall (permanent delete) on the selected fleet targets.")
            .WithDescription("DESTRUCTIVE + PERSISTENT: permanently deletes the model files of the given model from the "
                + "selected fleet nodes (DELETE {target}/api/delete per target daemon, request order, fail-soft per target — "
                + "a failing target never aborts the others; the loaded instance on the daemon is removed as well, and the "
                + "router's /api/ps tracker + digest-keyed capability cache pick up the deletion within their refresh "
                + "windows). API-key-protected in default mode (Authorization: Bearer or X-Api-Key). Request body (REQUIRED): "
                + "{\"model\": \"<name>\", \"targets\": [\"<target>\", ...]} — a non-empty 'model' name and a non-empty "
                + "'targets' array with at least one non-blank name (else 400); absent/empty/malformed JSON body → 400. "
                + "Target names are matched case-insensitively against the configured effective targets (the canonical "
                + "configured names are echoed in the response; the Cloud pseudo-target is never a valid target — no "
                + "OllamaTargetOptions → 404). 404: model unknown fleet-wide ('model not found: {model}') or any unknown "
                + "target name ('target not found: {name}') — both checked BEFORE any daemon call (zero deletes issued). "
                + "503: the model uninstall service is not registered (minimal hosts). 200: execution ran — per-target "
                + "results in request order (success vs. the daemon's fail-soft error text; partial failure is still 200).");
        return app;
    }

    /// <summary>
    /// Model uninstall handler (task #126): body-only contract (no query fallback — the
    /// target list is multi-valued). Reads the JSON body (absent/empty/malformed → 400),
    /// validates <c>model</c> (non-blank) and <c>targets</c> (non-empty with ≥ 1 non-blank
    /// name; else 400), resolves <see cref="IModelUninstallService"/> defensively
    /// (unregistered minimal host → 503, the preload endpoint's defensive vocabulary) and
    /// maps the service result: <c>Error</c> set → 404 (unknown model / unknown target —
    /// validated before any daemon call), otherwise 200 with the per-target rows in
    /// request order (fail-soft: partial failures are 200 with the failing rows'
    /// <c>error</c>).
    /// </summary>
    private static async Task<IResult> UninstallModelHandler(
        HttpRequest request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        RouterModelUninstallRequest? body;
        try
        {
            body = await System.Text.Json.JsonSerializer.DeserializeAsync(
                request.Body,
                OllamaRouterJsonSerializerContext.Default.RouterModelUninstallRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            body = null; // malformed JSON body → 400 (body-only contract, no query fallback)
        }
        catch (System.IO.EndOfStreamException)
        {
            body = null; // zero-length body → 400 (same contract as an absent body)
        }

        if (body is null ||
            string.IsNullOrWhiteSpace(body.Model) ||
            body.Targets is not { Count: > 0 } targets ||
            !targets.Any(t => !string.IsNullOrWhiteSpace(t)))
        {
            return Results.Json(
                new OllamaErrorResponse("request body with a 'model' name and a non-empty 'targets' array is required"),
                OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var uninstallService = serviceProvider.GetService<IModelUninstallService>();
        if (uninstallService is null)
        {
            // Defensive resolution (the file's minimal-host convention): a host without the
            // registered service cannot uninstall models — same 503 vocabulary as the preload endpoint.
            return Results.Json(
                new OllamaErrorResponse("model uninstall service is not available"),
                OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var result = await uninstallService
            .UninstallAsync(body.Model, targets, cancellationToken).ConfigureAwait(false);
        if (result is null || result.Error is not null)
        {
            return Results.Json(
                new OllamaErrorResponse(result?.Error ?? "model not found"),
                OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new RouterModelUninstallResponse(
                Model: result.Model,
                Targets: result.Targets.Select(t => new RouterModelUninstallTargetResult(
                    Target: t.Target,
                    Success: t.Success,
                    Error: t.Error)).ToList()),
            OllamaRouterJsonSerializerContext.Default.RouterModelUninstallResponse);
    }
}
