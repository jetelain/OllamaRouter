using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;
using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Benchmark;

/// <summary>
/// Request body of <c>POST /api/router/benchmark</c> (a null/absent body is valid and means
/// "benchmark all effective non-Cloud targets"): the optional target to queue (null → all
/// effective targets in configuration order), the optional role filter and the optional
/// single-model filter (both null/empty → all catalog candidates), and the optional
/// per-target model exclusion map (task #152): target name (case-insensitive) → model names
/// (case-insensitive) to SKIP for THAT RUN ONLY — the persisted results and the catalog are
/// untouched, a follow-up run (or a router restart) never remembers them; absent/empty is
/// exactly today's behavior. Unknown target keys and unknown model names are no-ops.
/// </summary>
public sealed record RouterBenchmarkRequest(
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("roles")] IReadOnlyList<UsageRole>? Roles = null,
    [property: JsonPropertyName("model")] string? Model = null,
    // Omitted from the wire when null (D5): consumers that never send exclusions keep their
    // exact pre-#152 request bodies; the field appears only when a run carries a list.
    [property: JsonPropertyName("excludedModels")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ExcludedModels = null);

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

/// <summary>
/// The pre-flight fit verdict of one catalog candidate on one node (task #152), derived by
/// the SAME <c>CandidateSizingCalculator.Evaluate</c> sizing the benchmark run loop applies
/// (one derivation, no drift): <see cref="Fits"/> (at least one ladder context is resident
/// within the fast-memory budget → the model will actually be measured), <see cref="Pruned"/>
/// (every ladder context is sizing-pruned → the run records sizing-pruned rows and never
/// pulls the model) or <see cref="Assumed"/> (no usable hardware profile — fail-open, the
/// benchmark proceeds and an OOM at runtime is the empirical safety net).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BenchmarkCandidateFit>))]
public enum BenchmarkCandidateFit
{
    Fits = 0,
    Pruned,
    Assumed
}

/// <summary>
/// One candidate model row of <c>GET /api/router/benchmark/candidates</c> (task #152): the
/// catalog entry (name / roles / quantization / architecture / params / native context) plus
/// the per-target pre-flight fit verdict and <c>maxFitsContext</c> — the largest ladder
/// context that survives sizing, capped by the model's <c>nativeContext</c> (0 when
/// <see cref="BenchmarkCandidateFit.Pruned"/>) — and the curated-catalog class metadata
/// (task #169; the roles are NOT exclusive assignments, Step 5 resolves them dynamically per
/// hardware):
/// <c>modelClass</c> is the plain string <c>"MoE"</c> (lightweight-active, CPU-offload
/// eligible) or <c>"Expert"</c> (dense/hybrid frontier tier) — exactly the server
/// <c>CandidateModelClass.ToString()</c> value (a string, not an enum, because the enum lives
/// in the server assembly this leaf SDK cannot reference); <c>allowCpuOffload</c> is the
/// curated catalog's CPU-offload eligibility (the sizing gate); <c>estimatedWeightsMB</c> is
/// the measured resident sizing anchor in MiB (the value the run's sizing uses) or <c>null</c>
/// for models without a curated anchor (parametric sizing).
/// All three parameters are optional with defaults mirroring the catalog's conservative
/// fallbacks (unknown/synthetic = dense-strict): an OLD daemon that omits them deserializes
/// to <c>"Expert"</c>/<c>false</c>/<c>null</c> (a cosmetic "Expert · VRAM only" degradation on
/// a mixed-version fleet only), and a NEW daemon's extra fields are ignored by an OLD app.
/// </summary>
public sealed record RouterBenchmarkCandidateModel(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("roles")] IReadOnlyList<UsageRole> Roles,
    [property: JsonPropertyName("quantization")] string Quantization,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("totalParams")] long TotalParams,
    [property: JsonPropertyName("activeParams")] long? ActiveParams,
    [property: JsonPropertyName("nativeContext")] int NativeContext,
    [property: JsonPropertyName("fit")] BenchmarkCandidateFit Fit,
    [property: JsonPropertyName("maxFitsContext")] int MaxFitsContext,
    [property: JsonPropertyName("modelClass")] string ModelClass = "Expert",
    [property: JsonPropertyName("allowCpuOffload")] bool AllowCpuOffload = false,
    [property: JsonPropertyName("estimatedWeightsMB")] double? EstimatedWeightsMB = null);

/// <summary>
/// One target group of <c>GET /api/router/benchmark/candidates</c> (task #152): the target
/// name, its STORED fleet-hardware profile (<c>null</c> when the store has nothing — no
/// probe is ever triggered by the read) and the EXACT catalog candidate set the run loop
/// would benchmark for that target, each annotated with its pre-flight fit verdict.
/// </summary>
public sealed record RouterBenchmarkCandidateTarget(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("hardware")] NodeHardwareProfile? Hardware,
    [property: JsonPropertyName("candidates")] IReadOnlyList<RouterBenchmarkCandidateModel> Candidates);

/// <summary>
/// 200 response of <c>GET /api/router/benchmark/candidates</c> (task #152): one row per
/// effective non-Cloud target (configuration order — the same list a fleet-wide run queues
/// via <c>GetEffectiveTargetNames()</c>), each with the stored hardware profile and the full
/// per-model candidate plan. Bodyless, keyless read, no network calls.
/// </summary>
public sealed record RouterBenchmarkCandidatesResponse(
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterBenchmarkCandidateTarget> Targets);

    // ------------------------------------------------------------------
    //  DTOs: dynamic fleet configuration / Setup API (task #19)
    // ------------------------------------------------------------------
