using System.Globalization;
using System.Text.Json.Nodes;
using OllamaRouter.Options;

namespace OllamaRouter.Services.Catalog;

/// <summary>
/// One canonical, type-erased model catalog entry shared by the Ollama <c>/api/tags</c> and
/// the OpenAI <c>/v1/models</c> synthesis paths of the fleet aggregation (task #62). A single
/// instance renders into EITHER wire shape via <see cref="ToOllamaTagNode"/> (the exact Ollama
/// <c>/api/tags</c> entry — IDE clients are strict about that shape) or
/// <see cref="ToOpenAiModelNode"/> (the exact OpenAI <c>/v1/models</c> entry).
/// <para>
/// Conventions (consistent with the existing Cloud syntheses): <see cref="Size"/> is 0 —
/// remote APIs expose no weight bytes — and unknown details degrade to the literal
/// <c>"cloud"</c> values. Entries produced by <see cref="FromOpenAiModel"/> and
/// <see cref="FromRosterEntry"/> apply the target's declared roster hints
/// (<see cref="TargetModelConfig.Quantization"/>) when the model name matches a roster entry
/// (decision D2: roster supplies informational hints only).
/// </para>
/// </summary>
public sealed record OllamaModelTagItem(
    string Name,
    DateTimeOffset ModifiedAt,
    long Size,
    string Family,
    string? ParameterSize,
    string? QuantizationLevel,
    string OwnedBy = "")
{
    /// <summary>
    /// Builds the canonical item from one entry of a live OpenAI <c>GET /models</c>
    /// <c>data</c> array: <c>Name</c> = the <c>id</c> property, <see cref="ModifiedAt"/> from
    /// the <c>created</c> unix-seconds property (absent or non-positive ⇒
    /// <see cref="DateTimeOffset.UtcNow"/>), <see cref="Size"/> = 0. Family / parameter size
    /// come from the family heuristics, falling back to the target's <c>Provider</c>
    /// (lower-invariant) and the <c>"cloud"</c> convention; the quantization level is the
    /// roster hint for this model name when present, else <c>"cloud"</c>.
    /// </summary>
    public static OllamaModelTagItem FromOpenAiModel(JsonObject modelNode, OllamaTargetOptions target)
    {
        var name = modelNode["id"]?.GetValue<string>() ?? string.Empty;
        long createdSeconds = 0;
        var hasPositiveCreated = false;
        if (modelNode["created"] is { } createdNode &&
            ModelMetadataParser.TryGetNodeLong(createdNode, out createdSeconds))
        {
            hasPositiveCreated = createdSeconds > 0;
        }

        // Absent or non-positive created ⇒ UtcNow (the provider timestamp is purely
        // informational for remote models; a zero timestamp is not a real one).
        var modifiedAt = hasPositiveCreated
            ? DateTimeOffset.FromUnixTimeSeconds(createdSeconds)
            : DateTimeOffset.UtcNow;

        return new OllamaModelTagItem(
            Name: name,
            ModifiedAt: modifiedAt,
            Size: 0,
            Family: ResolveFamily(name, target),
            ParameterSize: ModelFamilyHeuristics.ToHumanReadableParameterCount(
                ModelFamilyHeuristics.TryMatch(name)?.ParameterCount) ?? "cloud",
            QuantizationLevel: ResolveQuantization(name, target) ?? "cloud",
            OwnedBy: ResolveOwnedBy(target));
    }

    /// <summary>
    /// Builds the canonical item from one declared roster entry (<see cref="TargetModelConfig"/>)
    /// of a non-Ollama target: <see cref="Name"/> trimmed, <see cref="ModifiedAt"/> =
    /// <see cref="DateTimeOffset.UtcNow"/>, <see cref="Size"/> = 0. Same unknown-detail
    /// convention as <see cref="FromOpenAiModel"/>; the entry's own
    /// <see cref="TargetModelConfig.Quantization"/> hint wins when non-blank.
    /// </summary>
    public static OllamaModelTagItem FromRosterEntry(TargetModelConfig entry, OllamaTargetOptions target)
    {
        var name = entry.Name?.Trim() ?? string.Empty;
        return new OllamaModelTagItem(
            Name: name,
            ModifiedAt: DateTimeOffset.UtcNow,
            Size: 0,
            Family: ResolveFamily(name, target),
            ParameterSize: ModelFamilyHeuristics.ToHumanReadableParameterCount(
                ModelFamilyHeuristics.TryMatch(name)?.ParameterCount) ?? "cloud",
            QuantizationLevel: !string.IsNullOrWhiteSpace(entry.Quantization)
                ? entry.Quantization!.Trim()
                : ResolveQuantization(name, target) ?? "cloud",
            OwnedBy: ResolveOwnedBy(target));
    }

    /// <summary>
    /// Renders the exact Ollama <c>/api/tags</c> entry shape:
    /// <c>{ name, model, modified_at (ISO-8601 "o"), size, details: { family, parameter_size,
    /// quantization_level } }</c>.
    /// </summary>
    public JsonObject ToOllamaTagNode()
    {
        return new JsonObject
        {
            ["name"] = Name,
            ["model"] = Name,
            ["modified_at"] = ModifiedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["size"] = Size,
            ["details"] = new JsonObject
            {
                ["family"] = Family,
                ["parameter_size"] = ParameterSize ?? "cloud",
                ["quantization_level"] = QuantizationLevel ?? "cloud"
            }
        };
    }

    /// <summary>
    /// Renders the exact OpenAI <c>/v1/models</c> entry shape:
    /// <c>{ id, object: "model", created (unix seconds), owned_by }</c>.
    /// </summary>
    public JsonObject ToOpenAiModelNode()
    {
        return new JsonObject
        {
            ["id"] = Name,
            ["object"] = "model",
            ["created"] = ModifiedAt.ToUnixTimeSeconds(),
            ["owned_by"] = OwnedBy
        };
    }

    /// <summary>
    /// Renders one roster entry as an OpenAI <c>/v1/models</c> entry with <c>created = 0</c> —
    /// the static-roster synthesis convention (declared roster models carry no creation
    /// timestamp).
    /// </summary>
    public static JsonObject RosterOpenAiModelNode(TargetModelConfig entry, OllamaTargetOptions target)
    {
        return new JsonObject
        {
            ["id"] = entry.Name?.Trim() ?? string.Empty,
            ["object"] = "model",
            ["created"] = 0L,
            ["owned_by"] = ResolveOwnedBy(target)
        };
    }

    private static string ResolveFamily(string modelName, OllamaTargetOptions target)
    {
        if (ModelFamilyHeuristics.TryMatch(modelName) is { } family)
        {
            return family.Family;
        }

        return !string.IsNullOrWhiteSpace(target?.Provider)
            ? target.Provider!.Trim().ToLowerInvariant()
            : "cloud";
    }

    private static string? ResolveQuantization(string modelName, OllamaTargetOptions target)
    {
        if (FindRosterEntry(modelName, target) is { } entry && !string.IsNullOrWhiteSpace(entry.Quantization))
        {
            return entry.Quantization.Trim();
        }

        return null;
    }

    private static TargetModelConfig? FindRosterEntry(string modelName, OllamaTargetOptions? target)
    {
        if (string.IsNullOrWhiteSpace(modelName) || target?.Models is null)
        {
            return null;
        }

        return target.Models.FirstOrDefault(m =>
            !string.IsNullOrWhiteSpace(m.Name) &&
            string.Equals(m.Name!.Trim(), modelName, StringComparison.OrdinalIgnoreCase));
    }

    // owned_by of the /v1/models entries: the informational provider label, falling back to
    // the target name (the Cloud syntheses use the literal "cloud" by the same convention).
    private static string ResolveOwnedBy(OllamaTargetOptions? target) =>
        !string.IsNullOrWhiteSpace(target?.Provider)
            ? target!.Provider!.Trim()
            : target?.Name?.Trim() ?? string.Empty;
}
