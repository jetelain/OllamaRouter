using OllamaRouter.Services.Catalog;

namespace OllamaRouter.Tests.Services.Catalog;

public class ModelFamilyHeuristicsTests
{
    // --- Spec-mandated rows ---------------------------------------------------------------

    [Fact]
    public void TryMatch_Gpt4o_ReturnsGptFamily_128kContext_NoParams()
    {
        var match = ModelFamilyHeuristics.TryMatch("gpt-4o");

        Assert.NotNull(match);
        Assert.Equal("gpt", match!.Family);
        Assert.Null(match.ParameterCount);
        Assert.Null(match.ExpertCount);
        Assert.Equal(128_000, match.ContextLength);
    }

    [Fact]
    public void TryMatch_Claude35Sonnet_BothNotations_ReturnsClaude_200k()
    {
        var hyphen = ModelFamilyHeuristics.TryMatch("claude-3-5-sonnet");
        var dot = ModelFamilyHeuristics.TryMatch("claude-3.5-sonnet");

        Assert.NotNull(hyphen);
        Assert.NotNull(dot);
        Assert.Equal("claude", hyphen!.Family);
        Assert.Null(hyphen.ParameterCount);
        Assert.Equal(200_000, hyphen.ContextLength);
        Assert.Equal("claude", dot!.Family);
        Assert.Equal(200_000, dot.ContextLength);
    }

    [Fact]
    public void TryMatch_Llama3370b_ReturnsLlama_70B_Dense_131072()
    {
        var match = ModelFamilyHeuristics.TryMatch("llama-3.3-70b");

        Assert.NotNull(match);
        Assert.Equal("llama", match!.Family);
        Assert.Equal(70_000_000_000L, match.ParameterCount);
        Assert.Null(match.ExpertCount);
        Assert.Equal(131_072, match.ContextLength);
    }

    [Fact]
    public void TryMatch_DeepSeekV3_ReturnsMoE_671B_256Experts()
    {
        var match = ModelFamilyHeuristics.TryMatch("deepseek-v3");

        Assert.NotNull(match);
        Assert.Equal("deepseek", match!.Family);
        Assert.Equal(671_000_000_000L, match.ParameterCount);
        Assert.Equal(256L, match.ExpertCount);
        Assert.Equal(131_072, match.ContextLength);
    }

    // --- Normalization --------------------------------------------------------------------

    [Fact]
    public void TryMatch_StripsModelTag()
    {
        var tagged = ModelFamilyHeuristics.TryMatch("llama-3.3-70b:instruct");

        Assert.NotNull(tagged);
        Assert.Equal(ModelFamilyHeuristics.TryMatch("llama-3.3-70b"), tagged);
    }

    [Fact]
    public void TryMatch_StripsLeadingNamespace()
    {
        var namespaced = ModelFamilyHeuristics.TryMatch("openai/gpt-4o");

        Assert.NotNull(namespaced);
        Assert.Equal("gpt", namespaced!.Family);
        Assert.Equal(128_000, namespaced.ContextLength);
    }

    [Theory]
    [InlineData("GPT-4O", "gpt")]
    [InlineData("Llama-3.1-8B", "llama")]
    [InlineData("  deepseek-r1  ", "deepseek")]
    [InlineData("MIXTRAL-8X7B:INSTRUCT-2512", "mixtral")]
    [InlineData("Vendor/CLAUDE-3-OPUS", "claude")]
    public void TryMatch_IsCaseInsensitiveAndTrims(string name, string expectedFamily)
    {
        var match = ModelFamilyHeuristics.TryMatch(name);

        Assert.NotNull(match);
        Assert.Equal(expectedFamily, match!.Family);
    }

    [Fact]
    public void TryMatch_LongestPrefixWins_Llama33OverLlama3()
    {
        // "llama-3.3-70b" must resolve to the 131 072 row, never to a shorter "llama-3*" row.
        var match = ModelFamilyHeuristics.TryMatch("llama-3.3-70b");

        Assert.Equal(131_072, match!.ContextLength);
        Assert.Equal(70_000_000_000L, match.ParameterCount);
    }

    [Fact]
    public void TryMatch_Gpt41_DoesNotCollideWithGpt4oRow()
    {
        var match = ModelFamilyHeuristics.TryMatch("gpt-4.1");

        Assert.NotNull(match);
        Assert.Equal("gpt", match!.Family);
        Assert.Equal(128_000, match.ContextLength);
    }

    [Fact]
    public void TryMatch_O3_ReturnsOSeries_200k()
    {
        var match = ModelFamilyHeuristics.TryMatch("o3");

        Assert.NotNull(match);
        Assert.Equal("o-series", match!.Family);
        Assert.Equal(200_000, match.ContextLength);
    }

    [Fact]
    public void TryMatch_Unknown_ReturnsNull()
    {
        Assert.Null(ModelFamilyHeuristics.TryMatch("mystery-llm-9000"));
        Assert.Null(ModelFamilyHeuristics.TryMatch(""));
        Assert.Null(ModelFamilyHeuristics.TryMatch("   "));
        Assert.Null(ModelFamilyHeuristics.TryMatch(null));
        Assert.Null(ModelFamilyHeuristics.TryMatch(":latest"));
        Assert.Null(ModelFamilyHeuristics.TryMatch("vendor/"));
    }

    // --- Human-readable parameter count ----------------------------------------------------

    [Theory]
    [InlineData(70_000_000_000L, "70.0B")]
    [InlineData(46_700_000_000L, "46.7B")]
    [InlineData(405_000_000_000L, "405.0B")]
    [InlineData(8_000_000_000L, "8.0B")]
    [InlineData(138_000_000L, "138.0M")]
    public void ToHumanReadableParameterCount_FormatsInvariant(long count, string expected)
    {
        Assert.Equal(expected, ModelFamilyHeuristics.ToHumanReadableParameterCount(count));
    }

    [Fact]
    public void ToHumanReadableParameterCount_NullOrNonPositive_ReturnsNull()
    {
        Assert.Null(ModelFamilyHeuristics.ToHumanReadableParameterCount(null));
        Assert.Null(ModelFamilyHeuristics.ToHumanReadableParameterCount(0));
        Assert.Null(ModelFamilyHeuristics.ToHumanReadableParameterCount(-5));
    }
}
