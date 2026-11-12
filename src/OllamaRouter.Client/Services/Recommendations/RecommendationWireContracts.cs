using System.Text.Json.Serialization;

namespace OllamaRouter.Services.Recommendations;

/// <summary>
/// One node row of a role/model profile (task #66): the per-target context ceiling the
/// recommendation engine derived from the micro-benchmark ladder evidence for one native
/// model on one fleet node.
/// </summary>
public sealed record NodeRoleProfile(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("contextCeiling")] int ContextCeiling,
    [property: JsonPropertyName("baselineContext")] int? BaselineContext,
    [property: JsonPropertyName("tokensPerSecond")] double TokensPerSecond,
    [property: JsonPropertyName("vramMB")] int? VramMB);

/// <summary>
/// One role's recommended assignment (task #66): the winning NATIVE model name (never an
/// alias) plus the per-node profiles (fleet configuration order). Roles with no viable
/// candidate anywhere are ABSENT from the matrix (the wizard treats absence as
/// "benchmark this role's candidates").
/// </summary>
public sealed record RoleRecommendation(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("recommendedContextWindow")] int RecommendedContextWindow,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("nodes")] IReadOnlyList<NodeRoleProfile> Nodes);

/// <summary>
/// The per-node profiles for one native model across all roles it won (task #66; union
/// over roles, one row per node, fleet configuration order). The keys of the matrix's
/// <see cref="FleetRecommendations.Models"/> index are native model names; enforcement
/// lookups normalize the requested name with the tag-stripping model-name normalizer.
/// </summary>
public sealed record ModelNodeProfile(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("nativeContext")] int NativeContext,
    [property: JsonPropertyName("quantization")] string Quantization,
    [property: JsonPropertyName("nodes")] IReadOnlyList<NodeRoleProfile> Nodes);

/// <summary>
/// The full fleet recommendation matrix (task #66) persisted to
/// <c>&lt;stateDir&gt;/recommended.json</c> and served by
/// <c>GET /api/router/recommendations</c>. <see cref="Roles"/> is keyed by the canonical
/// snake_case aliases; <see cref="Models"/> is keyed by native model name. Empty matrices
/// (generatedAt null, empty dictionaries) are the stable "nothing active" shape.
/// </summary>
public sealed record FleetRecommendations(
    [property: JsonPropertyName("generatedAt")] DateTimeOffset? GeneratedAt,
    [property: JsonPropertyName("roles")] IReadOnlyDictionary<string, RoleRecommendation> Roles,
    [property: JsonPropertyName("models")] IReadOnlyDictionary<string, ModelNodeProfile> Models);
