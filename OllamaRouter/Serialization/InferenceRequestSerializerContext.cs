using System.Text.Json;
using System.Text.Json.Serialization;
using OllamaRouter.Services;

namespace OllamaRouter.Serialization;

/// <summary>
/// AOT source-generated serializer context for the inference request recording manifest.
/// Unlike <see cref="OllamaRouterJsonSerializerContext"/>, it writes compact (non-indented)
/// JSON so that each entry fits on a single JSON Lines line.
/// </summary>
[JsonSourceGenerationOptions]
[JsonSerializable(typeof(InferenceRequestRecord))]
public partial class InferenceRequestSerializerContext : JsonSerializerContext
{
}
