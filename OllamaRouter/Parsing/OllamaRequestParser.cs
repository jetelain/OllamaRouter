using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllamaRouter.Parsing;

/// <summary>
/// Extracts the relevant information (prompt, model name) from the JSON body of an Ollama or
/// OpenAI-compatible request. Static and pure class, with no dependency on external state.
/// </summary>
public static class OllamaRequestParser
{
    public static string ExtractPrompt(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;

            // Handles the /api/chat and /v1/chat/completions format
            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                var lastMessage = messages.EnumerateArray().LastOrDefault();
                if (lastMessage.ValueKind != JsonValueKind.Undefined && lastMessage.TryGetProperty("content", out var content))
                {
                    return content.GetString() ?? "";
                }
            }
            // Handles the /api/generate and /v1/completions format
            else if (root.TryGetProperty("prompt", out var prompt))
            {
                return prompt.GetString() ?? "";
            }
        }
        catch
        {
            // Invalid or non-JSON request body: no usable prompt.
        }

        return "";
    }

    /// <summary>
    /// Extracts the full textual context of a request (all messages, system prompt, template...),
    /// so that token estimation is not limited to the last prompt/message only.
    /// </summary>
    public static string ExtractContextText(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;
            var parts = new List<string>();

            // Handles the /api/chat and /v1/chat/completions format: concatenate every message content.
            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.TryGetProperty("content", out var content))
                    {
                        AppendContent(content, parts);
                    }

                    // Assistant messages requesting tool/function calls: include the call payload
                    // (name + arguments), since it is also part of the model's context.
                    if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var toolCall in toolCalls.EnumerateArray())
                        {
                            parts.Add(toolCall.GetRawText());
                        }
                    }
                }
            }

            // Handles the /api/generate and /v1/completions format.
            if (root.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String)
            {
                var text = prompt.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add(text);
                }
            }

            // Optional system prompt, present in both formats.
            if (root.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.String)
            {
                var text = system.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add(text);
                }
            }

            // Optional custom prompt template.
            if (root.TryGetProperty("template", out var template) && template.ValueKind == JsonValueKind.String)
            {
                var text = template.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add(text);
                }
            }

            // Optional tool/function definitions (chat and OpenAI-compatible formats): their JSON
            // schema is injected into the model's context and can be sizeable.
            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (var tool in tools.EnumerateArray())
                {
                    parts.Add(tool.GetRawText());
                }
            }

            return string.Join("\n", parts);
        }
        catch
        {
            // Invalid or non-JSON request body: no usable context.
        }

        return "";
    }

    /// <summary>
    /// Extracts the size (in tokens) of a previous conversation context carried over via the
    /// raw <c>context</c> field of the /api/generate endpoint (an array of token ids returned by
    /// a prior call). Ignoring this field leads to a large underestimation of the real context
    /// size for follow-up requests, since it is not plain text and cannot be tokenized again.
    /// </summary>
    public static int ExtractPriorContextTokenCount(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (doc.RootElement.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Array)
            {
                return context.GetArrayLength();
            }
        }
        catch
        {
            // Invalid or non-JSON request body: no usable context.
        }

        return 0;
    }

    /// <summary>
    /// Appends the textual content of a message's "content" property, which may be either a
    /// plain string or an array of content parts (OpenAI-compatible multimodal format, e.g.
    /// [{ "type": "text", "text": "..." }, { "type": "image_url", ... }]).
    /// </summary>
    private static void AppendContent(JsonElement content, List<string> parts)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(text);
            }
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    var text = part.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        parts.Add(text);
                    }
                }
                else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    var text = textElement.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        parts.Add(text);
                    }
                }
            }
        }
    }

    public static string ExtractModelName(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (doc.RootElement.TryGetProperty("model", out var modelElement))
            {
                return modelElement.GetString() ?? "";
            }

            // Legacy alias used by some /api/show clients.
            if (doc.RootElement.TryGetProperty("name", out var nameElement))
            {
                return nameElement.GetString() ?? "";
            }
        }
        catch
        {
            // Ignore parsing errors.
        }

        return "";
    }

    /// <summary>
    /// Returns a copy of the request body with the "model" property (or the legacy "name" alias)
    /// rewritten to <paramref name="newModelName"/>. Used to substitute a cloud-hosted model name
    /// when overflowing to ollama.com cloud. Returns the original body unchanged if it cannot be
    /// parsed or does not contain a model field.
    /// </summary>
    public static string ReplaceModelName(string jsonBody, string newModelName)
    {
        try
        {
            var node = JsonNode.Parse(jsonBody);
            if (node is JsonObject obj)
            {
                if (obj.ContainsKey("model"))
                {
                    obj["model"] = newModelName;
                }
                else if (obj.ContainsKey("name"))
                {
                    obj["name"] = newModelName;
                }
                else
                {
                    return jsonBody;
                }

                return obj.ToJsonString();
            }
        }
        catch
        {
            // Ignore parsing errors and fall back to the original body.
        }

        return jsonBody;
    }
}
