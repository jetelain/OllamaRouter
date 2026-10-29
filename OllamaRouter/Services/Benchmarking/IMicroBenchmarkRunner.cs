using System.Text.Json.Serialization;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Services.Benchmarking;

/// <summary>
/// Lifecycle state of one benchmark run for a single target.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BenchmarkRunStatus>))]
public enum BenchmarkRunStatus
{
    NotStarted = 0,
    Running,
    Succeeded,
    PartiallySucceeded,
    Failed,
    Cancelled
}

/// <summary>
/// One measured (candidate × context) benchmark row. <see cref="Reason"/> is null for
/// measured rows and one of <c>"below-threshold"</c>, <c>"oom"</c>, <c>"sizing-pruned"</c>
/// or <c>"error:..."</c> for non-measured / non-viable rows.
/// </summary>
public sealed record BenchmarkContextResult(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("role")] UsageRole Role,
    [property: JsonPropertyName("numCtx")] int NumCtx,
    [property: JsonPropertyName("evalCount")] int EvalCount,
    [property: JsonPropertyName("evalDurationNs")] long EvalDurationNs,
    [property: JsonPropertyName("decodeTokensPerSecond")] double DecodeTokensPerSecond,
    [property: JsonPropertyName("prefillTokensPerSecond")] double PrefillTokensPerSecond,
    [property: JsonPropertyName("viable")] bool Viable,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>
/// The full benchmark outcome for one target: status, start/finish stamps and the per-context
/// result rows (pruned rows included; the ladder stops at the first non-viable measured row,
/// so no rows after the stopping row are recorded).
/// </summary>
public sealed record BenchmarkTargetResult(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("status")] BenchmarkRunStatus Status,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAt,
    [property: JsonPropertyName("results")] IReadOnlyList<BenchmarkContextResult> Results);

/// <summary>
/// In-memory + persisted state of one target's last completed (or in-flight) benchmark run.
/// <see cref="SizingSource"/> records the provenance of the pre-flight memory estimates:
/// <c>"profile"</c> (a hardware profile was present) or <c>"assumed"</c> (unknown profile,
/// fail-open — the benchmark proceeded without pruning).
/// </summary>
public sealed record BenchmarkTargetState(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("status")] BenchmarkRunStatus Status,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("sizingSource")] string SizingSource,
    [property: JsonPropertyName("results")] IReadOnlyList<BenchmarkContextResult> Results);

/// <summary>
/// One (target, model) entry of <c>targets-speed.json</c> — the seed schema consumed by
/// task #23's auto-learned throughput EWMA. <see cref="TokensPerSecond"/> is the measured
/// decode tps of the LARGEST viable context row (the steady-state speed at the role's
/// operating context); <see cref="Samples"/> counts the measured ladder rows contributing.
/// <see cref="Source"/> is <c>"benchmark"</c> (this task) or <c>"learned"</c> (task #23);
/// benchmark seeding never overwrites <c>"learned"</c> entries.
/// </summary>
public sealed record SpeedSnapshotEntry(
    [property: JsonPropertyName("tokensPerSecond")] double TokensPerSecond,
    [property: JsonPropertyName("samples")] int Samples,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

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
