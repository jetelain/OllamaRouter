using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OllamaRouter.Converters;

/// <summary>
/// Shared, allocation-conscious conversion helpers for the protocol converters (task #59).
/// Zero reflection and no dynamic JSON typing — fully Native AOT safe.
/// </summary>
internal static class ProtocolConverterJsonHelpers
{
    /// <summary>
    /// Recursively converts a JSON object element into a plain <see cref="IDictionary{String, Object}"/>
    /// (string/number/bool/null/object/array), without reflection and without retaining the
    /// underlying <see cref="JsonDocument"/>. Numbers keep integral form when lossless.
    /// A non-object element yields an empty dictionary.
    /// </summary>
    public static IDictionary<string, object?> JsonToDictionary(JsonElement element)
    {
        var dictionary = new Dictionary<string, object?>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            return dictionary;
        }

        foreach (var property in element.EnumerateObject())
        {
            dictionary[property.Name] = JsonToValue(property.Value);
        }

        return dictionary;
    }

    /// <summary>
    /// Converts a parsed <see cref="Dictionary{String, JsonElement}"/> (source-generated OpenAI
    /// tool-argument parse target) into a plain value dictionary via the same rules as
    /// <see cref="JsonToDictionary(JsonElement)"/>.
    /// </summary>
    public static IDictionary<string, object?> DictionaryToJsonDictionary(Dictionary<string, JsonElement> raw)
    {
        var dictionary = new Dictionary<string, object?>(raw.Count);
        foreach (var pair in raw)
        {
            dictionary[pair.Key] = JsonToValue(pair.Value);
        }

        return dictionary;
    }


    /// <summary>
    /// Decodes a base64 payload into an in-memory <see cref="DataContent"/>. Ollama conveys no
    /// MIME type for images, so <c>image/png</c> is assumed. Throws <see cref="FormatException"/>
    /// for invalid base64.
    /// </summary>
    public static DataContent Base64ToImageDataContent(string base64)
        => new(Convert.FromBase64String(base64), "image/png");

    /// <summary>
    /// Maps a canonical finish reason onto the wire literal used by both protocols
    /// ("stop", "length", "tool_calls", "content_filter", lowercase custom values).
    /// <c>null</c> is treated as <see cref="ChatFinishReason.Stop"/>.
    /// </summary>
    public static string ToWireFinishReason(ChatFinishReason? finishReason)
        => (finishReason?.ToString() ?? ChatFinishReason.Stop.ToString()).ToLowerInvariant();

    /// <summary>
    /// Extracts the full assistant text from a canonical completion: the concatenated
    /// <see cref="TextContent"/> texts of the LAST assistant-role message ("" when absent).
    /// </summary>
    public static string ExtractAssistantText(ChatResponse completion)
    {
        var lastAssistant = completion.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastAssistant is null)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var content in lastAssistant.Contents)
        {
            if (content is TextContent text)
            {
                builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Explicit, allocation-conscious JSON value walker supporting the value shapes the canonical
    /// layer can actually carry (the <see cref="JsonToValue(JsonElement)"/> output rules):
    /// string / long / int / double / bool / JsonElement / Dictionary / list / null.
    /// Unrecognized shapes degrade to <c>null</c> rather than throwing mid-request. (Task #112:
    /// moved here from the deleted <c>OpenAiHttpChatClient</c>; the sole surviving consumer is the
    /// Ollama client's tool-result stringification.)
    /// </summary>
    public static void WriteJson(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;

            case string s:
                writer.WriteStringValue(s);
                break;

            case long l:
                writer.WriteNumberValue(l);
                break;

            case int i:
                writer.WriteNumberValue(i);
                break;

            case double d:
                writer.WriteNumberValue(d);
                break;

            case bool b:
                writer.WriteBooleanValue(b);
                break;

            case JsonElement element:
                element.WriteTo(writer);
                break;

            case IDictionary<string, object?> dictionary:
                writer.WriteStartObject();
                foreach (var pair in dictionary)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteJson(writer, pair.Value);
                }

                writer.WriteEndObject();
                break;

            case IEnumerable enumerable:
                // Catches IList<object?> and any non-string enumerable of values
                // (string is handled above; JsonElement implements IEnumerable but is too).
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    WriteJson(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static object? JsonToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out long integral) ? (object)integral : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => JsonToDictionary(element),
        JsonValueKind.Array => JsonToArray(element),
        _ => null,
    };

    private static List<object?> JsonToArray(JsonElement array)
    {
        var list = new List<object?>();
        foreach (var item in array.EnumerateArray())
        {
            list.Add(JsonToValue(item));
        }

        return list;
    }
}
