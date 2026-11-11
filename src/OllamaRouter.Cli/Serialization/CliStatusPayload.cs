using System.Text.Json.Serialization;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Info;

namespace OllamaRouter.Cli.Serialization;

/// <summary>
/// The <c>ollamarouter status --json</c> payload (task #100): one flat document with the
/// daemon state classification, an optional detail cause/hint, the parsed router identity
/// (version, PID, uptime — non-null when the probe authenticated/decoded the identity) and
/// the fleet rows (non-null when the daemon is running).
/// </summary>
public sealed record CliStatusPayload(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("detail")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail,
    [property: JsonPropertyName("info")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouterInfoResponse? Info,
    [property: JsonPropertyName("fleet")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouterFleetResponse? Fleet);
