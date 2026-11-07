using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Models;

/// <summary>
/// Per-model capability summary of <c>GET /api/router/models</c>: the discovered
/// architecture wire name ("Dense"/"Moe"/"Unknown"), the parameter count (null when unknown),
/// the native Modelfile num_ctx (Ollama default when the Modelfile omits it), the
/// router-calculated context recommendation and the cloud overflow hint.
/// <see cref="RecommendedContextWindow"/> is the node's pre-configured capacity (the optimal
/// bucket) for DENSE models — dense contexts are never expanded (clamp-only pass-through);
/// for MOE models it is the max extensible capacity min(native ?? 128k, MaxContextTokens ?? ∞)
/// (the single extensible bucket of the two-bucket ladder).
/// </summary>
public sealed record RouterModelSummary(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("parameterCount")] long? ParameterCount,
    [property: JsonPropertyName("nativeContextWindow")] int NativeContextWindow,
    [property: JsonPropertyName("recommendedContextWindow")] int RecommendedContextWindow,
    [property: JsonPropertyName("cloudOverflowAvailable")] bool CloudOverflowAvailable);

public sealed record RouterModelsResponse(
    [property: JsonPropertyName("models")] IReadOnlyList<RouterModelSummary> Models);

/// <summary>
/// Detailed capability report of <c>GET /api/router/models/{modelName}</c>: the summary
/// fields plus the per-target qualification matrix (the Cloud pseudo-target is never listed).
/// </summary>
public sealed record RouterModelDetailResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("parameterCount")] long? ParameterCount,
    [property: JsonPropertyName("nativeContextWindow")] int NativeContextWindow,
    [property: JsonPropertyName("recommendedContextWindow")] int RecommendedContextWindow,
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterModelTargetCompatibility> Targets);

/// <summary>
/// One row of the per-target qualification matrix: whether the target is capable of serving
/// the model (hardware + catalog presence), its context ceiling, the resident (hot) state
/// and the in-flight request count. <see cref="VetoReason"/> is emitted only when
/// <c>capable == false</c> (null is omitted from the JSON).
/// </summary>
public sealed record RouterModelTargetCompatibility(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("capable")] bool Capable,
    [property: JsonPropertyName("contextCeiling")] int ContextCeiling,
    [property: JsonPropertyName("isResident")] bool IsResident,
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("vetoReason")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VetoReason = null);
