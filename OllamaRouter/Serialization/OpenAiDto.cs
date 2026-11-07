using System.Text.Json;
using System.Text.Json.Serialization;
using OllamaRouter.Converters.Dtos;

namespace OllamaRouter.Serialization;

/// <summary>
/// Outbound (router → downstream) OpenAI-compatible <c>/chat/completions</c> wire DTOs (task #60).
/// Deliberately distinct from the inbound (client → router) protocol DTOs in
/// <see cref="OllamaRouter.Converters.Dtos"/>: the outbound graph only ever carries what the
/// canonical Microsoft.Extensions.AI request maps onto, and reuses the inbound
/// <see cref="OpenAiResponseFormatDto"/> (identical wire shape) instead of duplicating it.
/// Every type is a <c>public sealed class</c> with get/set properties registered on
/// <see cref="OllamaRouterJsonSerializerContext"/> (source-generated, Native AOT safe — internal
/// DTOs would trip CS0053, same constraint documented in ProtocolRequestDtos.cs).
/// Unknown wire fields on responses are silently ignored by the source-generated reader.
/// </summary>

// ---------------------------------------------------------------------------
// Request (router -> downstream)
// ---------------------------------------------------------------------------

/// <summary>Outbound <c>POST /chat/completions</c> request body.</summary>
public sealed class OpenAiChatCompletionRequestDto
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("messages")]
    public List<OpenAiRequestMessageDto> Messages { get; set; } = [];

    /// <summary>Set true only for streaming; omitted (null) otherwise.</summary>
    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    /// <summary>Sent together with <c>stream: true</c> so the downstream echoes a final usage chunk.</summary>
    [JsonPropertyName("stream_options")]
    public OpenAiStreamOptionsDto? StreamOptions { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    /// <summary>Mapped from <c>ChatOptions.MaxOutputTokens</c>; <c>max_tokens</c> is chosen over
    /// <c>max_completion_tokens</c> for cross-provider compatibility (vLLM, llama.cpp, Groq, ...).</summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    /// <summary>JSON string (single sequence) or string array, per the OpenAI wire contract.</summary>
    [JsonPropertyName("stop")]
    public JsonElement? Stop { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public double? FrequencyPenalty { get; set; }

    [JsonPropertyName("presence_penalty")]
    public double? PresencePenalty { get; set; }

    [JsonPropertyName("tools")]
    public List<OpenAiRequestToolDto>? Tools { get; set; }

    /// <summary>Only the literal <c>"required"</c> is emitted in v1 (see task spec §3).</summary>
    [JsonPropertyName("tool_choice")]
    public string? ToolChoice { get; set; }

    /// <summary>Only <c>{"type":"json_object"}</c> in v1; reuses the inbound DTO (same wire shape).</summary>
    [JsonPropertyName("response_format")]
    public OpenAiResponseFormatDto? ResponseFormat { get; set; }

    /// <summary>Mapped from <c>ChatOptions.Reasoning?.Effort</c> (lowercase; omitted for <c>None</c>).</summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }
}

/// <summary>Outbound chat message. <see cref="Content"/> is a JSON string (single text) or an
/// array of <see cref="OpenAiContentPartDto"/>; tool-result messages force <c>role:"tool"</c>
/// and carry <see cref="ToolCallId"/>.</summary>
public sealed class OpenAiRequestMessageDto
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("content")]
    public JsonElement? Content { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OpenAiRequestToolCallDto>? ToolCalls { get; set; }

    /// <summary>Present on tool-result messages (OpenAI requires a string <see cref="Content"/> there).</summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }
}

/// <summary>One entry of a multimodal content array (<c>{"type":"text"|"image_url", ...}</c>).</summary>
public sealed class OpenAiContentPartDto
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    public OpenAiImageUrlDto? ImageUrl { get; set; }
}

/// <summary><c>{"url": "data:&lt;mime&gt;;base64,&lt;payload&gt;"}</c> — v1 only emits base64 data URIs.</summary>
public sealed class OpenAiImageUrlDto
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>Assistant tool call; <see cref="OpenAiRequestToolCallFunctionDto.Arguments"/> is a JSON <b>string</b>
/// per the OpenAI wire contract.</summary>
public sealed class OpenAiRequestToolCallDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("function")]
    public OpenAiRequestToolCallFunctionDto? Function { get; set; }
}

/// <summary><c>{"name": ..., "arguments": "...json..."}</c>.</summary>
public sealed class OpenAiRequestToolCallFunctionDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

/// <summary>Declared tool: <c>{"type":"function","function":{...}}</c>.</summary>
public sealed class OpenAiRequestToolDto
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("function")]
    public OpenAiRequestToolFunctionDto? Function { get; set; }
}

/// <summary><c>{"name","description"?,"parameters"?(JSON Schema object)}</c>.</summary>
public sealed class OpenAiRequestToolFunctionDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}

/// <summary><c>{"include_usage": true}</c> — asks the downstream to emit a final usage chunk when streaming.</summary>
public sealed class OpenAiStreamOptionsDto
{
    [JsonPropertyName("include_usage")]
    public bool? IncludeUsage { get; set; }
}

// ---------------------------------------------------------------------------
// Response (downstream -> router)
// ---------------------------------------------------------------------------

/// <summary>Non-streaming <c>chat/completions</c> response body.</summary>
public sealed class OpenAiChatCompletionResponseDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("created")]
    public long? Created { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>May be empty or missing (provider-specific empty completions must not throw).</summary>
    [JsonPropertyName("choices")]
    public List<OpenAiResponseChoiceDto>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public OpenAiUsageDto? Usage { get; set; }
}

/// <summary>One completion choice of the non-streaming response.</summary>
public sealed class OpenAiResponseChoiceDto
{
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("message")]
    public OpenAiResponseMessageDto? Message { get; set; }

    /// <summary>"stop" / "length" / "tool_calls" / "content_filter" / provider-specific.</summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>Assistant message of a completion choice. <see cref="Content"/> is a string or a parts array.</summary>
public sealed class OpenAiResponseMessageDto
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public JsonElement? Content { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OpenAiResponseToolCallDto>? ToolCalls { get; set; }
}

/// <summary>Tool call — shared between full responses and streaming deltas
/// (in deltas, <see cref="OpenAiResponseToolCallFunctionDto.Arguments"/> arrives as fragments).</summary>
public sealed class OpenAiResponseToolCallDto
{
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("function")]
    public OpenAiResponseToolCallFunctionDto? Function { get; set; }
}

/// <summary><c>{"name"?,"arguments"?}</c>.</summary>
public sealed class OpenAiResponseToolCallFunctionDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

// ---------------------------------------------------------------------------
// Streaming (SSE data: payloads)
// ---------------------------------------------------------------------------

/// <summary>One SSE <c>data:</c> JSON payload of a streamed completion.</summary>
public sealed class OpenAiStreamChunkDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("created")]
    public long? Created { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Empty (not null) on the final usage chunk when <c>stream_options.include_usage</c> is honored.</summary>
    [JsonPropertyName("choices")]
    public List<OpenAiStreamChunkChoiceDto>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public OpenAiUsageDto? Usage { get; set; }
}

/// <summary>One streamed choice carrying a content delta.</summary>
public sealed class OpenAiStreamChunkChoiceDto
{
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("delta")]
    public OpenAiStreamChunkDeltaDto? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>Incremental assistant content. <c>tool_calls</c> are fragmentary (id/name once,
/// arguments as string fragments across chunks).</summary>
public sealed class OpenAiStreamChunkDeltaDto
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OpenAiResponseToolCallDto>? ToolCalls { get; set; }
}

// ---------------------------------------------------------------------------
// Usage + error envelope
// ---------------------------------------------------------------------------

/// <summary>Token usage (non-streaming <c>usage</c> field and streaming usage chunk).</summary>
public sealed class OpenAiUsageDto
{
    [JsonPropertyName("prompt_tokens")]
    public long? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public long? CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long? TotalTokens { get; set; }

    [JsonPropertyName("completion_tokens_details")]
    public OpenAiCompletionTokensDetailsDto? CompletionTokensDetails { get; set; }
}

/// <summary><c>{"reasoning_tokens": n}</c> (DeepSeek / vLLM / OpenAI reasoning breakdown).</summary>
public sealed class OpenAiCompletionTokensDetailsDto
{
    [JsonPropertyName("reasoning_tokens")]
    public long? ReasoningTokens { get; set; }
}

/// <summary>OpenAI error envelope: <c>{"error": {"message","type","param","code"}}</c>.</summary>
public sealed class OpenAiErrorResponseDto
{
    [JsonPropertyName("error")]
    public OpenAiErrorDetailDto? Error { get; set; }
}

/// <summary>Error detail of the OpenAI error envelope.</summary>
public sealed class OpenAiErrorDetailDto
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("param")]
    public string? Param { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}
