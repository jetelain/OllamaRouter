using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OllamaRouter.Services;

/// <summary>
/// Parsed, immutable capability data for one (target, model) pair, aggregated from the
/// target's <c>GET /api/tags</c> entry and the <c>POST /api/show</c> response.
/// When a network/parse failure occurs, the fields degrade to "unknown" values
/// (<see cref="ParameterCount"/>, <see cref="ModelfileNumCtx"/>, <see cref="ModelFamily"/>
/// null; <see cref="HasModelInfo"/> false) so classification falls back to the permissive
/// (MoE) path instead of blocking routing.
/// </summary>
public sealed record ModelMetadata(
    string ModelName,
    long WeightSizeBytes,
    long? ParameterCount,
    long ExpertCount,
    string? ModelFamily,
    int? ModelfileNumCtx,
    bool HasModelInfo,
    string? ParametersRaw,
    long? NativeContextLength);

/// <summary>
/// Pure, stateless parsers that turn raw Ollama <c>/api/tags</c> and <c>/api/show</c> JSON
/// payloads into <see cref="ModelMetadata"/>. All methods tolerate absent fields and never
/// throw on malformed input.
/// </summary>
public static class ModelMetadataParser
{
    // "num_ctx 49152" inside the Modelfile parameters block (multi-line string).
    private static readonly Regex ParametersNumCtxRegex = new(
        @"^\s*num_ctx\s+(\d+)\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "PARAMETER num_ctx 49152" inside the full Modelfile text.
    private static readonly Regex ModelfileNumCtxRegex = new(
        @"^PARAMETER\s+num_ctx\s+(\d+)\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Human-readable parameter size, e.g. "27.8B", "4B", "138M".
    private static readonly Regex ParameterSizeRegex = new(
        @"^(\d+(?:\.\d+)?)\s*([KM]?)B?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts the explicit <c>num_ctx</c> value from a Modelfile <c>parameters</c> block
    /// (a multi-line string such as "num_ctx 49152\nnum_predict 4096"). Returns null when the
    /// block is missing, contains no <c>num_ctx</c> line, or the value is not a positive int.
    /// </summary>
    public static int? ParseNumCtxFromParameters(string? parameters)
    {
        if (string.IsNullOrEmpty(parameters))
        {
            return null;
        }

        var match = ParametersNumCtxRegex.Match(parameters);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, out var value) && value > 0 ? value : null;
    }

    /// <summary>
    /// Extracts the explicit <c>num_ctx</c> value from a full Modelfile text via its
    /// "PARAMETER num_ctx ..." line. Returns null when absent or malformed.
    /// </summary>
    public static int? ParseNumCtxFromModelfile(string? modelfile)
    {
        if (string.IsNullOrEmpty(modelfile))
        {
            return null;
        }

        var match = ModelfileNumCtxRegex.Match(modelfile);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, out var value) && value > 0 ? value : null;
    }

    /// <summary>
    /// Extracts <c>num_ctx</c> from a parsed <c>/api/show</c> response: the <c>parameters</c>
    /// block first, falling back to the <c>modelfile</c> text when the <c>parameters</c> block
    /// is missing. Returns null when no explicit value is declared.
    /// </summary>
    public static int? ParseNumCtx(JsonObject? show)
    {
        if (show is null)
        {
            return null;
        }

        var hasParametersBlock = TryGetString(show["parameters"], out var parameters);
        if (hasParametersBlock)
        {
            return ParseNumCtxFromParameters(parameters);
        }

        TryGetString(show["modelfile"], out var modelfile);
        return ParseNumCtxFromModelfile(modelfile);
    }

    /// <summary>
    /// Parses a human-readable parameter size (e.g. "27.8B", "4B", "138M") into an absolute
    /// parameter count (K = 10³, M = 10⁶, B or no suffix = 10⁹). Returns null on no match.
    /// </summary>
    public static long? ParseParameterSize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var match = ParameterSizeRegex.Match(raw.Trim());
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var factor = match.Groups[2].Value switch
        {
            "K" => 1_000.0,
            "M" => 1_000_000.0,
            _ => 1_000_000_000.0
        };

        return (long)(value * factor);
    }

    /// <summary>
    /// Extracts the exact parameter count from <c>model_info["general.parameter_count"]</c>
    /// (accepting both JSON number and string forms), falling back to the regex parse of
    /// <c>details.parameter_size</c>. Returns null when neither signal is available.
    /// </summary>
    public static long? ExtractParameterCount(JsonObject? modelInfo, string? parameterSize)
    {
        if (modelInfo?["general.parameter_count"] is { } node)
        {
            if (TryGetNodeLong(node, out var parameterCount))
            {
                return parameterCount;
            }
            else if (node is JsonValue jsonValue &&
                     jsonValue.TryGetValue<string>(out var text) &&
                     long.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        return ParseParameterSize(parameterSize);
    }

    /// <summary>
    /// Robustly reads a non-negative integer from a <c>JsonValueKind.Number</c> node, returning
    /// true and the value on success. Unlike <c>JsonNode.GetValue&lt;long&gt;()</c> — which THROWS
    /// when the node holds a narrower CLR type (e.g. a C# <c>int</c> produced by
    /// <c>new JsonObject { ["k"] = 128 }</c>, as test fixtures and some Ollama payload shapes do)
    /// — this helper tolerates any representable width, so MoE <c>expert_count</c> and native
    /// <c>context_length</c> detection never miss a valid value.
    /// </summary>
    internal static bool TryGetNodeLong(JsonNode? node, out long value)
    {
        value = 0;
        if (node is null || node.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        try
        {
            // The node may hold any narrower CLR numeric type (e.g. a C# int), so read the
            // raw token text and parse — GetValue<T>() throws on type-mismatched boxed nodes.
            var raw = node.ToJsonString();
            if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            return value >= 0;
        }
        catch (Exception)
        {
            // Non-representable numeric value (e.g. NaN/Infinity): treat as absent.
            return false;
        }
    }

    /// <summary>
    /// Scans <c>model_info</c> for any key ending in "expert_count" with a numeric value and
    /// returns the maximum found (0 when none). Used to detect Mixture-of-Experts models.
    /// </summary>
    public static long ExtractExpertCount(JsonObject? modelInfo)
    {
        if (modelInfo is null)
        {
            return 0;
        }

        var max = 0L;
        foreach (KeyValuePair<string, JsonNode?> property in modelInfo)
        {
            if (!property.Key.EndsWith("expert_count", StringComparison.Ordinal) ||
                !TryGetNodeLong(property.Value, out var value))
            {
                continue;
            }

            if (value > max)
            {
                max = value;
            }
        }

        return max;
    }

    /// <summary>
    /// Scans <c>model_info</c> for any key ending in "context_length" with a numeric value and
    /// returns the maximum positive value found (the model's native GGUF context limit), or
    /// null when no such key is present (absent, non-numeric, zero, or non-representable).
    /// </summary>
    public static long? ExtractNativeContextLength(JsonObject? modelInfo)
    {
        if (modelInfo is null)
        {
            return null;
        }

        long? max = null;
        foreach (KeyValuePair<string, JsonNode?> property in modelInfo)
        {
            if (!property.Key.EndsWith(".context_length", StringComparison.Ordinal) ||
                !TryGetNodeLong(property.Value, out var value))
            {
                continue;
            }

            if (value > 0 && (max is null || value > max.Value))
            {
                max = value;
            }
        }

        return max;
    }

    /// <summary>
    /// Aggregates the raw <c>/api/tags</c> entry (for the model weight size) and the
    /// <c>/api/show</c> response (for <c>num_ctx</c>, parameter count, family, expert count)
    /// into a single <see cref="ModelMetadata"/>. A null <paramref name="show"/> produces the
    /// degraded ("unknown") metadata used when the target is unreachable.
    /// </summary>
    public static ModelMetadata ParseModelMetadata(string modelName, JsonArray? tags, JsonObject? show)
    {
        var modelInfo = show?["model_info"] as JsonObject;
        var parameterSize = TryGetString(show?["details"]?["parameter_size"], out var sizeText) ? sizeText : null;

        return new ModelMetadata(
            ModelName: modelName,
            WeightSizeBytes: ResolveWeightSize(modelName, tags, show),
            ParameterCount: ExtractParameterCount(modelInfo, parameterSize),
            ExpertCount: ExtractExpertCount(modelInfo),
            ModelFamily: TryGetString(show?["details"]?["family"], out var family) ? family!.ToLowerInvariant() : null,
            ModelfileNumCtx: ParseNumCtx(show),
            HasModelInfo: modelInfo is not null,
            ParametersRaw: TryGetString(show?["parameters"], out var parameters) ? parameters : null,
            NativeContextLength: ExtractNativeContextLength(modelInfo));
    }

    private static long ResolveWeightSize(string modelName, JsonArray? tags, JsonObject? show)
    {
        // Prefer the matched entry's "size" from the target's /api/tags (full name, case-insensitive).
        if (tags is not null && !string.IsNullOrEmpty(modelName))
        {
            foreach (var tag in tags)
            {
                if (tag?["name"]?.ToString() is not { } tagName ||
                    !string.Equals(tagName, modelName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (tag["size"] is { } sizeNode && sizeNode.GetValueKind() == JsonValueKind.Number)
                {
                    try
                    {
                        return sizeNode.GetValue<long>();
                    }
                    catch (Exception)
                    {
                        // Malformed size: fall through to the /api/show fallback.
                    }
                }
            }
        }

        // Fallback: model.size from /api/show.
        if (show?["model"]?["size"] is { } showSize && showSize.GetValueKind() == JsonValueKind.Number)
        {
            try
            {
                return showSize.GetValue<long>();
            }
            catch (Exception)
            {
                // Malformed size.
            }
        }

        return 0;
    }

    private static bool TryGetString(JsonNode? node, out string? value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = null;
        return false;
    }
}
