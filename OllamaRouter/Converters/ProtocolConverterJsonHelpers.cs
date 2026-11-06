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
