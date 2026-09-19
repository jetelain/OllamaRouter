using System.Collections.Generic;
using OllamaRouter.Services;
using Xunit;

namespace OllamaRouter.Tests.Services;

public class HeuristicTokenEstimatorTests
{
    private readonly HeuristicTokenEstimator _sut = new();

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
}

