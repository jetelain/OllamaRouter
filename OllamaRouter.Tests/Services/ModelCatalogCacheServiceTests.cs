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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task HasAnyModelsAsync_MissingBaseUrl_ReturnsFalse_WithoutQueryingCatalog(string? baseUrl)
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var sut = CreateSut(catalogClient);

        var result = await sut.HasAnyModelsAsync(baseUrl);

        Assert.False(result);
        catalogClient.Verify(c => c.GetTagsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HasAnyModelsAsync_WhenModelsPresent_ReturnsTrue()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tags("llama3"));
        var sut = CreateSut(catalogClient);

        var result = await sut.HasAnyModelsAsync(BaseUrl);

        Assert.True(result);
    }

    [Fact]
    public async Task HasAnyModelsAsync_WhenNoModels_ReturnsFalse()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray());
        var sut = CreateSut(catalogClient);

        var result = await sut.HasAnyModelsAsync(BaseUrl);

        Assert.False(result);
    }

    [Fact]
    public async Task HasAnyModelsAsync_SharesCacheWith_ModelExistsAsync()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tags("llama3"));
        var sut = CreateSut(catalogClient);

        var hasAny = await sut.HasAnyModelsAsync(BaseUrl);
        var exists = await sut.ModelExistsAsync(BaseUrl, "llama3");

        Assert.True(hasAny);
        Assert.True(exists);
        catalogClient.Verify(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshAsync_BypassesStaleCache_RepopulatesWithNewNames()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient
            .Setup(c => c.GetTagsAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tags("llama3"));
        catalogClient
            .Setup(c => c.GetTagsWithDigestAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tags("llama3", "qwen3:8b"), "digest-v2"));
        var sut = CreateSut(catalogClient);

        // Populate the cache with the stale (single-model) list.
        Assert.True(await sut.ModelExistsAsync(BaseUrl, "llama3"));
        Assert.False(await sut.ModelExistsAsync(BaseUrl, "qwen3:8b")); // Stale cache says no.

        // Force-refresh: the client now returns the expanded list.
        var refreshed = await sut.RefreshAsync(BaseUrl);
        Assert.Equal(2, refreshed.Count);

        // The refreshed names are immediately visible (cache was repopulated).
        Assert.True(await sut.ModelExistsAsync(BaseUrl, "qwen3:8b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RefreshAsync_MissingBaseUrl_ReturnsEmpty_WithoutQueryingCatalog(string? baseUrl)
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var sut = CreateSut(catalogClient);

        var result = await sut.RefreshAsync(baseUrl);

        Assert.Empty(result);
        catalogClient.Verify(c => c.GetTagsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshWithDigestAsync_ReturnsNamesAndDigest_UpdatesCurrentDigest()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.GetTagsWithDigestAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tags("llama3"), "digest-abc"));
        var sut = CreateSut(catalogClient);

        var (names, digest) = await sut.RefreshWithDigestAsync(BaseUrl);

        Assert.Equal(["llama3"], names);
        Assert.Equal("digest-abc", digest);
        Assert.Equal("digest-abc", sut.CurrentDigest(BaseUrl));
        Assert.Equal("digest-abc", await sut.GetCurrentDigestAsync(BaseUrl));
    }

    [Fact]
    public async Task RefreshWithDigestAsync_EmptyBaseUrl_ReturnsEmptyDigest()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var sut = CreateSut(catalogClient);

        var (names, digest) = await sut.RefreshWithDigestAsync("");

        Assert.Empty(names);
        Assert.Equal("", digest);
    }

    [Fact]
    public async Task GetCurrentDigestAsync_UnknownBaseUrl_TriggersRefresh()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.GetTagsWithDigestAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tags("llama3"), "digest-xyz"));
        var sut = CreateSut(catalogClient);

        var digest = await sut.GetCurrentDigestAsync(BaseUrl);

        Assert.Equal("digest-xyz", digest);
        catalogClient.Verify(c => c.GetTagsWithDigestAsync(BaseUrl, It.IsAny<CancellationToken>()), Times.Once);

        // Second call is served from the registry without another fetch.
        var again = await sut.GetCurrentDigestAsync(BaseUrl);
        Assert.Equal("digest-xyz", again);
        catalogClient.Verify(c => c.GetTagsWithDigestAsync(BaseUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCurrentDigestAsync_UnknownBaseUrl_EmptyCatalog_ReturnsEmpty()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.GetTagsWithDigestAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new JsonArray(), ""));
        var sut = CreateSut(catalogClient);

        var digest = await sut.GetCurrentDigestAsync(BaseUrl);

        Assert.Equal("", digest);
    }

    [Fact]
    public async Task DigestChange_RemovesOnlyThatTargetsCapabilityKeys()
    {
        const string targetB = "http://aiserver.local:11434";

        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.SetupSequence(c => c.GetTagsWithDigestAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tags("llama3"), "digest-d1"))
            .ReturnsAsync((Tags("llama3", "qwen3:8b"), "digest-d2"));
        catalogClient.Setup(c => c.GetTagsWithDigestAsync(targetB, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tags("qwen3:8b"), "digest-d1"));

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var sut = CreateSut(catalogClient, memoryCache);

        // Initial refreshes record the digests.
        await sut.RefreshWithDigestAsync(BaseUrl);
        await sut.RefreshWithDigestAsync(targetB);

        // Capability engine records keys under digest d1 on both targets.
        const string keyA = "ModelCapabilityEngine:" + BaseUrl + ":digest-d1:llama3";
        const string keyB = "ModelCapabilityEngine:" + targetB + ":digest-d1:qwen3:8b";
        memoryCache.Set(keyA, "meta-a", TimeSpan.FromSeconds(30));
        memoryCache.Set(keyB, "meta-b", TimeSpan.FromSeconds(30));
        sut.RegisterCapabilityCacheKeys(BaseUrl, [keyA], "digest-d1");
        sut.RegisterCapabilityCacheKeys(targetB, [keyB], "digest-d1");

        Assert.NotNull(memoryCache.Get(keyA));
        Assert.NotNull(memoryCache.Get(keyB));

        // Catalog digest of target A changes ⇒ only A's capability keys are removed.
        await sut.RefreshWithDigestAsync(BaseUrl);

        Assert.Equal("digest-d2", sut.CurrentDigest(BaseUrl));
        Assert.Equal("digest-d1", sut.CurrentDigest(targetB));
        Assert.Null(memoryCache.Get(keyA));
        Assert.NotNull(memoryCache.Get(keyB));
    }

    [Fact]
    public async Task SameDigestRefresh_DoesNotRemoveCapabilityKeys()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.SetupSequence(c => c.GetTagsWithDigestAsync(BaseUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tags("llama3"), "digest-d1"))
            .ReturnsAsync((Tags("llama3"), "digest-d1"));

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var sut = CreateSut(catalogClient, memoryCache);

        await sut.RefreshWithDigestAsync(BaseUrl);

        const string keyA = "ModelCapabilityEngine:" + BaseUrl + ":digest-d1:llama3";
        memoryCache.Set(keyA, "meta-a", TimeSpan.FromSeconds(30));
        sut.RegisterCapabilityCacheKeys(BaseUrl, [keyA], "digest-d1");

        // Refresh with the unchanged digest.
        await sut.RefreshWithDigestAsync(BaseUrl);

        Assert.NotNull(memoryCache.Get(keyA));
    }
}
