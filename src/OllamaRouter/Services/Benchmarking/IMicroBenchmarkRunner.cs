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
    /// Returns false (the endpoint maps this to 409) when a run is already in flight.
    /// </summary>
    bool TryStartRun(RoutingTarget? target, IEnumerable<UsageRole>? roles, string? model);

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
