using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Endpoints;

/// <summary>
/// Lightweight, programmatic management API for the OllamaRouter gateway:
/// <list type="bullet">
/// <item><c>GET /api/router/info</c> — instantly differentiates OllamaRouter from a
/// standard Ollama daemon (which returns 404 for this route) and reports the router
/// version, mode, fleet summary and active features. Pure in-memory snapshot reads.</item>
/// <item><c>GET /api/router/fleet</c> — the full fleet hardware topology: every configured
/// target (in configuration order) plus the Cloud pseudo-target (last), with real-time
/// liveness state, last-probe latency, detected GPU hardware (local workstation only),
/// in-flight request count, currently loaded model and the active KV-cache context lease.</item>
/// <item><c>GET /api/router/models</c> and <c>GET /api/router/models/{modelName}</c> — the
/// zero-config model capability catalog: per-model architecture classification (Dense/Moe),
/// parameter count, native Modelfile num_ctx and the router-calculated hardware-safe context
/// ceiling (recommendedContextWindow = max CMax over the hardware-capable fleet targets;
/// cloud overflow is never a ceiling contributor, only the cloudOverflowAvailable hint).</item>
/// <item><c>GET /api/router/cartography</c> — the fleet cartography: in one call, every fleet
/// node (hardware summary, liveness, context lease allocation, VRAM totals, its currently
/// loaded processes with the live /api/ps quantization) plus every fleet model with its
/// catalog-sourced quantization and per-node deployment matrix (present / capable / context
/// ceiling / resident / loaded footprint), plus a flat list of all loaded model processes.
/// Reads in-memory tracker/liveness snapshots and the 30 s single-flight capability cache;
/// it NEVER triggers a fresh /api/ps refresh.</item>
/// <item><c>POST /api/router/vram/reclaim</c> and <c>POST /api/router/fleet/unload</c> (exact
/// alias, identical contract) — fleet VRAM reclaim: unloads the running models on one named
/// target (case-insensitive; unknown name → 404) or on ALL effective configured targets
/// (fail-soft per target) and optionally administratively disables the processed target(s);
/// KV-cache leases of every processed target are reset to the baseline bucket.</item>
/// <item><c>POST /api/router/context/evaluate</c> — stateless routing + dynamic context sizing
/// preview: which target would service a request for the given prompt size and which
/// num_ctx bucket would be injected (no LLM inference; only catalog /api/tags + /api/show
/// discovery).</item>
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
/// The /info and /fleet handlers build exclusively from in-memory snapshots
/// (<c>GetEntries</c>/<c>GetState</c>/<c>GetLeaseSnapshot</c>/availability flags/configuration)
/// — no statistics, no history/7-day aggregation, and no blocking per-target HTTP (only the
/// single awaited local GPU CLI snapshot when a local workstation target is present). All
/// enrichment services are resolved defensively (<see cref="ServiceProvider.GetService(Type)"/>)
/// so a minimal host with only options + availability still serves every endpoint.
/// </summary>
public static class RouterApiEndpointsExtensions
{
    public static WebApplication MapOllamaRouterApiEndpoints(this WebApplication app)
    {
        app.MapGet("/api/router/info", async (
            IOptions<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
        {
            var (fleet, features) = await BuildInfoBlocksAsync(options, serviceProvider, cancellationToken);
            return Results.Json(new RouterInfoResponse(GetRouterVersion(), true, fleet, features),
                OllamaRouterJsonSerializerContext.Default.RouterInfoResponse);
        });

        app.MapGet("/api/router/fleet", async (
            IOptions<OllamaRouterOptions> options,
            ITargetAvailabilityService targetAvailability,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
        {
            var response = await BuildFleetResponseAsync(options, targetAvailability, serviceProvider, cancellationToken);
            return Results.Json(response, OllamaRouterJsonSerializerContext.Default.RouterFleetResponse);
        });

        // Fleet hardware profiling engine: the local node's own profile (cached after the
        // first probe), the full fleet topology (parallel peer probes, fail-soft per node),
        // and the manual profile push (for bare Ollama targets without a router daemon).
        app.MapGet("/api/router/hardware", async (IHardwareProfileProvider provider, CancellationToken cancellationToken) =>
        {
            var profile = await provider.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(profile, OllamaRouterJsonSerializerContext.Default.NodeHardwareProfile);
        });

        app.MapGet("/api/router/fleet/hardware", async (IFleetHardwareService fleetHardware, CancellationToken cancellationToken) =>
        {
            var nodes = await fleetHardware.GetFleetHardwareAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(new RouterFleetHardwareResponse(nodes), OllamaRouterJsonSerializerContext.Default.RouterFleetHardwareResponse);
        });

        app.MapPut("/api/router/fleet/hardware/{targetName}", async (HttpRequest request, string targetName, IFleetHardwareService fleetHardware, CancellationToken cancellationToken) =>
        {
            NodeHardwareProfile? profile = null;
            try
            {
                profile = await JsonSerializer.DeserializeAsync(
                    request.Body,
                    OllamaRouterJsonSerializerContext.Default.NodeHardwareProfile,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Malformed JSON body → 400 below.
            }

            if (profile is null || string.IsNullOrWhiteSpace(profile.NodeName))
            {
                return Results.Json(
                    new OllamaErrorResponse("invalid or missing hardware profile body"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var stored = await fleetHardware.UpsertProfileAsync(targetName, profile, cancellationToken).ConfigureAwait(false);
            if (!stored)
            {
                return Results.Json(
                    new OllamaErrorResponse($"target not found: {targetName}"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(profile, OllamaRouterJsonSerializerContext.Default.NodeHardwareProfile);
        });

        // Model benchmarking (task #28): non-blocking run trigger (202), single-flight 409,
        // results query (including in-flight Running rows) and result clearing.
        app.MapPost("/api/router/benchmark", async (
            HttpRequest request,
            IMicroBenchmarkRunner benchmarkRunner,
            CancellationToken cancellationToken) =>
        {
            // Null / empty body is valid = benchmark everything (fleet-wide zero-config flow).
            RouterBenchmarkRequest? benchmarkRequest = null;
            try
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    benchmarkRequest = JsonSerializer.Deserialize(
                        body, OllamaRouterJsonSerializerContext.Default.RouterBenchmarkRequest);
                }
            }
            catch (JsonException)
            {
                // Malformed JSON body → 400 below.
                return Results.Json(
                    new OllamaErrorResponse("invalid or missing benchmark request body"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var requestedTarget = benchmarkRequest?.Target;
            if (!string.IsNullOrWhiteSpace(requestedTarget) &&
                !benchmarkRunner.GetEffectiveTargetNames()
                    .Any(n => string.Equals(n, requestedTarget, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Json(
                    new OllamaErrorResponse($"target not found: {requestedTarget}"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            // Null target (absent body or omitted field) → all effective targets. Build the
            // struct explicitly and cast the null branch to RoutingTarget? — a bare `null`
            // in a conditional with a struct other-branch adopts the struct type (yielding
            // default(RoutingTarget), which lifts to a *non-null* value with an empty name).
            RoutingTarget? target = string.IsNullOrWhiteSpace(requestedTarget)
                ? (RoutingTarget?)null
                : new RoutingTarget(requestedTarget);
            var started = benchmarkRunner.TryStartRun(
                target,
                benchmarkRequest?.Roles,
                benchmarkRequest?.Model);
            if (!started)
            {
                return Results.Json(
                    new OllamaErrorResponse("benchmark run already in progress"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            var queuedTargets = string.IsNullOrWhiteSpace(requestedTarget)
                ? benchmarkRunner.GetEffectiveTargetNames().ToList()
                : [requestedTarget!];

            // 202: accepted and queued, the run loop drains the targets sequentially in the
            // background — this response never waits for the benchmarks to finish.
            return Results.Json(
                new RouterBenchmarkRunResponse(true, queuedTargets),
                OllamaRouterJsonSerializerContext.Default.RouterBenchmarkRunResponse,
                statusCode: StatusCodes.Status202Accepted);
        });

        app.MapGet("/api/router/benchmark/results", (IMicroBenchmarkRunner benchmarkRunner) =>
        {
            var response = new RouterBenchmarkResultsResponse(
                benchmarkRunner.GetAllResults());
            return Results.Json(response, OllamaRouterJsonSerializerContext.Default.RouterBenchmarkResultsResponse);
        });

        app.MapDelete("/api/router/benchmark/results", (IMicroBenchmarkRunner benchmarkRunner) =>
        {
            // In-flight runs keep their Running states: the delete applies to completed
            // targets only and the background work continues for its remaining targets.
            benchmarkRunner.ClearResults(keepRunning: true);
            return Results.Json(
                new RouterBenchmarkClearedResponse(true),
                OllamaRouterJsonSerializerContext.Default.RouterBenchmarkClearedResponse);
        });

        app.MapGet("/api/router/models", async (
            IOptions<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var response = await BuildModelsListAsync(options, serviceProvider, context, cancellationToken);
            return Results.Json(response, OllamaRouterJsonSerializerContext.Default.RouterModelsResponse);
        });

        app.MapGet("/api/router/models/{*modelName}", async (
            IOptions<OllamaRouterOptions> options,
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
        });

        // Fleet cartography (task #41): the model-to-node deployment map — every node's
        // hardware/context/VRAM/loaded processes, every model's catalog-sourced quantization
        // and per-target deployment matrix, and a flat list of all loaded processes. Reads
        // in-memory snapshots + the 30 s capability cache; never calls /api/ps refresh.
        app.MapGet("/api/router/cartography", async (
            IOptions<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var response = await BuildCartographyResponseAsync(options, serviceProvider, context, cancellationToken);
            return Results.Json(response, OllamaRouterJsonSerializerContext.Default.RouterCartographyResponse);
        });

        // Canonical fleet VRAM reclaim + exact alias (identical handler, parameters and
        // response shape). Both paths unload the running models on the selected effective
        // target(s) — or ALL effective targets when no target is specified (fail-soft per
        // target; the Cloud pseudo-target is never part of the effective list).
        app.MapPost("/api/router/vram/reclaim", ReclaimVramHandler);
        app.MapPost("/api/router/fleet/unload", ReclaimVramHandler);

        app.MapPost("/api/router/context/evaluate", async (
            HttpRequest request,
            IOptions<OllamaRouterOptions> options,
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
            });

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
        app.MapPost("/api/router/models/{*tail}", PreloadModelHandler);
        app.MapGet("/api/router/preload/preview", PreloadPreviewHandler);

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
        IOptions<OllamaRouterOptions> options,
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
        IOptions<OllamaRouterOptions> options,
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
        IOptions<OllamaRouterOptions> options,
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
        IOptions<OllamaRouterOptions> options,
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

        var effectiveTargets = options.Value.GetEffectiveTargets();
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

    // ------------------------------------------------------------------
    //  GET /api/router/info
    // ------------------------------------------------------------------

    private static async Task<(RouterFleetSummary Fleet, RouterFeatures Features)> BuildInfoBlocksAsync(
        IOptions<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var opt = options.Value;
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

    private static async Task<bool> CatalogHasAnyModelsAsync(
        IModelCatalogCacheService? modelCatalogCache,
        string? url,
        CancellationToken cancellationToken)
    {
        if (modelCatalogCache is null || string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            return await modelCatalogCache.HasAnyModelsAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A failing catalog check must never break the info payload.
            return false;
        }
    }

    private static string GetRouterVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    // ------------------------------------------------------------------
    //  GET /api/router/fleet
    // ------------------------------------------------------------------

    private static async Task<RouterFleetResponse> BuildFleetResponseAsync(
        IOptions<OllamaRouterOptions> options,
        ITargetAvailabilityService targetAvailability,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var opt = options.Value;
        var effectiveTargets = opt.GetEffectiveTargets();
        var availability = targetAvailability.GetSnapshot();
        var liveness = serviceProvider.GetService<ITargetLivenessService>();
        var livenessEntries = liveness?.GetEntries();
        var executionTracker = serviceProvider.GetService<ITargetExecutionTracker>();
        var leaseSnapshot = serviceProvider.GetService<IContextAutoShrinkManager>()?.GetLeaseSnapshot();
        var modelCatalogCache = serviceProvider.GetService<IModelCatalogCacheService>();
        var fleetHardware = serviceProvider.GetService<IFleetHardwareService>();

        var targets = new List<RouterTargetItem>(effectiveTargets.Count + 1);
        foreach (var target in effectiveTargets)
        {
            var name = target.Name!;
            var routingTarget = (RoutingTarget)name;
            var enabled = availability.Flags.TryGetValue(routingTarget, out var targetEnabled) ? targetEnabled : true;

            // Liveness: prefer the raw (non-TTL) liveness state machine; fall back to the
            // catalog-based check when the liveness service is not registered.
            string livenessText;
            long? latencyMs = null;
            if (livenessEntries is { } entries && entries.TryGetValue(routingTarget, out var entry))
            {
                livenessText = entry.State switch
                {
                    TargetLivenessState.Online => "Online",
                    TargetLivenessState.Offline => "Offline",
                    _ => "Probing"
                };
                latencyMs = entry.State == TargetLivenessState.Online ? entry.LastProbeLatencyMs : null;
            }
            else
            {
                livenessText = await CatalogHasAnyModelsAsync(modelCatalogCache, target.Url, cancellationToken)
                    ? "Online"
                    : "Offline";
            }

            var executionState = executionTracker?.GetState(routingTarget);
            var activeModel = executionState?.LoadedModels.FirstOrDefault()?.Name;
            var inFlight = executionState?.InFlightRequests ?? 0;

            // speedFactor: always present; null when the effective factor is 1.0 (unset/default),
            // consistent with maxVramMB's nullable semantics.
            var speedFactor = target.GetEffectiveSpeedFactor();

            // Hardware details served directly from the cached fleet hardware store
            // (all targets, including local, without initiating redundant local probes).
            NodeHardwareProfile? nodeProfile = null;
            try
            {
                nodeProfile = fleetHardware?.GetProfile(name);
            }
            catch
            {
                nodeProfile = null;
            }

            string gpuVendor = "Unknown";
            string? gpuName = null;
            IReadOnlyList<string> devices = Array.Empty<string>();

            if (nodeProfile is not null)
            {
                if (nodeProfile.Gpus.Count > 0)
                {
                    gpuVendor = nodeProfile.Gpus[0].Vendor;
                    gpuName = nodeProfile.Gpus[0].ModelName;
                    devices = nodeProfile.Gpus.Select(FormatGpuDevice).ToArray();
                }
                else if (nodeProfile.IsUnifiedMemory)
                {
                    gpuVendor = string.Equals(nodeProfile.Architecture, "arm64", StringComparison.OrdinalIgnoreCase)
                        ? "Apple"
                        : "Unified";
                    gpuName = $"{nodeProfile.Architecture} Unified Memory";
                    devices = [nodeProfile.SystemRamMB > 0 ? $"{gpuName} ({nodeProfile.SystemRamMB} MB)" : gpuName];
                }
            }

            targets.Add(new RouterTargetItem(
                Name: name,
                Url: target.Url,
                Enabled: enabled,
                Liveness: livenessText,
                LatencyMs: latencyMs,
                Location: TargetLocationClassifier.ToWireValue(TargetLocationClassifier.Classify(target)),
                Hardware: new RouterTargetHardware(
                    GpuVendor: gpuVendor,
                    Devices: devices,
                    MaxVramMB: target.GetEffectiveMaxVramMB(fleetHardware),
                    MaxParallel: target.MaxParallel,
                    SpeedFactor: speedFactor == 1.0 ? null : speedFactor,
                    IsUnifiedMemory: nodeProfile?.IsUnifiedMemory ?? false,
                    SystemRamMB: nodeProfile?.SystemRamMB is { } ram and > 0 ? ram : null,
                    MemoryType: nodeProfile is null ? null : DescribeMemoryTier(nodeProfile),
                    BandwidthGBs: nodeProfile?.EffectiveBandwidthGBs is { } bw and > 0 ? bw : null,
                    GpuName: gpuName),
                Execution: new RouterTargetExecution(
                    InFlightRequests: inFlight,
                    ActiveModel: activeModel,
                    ContextAllocation: FormatContextAllocation(routingTarget, leaseSnapshot))));
        }

        // The Cloud pseudo-target is never probed/tracked: it is "Online" iff cloud is
        // enabled, never has a loaded model or in-flight requests, and never carries a
        // context lease or a latency measurement.
        var cloudEnabled = opt.CloudOverflow.Enabled;
        var cloudFlag = availability.Flags.TryGetValue(RoutingTarget.Cloud, out var cloudEnabledFlag) ? cloudEnabledFlag : cloudEnabled;
        targets.Add(new RouterTargetItem(
            Name: RoutingTarget.Cloud.Value,
            Url: null,
            Enabled: cloudFlag,
            Liveness: cloudEnabled ? "Online" : "Offline",
            LatencyMs: null,
            Location: TargetLocationClassifier.ToWireValue(TargetLocation.Cloud),
            Hardware: new RouterTargetHardware(
                GpuVendor: "Cloud",
                Devices: new[] { "Cloud Overflow" },
                MaxVramMB: null,
                MaxParallel: 10,
                SpeedFactor: null,
                GpuName: "Cloud Overflow"),
            Execution: new RouterTargetExecution(
                InFlightRequests: 0,
                ActiveModel: null,
                ContextAllocation: "baseline")));

        return new RouterFleetResponse(targets);
    }

    private static string FormatGpuDevice(GpuDeviceProfile gpu)
    {
        var displayName = gpu.ModelName.StartsWith(gpu.Vendor, StringComparison.OrdinalIgnoreCase)
            ? gpu.ModelName
            : $"{gpu.Vendor} {gpu.ModelName}";
        return gpu.VramMB > 0 ? $"{displayName} ({gpu.VramMB} MB)" : displayName;
    }

    /// <summary>
    /// Human-readable memory tier label for the fleet view: the system RAM tier wins
    /// ("DDR5", "LPDDR5X", "AppleUnified"), falling back to the fastest GPU VRAM tier
    /// ("GDDR6X", "HBM3") and finally "Unknown".
    /// </summary>
    private static string DescribeMemoryTier(NodeHardwareProfile profile)
    {
        // Canonical tier labels (spec examples: "DDR5", "LPDDR5X", "AppleUnified", "GDDR6X").
        var ramLabel = profile.SystemRamType switch
        {
            SystemRamType.Ddr3 => "DDR3",
            SystemRamType.Ddr4 => "DDR4",
            SystemRamType.Ddr5 => "DDR5",
            SystemRamType.LpDdr5X => "LPDDR5X",
            SystemRamType.AppleUnified => "AppleUnified",
            _ => null
        };
        if (ramLabel is not null)
        {
            return ramLabel;
        }

        if (profile.Gpus is { Count: > 0 })
        {
            var types = profile.Gpus.Select(g => g.VramType)
                .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, "Unknown", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();
            if (types.Count > 0)
            {
                return string.Join("/", types);
            }
        }

        return "Unknown";
    }

    // Renders the current context allocation for one fleet target from the lease snapshot:
    // any expanded lease -> "<maxBucket>k (expanded)"; a single baseline lease ->
    // "<bucket>k"; no leases at all (or no manager) -> "baseline". Cloud is always "baseline".
    private static string FormatContextAllocation(
        RoutingTarget target,
        IReadOnlyDictionary<RoutingTarget, IReadOnlyList<ContextLeaseView>>? leaseSnapshot)
    {
        if (target == RoutingTarget.Cloud || leaseSnapshot is null ||
            !leaseSnapshot.TryGetValue(target, out var leases) || leases.Count == 0)
        {
            return "baseline";
        }

        if (leases.Any(lease => lease.IsExpanded))
        {
            return $"{FormatBucketTokens(leases.Max(lease => lease.ActiveBucket))} (expanded)";
        }

        return FormatBucketTokens(leases[0].ActiveBucket);
    }

    private static string FormatBucketTokens(int tokens)
    {
        if (tokens >= 1_000_000)
        {
            return TrimTrailingZero((tokens / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + "M";
        }
        if (tokens >= 1_000)
        {
            return TrimTrailingZero((tokens / 1_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + "k";
        }

        return tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // "32.0" → "32" (spec string set: "32k", "262.1k", "1.2M"); "65.5" stays "65.5".
    private static string TrimTrailingZero(string value) => value.EndsWith(".0", StringComparison.Ordinal)
        ? value[..^2]
        : value;

    // ------------------------------------------------------------------
    //  GET /api/router/models + GET /api/router/models/{modelName}
    // ------------------------------------------------------------------

    private static async Task<RouterModelsResponse> BuildModelsListAsync(
        IOptions<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var opt = options.Value;
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
        IOptions<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        HttpContext context,
        string modelName,
        CancellationToken cancellationToken)
    {
        var opt = options.Value;
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
    /// Merged fleet model names (first-seen order) plus the cloud synthesis names (appended
    /// when not already present): the unified catalog of local, remote LAN nodes, and cloud
    /// overflow models. The cloud syntheses are included only when the Cloud overflow switch
    /// AND the targets.json Cloud toggle are both on (same rules as the /api/tags endpoint).
    /// </summary>
    internal static async Task<List<string>> GetFleetModelNamesAsync(
        OllamaRouterOptions opt,
        ITargetAvailabilityService? availability,
        IFleetAggregationService? fleetAggregation,
        IModelCatalogCacheService? catalogCache,
        HttpContext? context,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();

        if (fleetAggregation is not null)
        {
            try
            {
                var merged = await fleetAggregation.GetTagsFleetAsync(
                    BuildCloudTagSyntheses(opt, availability), context).ConfigureAwait(false);
                foreach (var entry in merged)
                {
                    if (entry?["name"]?.ToString() is { Length: > 0 } name &&
                        ShouldIncludeModel(name, opt) &&
                        !ContainsName(names, name))
                    {
                        names.Add(name);
                    }
                }
            }
            catch
            {
                // A failing aggregation must never break the model capability listing.
            }
        }
        else if (catalogCache is not null)
        {
            // Degraded enumeration: no fan-out service, so read each effective target's
            // catalog directly in declaration order.
            foreach (var target in opt.GetEffectiveTargets())
            {
                if (string.IsNullOrWhiteSpace(target.Url))
                {
                    continue;
                }

                try
                {
                    foreach (var name in await catalogCache.RefreshAsync(target.Url, cancellationToken).ConfigureAwait(false))
                    {
                        if (ShouldIncludeModel(name, opt) && !ContainsName(names, name))
                        {
                            names.Add(name);
                        }
                    }
                }
                catch
                {
                    // Unreachable target contributes nothing.
                }
            }

            foreach (var synthesis in BuildCloudTagSyntheses(opt, availability) ?? [])
            {
                if (synthesis?["name"]?.ToString() is { Length: > 0 } name &&
                    ShouldIncludeModel(name, opt) &&
                    !ContainsName(names, name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Cloud synthesis model names (per-model <c>Models[*].CloudModel</c> in configuration
    /// order, then <c>CloudOverflow.DefaultModel</c>): the same rules and JSON shape as the
    /// /api/tags endpoint's syntheses. Returns null when Cloud overflow is unavailable
    /// (static switch off OR the targets.json Cloud toggle off).
    /// </summary>
    private static IReadOnlyList<JsonNode>? BuildCloudTagSyntheses(OllamaRouterOptions options, ITargetAvailabilityService? availability)
    {
        if (!options.CloudOverflow.Enabled ||
            (availability is not null && !availability.IsEnabled(RoutingTarget.Cloud)))
        {
            return null;
        }

        var syntheses = new List<JsonNode>();
        if (options.Models != null)
        {
            foreach (var (_, modelOpts) in options.Models)
            {
                if (!string.IsNullOrWhiteSpace(modelOpts.CloudModel))
                {
                    syntheses.Add(MakeCloudSynthesisNode(modelOpts.CloudModel));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(options.CloudOverflow.DefaultModel))
        {
            syntheses.Add(MakeCloudSynthesisNode(options.CloudOverflow.DefaultModel));
        }

        return syntheses.Count > 0 ? syntheses : null;
    }

    private static JsonNode MakeCloudSynthesisNode(string cloudName) => new JsonObject
    {
        ["name"] = cloudName,
        ["model"] = cloudName,
        ["modified_at"] = DateTime.UtcNow.ToString("o"),
        ["size"] = 0,
        ["details"] = new JsonObject
        {
            ["family"] = "cloud",
            ["parameter_size"] = "cloud",
            ["quantization_level"] = "cloud"
        }
    };

    /// <summary>
    /// Resolves an incoming model name against the fleet catalog: case-insensitive exact
    /// full name (tag included) first, then the tagless-prefix fallback (strip the incoming
    /// name after ':' — or use the whole tagless name — and match any catalog entry sharing
    /// that prefix, normally the ":latest" variant). Cloud synthesis names are catalog
    /// entries like any other.
    /// Returns null (→ 404) when nothing matches.
    /// </summary>
    internal static string? ResolveModelName(string requested, IReadOnlyList<string> names)
    {
        var exact = names.FirstOrDefault(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var colonIndex = requested.IndexOf(':');
        var prefix = colonIndex > 0 ? requested[..colonIndex] : requested;
        if (prefix.Length == 0)
        {
            return null;
        }

        return names.FirstOrDefault(name =>
            name.IndexOf(':') > 0 &&
            string.Equals(name[..name.IndexOf(':')], prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsName(IReadOnlyList<string> names, string candidate) =>
        names.Any(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when any effective target's cached /api/tags catalog contains the model
    /// (case-insensitive name match). Null catalog cache ⇒ false.
    /// </summary>
    private static async Task<bool> AnyTargetHasModelAsync(
        IModelCatalogCacheService? catalogCache,
        IReadOnlyList<OllamaTargetOptions> effectiveTargets,
        string modelName,
        CancellationToken cancellationToken)
    {
        if (catalogCache is null)
        {
            return false;
        }

        foreach (var target in effectiveTargets)
        {
            if (string.IsNullOrWhiteSpace(target.Url))
            {
                continue;
            }

            try
            {
                if (await catalogCache.ModelExistsAsync(target.Url, modelName, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch
            {
                // A failing catalog check must never break the capability report.
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether a model should be included in the fleet model listing.
    /// When FilterNamespacedModels is true (default), models containing a namespace separator ('/' or '\')
    /// are filtered out unless explicitly declared in <see cref="OllamaRouterOptions.Models"/> or
    /// configured as a Cloud Overflow model.
    /// </summary>
    internal static bool ShouldIncludeModel(string name, OllamaRouterOptions opt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (!opt.FilterNamespacedModels)
        {
            return true;
        }

        // Native library models and local aliases do not contain a namespace separator ('/' or '\').
        if (!name.Contains('/') && !name.Contains('\\'))
        {
            return true;
        }

        // Namespaced models (e.g. "jetelain/*") are filtered out unless explicitly configured.
        return IsModelConfigured(name, opt);
    }

    /// <summary>
    /// Returns true if the model is explicitly configured in <see cref="OllamaRouterOptions.Models"/>
    /// or <see cref="OllamaRouterOptions.CloudOverflow"/>.
    /// </summary>
    internal static bool IsModelConfigured(string name, OllamaRouterOptions opt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = RoutingDecisionService.NormalizeModelName(name);

        if (opt.Models.Count > 0)
        {
            foreach (var key in opt.Models.Keys)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var normalizedKey = RoutingDecisionService.NormalizeModelName(key);
                if (string.Equals(normalizedKey, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalizedKey, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (var threshold in opt.Models.Values)
            {
                if (!string.IsNullOrEmpty(threshold.CloudModel))
                {
                    var normalizedCloud = RoutingDecisionService.NormalizeModelName(threshold.CloudModel);
                    if (string.Equals(threshold.CloudModel, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(threshold.CloudModel, normalized, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normalizedCloud, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normalizedCloud, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(opt.CloudOverflow.DefaultModel))
        {
            var normalizedDefault = RoutingDecisionService.NormalizeModelName(opt.CloudOverflow.DefaultModel);
            if (string.Equals(opt.CloudOverflow.DefaultModel, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(opt.CloudOverflow.DefaultModel, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedDefault, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedDefault, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a requested model name across all effective target catalogs without namespace filtering,
    /// enabling direct lookup of specific models on /api/router/models/{name}.
    /// </summary>
    internal static async Task<string?> ResolveModelOnFleetAsync(
        IModelCatalogCacheService? catalogCache,
        IReadOnlyList<OllamaTargetOptions> effectiveTargets,
        string requested,
        CancellationToken cancellationToken)
    {
        if (catalogCache is null)
        {
            return null;
        }

        var allNames = new List<string>();
        foreach (var target in effectiveTargets)
        {
            if (string.IsNullOrWhiteSpace(target.Url))
            {
                continue;
            }

            try
            {
                var targetModels = await catalogCache.RefreshAsync(target.Url, cancellationToken).ConfigureAwait(false);
                foreach (var name in targetModels)
                {
                    if (!ContainsName(allNames, name))
                    {
                        allNames.Add(name);
                    }
                }
            }
            catch
            {
                // A failing catalog check must never break the model detail lookup.
            }
        }

        return ResolveModelName(requested, allNames);
    }

    /// <summary>
    /// Shared model-summary computation: discovers the metadata on the FIRST effective target
    /// (declaration order) that hosts the model — one /api/show + one /api/tags per model —
    /// reuses that single metadata for every target's pure VRAM-math ceiling derivation, and
    /// computes recommendedContextWindow as the max CMax over the HARDWARE-CAPABLE fleet
    /// targets (cloud overflow is never a ceiling contributor; it is surfaced only via
    /// CloudOverflowAvailable). Returns the public summary plus the raw per-target metadata
    /// (only when the model is present on the fleet) so the cartography endpoint can build
    /// its deployment rows without re-discovering anything.
    /// </summary>
    private static async Task<ModelSummaryData> ComputeModelSummaryAsync(
        string name,
        OllamaRouterOptions opt,
        IReadOnlyList<OllamaTargetOptions> effectiveTargets,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        ITargetAvailabilityService? availability,
        CancellationToken cancellationToken)
    {
        var cloudOverflowAvailable = availability is not null &&
            CloudRelayResolver.IsCloudOverflowAvailable(availability, opt, name);

        // No capability stack (minimal host) or the model is on no target: degrade.
        var presentOnFleet = await AnyTargetHasModelAsync(catalogCache, effectiveTargets, name, cancellationToken)
            .ConfigureAwait(false);
        if (catalogCache is null || capabilityEngine is null || !presentOnFleet)
        {
            return new ModelSummaryData(BuildDegradedSummary(name, cloudOverflowAvailable,
                cloudOnly: catalogCache is not null && !presentOnFleet), null, [], presentOnFleet);
        }

        var normalizedName = RoutingDecisionService.NormalizeModelName(name);
        var pinnedArchitecture = opt.Models.TryGetValue(normalizedName, out var thresholds) ? thresholds.Architecture : null;

        // Fetch per-target metadata across all effective targets hosting the model.
        // Also capture primaryMetadata from the first target with valid model info for general model properties.
        ModelMetadata? primaryMetadata = null;
        var rawTargetMetadata = new List<(OllamaTargetOptions Target, ModelMetadata Metadata)>();

        foreach (var target in effectiveTargets)
        {
            var targetMetadata = await GetTargetMetadataAsync(target, name, catalogCache, capabilityEngine, cancellationToken).ConfigureAwait(false);
            if (targetMetadata is not null)
            {
                if (primaryMetadata is null || (!primaryMetadata.HasModelInfo && targetMetadata.HasModelInfo))
                {
                    primaryMetadata = targetMetadata;
                }
                rawTargetMetadata.Add((target, targetMetadata));
            }
        }

        if (primaryMetadata is null)
        {
            // Per-model discovery failure degrades to the conservative defaults.
            return new ModelSummaryData(BuildDegradedSummary(name, cloudOverflowAvailable, cloudOnly: false), null, [], presentOnFleet);
        }

        var (architecture, _) = capabilityEngine.ClassifyArchitecture(primaryMetadata, pinnedArchitecture);

        // recommendedContextWindow (task #26): dense models report the max pre-configured CMax
        // across hardware-capable targets (each target evaluated with its own Modelfile/GGUF metadata);
        // MoE models report the max of the MoE-aware extensible capacity min(native ?? 128k, MaxContextTokens ?? ∞),
        // which is the extensible ceiling beyond the node's pre-configured context.
        var recommended = ModelCapabilityEngine.OllamaDefaultContext;
        long? maxNativeLength = null;

        foreach (var (target, rawMeta) in rawTargetMetadata)
        {
            var effectiveMeta = MergeMetadata(rawMeta, primaryMetadata);
            if (effectiveMeta.NativeContextLength is { } len)
            {
                maxNativeLength = Math.Max(maxNativeLength ?? 0, len);
            }

            var capability = capabilityEngine.DeriveCapability(effectiveMeta, target.Name!, target, pinnedArchitecture);
            if (capability.HardwareCapable)
            {
                var capacity = capability.Architecture == ModelArchitectureKind.Moe
                    ? ModelCapabilityEngine.GetMoeExtensibleCapacity(capability, thresholds?.MaxContextTokens)
                    : capability.CMax;
                recommended = Math.Max(recommended, capacity);
            }
        }

        var nativeContext = maxNativeLength is { } maxNative
            ? (int)Math.Min(maxNative, int.MaxValue)
            : (primaryMetadata.NativeContextLength is { } pLen ? (int)Math.Min(pLen, int.MaxValue) : ModelCapabilityEngine.OllamaDefaultContext);

        return new ModelSummaryData(
            new RouterModelSummary(
                Name: name,
                Architecture: ModelCapabilityEngine.ArchitectureWireName(architecture),
                ParameterCount: primaryMetadata.ParameterCount,
                NativeContextWindow: nativeContext,
                RecommendedContextWindow: recommended,
                CloudOverflowAvailable: cloudOverflowAvailable),
            primaryMetadata,
            rawTargetMetadata,
            presentOnFleet);
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

    private static RouterModelSummary BuildDegradedSummary(string name, bool cloudOverflowAvailable, bool cloudOnly)
    {
        // Cloud-only synthesis entries have no discoverable metadata at all (native +
        // recommended = 0); every other degraded case falls back to the Ollama default.
        var native = cloudOnly ? 0 : ModelCapabilityEngine.OllamaDefaultContext;
        return new RouterModelSummary(
            Name: name,
            Architecture: "Unknown",
            ParameterCount: null,
            NativeContextWindow: native,
            RecommendedContextWindow: native,
            CloudOverflowAvailable: cloudOverflowAvailable);
    }

    /// <summary>
    /// Merges target-specific metadata with primary fleet metadata: node-specific Modelfile parameters
    /// (such as <c>num_ctx</c>) remain distinct per target, while model-intrinsic properties (such as
    /// parameter count and architecture) fall back to primary metadata if the target daemon returned degraded info.
    /// </summary>
    internal static ModelMetadata MergeMetadata(ModelMetadata targetMetadata, ModelMetadata? primaryMetadata)
    {
        if (primaryMetadata is null)
        {
            return targetMetadata;
        }

        // If targetMetadata has no model info (e.g. show was missing/failed), use primaryMetadata
        // but preserve target's weight size if available.
        if (!targetMetadata.HasModelInfo && primaryMetadata.HasModelInfo)
        {
            return primaryMetadata with
            {
                WeightSizeBytes = targetMetadata.WeightSizeBytes > 0 ? targetMetadata.WeightSizeBytes : primaryMetadata.WeightSizeBytes,
                ModelfileNumCtx = targetMetadata.ModelfileNumCtx ?? primaryMetadata.ModelfileNumCtx
            };
        }

        // Target has model info: target's ModelfileNumCtx wins for that target (even if null!),
        // but intrinsic properties fall back to primary if missing.
        return targetMetadata with
        {
            ParameterCount = targetMetadata.ParameterCount ?? primaryMetadata.ParameterCount,
            ExpertCount = targetMetadata.ExpertCount > 0 ? targetMetadata.ExpertCount : primaryMetadata.ExpertCount,
            ModelFamily = targetMetadata.ModelFamily ?? primaryMetadata.ModelFamily,
            NativeContextLength = targetMetadata.NativeContextLength ?? primaryMetadata.NativeContextLength,
            QuantizationLevel = targetMetadata.QuantizationLevel ?? primaryMetadata.QuantizationLevel,
            HasModelInfo = targetMetadata.HasModelInfo || primaryMetadata.HasModelInfo
        };
    }

    /// <summary>
    /// Fetches model metadata from a specific target's daemon. Returns null if the target does not host
    /// the model, is unreachable, or metadata discovery fails.
    /// </summary>
    private static async Task<ModelMetadata?> GetTargetMetadataAsync(
        OllamaTargetOptions target,
        string modelName,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        CancellationToken cancellationToken)
    {
        if (catalogCache is null || capabilityEngine is null || string.IsNullOrWhiteSpace(target.Url))
        {
            return null;
        }

        try
        {
            var present = await catalogCache.ModelExistsAsync(target.Url, modelName, cancellationToken).ConfigureAwait(false);
            if (!present)
            {
                return null;
            }

            var digest = await catalogCache.GetCurrentDigestAsync(target.Url, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(digest))
            {
                return null;
            }

            return await capabilityEngine.GetMetadataAsync(target.Url, modelName, digest, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
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

    /// <summary>
    /// One cartography deployment row per (model, target) pair: the exact same qualification
    /// as the <c>GET /api/router/models/{name}</c> target matrix (present/capable/
    /// contextCeiling/vetoReason/isResident/inFlightRequests — same name-matching rule) plus
    /// the catalog-sourced <see cref="CartographyDeploymentRow.Quantization"/> (that target's
    /// metadata quantization, falling back to the primary fleet value) and
    /// <see cref="CartographyDeploymentRow.LoadedSizeVramBytes"/> (the tracker's exact
    /// <c>size_vram</c> of the resident process, null when not resident).
    /// </summary>
    private static async Task<CartographyDeploymentRow> BuildDeploymentRowAsync(
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

        // The resident process the isResident flag matched (same ordinal-ignore-case raw or
        // NormalizeModelName rule); its size_vram is the exact loaded footprint.
        TargetExecutionState? executionState = null;
        var routingTarget = (RoutingTarget)target.Name!;
        try
        {
            executionState = executionTracker?.GetState(routingTarget);
        }
        catch
        {
            executionState = null;
        }

        long? loadedSizeVramBytes = null;
        if (qualification.IsResident && executionState is { } state)
        {
            var resident = state.LoadedModels.FirstOrDefault(m =>
                string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name, RoutingDecisionService.NormalizeModelName(name), StringComparison.OrdinalIgnoreCase));
            if (resident is { } model)
            {
                loadedSizeVramBytes = model.SizeVramBytes;
            }
        }

        return new CartographyDeploymentRow(
            Target: qualification.Target,
            Present: qualification.Present,
            Capable: qualification.Capable,
            VetoReason: qualification.VetoReason,
            ContextCeiling: qualification.ContextCeiling,
            IsResident: qualification.IsResident,
            LoadedSizeVramBytes: loadedSizeVramBytes,
            InFlightRequests: qualification.InFlightRequests,
            Quantization: qualification.TargetMetadata?.QuantizationLevel);
    }

    /// <summary>
    /// Per-target qualification (the shared core of the <c>GET /api/router/models/{name}</c>
    /// target matrix and the cartography deployment rows): Capable = hardware-capable
    /// capability AND model present on that target; the veto reasons are: model absent →
    /// "model not present in target catalog"; LargeDense on a small-class target → the exact
    /// <see cref="ModelCapabilityEngine.LargeDenseVetoReason"/> string; undiscoverable
    /// capability → no veto reason (permissive fallback, matching the routing pipeline).
    /// </summary>
    private static async Task<TargetQualification> ComputeTargetQualificationAsync(
        string name,
        OllamaTargetOptions target,
        ModelMetadata? primaryMetadata,
        string? pinnedArchitecture,
        IModelCatalogCacheService? catalogCache,
        ModelCapabilityEngine? capabilityEngine,
        ITargetExecutionTracker? executionTracker,
        CancellationToken cancellationToken)
    {
        var targetName = target.Name!;
        var routingTarget = (RoutingTarget)targetName;

        bool present = false;
        if (catalogCache is not null && !string.IsNullOrWhiteSpace(target.Url))
        {
            try
            {
                present = await catalogCache.ModelExistsAsync(target.Url, name, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                present = false;
            }
        }

        ModelMetadata? metadata = null;
        if (present)
        {
            metadata = await GetTargetMetadataAsync(target, name, catalogCache, capabilityEngine, cancellationToken).ConfigureAwait(false);
        }

        var effectiveMetadata = metadata is not null
            ? MergeMetadata(metadata, primaryMetadata)
            : primaryMetadata;

        TargetModelCapability? capability = effectiveMetadata is not null && capabilityEngine is not null
            ? capabilityEngine.DeriveCapability(effectiveMetadata, targetName, target, pinnedArchitecture)
            : null;

        var capable = capability is not null && capability.HardwareCapable && present;

        string? vetoReason = null;
        if (!capable)
        {
            if (!present)
            {
                vetoReason = "model not present in target catalog";
            }
            else if (capability is { Architecture: ModelArchitectureKind.LargeDense, HardwareCapable: false })
            {
                vetoReason = ModelCapabilityEngine.LargeDenseVetoReason;
            }
            // Undiscoverable capability: permissive — no veto reason, capable follows the
            // model-presence flag (which is already false here).
        }

        var executionState = executionTracker?.GetState(routingTarget);
        var isResident = executionState?.LoadedModels.Any(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Name, RoutingDecisionService.NormalizeModelName(name), StringComparison.OrdinalIgnoreCase))
            ?? false;
        var inFlight = executionState?.InFlightRequests ?? 0;

        return new TargetQualification(
            Target: targetName,
            Present: present,
            Capable: capable,
            VetoReason: vetoReason,
            ContextCeiling: capability?.CMax ?? ModelCapabilityEngine.OllamaDefaultContext,
            IsResident: isResident,
            InFlightRequests: inFlight,
            EffectiveMetadata: effectiveMetadata,
            TargetMetadata: metadata);
    }

    // ------------------------------------------------------------------
    //  GET /api/router/cartography
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the fleet cartography: <c>nodes[]</c> from the in-memory liveness /
    /// hardware /<see cref="ITargetExecutionTracker"/> snapshots (the Cloud pseudo-target is
    /// appended last, mirroring <c>GET /api/router/fleet</c> semantics), <c>models[]</c> from
    /// the merged fleet catalog (+ cloud syntheses) with the shared summary/discovery helpers
    /// (30 s single-flight capability cache), and <c>loadedModels[]</c> as the flat,
    /// non-deduped cartesian of every node's loaded processes. EVERY sub-source is resolved
    /// defensively and degrades fail-soft (a minimal host answers 200 with degraded rows);
    /// the endpoint NEVER calls <see cref="ITargetExecutionTracker.RefreshAsync"/> — it only
    /// reads snapshots, so an unreachable node contributes its last-known data (flagged
    /// <c>stale</c>) and can never hang the request.
    /// </summary>
    private static async Task<RouterCartographyResponse> BuildCartographyResponseAsync(
        IOptions<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var opt = options.Value;
        var effectiveTargets = opt.GetEffectiveTargets();
        var availability = serviceProvider.GetService<ITargetAvailabilityService>();
        var liveness = serviceProvider.GetService<ITargetLivenessService>();
        var livenessEntries = liveness?.GetEntries();
        var executionTracker = serviceProvider.GetService<ITargetExecutionTracker>();
        var leaseSnapshot = serviceProvider.GetService<IContextAutoShrinkManager>()?.GetLeaseSnapshot();
        var modelCatalogCache = serviceProvider.GetService<IModelCatalogCacheService>();
        var fleetHardware = serviceProvider.GetService<IFleetHardwareService>();
        var fleetAggregation = serviceProvider.GetService<IFleetAggregationService>();
        var capabilityEngine = serviceProvider.GetService<ModelCapabilityEngine>();

        // ---- nodes[] (effective targets in configuration order) ----
        var nodes = new List<CartographyNodeRow>(effectiveTargets.Count + 1);
        foreach (var target in effectiveTargets)
        {
            var name = target.Name!;
            var routingTarget = (RoutingTarget)name;
            var enabled = availability is not null &&
                availability.GetSnapshot().Flags.TryGetValue(routingTarget, out var targetEnabled)
                ? targetEnabled
                : true;

            // Liveness: the same "Online"/"Offline"/"Probing" vocabulary as /fleet (with the
            // catalog-based fallback when the liveness service is not registered).
            string livenessText;
            if (livenessEntries is { } entries && entries.TryGetValue(routingTarget, out var entry))
            {
                livenessText = entry.State switch
                {
                    TargetLivenessState.Online => "Online",
                    TargetLivenessState.Offline => "Offline",
                    _ => "Probing"
                };
            }
            else
            {
                livenessText = await CatalogHasAnyModelsAsync(modelCatalogCache, target.Url, cancellationToken)
                    ? "Online"
                    : "Offline";
            }

            // Hardware summary from the cached fleet hardware store (fail-soft).
            NodeHardwareProfile? nodeProfile = null;
            try
            {
                nodeProfile = fleetHardware?.GetProfile(name);
            }
            catch
            {
                nodeProfile = null;
            }

            string? gpuName = null;
            string? gpuVendor = null;
            if (nodeProfile?.Gpus.Count > 0)
            {
                gpuName = nodeProfile.Gpus[0].ModelName;
                gpuVendor = nodeProfile.Gpus[0].Vendor;
            }
            else if (nodeProfile is not null && nodeProfile.IsUnifiedMemory)
            {
                gpuVendor = string.Equals(nodeProfile.Architecture, "arm64", StringComparison.OrdinalIgnoreCase)
                    ? "Apple"
                    : "Unified";
                gpuName = $"{nodeProfile.Architecture} Unified Memory";
            }

            // Tracker snapshot ONLY (never RefreshAsync — the 2 s hosted service owns /api/ps).
            TargetExecutionState? executionState = null;
            try
            {
                executionState = executionTracker?.GetState(routingTarget);
            }
            catch
            {
                executionState = null;
            }

            nodes.Add(new CartographyNodeRow(
                Name: name,
                Url: target.Url,
                Location: TargetLocationClassifier.ToWireValue(TargetLocationClassifier.Classify(target)),
                Enabled: enabled,
                Liveness: livenessText,
                Stale: executionState?.Stale ?? false,
                LastRefreshedAtUtc: executionState is not null && executionState.LastRefreshedAtUtc != DateTimeOffset.MinValue
                    ? executionState.LastRefreshedAtUtc
                    : null,
                Hardware: new CartographyNodeHardware(
                    GpuName: gpuName,
                    GpuVendor: gpuVendor,
                    TotalVramMB: target.GetEffectiveMaxVramMB(fleetHardware),
                    IsUnifiedMemory: nodeProfile?.IsUnifiedMemory ?? false,
                    SystemRamMB: nodeProfile?.SystemRamMB is { } ram and > 0 ? ram : null),
                ContextAllocation: FormatContextAllocation(routingTarget, leaseSnapshot),
                InFlightRequests: executionState?.InFlightRequests ?? 0,
                TotalLoadedVramBytes: executionState?.TotalVramBytes ?? 0L,
                LoadedModels: executionState?.LoadedModels.Select(m => new CartographyNodeLoadedModel(
                    Name: m.Name,
                    SizeVramBytes: m.SizeVramBytes,
                    Quantization: m.QuantizationLevel,
                    ContextLength: m.ContextLength)).ToArray() ?? []));
        }

        // The Cloud pseudo-target row (last), mirroring the /fleet cloud semantics: never
        // probed/tracked, "Online" iff cloud is enabled, no hardware, baseline allocation.
        var cloudEnabled = opt.CloudOverflow.Enabled;
        var cloudFlag = availability is not null &&
            availability.GetSnapshot().Flags.TryGetValue(RoutingTarget.Cloud, out var cloudEnabledFlag)
            ? cloudEnabledFlag
            : cloudEnabled;
        nodes.Add(new CartographyNodeRow(
            Name: RoutingTarget.Cloud.Value,
            Url: null,
            Location: TargetLocationClassifier.ToWireValue(TargetLocation.Cloud),
            Enabled: cloudFlag,
            Liveness: cloudEnabled ? "Online" : "Offline",
            Stale: false,
            LastRefreshedAtUtc: null,
            Hardware: null,
            ContextAllocation: "baseline",
            InFlightRequests: 0,
            TotalLoadedVramBytes: 0L,
            LoadedModels: []));

        // ---- models[] (merged fleet catalog + cloud syntheses) ----
        var names = await GetFleetModelNamesAsync(opt, availability, fleetAggregation, modelCatalogCache, context, cancellationToken)
            .ConfigureAwait(false);

        // Names are independent: run the per-model discovery concurrently (the metadata calls
        // hit the 30 s single-flight capability cache; a second call within the window
        // reissues zero /api/show requests).
        var modelRows = await Task.WhenAll(names.Select(async name =>
        {
            var data = await ComputeModelSummaryAsync(
                name, opt, effectiveTargets, modelCatalogCache, capabilityEngine, availability, cancellationToken)
                .ConfigureAwait(false);

            var primaryMetadata = data.PrimaryMetadata;
            var pinnedArchitecture = opt.Models.TryGetValue(
                RoutingDecisionService.NormalizeModelName(name), out var rowThresholds) ? rowThresholds.Architecture : null;

            // One deployment row per effective target (declaration order); the Cloud
            // pseudo-target never appears here (it has no OllamaTargetOptions). A model hosted
            // on NO fleet target (e.g. a cloud-only synthesis — PresentOnFleet is false, the
            // same signal the detail endpoint uses via AnyTargetHasModelAsync) carries an
            // empty deployments[], exactly like /api/router/models/{name}.
            var deployments = !data.PresentOnFleet
                ? []
                : await Task.WhenAll(effectiveTargets.Select(target => BuildDeploymentRowAsync(
                    name, target, primaryMetadata, pinnedArchitecture,
                    modelCatalogCache, capabilityEngine, executionTracker, cancellationToken)))
                    .ConfigureAwait(false);

            return new CartographyModelRow(
                Name: data.Summary.Name,
                Architecture: data.Summary.Architecture,
                ParameterCount: data.Summary.ParameterCount,
                WeightSizeBytes: primaryMetadata?.WeightSizeBytes ?? 0L,
                Quantization: primaryMetadata?.QuantizationLevel,
                NativeContextWindow: data.Summary.NativeContextWindow,
                RecommendedContextWindow: data.Summary.RecommendedContextWindow,
                CloudOverflowAvailable: data.Summary.CloudOverflowAvailable,
                Deployments: deployments);
        })).ConfigureAwait(false);

        // ---- loadedModels[] (flat: target order, then tracker order; no cross-target dedup) ----
        var loadedModels = nodes
            .Where(node => node.LoadedModels.Count > 0)
            .SelectMany(node => node.LoadedModels.Select(m => new CartographyLoadedModelRow(
                Name: m.Name,
                Target: node.Name,
                SizeVramBytes: m.SizeVramBytes,
                Quantization: m.Quantization,
                ContextLength: m.ContextLength,
                ContextAllocation: node.ContextAllocation,
                Liveness: node.Liveness,
                Stale: node.Stale)))
            .ToArray();

        return new RouterCartographyResponse(nodes, modelRows, loadedModels);
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
        IOptions<OllamaRouterOptions> options,
        IServiceProvider serviceProvider,
        RouterContextEvaluateRequest request,
        CancellationToken cancellationToken)
    {
        var opt = options.Value;
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

/// <summary>
/// Response payload for <c>GET /api/router/info</c>: the router version, the static
/// <c>isRouter</c> marker, the fleet summary and the active feature flags.
/// </summary>
public sealed record RouterInfoResponse(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("isRouter")] bool IsRouter,
    [property: JsonPropertyName("fleet")] RouterFleetSummary Fleet,
    [property: JsonPropertyName("features")] RouterFeatures Features);

public sealed record RouterFleetSummary(
    [property: JsonPropertyName("totalTargets")] int TotalTargets,
    [property: JsonPropertyName("onlineTargets")] int OnlineTargets,
    [property: JsonPropertyName("cloudEnabled")] bool CloudEnabled);

public sealed record RouterFeatures(
    [property: JsonPropertyName("dynamicContext")] bool DynamicContext,
    [property: JsonPropertyName("autoShrink")] bool AutoShrink,
    [property: JsonPropertyName("failover")] bool Failover,
    [property: JsonPropertyName("antiThrashingAffinity")] bool AntiThrashingAffinity);

public sealed record RouterFleetResponse(
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterTargetItem> Targets);

public sealed record RouterTargetItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("liveness")] string Liveness,
    [property: JsonPropertyName("latencyMs")] long? LatencyMs,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("hardware")] RouterTargetHardware Hardware,
    [property: JsonPropertyName("execution")] RouterTargetExecution Execution);

public sealed record RouterTargetHardware(
    [property: JsonPropertyName("gpuVendor")] string GpuVendor,
    [property: JsonPropertyName("devices")] IReadOnlyList<string> Devices,
    [property: JsonPropertyName("maxVramMB")] int? MaxVramMB,
    [property: JsonPropertyName("maxParallel")] int MaxParallel,
    [property: JsonPropertyName("speedFactor")] double? SpeedFactor,
    [property: JsonPropertyName("isUnifiedMemory")] bool IsUnifiedMemory = false,
    [property: JsonPropertyName("systemRamMB")] long? SystemRamMB = null,
    [property: JsonPropertyName("memoryType")] string? MemoryType = null,
    [property: JsonPropertyName("bandwidthGBs")] double? BandwidthGBs = null,
    [property: JsonPropertyName("gpuName")] string? GpuName = null);

public sealed record RouterTargetExecution(
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("activeModel")] string? ActiveModel,
    [property: JsonPropertyName("contextAllocation")] string ContextAllocation);

/// <summary>
/// Per-model capability summary of <c>GET /api/router/models</c>: the discovered
/// architecture wire name ("Dense"/"Moe"/"Unknown"), the parameter count (null when unknown),
/// the native Modelfile num_ctx (Ollama default when the Modelfile omits it), the
/// router-calculated context recommendation and the cloud overflow hint.
/// <see cref="RecommendedContextWindow"/> is the node's pre-configured capacity (the optimal
/// bucket) for DENSE models — dense contexts are never expanded (clamp-only pass-through);
/// for MOE models it is the max extensible capacity min(native ?? 128k, MaxContextTokens ?? ∞)
/// (the single extensible bucket of the two-bucket ladder).
/// </summary>
public sealed record RouterModelSummary(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("parameterCount")] long? ParameterCount,
    [property: JsonPropertyName("nativeContextWindow")] int NativeContextWindow,
    [property: JsonPropertyName("recommendedContextWindow")] int RecommendedContextWindow,
    [property: JsonPropertyName("cloudOverflowAvailable")] bool CloudOverflowAvailable);

public sealed record RouterModelsResponse(
    [property: JsonPropertyName("models")] IReadOnlyList<RouterModelSummary> Models);

/// <summary>
/// Detailed capability report of <c>GET /api/router/models/{modelName}</c>: the summary
/// fields plus the per-target qualification matrix (the Cloud pseudo-target is never listed).
/// </summary>
public sealed record RouterModelDetailResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("parameterCount")] long? ParameterCount,
    [property: JsonPropertyName("nativeContextWindow")] int NativeContextWindow,
    [property: JsonPropertyName("recommendedContextWindow")] int RecommendedContextWindow,
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterModelTargetCompatibility> Targets);

/// <summary>
/// One row of the per-target qualification matrix: whether the target is capable of serving
/// the model (hardware + catalog presence), its context ceiling, the resident (hot) state
/// and the in-flight request count. <see cref="VetoReason"/> is emitted only when
/// <c>capable == false</c> (null is omitted from the JSON).
/// </summary>
public sealed record RouterModelTargetCompatibility(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("capable")] bool Capable,
    [property: JsonPropertyName("contextCeiling")] int ContextCeiling,
    [property: JsonPropertyName("isResident")] bool IsResident,
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("vetoReason")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VetoReason = null);

    /// <summary>
    /// Optional request body of <c>POST /api/router/models/{*modelName}/preload</c> (task #44):
    /// the preload retention in seconds (<c>-1</c> = indefinitely, positive = seconds,
    /// null/absent = the selected target's effective class default: cloud/server-class = -1,
    /// desktop = 900), the context intent in tokens (null/absent = the model's
    /// recommendedContextWindow soft intent), and the explicit target pin (null/absent =
    /// auto-select; the pin overrides SELECTION, not the safety gates). The same parameters
    /// are also accepted as query string values; body values win per field when both are
    /// present (the vram-reclaim pattern).
    /// </summary>
    public sealed record RouterModelPreloadRequest(
        [property: JsonPropertyName("keepAliveSeconds")] int? KeepAliveSeconds = null,
        [property: JsonPropertyName("contextIntentTokens")] int? ContextIntentTokens = null,
        [property: JsonPropertyName("target")] string? Target = null);

    /// <summary>
    /// One candidate row of the preload response: every effective fleet target (declaration
    /// order; the Cloud pseudo-target is never a candidate) with its eligibility, the ordered
    /// skip reason of the first failing gate (omitted when eligible), the live in-flight
    /// count, the resident state, the effective context ceiling (MoE extensible capacity /
    /// dense CMax; the Ollama default sentinel when undiscoverable), the server/cloud-class
    /// flag and the busy flag (<c>inFlightRequests > 0</c> — the v1 busy definition).
    /// </summary>
    public sealed record RouterModelPreloadCandidate(
        [property: JsonPropertyName("target")] string Target,
        [property: JsonPropertyName("eligible")] bool Eligible,
        [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
        [property: JsonPropertyName("isResident")] bool IsResident,
        [property: JsonPropertyName("contextCeiling")] int ContextCeiling,
        [property: JsonPropertyName("serverClass")] bool ServerClass,
        [property: JsonPropertyName("busy")] bool Busy,
        [property: JsonPropertyName("skipReason")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SkipReason = null);

    /// <summary>
    /// Response of <c>POST /api/router/models/{*modelName}/preload</c> and
    /// <c>GET /api/router/models/{*modelName}/preload/preview</c> (task #44): the selected
    /// target, the effective <c>keep_alive</c> (seconds; -1 = indefinitely) and the exact
    /// <c>num_ctx</c> actually applied (<c>min(intent, selected ceiling)</c>), the active context
    /// intent (request value or recommendedContextWindow soft fallback), the load acceptance
    /// and bounded /api/ps residency confirmation flags plus the load-call wall-clock (all
    /// null on the side-effect-free preview), the full candidate evaluation and the
    /// human-readable selection reason. This shape is only emitted for 200 — the 404/503
    /// shapes use the shared <see cref="OllamaErrorResponse"/>.
    /// </summary>
    public sealed record RouterModelPreloadResponse(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("selectedTarget")] string? SelectedTarget,
        [property: JsonPropertyName("keepAliveSeconds")] int AppliedKeepAliveSeconds,
        [property: JsonPropertyName("numCtx")] int? AppliedNumCtx,
        [property: JsonPropertyName("contextIntentTokens")] int? ContextIntentTokens,
        [property: JsonPropertyName("loadAccepted")] bool? LoadAccepted,
        [property: JsonPropertyName("residencyConfirmed")] bool? ResidencyConfirmed,
        [property: JsonPropertyName("preloadDurationMs")] long? PreloadDurationMs,
        [property: JsonPropertyName("candidates")] IReadOnlyList<RouterModelPreloadCandidate> Candidates,
        [property: JsonPropertyName("reason")] string? Reason);

/// <summary>
/// Shared model-summary computation result of <c>ComputeModelSummaryAsync</c>: the public
/// summary plus the primary (first hoster with model_info) fleet metadata and the raw
/// per-target metadata pairs (empty when the model is present on no fleet target) so the
/// cartography endpoint can build its deployment rows without re-discovering.
/// </summary>
internal sealed record ModelSummaryData(
    RouterModelSummary Summary,
    ModelMetadata? PrimaryMetadata,
    List<(OllamaTargetOptions Target, ModelMetadata Metadata)> RawTargetMetadata,
    bool PresentOnFleet);

/// <summary>
/// Per-target qualification core shared by the <c>GET /api/router/models/{name}</c> matrix and
/// the cartography deployment rows: presence, capability, veto, context ceiling, residency,
/// in-flight count and the (merged, effective) metadata used for the derivation.
/// </summary>
internal sealed record TargetQualification(
    string Target,
    bool Present,
    bool Capable,
    string? VetoReason,
    int ContextCeiling,
    bool IsResident,
    int InFlightRequests,
    ModelMetadata? EffectiveMetadata,
    ModelMetadata? TargetMetadata = null)
{
    /// <summary>Projects onto the <c>GET /api/router/models/{name}</c> wire row (byte-identical to the pre-refactor builder).</summary>
    public RouterModelTargetCompatibility ToCompatibilityRow() => new(
        Target: Target,
        Capable: Capable,
        ContextCeiling: ContextCeiling,
        IsResident: IsResident,
        InFlightRequests: InFlightRequests,
        VetoReason: VetoReason);
}

/// <summary>
/// Response payload for <c>GET /api/router/cartography</c>: the fleet node rows (effective
/// targets in configuration order, Cloud pseudo-target last when present in configuration —
/// it is always appended), the per-model deployment rows, and the flat list of every loaded
/// model process (the cartesian flatten of <c>nodes[].loadedModels[]</c>, no cross-target
/// dedup). Sizes are strictly numeric: bytes (long) where ps-derived, MB (int) where
/// hardware-derived; there is deliberately NO computed free-VRAM field.
/// </summary>
public sealed record RouterCartographyResponse(
    [property: JsonPropertyName("nodes")] IReadOnlyList<CartographyNodeRow> Nodes,
    [property: JsonPropertyName("models")] IReadOnlyList<CartographyModelRow> Models,
    [property: JsonPropertyName("loadedModels")] IReadOnlyList<CartographyLoadedModelRow> LoadedModels);

/// <summary>
/// One fleet node (or the Cloud pseudo-target) in <c>nodes[]</c>: the wire location value,
/// liveness (the /fleet "Online"/"Offline"/"Probing" vocabulary), the hardware summary
/// (null for Cloud), the active KV-cache context lease allocation ("baseline" for Cloud), the
/// raw loaded-VRAM total (<c>TargetExecutionState.TotalVramBytes</c>), and the currently loaded
/// processes with their live /api/ps-sourced quantization.
/// </summary>
public sealed record CartographyNodeRow(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("liveness")] string Liveness,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("contextAllocation")] string ContextAllocation,
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("totalLoadedVramBytes")] long TotalLoadedVramBytes,
    [property: JsonPropertyName("loadedModels")] IReadOnlyList<CartographyNodeLoadedModel> LoadedModels,
    [property: JsonPropertyName("url")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null,
    [property: JsonPropertyName("lastRefreshedAtUtc")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastRefreshedAtUtc = null,
    [property: JsonPropertyName("hardware")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CartographyNodeHardware? Hardware = null);

/// <summary>Hardware summary of one cartography node (strictly numeric MB).</summary>
public sealed record CartographyNodeHardware(
    [property: JsonPropertyName("isUnifiedMemory")] bool IsUnifiedMemory,
    [property: JsonPropertyName("gpuName")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GpuName = null,
    [property: JsonPropertyName("gpuVendor")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GpuVendor = null,
    [property: JsonPropertyName("totalVramMB")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TotalVramMB = null,
    [property: JsonPropertyName("systemRamMB")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SystemRamMB = null);

/// <summary>
/// One currently loaded model process on a cartography node: the name as reported by
/// /api/ps, the exact <c>size_vram</c> in bytes, the live-process (ps-sourced)
/// quantization and the best-effort context length (null when absent).
/// </summary>
public sealed record CartographyNodeLoadedModel(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sizeVramBytes")] long SizeVramBytes,
    [property: JsonPropertyName("quantization")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Quantization = null,
    [property: JsonPropertyName("contextLength")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ContextLength = null);

/// <summary>
/// One fleet model in <c>models[]</c>: the same summary fields as
/// <c>GET /api/router/models</c> plus the weight size in bytes, the catalog-sourced
/// quantization (tags → show → null) and the per-target deployment matrix (Cloud never
/// listed; cloud-only models carry an empty list).
/// </summary>
public sealed record CartographyModelRow(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("weightSizeBytes")] long WeightSizeBytes,
    [property: JsonPropertyName("nativeContextWindow")] int NativeContextWindow,
    [property: JsonPropertyName("recommendedContextWindow")] int RecommendedContextWindow,
    [property: JsonPropertyName("cloudOverflowAvailable")] bool CloudOverflowAvailable,
    [property: JsonPropertyName("deployments")] IReadOnlyList<CartographyDeploymentRow> Deployments,
    [property: JsonPropertyName("parameterCount")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ParameterCount = null,
    [property: JsonPropertyName("quantization")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Quantization = null);

/// <summary>
/// One (model, target) deployment row: the exact same qualification as the
/// <c>GET /api/router/models/{name}</c> target matrix plus the catalog-sourced
/// quantization of that target's metadata and the exact resident footprint in bytes
/// (null when the model is not resident there). <see cref="VetoReason"/> is omitted when
/// absent (null) — same convention as the model-detail matrix.
/// </summary>
public sealed record CartographyDeploymentRow(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("present")] bool Present,
    [property: JsonPropertyName("capable")] bool Capable,
    [property: JsonPropertyName("contextCeiling")] int ContextCeiling,
    [property: JsonPropertyName("isResident")] bool IsResident,
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("vetoReason")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VetoReason = null,
    [property: JsonPropertyName("loadedSizeVramBytes")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? LoadedSizeVramBytes = null,
    [property: JsonPropertyName("quantization")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Quantization = null);

/// <summary>
/// One flat loaded-process row (no cross-target dedup): the model name, its hosting target,
/// the exact <c>size_vram</c> in bytes, the live-process (ps-sourced) quantization, the
/// best-effort context length, plus the hosting node's context allocation / liveness / stale
/// flags.
/// </summary>
public sealed record CartographyLoadedModelRow(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("sizeVramBytes")] long SizeVramBytes,
    [property: JsonPropertyName("contextAllocation")] string ContextAllocation,
    [property: JsonPropertyName("liveness")] string Liveness,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("quantization")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Quantization = null,
    [property: JsonPropertyName("contextLength")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ContextLength = null);

/// <summary>
/// Request body of <c>POST /api/router/context/evaluate</c>: the model to evaluate, the
/// estimated prompt token count and the client's static <c>options.num_ctx</c> (if any).
/// </summary>
public sealed record RouterContextEvaluateRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("estimatedTokens")] int EstimatedTokens,
    [property: JsonPropertyName("clientRequestedNumCtx")] int? ClientRequestedNumCtx = null);

/// <summary>
/// Preview response of <c>POST /api/router/context/evaluate</c>: the predicted target, the
/// num_ctx the sizing engine would inject, the context mode — one of the four lowercase engine
/// modes (task #26): <c>passthrough</c> (dense, payload untouched; <c>injectedNumCtx</c>
/// reports what would flow through), <c>clamped</c> (dense, oversized client value clamped
/// down to the pre-configured context), <c>baseline</c> / <c>expanded</c> (MoE two-bucket
/// ladder) — the margin-adjusted token estimate, the target's effective capacity (MoE
/// extensible limit / dense pre-configured CMax) and the winner's affinity score.
/// </summary>
public sealed record RouterContextEvaluateResponse(
    [property: JsonPropertyName("selectedTarget")] string SelectedTarget,
    [property: JsonPropertyName("injectedNumCtx")] int InjectedNumCtx,
    [property: JsonPropertyName("contextMode")] string ContextMode,
    [property: JsonPropertyName("estimatedTokensWithMargin")] int EstimatedTokensWithMargin,
    [property: JsonPropertyName("targetMaxContext")] int TargetMaxContext,
    [property: JsonPropertyName("affinityScore")] double AffinityScore);

/// <summary>
/// Optional request body of <c>POST /api/router/vram/reclaim</c> / <c>POST /api/router/fleet/unload</c>:
/// the target name to unload (null/absent = all effective targets) and whether to
/// administratively disable the processed target(s) (default false). The same parameters are
/// also accepted as query string values; body values win when both are present.
/// </summary>
public sealed record RouterVramReclaimRequest(
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("disableTarget")] bool DisableTarget = false);

/// <summary>
/// One row of the <c>targets</c> array of the fleet VRAM reclaim response: the target's
/// canonical configured name, the models unloaded on it (empty when the daemon was
/// unreachable or had no running models) and whether it was administratively disabled.
/// </summary>
public sealed record RouterVramReclaimTargetResult(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("unloadedModels")] IReadOnlyList<string> UnloadedModels,
    [property: JsonPropertyName("disabled")] bool Disabled);

/// <summary>
/// Response payload of <c>POST /api/router/vram/reclaim</c> / <c>POST /api/router/fleet/unload</c>:
/// whether the operation executed against a valid target set (individual unreachable daemons
/// are fail-soft), the total number of unloaded models across the fleet, and the per-target
/// results in configured fleet order. <see cref="Message"/> is omitted (null) on success and
/// set to "no targets configured" when the effective target list is empty.
/// </summary>
public sealed record RouterVramReclaimResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("totalUnloaded")] int TotalUnloaded,
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterVramReclaimTargetResult> Targets,
    [property: JsonPropertyName("message")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);

/// <summary>
/// Request body of <c>POST /api/router/benchmark</c> (a null/absent body is valid and means
/// "benchmark all effective non-Cloud targets"): the optional target to queue (null → all
/// effective targets in configuration order), the optional role filter and the optional
/// single-model filter (both null/empty → all catalog candidates).
/// </summary>
public sealed record RouterBenchmarkRequest(
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("roles")] IReadOnlyList<UsageRole>? Roles = null,
    [property: JsonPropertyName("model")] string? Model = null);

/// <summary>
/// 202 response of <c>POST /api/router/benchmark</c>: the run was accepted and queued
/// (non-blocking — the run loop drains the targets sequentially in the background).
/// </summary>
public sealed record RouterBenchmarkRunResponse(
    [property: JsonPropertyName("started")] bool Started,
    [property: JsonPropertyName("queuedTargets")] IReadOnlyList<string> QueuedTargets);

/// <summary>
/// 200 response of <c>GET /api/router/benchmark/results</c>: every known target result
/// (persisted or in-flight; in-flight rows carry <c>status: "Running"</c>), empty when
/// nothing ever ran.
/// </summary>
public sealed record RouterBenchmarkResultsResponse(
    [property: JsonPropertyName("targets")] IReadOnlyList<BenchmarkTargetResult> Targets);

/// <summary>
/// 200 response of <c>DELETE /api/router/benchmark/results</c>: the results were cleared
/// (in-memory + benchmarks.json + benchmark-seeded targets-speed.json entries; "learned"
/// entries are never touched).
/// </summary>
public sealed record RouterBenchmarkClearedResponse(
    [property: JsonPropertyName("cleared")] bool Cleared);
