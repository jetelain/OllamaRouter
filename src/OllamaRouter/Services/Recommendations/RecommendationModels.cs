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

// The task #66 wire records (NodeRoleProfile / RoleRecommendation / ModelNodeProfile /
// FleetRecommendations) and the RouterRecommendation* response records moved to the shared
// client SDK in task #103 (unchanged namespaces — the server serializer context + endpoints
// keep compiling against them). Only the RoleAlias alias vocabulary is server-owned here.
