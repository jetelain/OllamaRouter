using Microsoft.AspNetCore.Mvc;
using OllamaRouter.Middleware;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Routing;
using System.Text;
using System.Text.Json;

namespace OllamaRouter.Endpoints.RouterApi.Benchmark;

/// <summary>
/// The model benchmarking endpoints of the OllamaRouter programmatic management API:
/// a non-blocking run trigger (202), the results query (including in-flight Running rows)
/// and the result clearing.
/// </summary>

public static class RouterBenchmarkEndpoints
{
    /// <summary>
    /// Maps the model benchmarking endpoints (<c>/api/router/benchmark</c>, <c>/api/router/benchmark/results</c>).
    /// </summary>
    public static WebApplication MapRouterBenchmarkEndpoints(this WebApplication app)
    {
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
        })
        .WithName("PostRouterBenchmarkRun")
        .WithTags("Benchmarking")
        .WithSummary("Queue a non-blocking model benchmark run (protected: API key required).")
        .WithDescription("A null/absent body is valid and benchmarks ALL effective non-Cloud targets (fleet-wide zero-config flow); "
            + "'target' names a single effective target (unknown → 404); 'roles'/'model' filter the catalog candidates "
            + "(null/empty = all). 202 = accepted and queued non-blocking — the run loop drains the targets sequentially in the "
            + "background and this response never waits; 409 = a run is already in progress (single-flight); 400 = malformed body. "
            + "Protected by the API-key middleware (default protected set).");
        app.MapGet("/api/router/benchmark/results", (IMicroBenchmarkRunner benchmarkRunner) =>
        {
            var response = new RouterBenchmarkResultsResponse(
                benchmarkRunner.GetAllResults());
            return Results.Json(response, OllamaRouterJsonSerializerContext.Default.RouterBenchmarkResultsResponse);
        })
        .WithName("GetRouterBenchmarkResults")
        .WithTags("Benchmarking")
        .WithSummary("List benchmark results (including in-flight Running rows).")
        .WithDescription("Every known target result (persisted or in-flight; in-flight rows carry status \"Running\"), "
            + "empty when nothing ever ran. Read-only — no key required in default mode.");
        app.MapDelete("/api/router/benchmark/results", (IMicroBenchmarkRunner benchmarkRunner) =>
        {
            // In-flight runs keep their Running states: the delete applies to completed
            // targets only and the background work continues for its remaining targets.
            benchmarkRunner.ClearResults(keepRunning: true);
            return Results.Json(
                new RouterBenchmarkClearedResponse(true),
                OllamaRouterJsonSerializerContext.Default.RouterBenchmarkClearedResponse);
        })
        .WithName("DeleteRouterBenchmarkResults")
        .WithTags("Benchmarking")
        .WithSummary("Clear all benchmark results (protected: API key required).")
        .WithDescription("In-flight runs keep their Running states: the delete applies to completed targets only and the background "
            + "work continues for its remaining targets. Clears in-memory state + benchmarks.json + benchmark-seeded "
            + "targets-speed.json entries (\"learned\" entries are never touched). Protected by the API-key middleware (default "
            + "protected set).");
        return app;
    }
}
