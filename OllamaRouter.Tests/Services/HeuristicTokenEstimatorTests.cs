using System.Collections.Generic;
using Moq;
using OllamaRouter.Services;
using Xunit;

namespace OllamaRouter.Tests.Services;

public class HeuristicTokenEstimatorTests
{
    private readonly HeuristicTokenEstimator _sut = new();

    [Fact]
    public void MockITokenEstimator_WithExtensionMethod_FallsBackToBaseMethod()
    {
        var mock = new Mock<ITokenEstimator>();
        mock.Setup(m => m.EstimateTokens(It.IsAny<IReadOnlyList<string>>())).Returns(42);
        Assert.Equal(42, mock.Object.EstimateTokens(["hello"], "qwen"));
    }

    [Fact]
    public void EstimateTokens_NullOrEmptyList_ReturnsZero()
    {
        Assert.Equal(0, _sut.EstimateTokens((IReadOnlyList<string>)null!));
        Assert.Equal(0, _sut.EstimateTokens(new List<string>()));
    }

    [Fact]
    public void EstimateTokens_ListWithEmptyOrNullStrings_ReturnsZero()
    {
        Assert.Equal(0, _sut.EstimateTokens(new[] { "", "   ", "" }));
    }

    [Fact]
    public void EstimateTokens_SingleWord_ReturnsAtLeastOne()
    {
        var count = _sut.EstimateTokens(["Hello"]);
        Assert.True(count >= 1);
    }

    [Fact]
    public void EstimateTokens_MultipleSegments_SumsTokensAcrossSegments()
    {
        var part1 = "Hello, how are you?";
        var part2 = "I am an AI assistant.";

        var individual1 = _sut.EstimateTokens([part1]);
        var individual2 = _sut.EstimateTokens([part2]);
        var combined = _sut.EstimateTokens([part1, part2]);

        Assert.Equal(individual1 + individual2, combined);
    }

    [Fact]
    public void EstimateTokens_ExtensionMethod_WorksWithSingleString()
    {
        var count = _sut.EstimateTokens("This is a single prompt string.");
        Assert.True(count >= 5);
    }

    [Fact]
    public void EstimateTokens_CodeAndJson_EstimatesPunctuationAndSymbols()
    {
        var code = "function add(a, b) { return a + b; }";
        var count = _sut.EstimateTokens([code]);

        // Code has multiple symbols and keywords, should estimate appropriately
        Assert.True(count >= 8);
    }

    [Fact]
    public void EstimateTokens_NonAsciiCharacters_CountsUnicode()
    {
        var french = "L'intelligence artificielle générative révolutionne le monde.";
        var count = _sut.EstimateTokens([french]);

        Assert.True(count >= 8);
    }

    [Theory]
    [InlineData("qwen2.5:7b")]
    [InlineData("qwen3.5:32b")]
    [InlineData("Qwen3.8-27B:latest")]
    [InlineData("qwen")]
    [InlineData("my-qwen-finetune")]
    public void ResolveEstimator_QwenModels_ReturnsQwenPreset(string modelName)
    {
        var estimator = HeuristicTokenEstimator.ResolveEstimator(modelName);
        Assert.Same(HeuristicTokenEstimator.Qwen, estimator);
        Assert.Equal(1.36, estimator.WordFactor);
        Assert.Equal(0.66, estimator.SymbolFactor);
        Assert.Equal(0.40, estimator.NonAsciiFactor);
        Assert.Equal(3.8, estimator.CharFloorFactor);
    }

    [Theory]
    [InlineData("gemma4:12b")]
    [InlineData("gemma-4-e2b")]
    [InlineData("gemma:7b")]
    [InlineData("gemma2:9b")]
    [InlineData("Gemma4")]
    public void ResolveEstimator_GemmaModels_ReturnsGemmaPreset(string modelName)
    {
        var estimator = HeuristicTokenEstimator.ResolveEstimator(modelName);
        Assert.Same(HeuristicTokenEstimator.Gemma, estimator);
        Assert.Equal(1.43, estimator.WordFactor);
        Assert.Equal(0.72, estimator.SymbolFactor);
        Assert.Equal(0.37, estimator.NonAsciiFactor);
        Assert.Equal(3.6, estimator.CharFloorFactor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("llama3:8b")]
    [InlineData("mistral:7b")]
    [InlineData("phi-4")]
    public void ResolveEstimator_DefaultModels_ReturnsDefaultPreset(string? modelName)
    {
        var estimator = HeuristicTokenEstimator.ResolveEstimator(modelName);
        Assert.Same(HeuristicTokenEstimator.Default, estimator);
        Assert.Equal(1.25, estimator.WordFactor);
        Assert.Equal(0.8, estimator.SymbolFactor);
        Assert.Equal(1.0, estimator.NonAsciiFactor);
        Assert.Equal(3.8, estimator.CharFloorFactor);
    }

    [Fact]
    public void EstimateTokens_WithModelName_AppliesTunedFactors()
    {
        // 50 non-ASCII unicode characters
        var unicodeText = new string('é', 50);

        // Default factor for non-ASCII is 1.0, Qwen is 0.40
        var defaultCount = _sut.EstimateTokens([unicodeText], "llama3");
        var qwenCount = _sut.EstimateTokens([unicodeText], "qwen2.5:7b");
        var gemmaCount = _sut.EstimateTokens([unicodeText], "gemma4:12b");

        // Char floor for Default: ceil(50/3.8) = 14; token estimate: 50*1.0 = 50 => max(50, 14) = 50
        // Char floor for Qwen: ceil(50/3.8) = 14; token estimate: 50*0.4 = 20 => max(20, 14) = 20
        // Char floor for Gemma: ceil(50/3.6) = 14; token estimate: 50*0.37 = 19 => max(19, 14) = 19
        Assert.True(qwenCount < defaultCount, $"Qwen ({qwenCount}) should estimate fewer tokens than Default ({defaultCount}) for dense Unicode text");
        Assert.True(gemmaCount < defaultCount, $"Gemma ({gemmaCount}) should estimate fewer tokens than Default ({defaultCount}) for dense Unicode text");
    }

    [Fact]
    public void EstimateTextTokens_StaticMethod_SupportsModelName()
    {
        var text = "Hello world! This is a test.";
        var defaultTokens = HeuristicTokenEstimator.EstimateTextTokens(text.AsSpan());
        var qwenTokens = HeuristicTokenEstimator.EstimateTextTokens(text.AsSpan(), "qwen3.5:32b");

        Assert.True(defaultTokens > 0);
        Assert.True(qwenTokens > 0);
    }

    [Fact]
    public void Analyze_ExtractsMetricsCorrectly()
    {
        var metrics = HeuristicTokenEstimator.Analyze("Hello, world! 123");
        Assert.Equal(3, metrics.Words);
        Assert.Equal(2, metrics.Symbols);
        Assert.Equal(0, metrics.NonAscii);
        Assert.Equal(17, metrics.CharCount);
    }
}
