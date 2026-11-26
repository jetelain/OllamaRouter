using System.Text.Json;
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
/// The in-flight phase of one benchmark target (task #174) — the live progress metadata
/// published on <see cref="BenchmarkTargetResult.Progress"/> while the target is Running
/// (omitted on terminal rows; NEVER persisted to <c>benchmarks.json</c>). Wire strings are
/// the pinned kebab-case form ("queued", "downloading-model", …) — see
/// <see cref="BenchmarkPhaseJsonConverter"/>.
/// </summary>
[JsonConverter(typeof(BenchmarkPhaseJsonConverter))]
public enum BenchmarkPhase
{
    Queued = 0,
    Sizing,
    CheckingModel,
    DownloadingModel,
    InspectingModel,
    UnloadingModel,
    Benchmarking,
    Persisting
}

/// <summary>
/// The AOT-safe wire mapping of <see cref="BenchmarkPhase"/> to the task #174 pinned
/// kebab-case strings ("queued", "sizing", "checking-model", "downloading-model",
/// "inspecting-model", "unloading-model", "benchmarking", "persisting"). A plain
/// <see cref="JsonStringEnumConverter{T}"/> cannot express kebab-case via the attribute
/// route (no kebab naming policy exists), so the mapping is explicit here.
/// </summary>
public sealed class BenchmarkPhaseJsonConverter : JsonConverter<BenchmarkPhase>
{
    public override BenchmarkPhase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value?.ToLowerInvariant() switch
        {
            "queued" => BenchmarkPhase.Queued,
            "sizing" => BenchmarkPhase.Sizing,
            "checking-model" => BenchmarkPhase.CheckingModel,
            "downloading-model" => BenchmarkPhase.DownloadingModel,
            "inspecting-model" => BenchmarkPhase.InspectingModel,
            "unloading-model" => BenchmarkPhase.UnloadingModel,
            "benchmarking" => BenchmarkPhase.Benchmarking,
            "persisting" => BenchmarkPhase.Persisting,
            _ => throw new JsonException($"Unknown benchmark phase: {value}")
        };
    }

    public override void Write(Utf8JsonWriter writer, BenchmarkPhase value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            BenchmarkPhase.Queued => "queued",
            BenchmarkPhase.Sizing => "sizing",
            BenchmarkPhase.CheckingModel => "checking-model",
            BenchmarkPhase.DownloadingModel => "downloading-model",
            BenchmarkPhase.InspectingModel => "inspecting-model",
            BenchmarkPhase.UnloadingModel => "unloading-model",
            BenchmarkPhase.Benchmarking => "benchmarking",
            BenchmarkPhase.Persisting => "persisting",
            _ => throw new InvalidOperationException($"Unexpected benchmark phase: {value}")
        });
    }
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
/// The live in-flight progress of ONE Running benchmark target (task #174, in-memory only —
/// never persisted): the current <see cref="Phase"/>, the model being worked on (with its
/// role), the rung context + model/rung indices of the benchmarking phase, the raw daemon
/// status text + per-digest-aggregated byte counts during downloads, and <see cref="Message"/>
/// — the ready-to-render human sentence built by <see cref="BenchmarkProgressText"/>.
/// </summary>
public sealed record BenchmarkTargetProgress(
    [property: JsonPropertyName("phase")] BenchmarkPhase Phase,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("role")] UsageRole? Role = null,
    [property: JsonPropertyName("numCtx")] int? NumCtx = null,
    [property: JsonPropertyName("candidateIndex")] int CandidateIndex = 0,
    [property: JsonPropertyName("candidateCount")] int CandidateCount = 0,
    [property: JsonPropertyName("rungIndex")] int RungIndex = 0,
    [property: JsonPropertyName("rungCount")] int RungCount = 0,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("completedBytes")] long? CompletedBytes = null,
    [property: JsonPropertyName("totalBytes")] long? TotalBytes = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt = default);

/// <summary>
/// The full benchmark outcome for one target: status, start/finish stamps and the per-context
/// result rows (pruned rows included; the ladder stops at the first non-viable measured row,
/// so no rows after the stopping row are recorded).
/// <see cref="Progress"/> (task #174) is the live in-flight metadata of a Running target —
/// null (and then OMITTED from the JSON) on terminal rows and for older daemons, so the wire
/// shape stays backward-compatible in both directions.
/// </summary>
public sealed record BenchmarkTargetResult(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("status")] BenchmarkRunStatus Status,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAt,
    [property: JsonPropertyName("results")] IReadOnlyList<BenchmarkContextResult> Results,
    [property: JsonPropertyName("progress")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    BenchmarkTargetProgress? Progress = null);

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
