using System.Text.Json;

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
                    if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            parts.Add(text);
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

            return string.Join("\n", parts);
        }
        catch
        {
            // Invalid or non-JSON request body: no usable context.
        }

        return "";
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
        }
        catch
        {
            // Ignore parsing errors.
        }

        return "";
    }
}
