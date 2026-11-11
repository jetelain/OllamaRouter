using System.Text.Json.Serialization;
using OllamaRouter.Options;

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
