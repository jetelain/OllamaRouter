using System.Text.Json;
using System.Text.Json.Nodes;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Transforms;

/// <summary>
/// Stateless request payload transformer for dynamic context sizing: injects or overwrites
/// <c>options.num_ctx</c> in the outgoing inference payload with the selected bucket from a
/// <see cref="ContextSizingDecision"/>. Pure and tolerant - an unparseable body is NEVER
/// broken (returned unchanged), no property other than <c>options.num_ctx</c> is touched, and
/// the transform is a pure function of (options, decision, request facts) so it is
/// stateless-friendly for the later Auto-Shrink Manager.
/// </summary>
public static class DynamicContextPayloadTransformer
{
    /// <summary>
    /// Applies the sizing decision to the request body:
    /// <list type="number">
    /// <item>If <c>dynamic.Enabled</c> is false (or the decision carries no positive
    /// <c>InjectedNumCtx</c>) the body is returned unchanged with a null injected value.</item>
    /// <item>The body is parsed as JSON (same tolerance as
    /// <c>OllamaRequestParser.ReplaceModelName</c>); parse failure returns the original body
    /// unchanged.</item>
    /// <item><c>options.num_ctx</c> is set (creating the <c>options</c> object when absent);
    /// every other property - including OpenAI-style <c>max_tokens</c> fields - is left
    /// untouched. Ollama's OpenAI-compatible layer reads <c>options.num_ctx</c>, which is why
    /// the <c>/v1/*</c> endpoint shapes are handled identically.</item>
    /// </list>
    /// </summary>
    /// <returns>The (possibly rewritten) JSON body and the injected <c>num_ctx</c> value
    /// (null when no rewrite happened).</returns>
    public static (string NewBody, int? InjectedNumCtx) Apply(
        string body,
        ContextSizingDecision decision,
        DynamicContextOptions dynamic)
    {
        if (!dynamic.Enabled || decision.InjectedNumCtx <= 0)
        {
            return (body, null);
        }

        try
        {
            if (JsonNode.Parse(body) is not JsonObject root)
            {
                return (body, null);
            }

            if (root["options"] is JsonObject options)
            {
                options["num_ctx"] = decision.InjectedNumCtx;
            }
            else
            {
                root["options"] = new JsonObject
                {
                    ["num_ctx"] = decision.InjectedNumCtx
                };
            }

            return (root.ToJsonString(), decision.InjectedNumCtx);
        }
        catch (JsonException)
        {
            // Never break unparseable payloads: forward them untouched.
            return (body, null);
        }
    }

    /// <summary>
    /// Tolerant reader of the client's static <c>options.num_ctx</c> for the four inference
    /// endpoint shapes (<c>/api/chat</c>, <c>/api/generate</c>, <c>/v1/chat/completions</c>,
    /// <c>/v1/completions</c>). Returns null when the body is unparseable, the
    /// <c>options</c>/<c>num_ctx</c> path is absent, the value is not a positive JSON number,
    /// or the root is not an object.
    /// </summary>
    public static int? ExtractClientNumCtx(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("options", out var options) &&
                options.ValueKind == JsonValueKind.Object &&
                options.TryGetProperty("num_ctx", out var numCtx) &&
                numCtx.ValueKind == JsonValueKind.Number &&
                numCtx.TryGetInt32(out var value) &&
                value > 0)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Not JSON (or truncated): no client num_ctx.
        }

        return null;
    }
}
