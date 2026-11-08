using System.Text.Json.Nodes;
using OllamaRouter.Options;
using OllamaRouter.Services.Catalog;

namespace OllamaRouter.Tests.Services.Catalog;

public class SynthesizedShowBuilderTests
{
    private static ModelMetadata Metadata(
        long? parameterCount = null,
        long expertCount = 0,
        string? family = null,
        bool hasModelInfo = false,
        long? nativeContextLength = null,
        string? quantization = null)
    {
        return new ModelMetadata("test-model", 0, parameterCount, expertCount, family, null, hasModelInfo, null, nativeContextLength, quantization);
    }

    private static OllamaTargetOptions Target(string? provider = null) =>
        new() { Name = "Provider", Provider = provider };

    [Fact]
    public void Build_UnknownModel_UsesFallbackContextAndCloudFamily()
    {
        var payload = SynthesizedShowBuilder.Build("mystery-llm", Target(), Metadata());

        Assert.Equal("mystery-llm", payload["model"]?.GetValue<string>());
        Assert.Equal(131_072, payload["model_info"]!["general.context_length"]!.GetValue<long>());
        Assert.Equal(131_072, payload["details"]!["context_length"]!.GetValue<long>());
        Assert.Equal("cloud", payload["details"]!["family"]?.GetValue<string>());
        Assert.Equal(131_072, ModelMetadataParser.ParseNumCtx(payload));
        // Unknown parameter / quantization keys are OMITTED.
        Assert.Null(payload["details"]!["parameter_size"]);
        Assert.Null(payload["details"]!["quantization_level"]);
        Assert.Null(payload["model_info"]!["general.parameter_count"]);
        // Strict base keys are present.
        Assert.Equal("server", payload["details"]!["format"]?.GetValue<string>());
        Assert.Equal("cloud", payload["model_info"]!["general.architecture"]?.GetValue<string>());
        Assert.Single(payload["details"]!["families"]!.AsArray());
        Assert.NotNull(payload["template"]);
        Assert.NotNull(payload["modelfile"]);
    }

    [Fact]
    public void Build_RosterContextTier_WinsOverHeuristicAndFallback()
    {
        // Roster ContextLength 98304 must beat the family heuristic (200 000) and the 131 072 fallback.
        var payload = SynthesizedShowBuilder.Build("claude-3-5-sonnet", Target(),
            Metadata(family: "claude", hasModelInfo: true, nativeContextLength: 98_304));

        Assert.Equal(98_304, payload["model_info"]!["general.context_length"]!.GetValue<long>());
        Assert.Equal(98_304, ModelMetadataParser.ParseNumCtx(payload));
        Assert.Equal("num_ctx 98304\n", payload["parameters"]?.GetValue<string>());
        Assert.Equal("claude", payload["details"]!["family"]?.GetValue<string>());
    }

    [Fact]
    public void Build_HeuristicTier_UsedWhenNoRosterContext()
    {
        var payload = SynthesizedShowBuilder.Build("gpt-4o", Target(),
            Metadata(family: "gpt", hasModelInfo: true, nativeContextLength: 128_000));

        Assert.Equal(128_000, payload["model_info"]!["general.context_length"]!.GetValue<long>());
        Assert.Equal(128_000, ModelMetadataParser.ParseNumCtx(payload));
    }

    [Fact]
    public void Build_ParametersAndModelfile_RoundTripThroughParseNumCtx()
    {
        var payload = SynthesizedShowBuilder.Build("deepseek-v3", Target(),
            Metadata(parameterCount: 671_000_000_000L, expertCount: 256, family: "deepseek", hasModelInfo: true, nativeContextLength: 131_072));

        // parameters block first...
        Assert.Equal(131_072, ModelMetadataParser.ParseNumCtxFromParameters(payload["parameters"]?.GetValue<string>()));
        // ...and the Modelfile PARAMETER line (the synthetic Modelfile names the hosting target).
        Assert.Equal(131_072, ModelMetadataParser.ParseNumCtxFromModelfile(payload["modelfile"]?.GetValue<string>() ?? string.Empty));
        Assert.Contains("Provider", payload["modelfile"]?.GetValue<string>() ?? string.Empty);
    }

    [Fact]
    public void Build_KnownParameterAndQuantization_Present()
    {
        var payload = SynthesizedShowBuilder.Build("llama-3.3-70b", Target(),
            Metadata(parameterCount: 70_000_000_000L, family: "llama", hasModelInfo: true,
                nativeContextLength: 131_072, quantization: "Q8_0"));

        Assert.Equal("70.0B", payload["details"]!["parameter_size"]?.GetValue<string>());
        Assert.Equal(70_000_000_000L, payload["model_info"]!["general.parameter_count"]!.GetValue<long>());
        Assert.Equal("Q8_0", payload["details"]!["quantization_level"]?.GetValue<string>());
        Assert.Equal("llama", payload["details"]!["families"]!.AsArray()[0]?.GetValue<string>());
    }

    [Fact]
    public void Build_ProviderLabel_BecomesFamily_WhenNoHeuristic()
    {
        var payload = SynthesizedShowBuilder.Build("mystery-1", Target(provider: "Groq"), Metadata());

        Assert.Equal("groq", payload["details"]!["family"]?.GetValue<string>());
        Assert.Equal("groq", payload["model_info"]!["general.architecture"]?.GetValue<string>());
        Assert.Equal("groq", payload["details"]!["families"]!.AsArray()[0]?.GetValue<string>());
    }

    [Fact]
    public void Build_IsDeterministic_TwoBuildsDeepEqual()
    {
        var metadata = Metadata(parameterCount: 70_000_000_000L, family: "llama", hasModelInfo: true,
            nativeContextLength: 131_072, quantization: "none");

        var a = SynthesizedShowBuilder.Build("llama-3.3-70b", Target(provider: "x"), metadata);
        var b = SynthesizedShowBuilder.Build("llama-3.3-70b", Target(provider: "x"), metadata);

        Assert.True(JsonNode.DeepEquals(a, b));
    }
}
