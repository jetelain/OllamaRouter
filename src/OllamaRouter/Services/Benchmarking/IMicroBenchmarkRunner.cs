using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Options;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Services.Benchmarking;

/// <summary>
/// The automated micro-benchmark engine (task #28): pre-flight memory sizing, model
/// pull-if-missing, the ascending context ladder with <c>num_predict: 64</c>, the ≥ 20 t/s
/// viability gate (exactly 20.0 is viable) and the <c>benchmarks.json</c> +
/// <c>targets-speed.json</c> persistence.
/// </summary>
public interface IMicroBenchmarkRunner
{
    /// <summary>
    /// Queues a benchmark run. <paramref name="target"/> null/empty → all effective non-Cloud
    /// targets are queued sequentially (configuration order). <paramref name="roles"/>/
    /// <paramref name="model"/> filter which catalog candidates run (empty + null → all).
    /// <paramref name="excludedModels"/> (task #152): the optional per-target run-scoped
    /// exclusion map (target name, case-insensitive → model names, case-insensitive); excluded
    /// models produce no rows for that RUN only (no pull, no benchmark) and are never persisted
    /// (benchmarks.json / targets-speed.json / the catalog are untouched). Unknown target keys
    /// and unknown model names are no-ops; null/empty = exactly the pre-#152 behavior.
    /// Returns false (the endpoint maps this to 409) when a run is already in flight.
    /// </summary>
    bool TryStartRun(
        RoutingTarget? target,
        IEnumerable<UsageRole>? roles,
        string? model,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? excludedModels = null);

    /// <summary>
    /// The per-target benchmark candidate plan (task #152): one row per effective non-Cloud
    /// target (configuration order — the same list a fleet-wide run queues), each annotated
    /// with the STORED fleet-hardware profile (null when unknown — a read-only store read,
    /// never a peer probe / network call) and the EXACT catalog candidate set the run loop
    /// would benchmark for that target (the full deduplicated catalog — sizing-pruned models
    /// included), each with its pre-flight fit verdict (<c>Fits</c> / <c>Pruned</c> /
    /// <c>Assumed</c> + the largest surviving ladder context, capped by the model's
    /// nativeContext; 0 when pruned). Read-only: no network, no state mutation.
    /// </summary>
    RouterBenchmarkCandidatesResponse GetBenchmarkCandidates();

    /// <summary>
    /// The current state of the named target: an in-flight row with <c>status: "Running"</c>
    /// while its run is in progress, the last persisted state after completion, or null when
    /// the target was never benchmarked.
    /// </summary>
    Task<BenchmarkTargetResult?> GetStatusAsync(RoutingTarget target);

    /// <summary>
    /// All benchmark target results known (persisted or in-flight), in queue order.
    /// Empty when nothing ever ran.
    /// </summary>
    IReadOnlyList<BenchmarkTargetResult> GetAllResults();

    /// <summary>
    /// The effective non-Cloud target names a fleet-wide run (no explicit target) would
    /// queue, in configuration order. Used by the REST layer to report <c>queuedTargets</c>.
    /// </summary>
    IReadOnlyList<string> GetEffectiveTargetNames();

    /// <summary>
    /// Clears the in-memory state, deletes <c>benchmarks.json</c> and removes only the
    /// benchmark-seeded (<c>source: "benchmark"</c>) entries from <c>targets-speed.json</c>
    /// (never touching <c>"learned"</c> entries). With <paramref name="keepRunning"/> a run in
    /// flight applies to completed targets only: <c>Running</c> states are retained and the
    /// in-flight work continues for its remaining targets.
    /// </summary>
    void ClearResults(bool keepRunning = false);
}
