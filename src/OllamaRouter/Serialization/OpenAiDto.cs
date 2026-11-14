using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaRouter.Serialization;

/// <summary>
/// OpenAI-compatible error-envelope wire DTOs (task #60; task #112: the outbound
/// <c>chat/completions</c> request/response DTOs were deleted with the hand-rolled
/// <c>OpenAiHttpChatClient</c> — the native Microsoft.Extensions.AI.OpenAI client owns that wire
/// surface, and the inbound (client → router) protocol DTOs in
/// <see cref="OllamaRouter.Converters.Dtos"/> are unaffected). The error envelope stays: the
/// routing middleware both WRITES it (protocol-shaped converter-path error bodies for the
/// OpenAI-family endpoints) and PARSES it (user-facing error detail of a native-client upstream
/// error). Every type is a <c>public sealed class</c> with get/set properties registered on
/// <see cref="OllamaRouterJsonSerializerContext"/> (source-generated, Native AOT safe — internal
/// DTOs would trip CS0053). Unknown wire fields are silently ignored by the source-generated
/// reader.
/// </summary>

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
