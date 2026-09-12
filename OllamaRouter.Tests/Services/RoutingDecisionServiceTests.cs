using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class RoutingDecisionServiceTests
{
    private const string ModelName = "llama3";

    private static RoutingDecisionService CreateSut(
        Mock<IOllamaModelCatalogClient> catalogClient,
        Mock<IGpuVramProvider> gpuVramProvider,
        int maxLocalTokens = 40_000,
        int minRequiredVramMB = 13_500,
        string modelName = ModelName,
        string? cloudModel = null,
        Mock<IModelCatalogCacheService>? modelCatalogCacheService = null,
        Mock<IActivityMonitorService>? activityMonitor = null,
        TargetAvailabilitySnapshot? targets = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions
        {
            Models = new Dictionary<string, ModelThresholds>
            {
                [modelName] = new ModelThresholds
                {
                    MaxLocalTokens = maxLocalTokens,
                    MinRequiredVramMB = minRequiredVramMB,
                    CloudModel = cloudModel
                }
            },
            LocalUrl = "http://127.0.0.1:11435",
            RemoteUrl = "http://aiserver.local:11434"
        });

        var targetAvailability = new Mock<ITargetAvailabilityService>();
        var effectiveTargets = targets ?? new TargetAvailabilitySnapshot(true, true, true);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Local)).Returns(effectiveTargets.Local);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Remote)).Returns(effectiveTargets.Remote);
        targetAvailability.Setup(t => t.IsEnabled(RoutingTarget.Cloud)).Returns(effectiveTargets.Cloud);

        modelCatalogCacheService ??= CreateModelCatalogCacheServiceAvailableOnBoth();
        activityMonitor ??= CreateActivityMonitorNotBusy();

        return new RoutingDecisionService(options, catalogClient.Object, modelCatalogCacheService.Object, gpuVramProvider.Object, activityMonitor.Object, targetAvailability.Object, NullLogger<RoutingDecisionService>.Instance);
    }

    private static Mock<IActivityMonitorService> CreateActivityMonitorNotBusy()
    {
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(a => a.IsBusy(It.IsAny<RoutingTarget>())).Returns(false);
        return activityMonitor;
    }

    private static Mock<IActivityMonitorService> CreateActivityMonitorRemoteBusy()
    {
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(a => a.IsBusy(RoutingTarget.Remote)).Returns(true);
        return activityMonitor;
    }

    private static Mock<IActivityMonitorService> CreateActivityMonitorBothBusy()
    {
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(a => a.IsBusy(RoutingTarget.Local)).Returns(true);
        activityMonitor.Setup(a => a.IsBusy(RoutingTarget.Remote)).Returns(true);
        return activityMonitor;
    }

    private static Mock<IModelCatalogCacheService> CreateModelCatalogCacheServiceAvailableOnBoth()
    {
        var modelCatalogCacheService = new Mock<IModelCatalogCacheService>();
        modelCatalogCacheService
            .Setup(c => c.ModelExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return modelCatalogCacheService;
    }

    [Fact]
    public async Task DecideAsync_ModelNotConfigured_ReturnsRemote_WithoutQueryingCatalogOrGpu()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        // The SUT only has "llama3" configured, so any other model name must be routed remote.
        var sut = CreateSut(catalogClient, gpuVramProvider);

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: "unknown-model");

        Assert.Equal(RoutingTarget.Remote, target);
        catalogClient.Verify(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        gpuVramProvider.Verify(g => g.GetFreeVramMB(), Times.Never);
    }

    [Fact]
    public async Task DecideAsync_TokenCountExceedsMax_ReturnsRemote_WithoutQueryingCatalogOrGpu()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var sut = CreateSut(catalogClient, gpuVramProvider);

        var target = await sut.DecideAsync(tokenCount: 50_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Remote, target);
        catalogClient.Verify(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        gpuVramProvider.Verify(g => g.GetFreeVramMB(), Times.Never);
    }

    [Fact]
    public async Task DecideAsync_TokenCountExceedsMax_RemoteBusy_CloudModelConfigured_ReturnsCloud()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var activityMonitor = CreateActivityMonitorRemoteBusy();
        var sut = CreateSut(catalogClient, gpuVramProvider, cloudModel: "deepseek-v3.1:671b-cloud", activityMonitor: activityMonitor);

        var target = await sut.DecideAsync(tokenCount: 50_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Cloud, target);
    }

    [Fact]
    public async Task DecideAsync_ModelNotLoaded_NotEnoughVram_RemoteBusy_CloudModelConfigured_ReturnsCloud()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(5_000);
        var activityMonitor = CreateActivityMonitorRemoteBusy();
        var sut = CreateSut(catalogClient, gpuVramProvider, cloudModel: "deepseek-v3.1:671b-cloud", activityMonitor: activityMonitor);

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Cloud, target);
    }

    [Fact]
    public async Task DecideAsync_ModelNotLoaded_NotEnoughVram_RemoteNotBusy_ReturnsRemote()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(5_000);
        var sut = CreateSut(catalogClient, gpuVramProvider, cloudModel: "deepseek-v3.1:671b-cloud");

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Remote, target);
    }

    [Fact]
    public async Task DecideAsync_ModelAlreadyLoadedLocally_ReturnsLocal_WithoutQueryingGpu()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), ModelName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var sut = CreateSut(catalogClient, gpuVramProvider);

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Local, target);
        gpuVramProvider.Verify(g => g.GetFreeVramMB(), Times.Never);
    }

    [Fact]
    public async Task DecideAsync_ModelNotLoaded_EnoughVram_ReturnsLocal()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(15_000);
        var sut = CreateSut(catalogClient, gpuVramProvider);

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Local, target);
    }

    [Fact]
    public async Task DecideAsync_ModelNotLoaded_NotEnoughVram_ReturnsRemote()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(5_000);
        var sut = CreateSut(catalogClient, gpuVramProvider);

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Remote, target);
    }

    [Fact]
    public async Task DecideAsync_TokenCountEqualsMax_IsWithinLocalLimit()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(20_000);
        var sut = CreateSut(catalogClient, gpuVramProvider);

        var target = await sut.DecideAsync(tokenCount: 40_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Local, target);
    }

    [Fact]
    public async Task DecideAsync_ModelNameWithLatestTag_ResolvesConfiguredModel()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(20_000);
        var sut = CreateSut(catalogClient, gpuVramProvider);

        // Clients frequently send the implicit ":latest" tag; the configured key has no tag.
        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName + ":latest");

        Assert.Equal(RoutingTarget.Local, target);
    }

    [Fact]
    public async Task DecideAsync_ModelOnlyAvailableLocally_ReturnsLocal_WithoutQueryingGpu()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var modelCatalogCacheService = new Mock<IModelCatalogCacheService>();
        modelCatalogCacheService.Setup(c => c.ModelExistsAsync("http://127.0.0.1:11435", ModelName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        modelCatalogCacheService.Setup(c => c.ModelExistsAsync("http://aiserver.local:11434", ModelName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var sut = CreateSut(catalogClient, gpuVramProvider, modelCatalogCacheService: modelCatalogCacheService);

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Local, target);
        gpuVramProvider.Verify(g => g.GetFreeVramMB(), Times.Never);
    }

    [Fact]
    public async Task DecideAsync_ModelOnlyAvailableRemotely_ReturnsRemote_WithoutQueryingGpu()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var modelCatalogCacheService = new Mock<IModelCatalogCacheService>();
        modelCatalogCacheService.Setup(c => c.ModelExistsAsync("http://127.0.0.1:11435", ModelName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        modelCatalogCacheService.Setup(c => c.ModelExistsAsync("http://aiserver.local:11434", ModelName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sut = CreateSut(catalogClient, gpuVramProvider, modelCatalogCacheService: modelCatalogCacheService);

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Remote, target);
        gpuVramProvider.Verify(g => g.GetFreeVramMB(), Times.Never);
    }

    [Fact]
    public async Task DecideAsync_ModelNotAvailableOnEitherTarget_ReturnsRemote_WithoutQueryingGpu()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var modelCatalogCacheService = new Mock<IModelCatalogCacheService>();
        modelCatalogCacheService
            .Setup(c => c.ModelExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var sut = CreateSut(catalogClient, gpuVramProvider, modelCatalogCacheService: modelCatalogCacheService);

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Remote, target);
        gpuVramProvider.Verify(g => g.GetFreeVramMB(), Times.Never);
    }

    [Fact]
    public async Task DecideAsync_ChecksAvailability_WithFullModelNameAndCorrectUrls()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient.Setup(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(20_000);
        var modelCatalogCacheService = CreateModelCatalogCacheServiceAvailableOnBoth();
        var sut = CreateSut(catalogClient, gpuVramProvider, modelCatalogCacheService: modelCatalogCacheService);

        await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName + ":latest");

        modelCatalogCacheService.Verify(c => c.ModelExistsAsync("http://127.0.0.1:11435", ModelName + ":latest", It.IsAny<CancellationToken>()), Times.Once);
        modelCatalogCacheService.Verify(c => c.ModelExistsAsync("http://aiserver.local:11434", ModelName + ":latest", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DecideAsync_ModelNotAvailableOnEitherTarget_TokenCountExceedsMax_ReturnsRemote_WithoutQueryingCatalogOrGpu()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var modelCatalogCacheService = new Mock<IModelCatalogCacheService>();
        modelCatalogCacheService
            .Setup(c => c.ModelExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var sut = CreateSut(catalogClient, gpuVramProvider, modelCatalogCacheService: modelCatalogCacheService);

        var target = await sut.DecideAsync(tokenCount: 50_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Remote, target);
        catalogClient.Verify(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        gpuVramProvider.Verify(g => g.GetFreeVramMB(), Times.Never);
    }

    [Fact]
    public async Task DecideAsync_ModelAvailableOnBoth_ButExceedsTokenLimit_ReturnsRemote()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var sut = CreateSut(catalogClient, gpuVramProvider);

        var target = await sut.DecideAsync(tokenCount: 50_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Remote, target);
        catalogClient.Verify(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        gpuVramProvider.Verify(g => g.GetFreeVramMB(), Times.Never);
    }

    [Fact]
    public async Task DecideAsync_LocalAndRemoteBothBusy_CloudModelConfigured_ReturnsCloud()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var activityMonitor = CreateActivityMonitorBothBusy();
        var sut = CreateSut(catalogClient, gpuVramProvider, cloudModel: "deepseek-v3.1:671b-cloud", activityMonitor: activityMonitor);

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Cloud, target);
        catalogClient.Verify(c => c.IsModelLoadedLocallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        gpuVramProvider.Verify(g => g.GetFreeVramMB(), Times.Never);
    }

    [Fact]
    public async Task DecideAsync_LocalAndRemoteBothBusy_NoCloudModelConfigured_DoesNotOverflow()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(20_000);
        var activityMonitor = CreateActivityMonitorBothBusy();
        var sut = CreateSut(catalogClient, gpuVramProvider, activityMonitor: activityMonitor);

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.NotEqual(RoutingTarget.Cloud, target);
    }

    [Theory]
    [InlineData("llama3", "llama3")]
    [InlineData("llama3:latest", "llama3")]
    [InlineData("qwen3.8-27B:latest", "qwen3.8-27B")]
    [InlineData("qwen3.8-27B:myquant", "qwen3.8-27B")]
    [InlineData("no-tag", "no-tag")]
    [InlineData("", "")]
    public void NormalizeModelName_StripsTagPrefix(string input, string expected)
    {
        Assert.Equal(expected, RoutingDecisionService.NormalizeModelName(input));
    }

    [Fact]
    public async Task DecideAsync_LocalDisabled_RemoteBusy_CloudModelConfigured_ReturnsCloud()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var activityMonitor = CreateActivityMonitorRemoteBusy();
        var sut = CreateSut(catalogClient, gpuVramProvider,
            cloudModel: "deepseek-v3.1:671b-cloud",
            activityMonitor: activityMonitor,
            targets: new TargetAvailabilitySnapshot(false, true, true));

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Cloud, target);
    }

    [Fact]
    public async Task DecideAsync_RemoteDisabled_TokenCountExceedsMax_CloudModelConfigured_ReturnsCloud()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var sut = CreateSut(catalogClient, gpuVramProvider,
            cloudModel: "deepseek-v3.1:671b-cloud",
            targets: new TargetAvailabilitySnapshot(true, false, true));

        var target = await sut.DecideAsync(tokenCount: 50_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Cloud, target);
    }

    [Fact]
    public async Task DecideAsync_CloudDisabled_LocalAndRemoteBothBusy_CloudModelConfigured_DoesNotOverflow()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(20_000);
        var activityMonitor = CreateActivityMonitorBothBusy();
        var sut = CreateSut(catalogClient, gpuVramProvider,
            cloudModel: "deepseek-v3.1:671b-cloud",
            activityMonitor: activityMonitor,
            targets: new TargetAvailabilitySnapshot(true, true, false));

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.NotEqual(RoutingTarget.Cloud, target);
    }

    [Fact]
    public async Task DecideAsync_RemoteDisabled_CloudDisabled_TokenCountExceedsMax_ReturnsRemote()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var sut = CreateSut(catalogClient, gpuVramProvider,
            cloudModel: "deepseek-v3.1:671b-cloud",
            targets: new TargetAvailabilitySnapshot(true, false, false));

        var target = await sut.DecideAsync(tokenCount: 50_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Remote, target);
    }
}
