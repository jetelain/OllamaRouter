using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class RoutingContextCapabilityWiringTests
{
    private const string LocalUrl = "http://127.0.0.1:11435";
    private const string Digest = "digest-abc123";

    private static (RoutingContext Context, ModelCapabilityEngine Engine) CreateContext(
        Mock<IOllamaModelCatalogClient>? catalogClient = null,
        ModelThresholds? thresholds = null,
        bool withEngine = true,
        IReadOnlyList<OllamaTargetOptions>? targets = null)
    {
        var mockCatalog = catalogClient ?? new Mock<IOllamaModelCatalogClient>();
        if (catalogClient is null)
        {
            mockCatalog
                .Setup(c => c.GetTagsWithDigestAsync(LocalUrl, It.IsAny<CancellationToken>()))
                .ReturnsAsync((new JsonArray { new JsonObject { ["name"] = "m:latest", ["size"] = (JsonNode)2L } }, Digest));
            mockCatalog
                .Setup(c => c.GetShowAsync(LocalUrl, "m:latest", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new JsonObject
                {
                    ["model_info"] = new JsonObject { ["general.parameter_count"] = (JsonNode)27_800_000_000L },
                    ["details"] = new JsonObject { ["family"] = "qwen3" }
                });
        }

        var cacheService = new ModelCatalogCacheService(
            mockCatalog.Object,
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelCatalogCacheService>.Instance);

        ModelCapabilityEngine? capabilityEngine = withEngine
            ? new ModelCapabilityEngine(
                mockCatalog.Object,
                new MemoryCache(new MemoryCacheOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelCapabilityEngine>.Instance,
                cacheService)
            : null;

        var context = new RoutingContext(
            tokenCount: 1000,
            modelName: "m:latest",
            normalizedModelName: "m",
            thresholds,
            cacheService,
            LocalUrl,
            "http://remote:11434",
            CancellationToken.None,
            capabilityEngine,
            targets);

        return (context, capabilityEngine!);
    }

    [Fact]
    public async Task GetTargetCapabilityAsync_WithEngine_ReturnsDerivedCapability()
    {
        var (context, _) = CreateContext();

        var capability = await context.GetTargetCapabilityAsync(new RoutingTarget("Local"), LocalUrl);

        Assert.NotNull(capability);
        Assert.Equal(ModelArchitectureKind.LargeDense, capability!.Architecture);
        Assert.Equal(ArchitectureDetectionSource.DenseParameterCount, capability.Source);
        Assert.Equal(ModelCapabilityEngine.OllamaDefaultContext, capability.CDefault);
        // Unknown target (no targets list) → unknown VRAM → default ceiling, no headroom.
        Assert.Equal(ModelCapabilityEngine.OllamaDefaultContext, capability.CMax);
        Assert.Null(capability.AvailableVramForKVCacheMB);
    }

    [Fact]
    public async Task GetTargetCapabilityAsync_TargetWithVram_UsesHardwareCeiling()
    {
        var (context, _) = CreateContext(targets: [new OllamaTargetOptions { Name = "Local", Url = LocalUrl, HardwareHint = "16gb" }]);

        var capability = await context.GetTargetCapabilityAsync(new RoutingTarget("Local"), LocalUrl);

        Assert.NotNull(capability);
        Assert.Equal(ModelCapabilityEngine.DesktopCeiling, capability!.CMax);
        Assert.True(capability.HardwareCapable);
    }

    [Fact]
    public async Task GetTargetCapabilityAsync_NoEngine_ReturnsNull()
    {
        var (context, _) = CreateContext(withEngine: false);

        var capability = await context.GetTargetCapabilityAsync(new RoutingTarget("Local"), LocalUrl);

        Assert.Null(capability);
    }

    [Fact]
    public async Task GetTargetCapabilityAsync_EmptyDigest_ReturnsNull()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient
            .Setup(c => c.GetTagsWithDigestAsync(LocalUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new JsonArray(), ""));
        var (context, _) = CreateContext(catalogClient: catalogClient);

        var capability = await context.GetTargetCapabilityAsync(new RoutingTarget("Local"), LocalUrl);

        Assert.Null(capability);
    }
}
