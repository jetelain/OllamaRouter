using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;
using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Benchmark;

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

    // ------------------------------------------------------------------
    //  DTOs: dynamic fleet configuration / Setup API (task #19)
    // ------------------------------------------------------------------
