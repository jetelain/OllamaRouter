using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;

namespace OllamaRouter.Serialization;

/// <summary>
/// Source-generated serializer context for the native OllamaSharp chat wire surface (task #111) —
/// the CONSUMER-defined <see cref="JsonSerializerContext"/> that OllamaSharp's Native AOT support
/// is designed to take (<c>OllamaApiClient(HttpClient, string, JsonSerializerContext?)</c>). The
/// package's own generated context (<c>OllamaSharp.Models.JsonSourceGenerationContext</c>) is
/// <c>internal</c> and indented, so the server defines its own:
/// <list type="bullet">
/// <item><c>WriteIndented = false</c> — compact single-line wire bodies (the package context
/// writes indented JSON, which is unusable for request bodies);</item>
/// <item>outbound request root: <see cref="ChatRequest"/> (<c>Message</c>, <c>Message.ToolCall</c>,
/// <c>Message.Function</c>, <c>RequestOptions</c>, <c>ThinkValue</c> arrive transitively via
/// property types — the converters on <c>ChatRole</c>/<c>ThinkValue</c> are honored by the
/// generator);</item>
/// <item>inbound response roots: <see cref="ChatResponseStream"/> (per NDJSON frame) and
/// <see cref="ChatDoneResponseStream"/> (the done frame) — both deserialized explicitly by the
/// client's streaming reader; <see cref="ErrorResponse"/> for the <c>{"error": ...}</c> probe the
/// non-chat streams perform;</item>
/// <item>runtime value types that flow through the <c>object</c>-typed wire properties
/// (<c>ChatRequest.Tools</c> = <c>IEnumerable&lt;object&gt;</c> of <see cref="JsonObject"/>,
/// <c>Message.Function.Arguments</c> = <c>IDictionary&lt;string, object?&gt;</c>): <see cref="string"/>,
/// <see cref="JsonElement"/>, <see cref="JsonNode"/>, <see cref="JsonObject"/>,
/// <c>Dictionary&lt;string, object?&gt;</c>, <c>List&lt;object?&gt;</c> (primitives are built-in).</item>
/// </list>
/// This exact registration set was runtime-verified in AOT context mode for the full chat surface
/// (tools, tool results after the adapter's normalization, thinking, images, streaming,
/// non-streaming, 400/404 errors). If ILC/<c>SYSLIB1030</c> ever flags a missing type during the
/// AOT publish, extend the registration list — never fall back to reflection.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatResponseStream))]
[JsonSerializable(typeof(ChatDoneResponseStream))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(List<object?>))]
public partial class OllamaSharpWireContext : JsonSerializerContext
{
}
