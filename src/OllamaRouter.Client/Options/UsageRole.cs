using System.Text.Json.Serialization;

namespace OllamaRouter.Options;

/// <summary>
/// Primary usage role of a candidate model in the fleet benchmark matrix.
/// A single model record may list multiple roles (data, not exclusive).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UsageRole>))]
public enum UsageRole
{
    BasicModel = 0,
    AgenticChat,
    SoftwareArchitect,
    SeniorDeveloper,
    JuniorDeveloper
}
