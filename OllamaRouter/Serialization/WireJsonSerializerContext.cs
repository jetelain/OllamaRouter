using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaRouter.Serialization;

/// <summary>
/// Source-generated serializer context dedicated to the OUTBOUND OpenAI-compatible wire bodies
/// (task #60). Deliberately separate from <see cref="OllamaRouterJsonSerializerContext"/> — that
/// context is configured <c>WriteIndented = true</c> for the management/monitor API responses,
/// while request bodies posted to downstream endpoints must stay compact (single-line JSON, no
/// whitespace bloat on large prompts). ALL serialization in <c>OpenAiHttpChatClient</c> goes
/// through the typed <see cref="JsonTypeInfo{T}"/> members here (or in the shared context for
/// inbound deserialization) — never the reflection-based generic overloads (Native AOT safe).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<OpenAiContentPartDto>))]
[JsonSerializable(typeof(OpenAiChatCompletionRequestDto))]
public partial class WireJsonSerializerContext : JsonSerializerContext
{
}
