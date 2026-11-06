using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaRouter.Converters.Dtos;

// Inbound wire DTOs for the protocol converters (task #59). Deserialized exclusively through the
// source-generated OllamaRouterJsonSerializerContext (Native AOT: no reflection, no dynamic typing).
// The DTOs are public (not internal) because the source generator emits public members on the
// public serializer context; internal types would trip CS0053. Unknown wire fields are never
// declared here, so they are silently ignored by design.

public sealed class OllamaRequestDto
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    // Legacy alias of "model".
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>/api/chat: present (even as []) means chat shape.</summary>
    [JsonPropertyName("messages")]
    public List<OllamaChatMessageDto>? Messages { get; set; }

    /// <summary>/api/generate: single prompt string (present-but-empty still counts as present).</summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    /// <summary>/api/generate: string or []string of system instructions.</summary>
    [JsonPropertyName("system")]
    public JsonElement? System { get; set; }

    /// <summary>/api/generate: base64 image payloads appended to the user message.</summary>
    [JsonPropertyName("images")]
    public List<string>? Images { get; set; }

    /// <summary>Generation options; the entire dictionary is passed through to ChatOptions.AdditionalProperties.</summary>
    [JsonPropertyName("options")]
    public Dictionary<string, JsonElement>? Options { get; set; }
}

public sealed class OllamaChatMessageDto
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>Base64 image payloads (Ollama conveys no MIME; mapped to image/png).</summary>
    [JsonPropertyName("images")]
    public List<string>? Images { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OllamaToolCallDto>? ToolCalls { get; set; }

    /// <summary>Only on role:"tool" result messages.</summary>
    [JsonPropertyName("tool_name")]
    public string? ToolName { get; set; }
}

public sealed class OllamaToolCallDto
{
    [JsonPropertyName("function")]
    public OllamaToolCallFunctionDto? Function { get; set; }
}

public sealed class OllamaToolCallFunctionDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Ollama schema: a JSON object (not a string).</summary>
    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; set; }
}

public sealed class OpenAiChatRequestDto
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>/v1/chat/completions: present (even as []) means chat shape.</summary>
    [JsonPropertyName("messages")]
    public List<OpenAiChatMessageDto>? Messages { get; set; }

    /// <summary>Legacy /v1/completions: single prompt string.</summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    /// <summary>Preferred over <see cref="MaxTokens"/> when both are present.</summary>
    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    /// <summary>string or []string of stop sequences.</summary>
    [JsonPropertyName("stop")]
    public JsonElement? Stop { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public float? FrequencyPenalty { get; set; }

    [JsonPropertyName("presence_penalty")]
    public float? PresencePenalty { get; set; }

    /// <summary>Non-standard, accepted by some clients.</summary>
    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("response_format")]
    public OpenAiResponseFormatDto? ResponseFormat { get; set; }
}

public sealed class OpenAiChatMessageDto
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>Optional message author name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>string (plain text) or array of content parts ({"type":"text"|...}).</summary>
    [JsonPropertyName("content")]
    public JsonElement? Content { get; set; }

    /// <summary>Assistant messages only.</summary>
    [JsonPropertyName("tool_calls")]
    public List<OpenAiToolCallDto>? ToolCalls { get; set; }

    /// <summary>Only on role:"tool" result messages.</summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    /// <summary>Assistant reasoning/thinking trace kept as canonical TextReasoningContent.</summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }
}

public sealed class OpenAiToolCallDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("function")]
    public OpenAiToolCallFunctionDto? Function { get; set; }
}

public sealed class OpenAiToolCallFunctionDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>OpenAI schema: a JSON-encoded string (not a nested object).</summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

public sealed class OpenAiResponseFormatDto
{
    /// <summary>"text" or "json_object".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
