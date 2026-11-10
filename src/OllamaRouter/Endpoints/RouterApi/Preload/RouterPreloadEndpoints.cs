using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.ContextSizing;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Preload;

namespace OllamaRouter.Endpoints.RouterApi.Preload;

/// <summary>
/// The model pre-load endpoints of the OllamaRouter programmatic management API:
/// <list type="bullet">
/// <item><c>POST /api/router/models/{*modelName}/preload</c> — router-owned model pre-load:
/// selects the fleet target that should host the model (capability-reuse selection: enabled
/// + online + present + hardware-capable + context-intent qualified, ranked idle-first,
/// server/cloud-class-first for large intents, least-busy then declaration order), issues ONE
/// bounded <c>keep_alive</c>-aware empty-prompt <c>POST /api/generate</c> load directly to that
/// daemon (plus a bounded /api/ps residency confirmation), and reports the selection, the
/// applied keep_alive / num_ctx, and the full candidate evaluation. Per-node keep-alive
/// convention: cloud/server-class = -1 (indefinite), desktop = 900 s (15 min). Parameters
/// (keepAliveSeconds / contextIntentTokens / target) via query and/or optional JSON body
/// (body wins per field, the vram-reclaim pattern).</item>
/// <item><c>GET /api/router/models/{*modelName}/preload/preview</c> — the side-effect-free
/// variant of the preload (same selection, zero daemon calls; loadAccepted /
/// residencyConfirmed / preloadDurationMs are null), paralleling /api/router/context/evaluate.</item>
/// </list>
/// </summary>

public static class RouterPreloadEndpoints
{
    /// <summary>
    /// Maps the model pre-load endpoints (<c>POST /api/router/models/{*tail}</c>, <c>GET /api/router/preload/preview</c>).
    /// </summary>
    public static WebApplication MapRouterPreloadEndpoints(this WebApplication app)
    {
        // Model pre-load (task #44): router-owned, stateless + idempotent model warm-up. The
        // POST issues ONE bounded keep_alive-aware load directly to the selected daemon (never
        // through the routing middleware / YARP) plus a bounded /api/ps residency
        // confirmation; the GET preview is the EXACT same selection with zero side effects.
        // ROUTE NOTE (catch-all constraint): a catch-all parameter may only occupy the LAST
        // segment of a route template (the route parser rejects it mid-template), so the
        // spec's POST /api/router/models/{*modelName}/preload cannot be mapped verbatim.
        // The POST is therefore registered as the POST /api/router/models/{*tail} catch-all
        // (the POST method makes it distinct from the GET model-detail catch-all) and the
        // handler sniffs the mandatory trailing "/preload" suffix to recover the model name
        // (namespaced names with slashes keep working; a POST without the suffix is a 404).
        // The GET preview likewise cannot share the model-detail catch-all's template space
        // (same template, different action = ambiguous match), so it uses the spec's
        // sanctioned fallback shape: GET /api/router/preload/preview?model=...
        app.MapPost("/api/router/models/{*tail}", PreloadModelHandler)
            .WithName("PostRouterModelPreload")
            .WithTags("Fleet Operations")
            .WithSummary("Router-owned model pre-load on the selected fleet target.")
            .WithDescription("Registered as the catch-all POST /api/router/models/{*tail} route (a catch-all parameter may only "
                + "occupy the LAST segment of a template): the mandatory trailing '/preload' suffix is sniffed to recover the "
                + "model name — namespaced model names with slashes work, and a POST without the suffix → 404 'invalid preload "
                + "path'. Selects the fleet target that should host the model (capability-reuse selection: enabled + online + "
                + "present + hardware-capable + context-intent qualified, ranked idle-first, server/cloud-class-first for large "
                + "intents, least-busy then declaration order — the Cloud pseudo-target is never a preload candidate), issues ONE "
                + "bounded keep_alive-aware empty-prompt POST /api/generate load directly to that daemon (plus a bounded /api/ps "
                + "residency confirmation), and reports the selection, the applied keep_alive / num_ctx and the full candidate "
                + "evaluation. Per-node keep-alive convention: cloud/server-class = -1 (indefinite), desktop = 900 s. Parameters "
                + "(keepAliveSeconds, contextIntentTokens, target) via query string and/or an optional JSON body: an absent/empty "
                + "body is valid, a malformed body falls through to the query values, an explicit body is authoritative per field "
                + "(body wins over query). Fail-soft: a failed load/confirmation is 200 with loadAccepted/residencyConfirmed "
                + "false, never 5xx; 400 on keepAliveSeconds (must be -1 or positive) / contextIntentTokens (>= 1) validation; "
                + "404 on unknown model/pinned target; 503 when no candidate qualified (candidates carry the skip reasons). No key "
                + "required in default mode.");
        app.MapGet("/api/router/preload/preview", PreloadPreviewHandler)
            .WithName("GetRouterPreloadPreview")
            .WithTags("Fleet Operations")
            .WithSummary("Side-effect-free preload preview (query parameters only).")
            .WithDescription("The EXACT same selection as the POST preload with zero daemon calls (no /api/generate load, no "
                + "/api/ps residency confirmation — loadAccepted / residencyConfirmed / preloadDurationMs are null), paralleling "
                + "POST /api/router/context/evaluate. Query parameters: 'model' required (400 when missing), 'keepAliveSeconds', "
                + "'contextIntentTokens' and 'target' with the same validation and mapping as the POST (400/404/503). The model "
                + "name in the path is not expressible here (the GET model-detail catch-all owns that template space). No key "
                + "required in default mode.");
        return app;
    }

    // ------------------------------------------------------------------
    //  POST /api/router/models/{model}/preload (+ GET /api/router/preload/preview)
    // ------------------------------------------------------------------

    /// <summary>
    /// Model preload handler (task #44): unloads NOTHING — it selects the fleet target that
    /// should host the model and issues ONE bounded <c>keep_alive</c>-aware load call
    /// directly to that daemon, followed by a bounded /api/ps residency confirmation.
    /// Registered as the POST /api/router/models/{*tail} catch-all (a catch-all parameter
    /// can only be the LAST segment of a template); the mandatory trailing "/preload"
    /// suffix is sniffed here to recover the model name (case-insensitive suffix match,
    /// mirroring ASP.NET Core's case-insensitive path matching). Parameters are accepted
    /// via query string and/or an optional JSON body (body wins per field, the
    /// vram-reclaim pattern; an absent/empty body is valid, a malformed body falls through
    /// to the query values). Validation: keepAliveSeconds must be -1 (indefinite) or
    /// positive (0 / &lt;= -2 → 400); contextIntentTokens must be &gt;= 1 (else 400).
    /// Mapping: model or pinned target unknown → 404; no eligible candidate → 503
    /// (candidates carry the skip reasons); otherwise 200 with the full selection report
    /// (a failed load/confirmation is fail-soft: 200 with loadAccepted /
    /// residencyConfirmed false, never 5xx).
    /// </summary>
    private static async Task<IResult> PreloadModelHandler(
        HttpRequest request,
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        [FromQuery] int? keepAliveSeconds,
        [FromQuery] int? contextIntentTokens,
        [FromQuery] string? target,
        string tail,
        CancellationToken cancellationToken)
    {
        const string PreloadSuffix = "/preload";
        var trimmedTail = Uri.UnescapeDataString(tail).TrimStart('/');
        if (!trimmedTail.EndsWith(PreloadSuffix, StringComparison.OrdinalIgnoreCase) ||
            trimmedTail.Length == PreloadSuffix.Length)
        {
            return Results.Json(
                new OllamaErrorResponse("invalid preload path: expected /api/router/models/{model}/preload"),
                OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                statusCode: StatusCodes.Status404NotFound);
        }

        var modelName = trimmedTail[..^PreloadSuffix.Length];

        var (effectiveKeepAlive, effectiveIntent, effectiveTarget) = await ResolvePreloadParametersAsync(
            request, keepAliveSeconds, contextIntentTokens, target, cancellationToken).ConfigureAwait(false);

        return await ValidateAndExecutePreloadAsync(
            options, serviceProvider, modelName, effectiveKeepAlive, effectiveIntent, effectiveTarget,
            preview: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Side-effect-free preload preview (query parameters only, task #44): the EXACT same
    /// selection as the POST, with zero daemon calls (no /api/generate, no /api/ps
    /// load/confirmation) — the response reuses <see cref="RouterModelPreloadResponse"/>
    /// with loadAccepted / residencyConfirmed / preloadDurationMs null. Mapped at
    /// GET /api/router/preload/preview?model=... (the model-name-in-path shape is not
    /// expressible — see the route note above the mapping); the <c>model</c> query
    /// parameter is required (400 when missing). Same validation and 200/404/503 mapping.
    /// </summary>
    private static async Task<IResult> PreloadPreviewHandler(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        [FromQuery] string? model,
        [FromQuery] int? keepAliveSeconds,
        [FromQuery] int? contextIntentTokens,
        [FromQuery] string? target,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Results.Json(
                new OllamaErrorResponse("model query parameter is required"),
                OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var modelName = Uri.UnescapeDataString(model).TrimStart('/');
        return await ValidateAndExecutePreloadAsync(
            options, serviceProvider, modelName, keepAliveSeconds, contextIntentTokens, target,
            preview: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges the optional JSON body over the query parameters (the vram-reclaim pattern):
    /// an absent/empty body is valid (all nulls → query values); a malformed body falls
    /// through to the query values; a parsed body is authoritative PER FIELD (an absent field
    /// → the query value).
    /// </summary>
    private static async Task<(int? KeepAliveSeconds, int? ContextIntentTokens, string? Target)> ResolvePreloadParametersAsync(
        HttpRequest request,
        int? keepAliveSeconds,
        int? contextIntentTokens,
        string? target,
        CancellationToken cancellationToken)
    {
        RouterModelPreloadRequest? body = null;
        try
        {
            body = await System.Text.Json.JsonSerializer.DeserializeAsync(
                request.Body,
                OllamaRouterJsonSerializerContext.Default.RouterModelPreloadRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed JSON body: fall through to the query values (the established
            // reclaim/evaluate pattern — a bad body never 400s a request the query satisfies).
        }

        return (
            body is { KeepAliveSeconds: not null } ? body.KeepAliveSeconds : keepAliveSeconds,
            body is { ContextIntentTokens: not null } ? body.ContextIntentTokens : contextIntentTokens,
            body is not null && !string.IsNullOrWhiteSpace(body.Target) ? body.Target : target);
    }

    /// <summary>
    /// Shared validation + service invocation + status mapping of the preload endpoints
    /// (POST and GET preview): 400 on invalid keep_alive / context intent, 404 on the
    /// service's error strings ("model not found" / "target not found: {name}"), 503 when no
    /// candidate qualified (or the service is unregistered), 200 with the full report.
    /// </summary>
    private static async Task<IResult> ValidateAndExecutePreloadAsync(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        string modelName,
        int? keepAliveSeconds,
        int? contextIntentTokens,
        string? target,
        bool preview,
        CancellationToken cancellationToken)
    {
        if (keepAliveSeconds is { } keepAlive && (keepAlive == 0 || keepAlive <= -2))
        {
            return Results.Json(
                new OllamaErrorResponse("keepAliveSeconds must be -1 (indefinite) or a positive number of seconds"),
                OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (contextIntentTokens is { } intent and < 1)
        {
            return Results.Json(
                new OllamaErrorResponse("contextIntentTokens must be >= 1"),
                OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var preloadService = serviceProvider.GetService<IModelPreloadService>();
        if (preloadService is null)
        {
            // Defensive resolution (the file's minimal-host convention): a host without the
            // registered service cannot preload — same 503 vocabulary as "no capable target".
            return Results.Json(
                new OllamaErrorResponse("no capable target available for the requested context"),
                OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var result = preview
            ? await preloadService.PreviewAsync(modelName, keepAliveSeconds, contextIntentTokens, target, cancellationToken).ConfigureAwait(false)
            : await preloadService.PreloadAsync(modelName, keepAliveSeconds, contextIntentTokens, target, cancellationToken).ConfigureAwait(false);

        if (result is null || result.Error is not null)
        {
            return Results.Json(
                new OllamaErrorResponse(result?.Error ?? "model not found"),
                OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                statusCode: StatusCodes.Status404NotFound);
        }

        if (result.SelectedTarget is null)
        {
            return Results.Json(
                new OllamaErrorResponse("no capable target available for the requested context"),
                OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(
            new RouterModelPreloadResponse(
                Model: result.Model,
                SelectedTarget: result.SelectedTarget,
                AppliedKeepAliveSeconds: result.AppliedKeepAliveSeconds,
                AppliedNumCtx: result.AppliedNumCtx,
                ContextIntentTokens: result.ContextIntentTokens,
                LoadAccepted: result.LoadAccepted,
                ResidencyConfirmed: result.ResidencyConfirmed,
                PreloadDurationMs: result.PreloadDurationMs,
                Candidates: result.Candidates.Select(c => new RouterModelPreloadCandidate(
                    Target: c.Target,
                    Eligible: c.Eligible,
                    SkipReason: c.SkipReason,
                    InFlightRequests: c.InFlightRequests,
                    IsResident: c.IsResident,
                    ContextCeiling: c.ContextCeiling,
                    ServerClass: c.ServerClass,
                    Busy: c.Busy)).ToList(),
                Reason: result.Reason),
            OllamaRouterJsonSerializerContext.Default.RouterModelPreloadResponse);
    }
}
