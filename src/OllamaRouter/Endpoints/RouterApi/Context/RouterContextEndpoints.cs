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

namespace OllamaRouter.Endpoints.RouterApi.Context;

/// <summary>
/// The stateless context evaluation endpoint of the OllamaRouter programmatic management API:
/// <list type="bullet">
/// <item><c>POST /api/router/context/evaluate</c> — stateless routing + dynamic context sizing
/// preview: which target would service a request for the given prompt size and which
/// num_ctx bucket would be injected (no LLM inference; only catalog /api/tags + /api/show
/// discovery).</item>
/// </list>
/// </summary>

public static class RouterContextEndpoints
{
    /// <summary>
    /// Maps <c>POST /api/router/context/evaluate</c>.
    /// </summary>
    public static WebApplication MapRouterContextEndpoints(this WebApplication app)
    {
        app.MapPost("/api/router/context/evaluate", async (
            HttpRequest request,
            IOptionsMonitor<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
        {
            RouterContextEvaluateRequest? evaluateRequest = null;
            try
            {
                evaluateRequest = await System.Text.Json.JsonSerializer.DeserializeAsync(
                    request.Body,
                    OllamaRouterJsonSerializerContext.Default.RouterContextEvaluateRequest,
                    cancellationToken);
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed JSON body: fall through to the validation errors below.
            }

            if (string.IsNullOrWhiteSpace(evaluateRequest?.Model))
            {
                return Results.Json(
                    new OllamaErrorResponse("model is required"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (evaluateRequest.EstimatedTokens < 1)
            {
                return Results.Json(
                    new OllamaErrorResponse("estimatedTokens must be >= 1"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await EvaluateContextPreviewAsync(options, serviceProvider, evaluateRequest, cancellationToken);
            if (result is null)
            {
                return Results.Json(
                    new OllamaErrorResponse("no available routing target"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Json(result, OllamaRouterJsonSerializerContext.Default.RouterContextEvaluateResponse);
        })
        .WithName("PostRouterContextEvaluate")
        .WithTags("Fleet Operations")
        .WithSummary("Stateless routing + dynamic context sizing preview.")
        .WithDescription("Which target would service a request for the given prompt size and which num_ctx bucket would be "
            + "injected — runs the exact production routing decision plus the sizing engine preview (no LLM inference, no "
            + "payload rewriting, no lease mutations; only the catalog /api/tags + /api/show discovery the pipeline itself "
            + "performs). Request body: 'model' required, 'estimatedTokens' >= 1 (otherwise 400; a malformed body falls "
            + "through to these validation errors), 'clientRequestedNumCtx' optional (the client's static options.num_ctx). "
            + "503 = no available routing target. No key required in default mode.");
        return app;
    }

    // ------------------------------------------------------------------
    //  POST /api/router/context/evaluate
    // ------------------------------------------------------------------

    /// <summary>
    /// Stateless routing + sizing preview: runs the EXACT production routing decision
    /// (including the failure-streak switching) through <see cref="RoutingDecisionService"/>,
    /// then previews the <see cref="DynamicContextSizingEngine"/> bucket that the selected
    /// target would receive for the prompt. No payload rewriting, no lease mutations, no
    /// LLM inference (only the catalog /api/tags + /api/show discovery the pipeline itself
    /// performs). Returns null (→ 503) when the decision pipeline throws
    /// <see cref="NoAvailableTargetException"/> or no decision service is registered.
    /// </summary>
    private static async Task<RouterContextEvaluateResponse?> EvaluateContextPreviewAsync(
        IOptionsMonitor<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        RouterContextEvaluateRequest request,
        CancellationToken cancellationToken)
    {
        var opt = options.CurrentValue;
        var decisionService = serviceProvider.GetService<IRoutingDecisionService>() as RoutingDecisionService;
        if (decisionService is null)
        {
            // Minimal host without the decision pipeline: no target can be predicted.
            return null;
        }

        RoutingTarget selected;
        var evaluations = new List<RoutingCandidateEvaluation>();
        try
        {
            selected = await decisionService.DecideWithEvaluationsAsync(
                request.EstimatedTokens,
                request.Model,
                request.ClientRequestedNumCtx,
                reportEvaluation: evaluations.Add,
                cancellationToken).ConfigureAwait(false);
        }
        catch (NoAvailableTargetException)
        {
            return null;
        }

        var affinityScore = evaluations.FirstOrDefault(e => e.Target == selected)?.Score ?? 0;
        var estimatedTokensWithMargin = request.EstimatedTokens + Math.Max(0, opt.ContextFitMarginTokens);

        // Cloud pseudo-target: the preview cannot know a cloud daemon's ceiling; it reports
        // what the client's/static value would flow through (contextMode "expanded").
        if (selected == RoutingTarget.Cloud)
        {
            return new RouterContextEvaluateResponse(
                SelectedTarget: RoutingTarget.Cloud.Value,
                InjectedNumCtx: request.ClientRequestedNumCtx ?? estimatedTokensWithMargin,
                ContextMode: "expanded",
                EstimatedTokensWithMargin: estimatedTokensWithMargin,
                TargetMaxContext: 0,
                AffinityScore: affinityScore);
        }

        var targetOptions = opt.GetEffectiveTargets()
            .FirstOrDefault(t => string.Equals(t.Name, selected.Value, StringComparison.OrdinalIgnoreCase));
        if (targetOptions is null || string.IsNullOrWhiteSpace(targetOptions.Url))
        {
            // Selected a target with no configured URL: pass-through preview.
            return new RouterContextEvaluateResponse(
                SelectedTarget: selected.Value,
                InjectedNumCtx: request.ClientRequestedNumCtx ?? estimatedTokensWithMargin,
                ContextMode: "baseline",
                EstimatedTokensWithMargin: estimatedTokensWithMargin,
                TargetMaxContext: 0,
                AffinityScore: affinityScore);
        }

        // Dynamic context disabled: the middleware never rewrites the payload (pass-through).
        if (!opt.DynamicContext.Enabled)
        {
            return new RouterContextEvaluateResponse(
                SelectedTarget: selected.Value,
                InjectedNumCtx: request.ClientRequestedNumCtx ?? estimatedTokensWithMargin,
                ContextMode: "baseline",
                EstimatedTokensWithMargin: estimatedTokensWithMargin,
                TargetMaxContext: 0,
                AffinityScore: affinityScore);
        }

        var capabilityEngine = serviceProvider.GetService<ModelCapabilityEngine>();
        var catalogCache = serviceProvider.GetService<IModelCatalogCacheService>();
        if (capabilityEngine is null || catalogCache is null)
        {
            // No capability stack: pass-through preview.
            return new RouterContextEvaluateResponse(
                SelectedTarget: selected.Value,
                InjectedNumCtx: request.ClientRequestedNumCtx ?? estimatedTokensWithMargin,
                ContextMode: "baseline",
                EstimatedTokensWithMargin: estimatedTokensWithMargin,
                TargetMaxContext: 0,
                AffinityScore: affinityScore);
        }

        ModelMetadata? metadata = null;
        try
        {
            var digest = await catalogCache.GetCurrentDigestAsync(targetOptions.Url, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(digest))
            {
                metadata = await capabilityEngine.GetMetadataAsync(targetOptions.Url, request.Model, digest, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            metadata = null;
        }

        var normalizedName = RoutingDecisionService.NormalizeModelName(request.Model);
        var pinnedArchitecture = opt.Models.TryGetValue(normalizedName, out var thresholds) ? thresholds.Architecture : null;

        var capability = metadata is not null
            ? capabilityEngine.DeriveCapability(metadata, selected.Value, targetOptions, pinnedArchitecture)
            : null;

        if (capability is null)
        {
            // Undiscoverable capability for the selected fleet target: pass-through preview
            // (the middleware behaves exactly this way — it never rewrites the payload).
            return new RouterContextEvaluateResponse(
                SelectedTarget: selected.Value,
                InjectedNumCtx: request.ClientRequestedNumCtx ?? estimatedTokensWithMargin,
                ContextMode: "baseline",
                EstimatedTokensWithMargin: estimatedTokensWithMargin,
                TargetMaxContext: 0,
                AffinityScore: affinityScore);
        }

        var sizing = DynamicContextSizingEngine.Decide(
            opt.DynamicContext,
            capability,
            request.EstimatedTokens,
            request.ClientRequestedNumCtx,
            Math.Max(0, opt.ContextFitMarginTokens),
            thresholds?.MaxContextTokens);

        // contextMode vocabulary (task #26, lowercase engine modes): passthrough (dense,
        // untouched — injectedNumCtx reports what would flow through, matching the existing
        // degraded-preview convention), clamped (dense, down-clamped), baseline/expanded
        // (MoE two-bucket ladder).
        var (injectedNumCtx, contextMode) = sizing.Mode switch
        {
            ContextSizingMode.PassThrough => (request.ClientRequestedNumCtx ?? estimatedTokensWithMargin, "passthrough"),
            _ => (sizing.InjectedNumCtx, sizing.Mode.ToString().ToLowerInvariant())
        };

        return new RouterContextEvaluateResponse(
            SelectedTarget: selected.Value,
            InjectedNumCtx: injectedNumCtx,
            ContextMode: contextMode,
            EstimatedTokensWithMargin: estimatedTokensWithMargin,
            TargetMaxContext: sizing.EffectiveCeiling,
            AffinityScore: affinityScore);
    }
}
