using System.Collections.Generic;
using Xunit;
using OllamaRouter.Services.ContextSizing;

namespace OllamaRouter.Tests.Services.ContextSizing;

public class TiktokenTokenEstimatorTests
{
    private readonly TiktokenTokenEstimator _sut = new();

    [Fact]
    public void EstimateTokens_NullOrEmpty_ReturnsZero()
    {
        Assert.Equal(0, _sut.EstimateTokens((IReadOnlyList<string>)null!));
        Assert.Equal(0, _sut.EstimateTokens(new List<string>()));
    }

    [Fact]
    public void EstimateTokens_MultipleSegments_SumsTokens()
    {
        var count = _sut.EstimateTokens(["Hello", "world"]);
        Assert.True(count >= 2);
    }

    [Fact]
    public void EstimateTokens_ExtensionMethod_WorksWithSingleString()
    {
        var count = _sut.EstimateTokens("Hello world");
        Assert.Equal(2, count);
    }
}

