using System.Text.Json.Serialization;
using OllamaRouter.Services.Recommendations;

namespace OllamaRouter.Endpoints.RouterApi.Recommendations;

/// <summary>
/// Response of <c>POST /api/router/recommendations/generate</c> (task #66): the recompute +
/// persist + activate outcome. <see cref="Recommendations"/> is the freshly activated
/// matrix; <see cref="Targets"/> lists the distinct fleet targets it covers (configuration
/// order).
/// </summary>
public sealed record RouterRecommendationsGeneratedResponse(
    [property: JsonPropertyName("generated")] bool Generated,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("roleCount")] int RoleCount,
    [property: JsonPropertyName("modelCount")] int ModelCount,
    [property: JsonPropertyName("targets")] IReadOnlyList<string> Targets,
    [property: JsonPropertyName("recommendations")] FleetRecommendations Recommendations);

/// <summary>
/// One target row of <c>RouterRecommendationsAppliedResponse</c>: the count of models with
/// active per-node ceilings on the target and the highest active ceiling there.
/// </summary>
public sealed record RouterRecommendationsAppliedTarget(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("models")] int Models,
    [property: JsonPropertyName("maxContextCeiling")] int MaxContextCeiling);

/// <summary>
/// Response of <c>POST /api/router/recommendations/apply</c> (task #66): the idempotent
/// re-activation of the current <c>recommended.json</c> into the live store, reporting the
/// per-target ceilings now enforced.
/// </summary>
public sealed record RouterRecommendationsAppliedResponse(
    [property: JsonPropertyName("applied")] bool Applied,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset? GeneratedAt,
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterRecommendationsAppliedTarget> Targets);
