using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaRouter.Converters.Dtos;
using OllamaRouter.Serialization;

namespace OllamaRouter.Converters;

/// <summary>
/// Normalizes OpenAI wire requests — <c>/v1/chat/completions</c> and legacy <c>/v1/completions</c> —
/// into the canonical Microsoft.Extensions.AI representation. Stateless and thread-safe; shared singleton.
/// </summary>
public sealed class OpenAIChatRequestParser : IChatRequestConverter
{
    public async ValueTask<(IList<ChatMessage> Messages, ChatOptions Options, string ModelName)> ParseChatRequestAsync(
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var request = await JsonSerializer
            .DeserializeAsync(requestBody, OllamaRouterJsonSerializerContext.Default.OpenAiChatRequestDto, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            throw new JsonException("Request body must be a JSON object.");
        }

        var modelName = request.Model ?? string.Empty;
        var messages = new List<ChatMessage>();

        if (request.Messages is not null)
        {
            // /v1/chat/completions shape.
            foreach (var dto in request.Messages)
            {
                messages.Add(MapChatMessage(dto));
            }
        }
        else if (request.Prompt is not null)
        {
            // Legacy /v1/completions shape: single user message.
            messages.Add(new ChatMessage(ChatRole.User, request.Prompt));
        }

        return (messages, BuildOptions(request), modelName);
    }

    private static ChatMessage MapChatMessage(OpenAiChatMessageDto dto)
    {
        var role = MapRole(dto.Role);
        var message = new ChatMessage(role, MapContents(dto));

        if (!string.IsNullOrEmpty(dto.Name))
        {
            message.AuthorName = dto.Name;
        }

        return message;
    }

    private static IList<AIContent> MapContents(OpenAiChatMessageDto dto)
    {
        var contents = new List<AIContent>();

        // Tool result message: content is the function result payload.
        if (IsToolRole(dto.Role))
        {
            contents.Add(new FunctionResultContent(dto.ToolCallId ?? string.Empty, dto.Content?.GetString() ?? string.Empty));
            return contents;
        }

        if (dto.ReasoningContent is not null)
        {
            // The router's existing OllamaRequestParser treats reasoning as prompt context; keep it canonical too.
            contents.Add(new TextReasoningContent(dto.ReasoningContent));
        }

        if (dto.Content is JsonElement contentElement)
        {
            switch (contentElement.ValueKind)
            {
                case JsonValueKind.String:
                    contents.Add(new TextContent(contentElement.GetString() ?? string.Empty));
                    break;
                case JsonValueKind.Array:
                    foreach (var part in contentElement.EnumerateArray())
                    {
                        MapContentPart(part, contents);
                    }

                    break;
            }
        }

        if (dto.ToolCalls is not null)
        {
            foreach (var toolCall in dto.ToolCalls)
            {
                if (toolCall.Function is null)
                {
                    continue;
                }

                contents.Add(new FunctionCallContent(
                    toolCall.Id ?? string.Empty,
                    toolCall.Function.Name ?? string.Empty,
                    ParseOpenAiToolArguments(toolCall.Function.Arguments)));
            }
        }

        return contents;
    }

    private static bool IsToolRole(string? role) => role == "tool";

    private static void MapContentPart(JsonElement part, List<AIContent> contents)
    {
        if (part.ValueKind != JsonValueKind.Object ||
            !part.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
        {
            return;
        }

        switch (type.GetString())
        {
            case "text" when part.TryGetProperty("text", out var text):
                contents.Add(new TextContent(text.GetString() ?? string.Empty));
                break;
            case "image_url" when part.TryGetProperty("image_url", out var imageUrl) &&
                imageUrl.ValueKind == JsonValueKind.Object &&
                imageUrl.TryGetProperty("url", out var url) &&
                url.ValueKind == JsonValueKind.String:
                MapImageUrl(url.GetString(), contents);
                break;
            // Unknown part types are ignored (never fail on unknown fields).
        }
    }

    /// <summary>
    /// Maps an image_url to canonical content: data URIs decode to in-memory <see cref="DataContent"/>,
    /// http(s) URLs to <see cref="UriContent"/> (MIME inferred downstream). Returns false when the URL
    /// is unrecognized and therefore ignored.
    /// </summary>
    private static bool MapImageUrl(string? url, List<AIContent> contents)
    {
        if (url is null)
        {
            return false;
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var dataContent = ParseDataUri(url);
            if (dataContent is not null)
            {
                contents.Add(dataContent);
                return true;
            }
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                 (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            contents.Add(new UriContent(uri, null));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses <c>data:&lt;mime&gt;;base64,&lt;payload&gt;</c>; null when the shape is unrecognized
    /// (an invalid base64 payload propagates as <see cref="FormatException"/>).
    /// </summary>
    private static DataContent? ParseDataUri(string url)
    {
        const int dataPrefixLength = 5; // "data:"
        var commaIndex = url.IndexOf(',');
        if (commaIndex <= dataPrefixLength)
        {
            return null;
        }

        var metadata = url[dataPrefixLength..commaIndex];
        if (!metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var mediaType = metadata[..^";base64".Length];
        var payload = url[(commaIndex + 1)..];
        return new DataContent(Convert.FromBase64String(payload), string.IsNullOrEmpty(mediaType) ? "image/png" : mediaType);
    }

    private static ChatRole MapRole(string? role)
    {
        if (string.IsNullOrEmpty(role))
        {
            return ChatRole.User;
        }

        return role switch
        {
            "system" or "developer" => ChatRole.System, // "developer" is OpenAI's newer system variant.
            "user" => ChatRole.User,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            // Unknown roles never throw: canonical MEAI supports custom role literals.
            _ => new ChatRole(role),
        };
    }

    /// <summary>
    /// OpenAI tool-call arguments are a JSON-encoded STRING. Empty/invalid arguments yield an empty
    /// dictionary — parsing a tool call never throws mid-request.
    /// </summary>
    private static IDictionary<string, object?> ParseOpenAiToolArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            var raw = JsonSerializer.Deserialize(arguments, OllamaRouterJsonSerializerContext.Default.DictionaryStringJsonElement)
                ?? new Dictionary<string, JsonElement>();
            return ProtocolConverterJsonHelpers.DictionaryToJsonDictionary(raw);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }


    private static ChatOptions BuildOptions(OpenAiChatRequestDto request)
    {
        var chatOptions = new ChatOptions
        {
            Temperature = request.Temperature,
            TopP = request.TopP,
            // max_completion_tokens is preferred over max_tokens when both are present.
            MaxOutputTokens = request.MaxCompletionTokens ?? request.MaxTokens,
            Seed = request.Seed,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            TopK = request.TopK,
        };

        if (request.Stop is JsonElement stop)
        {
            switch (stop.ValueKind)
            {
                case JsonValueKind.String:
                    chatOptions.StopSequences = [stop.GetString() ?? string.Empty];
                    break;
                case JsonValueKind.Array:
                    var sequences = new List<string>();
                    foreach (var entry in stop.EnumerateArray())
                    {
                        sequences.Add(entry.GetString() ?? string.Empty);
                    }

                    chatOptions.StopSequences = sequences;
                    break;
            }
        }

        switch (request.ResponseFormat?.Type)
        {
            case "json_object":
                chatOptions.ResponseFormat = ChatResponseFormat.Json;
                break;
            case "text":
                chatOptions.ResponseFormat = ChatResponseFormat.Text;
                break;
        }

        return chatOptions;
        }
        }
