using System.Text.Json.Nodes;
using OllamaRouter.Options;

namespace OllamaRouter.Services.Catalog;

/// <summary>
/// Builds a valid, deterministic Ollama <c>POST /api/show</c> payload for a model hosted on a
/// non-Ollama target (task #62): the response is synthesized from the target configuration
/// and the <see cref="ModelMetadata"/> produced by
/// <see cref="ModelCapabilityEngine.SynthesizeNonOllamaMetadata"/> — zero network I/O.
/// <para>
/// The <c>parameters</c> block carries <c>num_ctx &lt;CMax&gt;</c> so
/// <see cref="ModelMetadataParser.ParseNumCtx"/> extracts it — synthesized payloads therefore
/// participate in the fleet-wide best-host scoring (largest declared num_ctx wins) exactly
/// like fetched payloads. <c>parameter_size</c> / <c>quantization_level</c> /
/// <c>general.parameter_count</c> are OMITTED when unknown (absent keys degrade gracefully in
/// <see cref="ModelMetadataParser"/> and keep strict IDE clients happy). CMax =
/// <see cref="ModelCapabilityEngine.ResolveContextCeiling"/> (roster ContextLength → family
/// heuristic → 131 072 fallback). Continue/Zed <c>ollama show</c>-style consumers get a
/// parseable body and never see a 502 for a non-Ollama model.
/// </para>
/// </summary>
public static class SynthesizedShowBuilder
{
    // Llama-3 style default chat template: a representative, well-formed template that
    // satisfies strict clients without claiming fidelity to any specific remote model.
    private const string DefaultChatTemplate =
        "{{ bos_token }}{% for message in messages %}" +
        "{{ '%s' | format(message['role'] | capitalize) }}: {{ message['content'] }}\n" +
        "{% endfor %}{% if add_generation_prompt %}{{ '%s' | format('Assistant' | capitalize) }}: {% endif %}";

    /// <summary>
    /// Builds the synthesized <c>/api/show</c> payload for <paramref name="modelName"/> on
    /// <paramref name="target"/>. Pure (no I/O, no timestamps): two calls with the same
    /// inputs produce deep-equal payloads.
    /// </summary>
    public static JsonObject Build(string modelName, OllamaTargetOptions target, ModelMetadata metadata)
    {
        var cMax = ModelCapabilityEngine.ResolveContextCeiling(metadata);
        var cMaxLong = (long)cMax;
        var family = metadata.ModelFamily is { Length: > 0 }
            ? metadata.ModelFamily
            : !string.IsNullOrWhiteSpace(target?.Provider)
                ? target.Provider.Trim().ToLowerInvariant()
                : "cloud";
        long? parameterCount = null;
        if (metadata.ParameterCount is { } count and > 0)
        {
            parameterCount = count;
        }

        var details = new JsonObject
        {
            ["parent_model"] = string.Empty,
            ["format"] = "server",
            ["family"] = family,
            ["families"] = new JsonArray(family),
            ["context_length"] = cMaxLong
        };
        if (parameterCount is not null)
        {
            details["parameter_size"] = ModelFamilyHeuristics.ToHumanReadableParameterCount(parameterCount);
        }
        if (!string.IsNullOrWhiteSpace(metadata.QuantizationLevel))
        {
            details["quantization_level"] = metadata.QuantizationLevel;
        }

        var modelInfo = new JsonObject
        {
            ["general.architecture"] = family,
            ["general.context_length"] = cMaxLong
        };
        if (parameterCount is not null)
        {
            modelInfo["general.parameter_count"] = parameterCount.Value;
        }

        return new JsonObject
        {
            ["model"] = modelName,
            ["modelfile"] = $"# Modelfile\n# Synthetic: hosted on non-Ollama target '{target?.Name}'\nPARAMETER num_ctx {cMax}\n",
            ["parameters"] = $"num_ctx {cMax}\n",
            ["template"] = DefaultChatTemplate,
            ["details"] = details,
            ["model_info"] = modelInfo
        };
    }
}
