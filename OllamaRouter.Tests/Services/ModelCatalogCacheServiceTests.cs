using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class ModelCatalogCacheServiceTests
{
    private const string BaseUrl = "http://127.0.0.1:11435";

    private static ModelCatalogCacheService CreateSut(Mock<IOllamaModelCatalogClient> catalogClient, IMemoryCache? memoryCache = null)
    {
        return new ModelCatalogCacheService(catalogClient.Object, memoryCache ?? new MemoryCache(new MemoryCacheOptions()));
    }

    private static JsonArray Tags(params string[] modelNames)
    {
        return new JsonArray(modelNames.Select(name => (JsonNode)new JsonObject { ["name"] = name }).ToArray());
    }

    [Theory]
    [InlineData(null, "llama3")]
    [InlineData("", "llama3")]
    [InlineData(BaseUrl, "")]
    [InlineData(BaseUrl, null)]
    public async Task ModelExistsAsync_MissingBaseUrlOrModelName_ReturnsFalse_WithoutQueryingCatalog(string? baseUrl, string? modelName)
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var sut = CreateSut(catalogClient);

        var result = await sut.ModelExistsAsync(baseUrl, modelName!);

        Assert.False(result);
        catalogClient.Verify(c => c.GetTagsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ModelExistsAsync_ModelPresentInTags_ReturnsTrue()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tags("llama3", "qwen3:8b"));
        var sut = CreateSut(catalogClient);

        var result = await sut.ModelExistsAsync(BaseUrl, "llama3");

        Assert.True(result);
    }

    [Fact]
    public async Task ModelExistsAsync_ModelAbsentFromTags_ReturnsFalse()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tags("llama3"));
        var sut = CreateSut(catalogClient);

        var result = await sut.ModelExistsAsync(BaseUrl, "unknown-model");

        Assert.False(result);
    }

    [Fact]
    public async Task ModelExistsAsync_ModelNameIsCaseInsensitive_ReturnsTrue()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tags("Llama3:Latest"));
        var sut = CreateSut(catalogClient);

        var result = await sut.ModelExistsAsync(BaseUrl, "llama3:latest");

        Assert.True(result);
    }

    [Fact]
    public async Task ModelExistsAsync_CalledTwiceForSameInstance_OnlyFetchesTagsOnce()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tags("llama3"));
        var sut = CreateSut(catalogClient);

        await sut.ModelExistsAsync(BaseUrl, "llama3");
        await sut.ModelExistsAsync(BaseUrl, "llama3");

        catalogClient.Verify(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ModelExistsAsync_DifferentBaseUrls_FetchesTagsSeparately()
    {
        const string remoteUrl = "http://aiserver.local:11434";

        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tags("llama3"));
        catalogClient.Setup(c => c.GetTagsAsync(remoteUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tags("qwen3:8b"));
        var sut = CreateSut(catalogClient);

        var existsLocally = await sut.ModelExistsAsync(BaseUrl, "llama3");
        var existsRemotely = await sut.ModelExistsAsync(remoteUrl, "qwen3:8b");

        Assert.True(existsLocally);
        Assert.True(existsRemotely);
        catalogClient.Verify(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()), Times.Once);
        catalogClient.Verify(c => c.GetTagsAsync(remoteUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ModelExistsAsync_EmptyTags_ReturnsFalse()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray());
        var sut = CreateSut(catalogClient);

        var result = await sut.ModelExistsAsync(BaseUrl, "llama3");

        Assert.False(result);
    }
}
