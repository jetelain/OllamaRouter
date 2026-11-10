using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaRouter.Converters.Dtos;
using OllamaRouter.Serialization;

namespace OllamaRouter.Converters;

/// <summary>
/// Normalizes Ollama wire requests — <c>/api/chat</c> and <c>/api/generate</c> — into the
/// canonical Microsoft.Extensions.AI representation. Stateless and thread-safe; shared singleton.
/// </summary>
public sealed class OllamaChatRequestParser : IChatRequestConverter
{
    public async ValueTask<(IList<ChatMessage> Messages, ChatOptions Options, string ModelName)> ParseChatRequestAsync(
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var request = await JsonSerializer
            .DeserializeAsync(requestBody, OllamaRouterJsonSerializerContext.Default.OllamaRequestDto, cancellationToken)
            .ConfigureAwait(false);

        // A bare JSON "null" body is not a valid request object ("a body that is not a JSON object
        // is not a valid request" — error semantics per spec).
        if (request is null)
        {
            throw new JsonException("Request body must be a JSON object.");
        }


        var modelName = request.Model ?? request.Name ?? string.Empty;
        var messages = new List<ChatMessage>();

        if (request.Messages is not null)
        {
            // /api/chat shape.
            foreach (var dto in request.Messages)
            {
                messages.Add(MapChatMessage(dto));
            }
        }
        else
        {
            // /api/generate shape: top-level system (string or []string) then prompt (string).
            // Absent prompt + absent system => keep-alive ping, parses to zero messages.
            if (request.System is JsonElement systemElement)
            {
                switch (systemElement.ValueKind)
                {
                    case JsonValueKind.String:
                        messages.Add(new ChatMessage(ChatRole.System, systemElement.GetString() ?? string.Empty));
                        break;
                    case JsonValueKind.Array:
                        foreach (var entry in systemElement.EnumerateArray())
                        {
                            messages.Add(new ChatMessage(ChatRole.System, entry.GetString() ?? string.Empty));
                        }

                        break;
                }
            }

            if (request.Prompt is not null)
            {
                var userContents = new List<AIContent> { new TextContent(request.Prompt) };
                if (request.Images is not null)
                {
                    foreach (var image in request.Images)
                    {
                        userContents.Add(ProtocolConverterJsonHelpers.Base64ToImageDataContent(image));
                    }
                }

                messages.Add(new ChatMessage(ChatRole.User, userContents));
            }
        }

        return (messages, BuildOptions(request.Options), modelName);
    }

    private static ChatMessage MapChatMessage(OllamaChatMessageDto dto)
    {
        var role = MapRole(dto.Role);

        // Tool result message: content is the function result payload.
        if (role == ChatRole.Tool)
        {
            return new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent(dto.ToolName ?? string.Empty, dto.Content ?? string.Empty),
            ]);
        }

        var contents = new List<AIContent>();
        // A present-but-empty content string (e.g. assistant tool-call messages) carries no part.
        if (dto.Content is { Length: > 0 })
        {
            contents.Add(new TextContent(dto.Content));
        }

        if (dto.Images is not null)
        {
            foreach (var image in dto.Images)
            {
                contents.Add(ProtocolConverterJsonHelpers.Base64ToImageDataContent(image));
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

                // Ollama conveys no server-side call id: synthesize one.
                var arguments = toolCall.Function.Arguments is JsonElement argumentsElement
                    ? ProtocolConverterJsonHelpers.JsonToDictionary(argumentsElement)
                    : new Dictionary<string, object?>();

                contents.Add(new FunctionCallContent(Guid.NewGuid().ToString("N"), toolCall.Function.Name ?? string.Empty, arguments));
            }
        }

        return contents.Count == 0
            ? new ChatMessage(role, string.Empty)
            : new ChatMessage(role, contents);
    }

    private static ChatRole MapRole(string? role)
    {
        if (string.IsNullOrEmpty(role))
        {
            return ChatRole.User;
        }

        return role switch
        {
            "system" => ChatRole.System,
            "user" => ChatRole.User,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            // Unknown roles never throw: canonical MEAI supports custom role literals.
            _ => new ChatRole(role),
        };
    }

    private static ChatOptions BuildOptions(Dictionary<string, JsonElement>? options)
    {
        var chatOptions = new ChatOptions();
        if (options is null)
        {
            return chatOptions;
        }

        // Canonical hyperparameter mapping.
        if (options.TryGetValue("temperature", out var temperature) && temperature.ValueKind == JsonValueKind.Number)
        {
            chatOptions.Temperature = temperature.GetSingle();
        }

        if (options.TryGetValue("top_p", out var topP) && topP.ValueKind == JsonValueKind.Number)
        {
            chatOptions.TopP = topP.GetSingle();
        }

        if (options.TryGetValue("top_k", out var topK) && topK.ValueKind == JsonValueKind.Number)
        {
            chatOptions.TopK = topK.GetInt32();
        }

        if (options.TryGetValue("num_predict", out var numPredict) && numPredict.ValueKind == JsonValueKind.Number)
        {
            chatOptions.MaxOutputTokens = numPredict.GetInt32();
        }

        if (options.TryGetValue("stop", out var stop))
        {
            chatOptions.StopSequences = ParseStopSequences(stop);
        }

        if (options.TryGetValue("seed", out var seed) && seed.ValueKind == JsonValueKind.Number)
        {
            chatOptions.Seed = seed.GetInt64();
        }

        // The ENTIRE options dictionary is passed through so downstream dispatch can recover
        // non-canonical fields (num_ctx, repeat_penalty, ...). stream/keep_alive/format/template
        // are recognized-but-ignored there.
        if (chatOptions.AdditionalProperties is null)
        {
            // ChatOptions starts with a null AdditionalProperties; materialize it lazily.
            chatOptions.AdditionalProperties = new AdditionalPropertiesDictionary();
        }

        foreach (var pair in options)
        {
            chatOptions.AdditionalProperties[pair.Key] = pair.Value;
        }

        return chatOptions;
    }

    private static IList<string> ParseStopSequences(JsonElement stop)
    {
        switch (stop.ValueKind)
        {
            case JsonValueKind.String:
                return [stop.GetString() ?? string.Empty];
            case JsonValueKind.Array:
                var sequences = new List<string>();
                foreach (var entry in stop.EnumerateArray())
                {
                    sequences.Add(entry.GetString() ?? string.Empty);
                }

                return sequences;
            default:
                return [];
        }
    }
}
