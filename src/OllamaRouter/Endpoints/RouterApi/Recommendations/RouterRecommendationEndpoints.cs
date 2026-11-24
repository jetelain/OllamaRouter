using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Recommendations;
using System.Text.Json;

namespace OllamaRouter.Endpoints.RouterApi.Recommendations;

/// <summary>
/// The role recommendation endpoints of the OllamaRouter programmatic management API
/// (task #66, consumed by NightCodeForge's Phase 12 Environment Wizard): the live matrix
/// read, the recompute + persist + activate flow and the idempotent re-activation.
/// The matrix persists to <c>&lt;stateDir&gt;/recommended.json</c> (machine-generated state,
/// never <c>config/</c> — same convention as <c>benchmarks.json</c> /
/// <c>targets-hardware.json</c>).
/// </summary>
public static class RouterRecommendationEndpoints
{
    /// <summary>
    /// Maps the role recommendation endpoints
    /// (<c>/api/router/recommendations</c>, <c>.../generate</c>, <c>.../apply</c>).
    /// </summary>
    public static WebApplication MapRouterRecommendationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/router/recommendations", (IRecommendationStore store) =>
        {
            // Read-only (like GET /api/router/benchmark/results): the live matrix in its
            // stable shape — generatedAt null + empty roles/models when nothing is active.
            return Results.Json(
                store.GetMatrix(),
                OllamaRouterJsonSerializerContext.Default.FleetRecommendations);
        })
        .WithName("GetRouterRecommendations")
        .WithTags("Recommendations")
        .WithSummary("Read the live role recommendation matrix.")
        .WithDescription("Returns the active fleet recommendation matrix (role → NATIVE model + per-node "
            + "context ceilings + MoE baselines). Stable empty shape (generatedAt null, empty roles/models) "
            + "when nothing is active — the wizard polls this. Read-only: no key required in default mode.");

        app.MapPost("/api/router/recommendations/generate", (
            IMicroBenchmarkRunner benchmarkRunner,
            IFleetHardwareService fleetHardware,
            IRecommendationEngine engine,
            IRecommendationStore store,
            IOptions<CandidateModelCatalogOptions> catalog) =>
        {
            // Recompute from the DI-injected catalog (IOptions<CandidateModelCatalogOptions>,
            // the curated 7-model DefaultModels in production; task #168) + the persisted
            // benchmark results (the runner is already backed by <state>/benchmarks.json) +
            // the fleet hardware profiles (<state>/targets-hardware.json).
            var matrix = engine.Generate(
                benchmarkRunner.GetAllResults(),
                catalog.Value.Models,
                hardwareProfileLookup: name => fleetHardware.GetProfile(name));

            if (matrix.Models.Count == 0 && matrix.Roles.Count == 0)
            {
                // Zero terminal-status benchmark data contributed any viable row: the
                // wizard must run the benchmark first (no file write, no activation).
                return Results.Json(
                    new OllamaErrorResponse("no benchmark results available; run POST /api/router/benchmark first"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Persist atomically (temp file + replace, same pattern as benchmarks.json),
            // then activate in the live store — one call, wizard-friendly.
            PersistAtomically(store, matrix);
            store.Activate(matrix);

            var targets = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in matrix.Models.Values)
            {
                foreach (var node in model.Nodes)
                {
                    if (seen.Add(node.Target))
                    {
                        targets.Add(node.Target);
                    }
                }
            }

            return Results.Json(
                new RouterRecommendationsGeneratedResponse(
                    Generated: true,
                    GeneratedAt: matrix.GeneratedAt ?? DateTimeOffset.UtcNow,
                    RoleCount: matrix.Roles.Count,
                    ModelCount: matrix.Models.Count,
                    Targets: targets,
                    Recommendations: matrix),
                OllamaRouterJsonSerializerContext.Default.RouterRecommendationsGeneratedResponse);
        })
        .WithName("PostRouterRecommendationsGenerate")
        .WithTags("Recommendations")
        .WithSummary("Recompute, persist and activate the role recommendations (protected: API key required).")
        .WithDescription("Consumes IMicroBenchmarkRunner.GetAllResults() + IFleetHardwareService.GetProfile over "
            + "the injected IOptions<CandidateModelCatalogOptions> catalog (the curated 7-model DefaultModels in "
            + "production), persists <stateDir>/recommended.json (atomic write) and activates the matrix in the "
            + "live store (Stage-1 ceilings + dense clamp/inject + MoE baseline take effect "
            + "immediately, no restart). 200 = generated + activated; 409 = no terminal benchmark data "
            + "(run POST /api/router/benchmark first; nothing is written). Protected by the API-key middleware.");

        app.MapPost("/api/router/recommendations/apply", (IRecommendationStore store) =>
        {
            // Idempotent re-activation of the current file (after a manual edit, a
            // fleet/target change, or a post-restart re-sync) — safe to call repeatedly.
            if (!store.TryActivateFromFile(out var matrix))
            {
                return Results.Json(
                    new OllamaErrorResponse("no recommendations file; run POST /api/router/recommendations/generate first"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            var targets = new List<RouterRecommendationsAppliedTarget>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in matrix.Models.Values)
            {
                foreach (var node in model.Nodes)
                {
                    if (!seen.Add(node.Target))
                    {
                        continue;
                    }

                    var modelCount = matrix.Models.Values.Count(m => m.Nodes.Any(n => string.Equals(n.Target, node.Target, StringComparison.OrdinalIgnoreCase)));
                    var maxCeiling = matrix.Models.Values
                        .Where(m => m.Nodes.Any(n => string.Equals(n.Target, node.Target, StringComparison.OrdinalIgnoreCase)))
                        .Max(m => m.Nodes.First(n => string.Equals(n.Target, node.Target, StringComparison.OrdinalIgnoreCase)).ContextCeiling);
                    targets.Add(new RouterRecommendationsAppliedTarget(node.Target, modelCount, maxCeiling));
                }
            }

            return Results.Json(
                new RouterRecommendationsAppliedResponse(true, matrix.GeneratedAt, targets),
                OllamaRouterJsonSerializerContext.Default.RouterRecommendationsAppliedResponse);
        })
        .WithName("PostRouterRecommendationsApply")
        .WithTags("Recommendations")
        .WithSummary("Re-activate the current recommended.json into the live store (protected: API key required).")
        .WithDescription("Idempotent re-sync of <stateDir>/recommended.json into the live in-memory matrix "
            + "(useful after a manual edit of the file, a fleet/target change, or a router restart); reports the "
            + "per-target ceilings now enforced. 200 = applied (repeatable); 409 = no file (run "
            + "POST /api/router/recommendations/generate first). Protected by the API-key middleware.");

        return app;
    }

    /// <summary>
    /// Atomic persistence (temp file + replace — the benchmarks.json pattern): a mid-write
    /// crash never leaves a torn <c>recommended.json</c> behind.
    /// </summary>
    private static void PersistAtomically(IRecommendationStore store, FleetRecommendations matrix)
    {
        if (store is not RecommendationStore fileStore)
        {
            throw new NotSupportedException("The recommendation store does not expose a persistence path.");
        }

        var path = fileStore.FilePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(directory!, path + ".tmp");
        File.WriteAllText(tempPath, JsonSerializer.Serialize(matrix, OllamaRouterJsonSerializerContext.Default.FleetRecommendations));
        File.Move(tempPath, path, overwrite: true);
    }
}
