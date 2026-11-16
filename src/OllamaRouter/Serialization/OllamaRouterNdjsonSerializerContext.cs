using System.Text.Json.Serialization;
using OllamaRouter.Endpoints.RouterApi.Models;

namespace OllamaRouter.Serialization;

/// <summary>
/// The COMPACT serializer context of the <c>POST /api/router/models/pull</c> NDJSON stream
/// (task #125): the shared <see cref="OllamaRouterJsonSerializerContext"/> is
/// <c>WriteIndented = true</c> (pretty-printed <c>Results.Json</c> management responses),
/// which would break the one-event-per-line wire shape — this dedicated context serializes
/// <see cref="OllamaRouterModelPullEvent"/> as a SINGLE compact JSON line (AOT-safe, same
/// property-name conventions as the shared context).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(OllamaRouterModelPullEvent))]
public partial class OllamaRouterNdjsonSerializerContext : JsonSerializerContext
{
}
