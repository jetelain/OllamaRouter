using OllamaRouter.Endpoints.RouterApi.Models;
using Xunit;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// The pure naming/validation core of the model pull (task #125): <c>name[:tag]</c> model
/// reference parsing (plain / tagged / namespaced / invalid) and the context-alias
/// computation (the <c>k</c> suffix for whole-k contexts, the raw suffix otherwise, the
/// default <c>latest</c> tag, the collision guard).
/// </summary>
public class ModelPullNamingTests
{
    // ------------------------------------------------------------------ parsing

    [Theory]
    [InlineData("llama3.2", "llama3.2", null)]
    [InlineData("llama3.2:13b-q4_K_M", "llama3.2", "13b-q4_K_M")]
    [InlineData("qwen3", "qwen3", null)]
    [InlineData("org/model:tag", "org/model", "tag")]
    [InlineData("llama3:latest", "llama3", "latest")]
    [InlineData("meta-llama/llama-3.1-8b-instruct", "meta-llama/llama-3.1-8b-instruct", null)]
    [InlineData("  llama3.2  ", "llama3.2", null)] // surrounding whitespace is tolerated
    public void TryParseModelReference_Valid_ReturnsParsedNameAndTag(string reference, string expectedName, string? expectedTag)
    {
        var ok = ModelPullNaming.TryParseModelReference(reference, out var name, out var tag);

        Assert.True(ok);
        Assert.Equal(expectedName, name);
        Assert.Equal(expectedTag, tag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a:b:c")]            // two colons
    [InlineData("llama3:")]          // trailing ':' with no tag
    [InlineData(":tag")]             // empty name
    [InlineData("llama 3")]          // space in the name
    [InlineData("-llama3")]          // leading dash
    [InlineData("_llama3")]          // leading underscore
    [InlineData("llama 3:tag")]      // space in the name, valid tag
    [InlineData("llama3:tag name")]  // space in the tag
    [InlineData("llama3:-tag")]      // tag starting with '-'
    [InlineData("llama3:t:ag")]      // colon in the tag
    [InlineData("a/b/c/d")]          // more than two path segments
    public void TryParseModelReference_Invalid_ReturnsFalse(string? reference)
    {
        var ok = ModelPullNaming.TryParseModelReference(reference, out var name, out var tag);

        Assert.False(ok);
        Assert.Equal(string.Empty, name);
        Assert.Null(tag);
    }

    // ------------------------------------------------------------------ alias computation

    [Theory]
    [InlineData("llama3.2:13b-q4_K_M", 8192, "llama3.2:13b-q4_K_M-8k")]
    [InlineData("qwen3", 4096, "qwen3:latest-4k")]
    [InlineData("llama3", 131072, "llama3:latest-128k")]
    [InlineData("llama3", 1024, "llama3:latest-1k")]
    [InlineData("llama3", 20000, "llama3:latest-20000")]   // non-k value → raw suffix
    [InlineData("org/model:tag", 16384, "org/model:tag-16k")]
    public void TryComputeAliasName_Valid_ComputesTheAlias(string modelReference, int context, string expectedAlias)
    {
        var ok = ModelPullNaming.TryComputeAliasName(modelReference, context, out var alias);

        Assert.True(ok);
        Assert.Equal(expectedAlias, alias);
    }

    [Theory]
    [InlineData("llama3", 0)]
    [InlineData("llama3", -1)]
    [InlineData("a:b:c", 4096)] // invalid reference
    [InlineData("", 4096)]
    public void TryComputeAliasName_InvalidInput_ReturnsFalse(string modelReference, int context)
    {
        var ok = ModelPullNaming.TryComputeAliasName(modelReference, context, out var alias);

        Assert.False(ok);
        Assert.Equal(string.Empty, alias);
    }

    [Fact]
    public void TryComputeAliasName_ReferenceAlreadyEndingWithTheSuffix_StillYieldsADistinctAlias()
    {
        // The alias ALWAYS appends '-{suffix}' to the effective tag, so it stays distinct from
        // the reference even when the reference's tag already ends with the same suffix
        // (the collision guard is defensive and cannot misfire on the '{name}:{tag}-{suf}' shape).
        var ok = ModelPullNaming.TryComputeAliasName("llama3:latest-8k", 8192, out var alias);

        Assert.True(ok);
        Assert.Equal("llama3:latest-8k-8k", alias);
    }
}
