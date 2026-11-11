using System.Text.Json.Serialization;
using OllamaRouter.Options;

namespace OllamaRouter.Services.Recommendations;

/// <summary>
/// The canonical virtual-role alias vocabulary (task #66): the reserved, case-insensitive
/// logical role identifiers that clients may address in a completion request's <c>model</c>
/// property. The router translates an active alias to its recommended NATIVE model name
/// before forwarding, so Ollama only ever sees native names (no alias model evictions /
/// reloads). <see cref="TryFromAlias"/> is the ONLY reserved virtual-role identifier set.
/// </summary>
public static class RoleAlias
{
    public const string Basic = "basic";
    public const string AgenticChat = "agentic_chat";
    public const string SoftwareArchitect = "software_architect";
    public const string SeniorDeveloper = "senior_developer";
    public const string JuniorDeveloper = "junior_developer";

    private static readonly (UsageRole Role, string Alias)[] Map =
    [
        (UsageRole.BasicModel, Basic),
        (UsageRole.AgenticChat, AgenticChat),
        (UsageRole.SoftwareArchitect, SoftwareArchitect),
        (UsageRole.SeniorDeveloper, SeniorDeveloper),
        (UsageRole.JuniorDeveloper, JuniorDeveloper)
    ];

    /// <summary>
    /// The canonical snake_case alias for a usage role (data-driven from the catalog roles).
    /// </summary>
    public static string ToAlias(UsageRole role)
    {
        foreach (var (r, alias) in Map)
        {
            if (r == role)
            {
                return alias;
            }
        }

        return role.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Case-insensitive (OrdinalIgnoreCase) canonical alias lookup. The only way a request
    /// model name can be interpreted as a virtual role.
    /// </summary>
    public static bool TryFromAlias(string? alias, out UsageRole role)
    {
        if (!string.IsNullOrWhiteSpace(alias))
        {
            foreach (var (r, a) in Map)
            {
                if (string.Equals(a, alias, StringComparison.OrdinalIgnoreCase))
                {
                    role = r;
                    return true;
                }
            }
        }

        role = default;
        return false;
    }
}

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
