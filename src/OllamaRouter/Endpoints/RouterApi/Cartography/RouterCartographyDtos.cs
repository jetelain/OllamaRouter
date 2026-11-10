using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Cartography;

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
