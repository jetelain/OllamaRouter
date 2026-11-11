using System.Text.Json.Serialization;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Info;

namespace OllamaRouter.Cli.Serialization;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> of the CLI-owned JSON output types
/// (task #100): the CLI is BCL + <c>OllamaRouter.Client</c> only, so <c>status --json</c>
/// NEVER goes through reflection-based <c>JsonSerializer.Serialize&lt;object&gt;</c>.
/// </summary>
[JsonSerializable(typeof(CliStatusPayload))]
[JsonSerializable(typeof(RouterInfoResponse))]
[JsonSerializable(typeof(RouterFleetResponse))]
public partial class CliJsonContext : JsonSerializerContext
{
}
