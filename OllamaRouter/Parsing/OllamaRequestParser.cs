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
    /// Parses a completion or chat request in a single JSON pass, extracting the model name,
    /// combined textual context (including reasoning/thinking fields echoed on prior messages),
    /// and any prior context tokens without redundant DOM allocations.
    /// </summary>
    public static OllamaCompletionRequestInfo ParseCompletionRequest(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;

            string modelName = "";
            if (root.TryGetProperty("model", out var modelElement))
            {
                modelName = modelElement.GetString() ?? "";
            }
            else if (root.TryGetProperty("name", out var nameElement))
            {
                modelName = nameElement.GetString() ?? "";
            }

            // The request's reasoning/think effort, rendered verbatim as sent (e.g. "true",
            // "1024", "high"): Ollama-native "think" takes precedence, OpenAI-compatible
            // "reasoning_effort" is the fallback; null/object/array/whitespace values are
            // treated as absent (null).
            string? reasoningEffort = null;
            if (root.TryGetProperty("think", out var think))
            {
                reasoningEffort = RenderReasoningValue(think);
            }
            if (reasoningEffort is null && root.TryGetProperty("reasoning_effort", out var reasoningEffortElement))
            {
                reasoningEffort = RenderReasoningValue(reasoningEffortElement);
            }

            int priorContextTokenCount = 0;
            if (root.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Array)
            {
                priorContextTokenCount = context.GetArrayLength();
            }

            var parts = new List<string>();

            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.TryGetProperty("content", out var content))
                    {
                        AppendContent(content, parts);
                    }

                    if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var toolCall in toolCalls.EnumerateArray())
                        {
                            parts.Add(toolCall.GetRawText());
                        }
                    }

                    // OpenCode-style clients echo prior reasoning (thinking) content back on assistant
                    // messages under a "reasoning" key, with the "thinking" and "reasoning_content"
                    // variants handled just below. Ollama renders it into the prompt, so it must be
                    // counted or the prompt size is severely underestimated for agent sessions.
                    if (message.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind != JsonValueKind.Null)
                    {
                        AppendContent(reasoning, parts);
                    }

                    // Ollama's native /api/chat protocol (and OllamaSharp.Models.Chat.Message.Thinking)
                    // echoes prior assistant reasoning under "thinking"; some OpenAI-compatible reasoning
                    // clients use "reasoning_content". Same underestimation rationale as "reasoning".
                    if (message.TryGetProperty("thinking", out var thinking) && thinking.ValueKind != JsonValueKind.Null)
                    {
                        AppendContent(thinking, parts);
                    }

                    if (message.TryGetProperty("reasoning_content", out var reasoningContent) && reasoningContent.ValueKind != JsonValueKind.Null)
                    {
                        AppendContent(reasoningContent, parts);
                    }
                }
            }

            if (root.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String)
            {
                var text = prompt.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add(text);
                }
            }

            if (root.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.String)
            {
                var text = system.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add(text);
                }
            }

            if (root.TryGetProperty("template", out var template) && template.ValueKind == JsonValueKind.String)
            {
                var text = template.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add(text);
                }
            }

            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (var tool in tools.EnumerateArray())
                {
                    parts.Add(tool.GetRawText());
                }
            }

            if (root.TryGetProperty("format", out var formatElement) && formatElement.ValueKind == JsonValueKind.Object)
            {
                parts.Add(formatElement.GetRawText());
            }

            return new OllamaCompletionRequestInfo(modelName, parts, priorContextTokenCount, reasoningEffort);
        }
        catch
        {
            return new OllamaCompletionRequestInfo("", Array.Empty<string>(), 0);
        }
    }

    /// <summary>
    /// Extracts the full textual context of a request (all messages, system prompt, template...),
    /// so that token estimation is not limited to the last prompt/message only.
    /// </summary>
    public static string ExtractContextText(string jsonBody)
    {
        var parts = ParseCompletionRequest(jsonBody).ContextParts;
        return parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => string.Join("\n", parts)
        };
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
    /// Appends the textual content of a message field (e.g. "content", "reasoning", "thinking",
    /// or "reasoning_content"), which may
    /// be either a plain string, an array of content parts (OpenAI-compatible multimodal format,
    /// e.g. [{ "type": "text", "text": "..." }, { "type": "image_url", ... }]), or a single object
    /// holding its text under a "text" (OpenAI) or "content" property.
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
        else if (content.ValueKind == JsonValueKind.Object)
        {
            // A single object holding its text under a "content" or "text" property.
            var text = (content.TryGetProperty("content", out var objContent) && objContent.ValueKind == JsonValueKind.String)
                ? objContent.GetString()
                : (content.TryGetProperty("text", out var objText) && objText.ValueKind == JsonValueKind.String
                    ? objText.GetString()
                    : null);

            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(text);
            }
        }
    }

    /// <summary>
    /// Renders a reasoning-effort JSON value verbatim as sent: boolean → "true"/"false",
    /// string → the verbatim value (whitespace-only ⇒ absent), number → its raw text
    /// (defensive, e.g. a numeric think budget). JSON null, objects, arrays and any other
    /// kinds count as absent (null).
    /// </summary>
    private static string? RenderReasoningValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.String:
                var text = value.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            case JsonValueKind.Number:
                return value.GetRawText();
            default:
                return null;
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

public readonly record struct OllamaCompletionRequestInfo(
    string ModelName,
    IReadOnlyList<string> ContextParts,
    int PriorContextTokenCount,
    string? ReasoningEffort = null);
