using Microsoft.Extensions.DependencyInjection;
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
        var handlers = new IRoutingTargetHandler[]
        {
            new LocalRoutingTargetHandler(options, catalogClient.Object, gpuVramProvider.Object, activityMonitor.Object, targetAvailability.Object),
            new RemoteRoutingTargetHandler(activityMonitor.Object, targetAvailability.Object),
            new CloudRoutingTargetHandler(targetAvailability.Object)
        };

        return new RoutingDecisionService(handlers, options, modelCatalogCacheService.Object, activityMonitor.Object, NullLogger<RoutingDecisionService>.Instance);
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
    public async Task DecideAsync_RemoteDisabled_CloudDisabled_TokenCountExceedsMax_ThrowsNoAvailableTargetException()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var sut = CreateSut(catalogClient, gpuVramProvider,
            cloudModel: "deepseek-v3.1:671b-cloud",
            targets: new TargetAvailabilitySnapshot(true, false, false));

        await Assert.ThrowsAsync<NoAvailableTargetException>(() => sut.DecideAsync(tokenCount: 50_000, modelName: ModelName));
    }

    [Fact]
    public async Task DecideAsync_RemoteDisabled_CloudDisabled_WithinLocalLimits_ReturnsLocal()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(20_000);
        var sut = CreateSut(catalogClient, gpuVramProvider,
            cloudModel: "deepseek-v3.1:671b-cloud",
            targets: new TargetAvailabilitySnapshot(true, false, false));

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Local, target);
    }

    [Fact]
    public async Task DecideAsync_RemoteDisabled_LocalBusy_WithinLocalLimits_FallsBackToLocal()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(20_000);
        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(a => a.IsBusy(RoutingTarget.Local)).Returns(true);
        activityMonitor.Setup(a => a.IsBusy(RoutingTarget.Remote)).Returns(false);
        var sut = CreateSut(catalogClient, gpuVramProvider,
            activityMonitor: activityMonitor,
            targets: new TargetAvailabilitySnapshot(true, false, false));

        var target = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Local, target);
    }

    [Fact]
    public async Task DecideAsync_AllTargetsDisabled_ThrowsNoAvailableTargetException()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(20_000);
        var sut = CreateSut(catalogClient, gpuVramProvider,
            targets: new TargetAvailabilitySnapshot(false, false, false));

        await Assert.ThrowsAsync<NoAvailableTargetException>(() => sut.DecideAsync(tokenCount: 1_000, modelName: ModelName));
    }

    [Fact]
    public async Task DecideAsync_MultiTarget_DispatchesToFirstAvailableTarget()
    {
        var target1 = new Mock<IRoutingTargetHandler>();
        target1.Setup(t => t.Target).Returns(RoutingTarget.Local);
        target1.Setup(t => t.IsEnabled).Returns(true);
        target1.Setup(t => t.IsAvailable()).Returns(false); // busy
        target1.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var target2 = new Mock<IRoutingTargetHandler>();
        target2.Setup(t => t.Target).Returns(RoutingTarget.Remote);
        target2.Setup(t => t.IsEnabled).Returns(true);
        target2.Setup(t => t.IsAvailable()).Returns(true);
        target2.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var target3Overflow = new Mock<IRoutingTargetHandler>();
        target3Overflow.Setup(t => t.Target).Returns(RoutingTarget.Cloud);
        target3Overflow.Setup(t => t.IsOverflow).Returns(true);
        target3Overflow.Setup(t => t.IsEnabled).Returns(true);
        target3Overflow.Setup(t => t.IsAvailable()).Returns(true);
        target3Overflow.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var options = Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions());
        var catalogCache = new Mock<IModelCatalogCacheService>();
        var sut = new RoutingDecisionService(
            [target1.Object, target2.Object, target3Overflow.Object],
            options,
            catalogCache.Object,
            NullLogger<RoutingDecisionService>.Instance);

        var decision = await sut.DecideAsync(100, "test-model");

        Assert.Equal(RoutingTarget.Remote, decision);
        target3Overflow.Verify(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DecideAsync_MultiTarget_AllPrimaryBusy_DispatchesToLastOverflowTarget()
    {
        var target1 = new Mock<IRoutingTargetHandler>();
        target1.Setup(t => t.Target).Returns(RoutingTarget.Local);
        target1.Setup(t => t.IsEnabled).Returns(true);
        target1.Setup(t => t.IsAvailable()).Returns(false); // busy

        var target2 = new Mock<IRoutingTargetHandler>();
        target2.Setup(t => t.Target).Returns(RoutingTarget.Remote);
        target2.Setup(t => t.IsEnabled).Returns(true);
        target2.Setup(t => t.IsAvailable()).Returns(false); // busy

        var target3Overflow = new Mock<IRoutingTargetHandler>();
        target3Overflow.Setup(t => t.Target).Returns(RoutingTarget.Cloud);
        target3Overflow.Setup(t => t.IsOverflow).Returns(true);
        target3Overflow.Setup(t => t.IsEnabled).Returns(true);
        target3Overflow.Setup(t => t.IsAvailable()).Returns(true);
        target3Overflow.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var options = Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions());
        var catalogCache = new Mock<IModelCatalogCacheService>();
        var sut = new RoutingDecisionService(
            [target1.Object, target2.Object, target3Overflow.Object],
            options,
            catalogCache.Object,
            NullLogger<RoutingDecisionService>.Instance);

        var decision = await sut.DecideAsync(100, "test-model");

        Assert.Equal(RoutingTarget.Cloud, decision);
    }

    [Fact]
    public async Task DecideAsync_MultiTarget_NoTargetAvailable_FallsBackToFirstCapableTarget()
    {
        var target1 = new Mock<IRoutingTargetHandler>();
        target1.Setup(t => t.Target).Returns(RoutingTarget.Local);
        target1.Setup(t => t.IsEnabled).Returns(true);
        target1.Setup(t => t.IsAvailable()).Returns(false); // busy
        target1.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var target2 = new Mock<IRoutingTargetHandler>();
        target2.Setup(t => t.Target).Returns(RoutingTarget.Remote);
        target2.Setup(t => t.IsEnabled).Returns(true);
        target2.Setup(t => t.IsAvailable()).Returns(false); // busy
        target2.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var target3Overflow = new Mock<IRoutingTargetHandler>();
        target3Overflow.Setup(t => t.Target).Returns(RoutingTarget.Cloud);
        target3Overflow.Setup(t => t.IsOverflow).Returns(true);
        target3Overflow.Setup(t => t.IsEnabled).Returns(true);
        target3Overflow.Setup(t => t.IsAvailable()).Returns(false); // busy
        target3Overflow.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var options = Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions());
        var catalogCache = new Mock<IModelCatalogCacheService>();
        var sut = new RoutingDecisionService(
            [target1.Object, target2.Object, target3Overflow.Object],
            options,
            catalogCache.Object,
            NullLogger<RoutingDecisionService>.Instance);

        var decision = await sut.DecideAsync(100, "test-model");

        // Falls back to target1 (Local) as the first capable target
        Assert.Equal(RoutingTarget.Local, decision);
    }

    [Fact]
    public async Task DecideAsync_MultiTarget_NoTargetAvailable_FirstUnable_FallsBackToSecondCapableTarget()
    {
        var target1 = new Mock<IRoutingTargetHandler>();
        target1.Setup(t => t.Target).Returns(RoutingTarget.Local);
        target1.Setup(t => t.IsEnabled).Returns(true);
        target1.Setup(t => t.IsAvailable()).Returns(false); // busy
        target1.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(false); // cannot handle

        var target2 = new Mock<IRoutingTargetHandler>();
        target2.Setup(t => t.Target).Returns(RoutingTarget.Remote);
        target2.Setup(t => t.IsEnabled).Returns(true);
        target2.Setup(t => t.IsAvailable()).Returns(false); // busy
        target2.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(true); // can handle

        var target3Overflow = new Mock<IRoutingTargetHandler>();
        target3Overflow.Setup(t => t.Target).Returns(RoutingTarget.Cloud);
        target3Overflow.Setup(t => t.IsOverflow).Returns(true);
        target3Overflow.Setup(t => t.IsEnabled).Returns(true);
        target3Overflow.Setup(t => t.IsAvailable()).Returns(false); // busy

        var options = Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions());
        var catalogCache = new Mock<IModelCatalogCacheService>();
        var sut = new RoutingDecisionService(
            [target1.Object, target2.Object, target3Overflow.Object],
            options,
            catalogCache.Object,
            NullLogger<RoutingDecisionService>.Instance);

        var decision = await sut.DecideAsync(100, "test-model");

        // Falls back to target2 (Remote) as target1 cannot handle the request
        Assert.Equal(RoutingTarget.Remote, decision);
    }

    [Fact]
    public async Task DecideAsync_MultiTarget_DisabledTarget_NeverHandlesRequest()
    {
        var target1 = new Mock<IRoutingTargetHandler>();
        target1.Setup(t => t.Target).Returns(RoutingTarget.Local);
        target1.Setup(t => t.IsEnabled).Returns(false); // disabled!
        target1.Setup(t => t.IsAvailable()).Returns(false);
        target1.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var target2 = new Mock<IRoutingTargetHandler>();
        target2.Setup(t => t.Target).Returns(RoutingTarget.Remote);
        target2.Setup(t => t.IsEnabled).Returns(true); // enabled but busy
        target2.Setup(t => t.IsAvailable()).Returns(false);
        target2.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var options = Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions());
        var catalogCache = new Mock<IModelCatalogCacheService>();
        var sut = new RoutingDecisionService(
            [target1.Object, target2.Object],
            options,
            catalogCache.Object,
            NullLogger<RoutingDecisionService>.Instance);

        var decision = await sut.DecideAsync(100, "test-model");

        // Disabled target1 must never be selected; falls back to busy enabled target2
        Assert.Equal(RoutingTarget.Remote, decision);
    }

    [Fact]
    public async Task DecideAsync_MultiTarget_NoTargetCapable_ThrowsInvalidOperationException()
    {
        var target1 = new Mock<IRoutingTargetHandler>();
        target1.Setup(t => t.Target).Returns(RoutingTarget.Local);
        target1.Setup(t => t.IsEnabled).Returns(true);
        target1.Setup(t => t.IsAvailable()).Returns(false);
        target1.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var target2 = new Mock<IRoutingTargetHandler>();
        target2.Setup(t => t.Target).Returns(RoutingTarget.Remote);
        target2.Setup(t => t.IsEnabled).Returns(true);
        target2.Setup(t => t.IsAvailable()).Returns(false);
        target2.Setup(t => t.CanProcessAsync(It.IsAny<RoutingContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var options = Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions());
        var catalogCache = new Mock<IModelCatalogCacheService>();
        var sut = new RoutingDecisionService(
            [target1.Object, target2.Object],
            options,
            catalogCache.Object,
            NullLogger<RoutingDecisionService>.Instance);

        await Assert.ThrowsAsync<NoAvailableTargetException>(() => sut.DecideAsync(100, "test-model"));
    }

    [Fact]
    public void ServiceCollection_CanResolveRoutingDecisionService_WithoutAmbiguousConstructors()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<OllamaRouterOptions>(_ => { });
        services.AddSingleton(new Mock<IOllamaModelCatalogClient>().Object);
        services.AddSingleton(new Mock<IModelCatalogCacheService>().Object);
        services.AddSingleton(new Mock<IGpuVramProvider>().Object);
        services.AddSingleton(new Mock<IActivityMonitorService>().Object);
        services.AddSingleton(new Mock<ITargetAvailabilityService>().Object);

        services.AddTransient<IRoutingTargetHandler, LocalRoutingTargetHandler>();
        services.AddTransient<IRoutingTargetHandler, RemoteRoutingTargetHandler>();
        services.AddTransient<IRoutingTargetHandler, CloudRoutingTargetHandler>();
        services.AddTransient<IRoutingDecisionService, RoutingDecisionService>();

        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var instance = serviceProvider.GetRequiredService<IRoutingDecisionService>();
        Assert.NotNull(instance);
    }

    [Fact]
    public async Task DecideAsync_WhenRemoteFails3Times_AndCloudEnabled_SwitchesToCloud()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var activityMonitor = CreateActivityMonitorNotBusy();

        // Remote has failed 3 times for 91,300 tokens
        activityMonitor
            .Setup(a => a.GetConsecutiveFailures(RoutingTarget.Remote, 91_300))
            .Returns(3);

        var sut = CreateSut(
            catalogClient,
            gpuVramProvider,
            maxLocalTokens: 36_864, // 91.3k > 36.8k so Local cannot process
            modelName: "Qwen3.8-27B",
            cloudModel: "glm-5.3-flash:cloud",
            activityMonitor: activityMonitor);

        var decision = await sut.DecideAsync(tokenCount: 91_300, modelName: "Qwen3.8-27B:latest");

        Assert.Equal(RoutingTarget.Cloud, decision);
    }

    [Fact]
    public async Task DecideAsync_WhenRemoteFails3Times_AndCloudDisabled_KeepsRemote()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var activityMonitor = CreateActivityMonitorNotBusy();

        activityMonitor
            .Setup(a => a.GetConsecutiveFailures(RoutingTarget.Remote, 91_300))
            .Returns(3);

        // Cloud target is disabled
        var targets = new TargetAvailabilitySnapshot(Local: true, Remote: true, Cloud: false);

        var sut = CreateSut(
            catalogClient,
            gpuVramProvider,
            maxLocalTokens: 36_864,
            modelName: "Qwen3.8-27B",
            cloudModel: "glm-5.3-flash:cloud",
            activityMonitor: activityMonitor,
            targets: targets);

        var decision = await sut.DecideAsync(tokenCount: 91_300, modelName: "Qwen3.8-27B:latest");

        Assert.Equal(RoutingTarget.Remote, decision);
    }

    [Fact]
    public async Task DecideAsync_WhenRemoteFails3Times_AndNoCloudModelConfigured_KeepsRemote()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var activityMonitor = CreateActivityMonitorNotBusy();

        activityMonitor
            .Setup(a => a.GetConsecutiveFailures(RoutingTarget.Remote, 91_300))
            .Returns(3);

        // cloudModel is null
        var sut = CreateSut(
            catalogClient,
            gpuVramProvider,
            maxLocalTokens: 36_864,
            modelName: "Qwen3.8-27B",
            cloudModel: null,
            activityMonitor: activityMonitor);

        var decision = await sut.DecideAsync(tokenCount: 91_300, modelName: "Qwen3.8-27B:latest");

        Assert.Equal(RoutingTarget.Remote, decision);
    }

    [Fact]
    public async Task DecideAsync_WhenLocalFails3Times_SwitchesToRemote()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(20_000);
        var activityMonitor = CreateActivityMonitorNotBusy();

        // Local has failed 3 times for 2,000 tokens
        activityMonitor
            .Setup(a => a.GetConsecutiveFailures(RoutingTarget.Local, 2_000))
            .Returns(3);

        var sut = CreateSut(
            catalogClient,
            gpuVramProvider,
            maxLocalTokens: 40_000,
            modelName: "llama3",
            cloudModel: "glm-5.3-flash:cloud",
            activityMonitor: activityMonitor);

        var decision = await sut.DecideAsync(tokenCount: 2_000, modelName: "llama3");

        Assert.Equal(RoutingTarget.Remote, decision);
    }

    [Fact]
    public async Task DecideAsync_WhenLocalAndRemoteBothFail3Times_SwitchesToCloud()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(20_000);
        var activityMonitor = CreateActivityMonitorNotBusy();

        // Both Local and Remote have failed 3 times
        activityMonitor
            .Setup(a => a.GetConsecutiveFailures(RoutingTarget.Local, 2_000))
            .Returns(3);
        activityMonitor
            .Setup(a => a.GetConsecutiveFailures(RoutingTarget.Remote, 2_000))
            .Returns(3);

        var sut = CreateSut(
            catalogClient,
            gpuVramProvider,
            maxLocalTokens: 40_000,
            modelName: "llama3",
            cloudModel: "glm-5.3-flash:cloud",
            activityMonitor: activityMonitor);

        var decision = await sut.DecideAsync(tokenCount: 2_000, modelName: "llama3");

        Assert.Equal(RoutingTarget.Cloud, decision);
    }

    [Fact]
    public async Task DecideAsync_WhenFailuresBelowThreshold_KeepsOriginalTarget()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var activityMonitor = CreateActivityMonitorNotBusy();

        // Only 2 failures (below threshold of 3)
        activityMonitor
            .Setup(a => a.GetConsecutiveFailures(RoutingTarget.Remote, 91_300))
            .Returns(2);

        var sut = CreateSut(
            catalogClient,
            gpuVramProvider,
            maxLocalTokens: 36_864,
            modelName: "Qwen3.8-27B",
            cloudModel: "glm-5.3-flash:cloud",
            activityMonitor: activityMonitor);

        var decision = await sut.DecideAsync(tokenCount: 91_300, modelName: "Qwen3.8-27B:latest");

        Assert.Equal(RoutingTarget.Remote, decision);
    }

    [Fact]
    public async Task DecideAsync_ConcurrentRequests_WhenLocalAndRemoteBusyAndCloudBusy_RoutesAllSubsequentRequestsToCloud()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        var activityMonitor = new Mock<IActivityMonitorService>();

        // Local, Remote and Cloud are ALL busy
        activityMonitor.Setup(a => a.IsBusy(RoutingTarget.Local)).Returns(true);
        activityMonitor.Setup(a => a.IsBusy(RoutingTarget.Remote)).Returns(true);
        activityMonitor.Setup(a => a.IsBusy(RoutingTarget.Cloud)).Returns(true);

        var sut = CreateSut(catalogClient, gpuVramProvider, cloudModel: "deepseek-v3.1:671b-cloud", activityMonitor: activityMonitor);

        // Multiple concurrent requests should ALL route to Cloud despite Cloud already being busy
        var decision1 = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);
        var decision2 = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);
        var decision3 = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);

        Assert.Equal(RoutingTarget.Cloud, decision1);
        Assert.Equal(RoutingTarget.Cloud, decision2);
        Assert.Equal(RoutingTarget.Cloud, decision3);
    }

    [Fact]
    public async Task DecideAsync_ProgressiveLoad_FirstToLocal_SecondToRemote_SubsequentAllToCloud()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var gpuVramProvider = new Mock<IGpuVramProvider>();
        gpuVramProvider.Setup(g => g.GetFreeVramMB()).Returns(20_000);

        var localBusy = false;
        var remoteBusy = false;
        var cloudBusy = false;

        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(a => a.IsBusy(RoutingTarget.Local)).Returns(() => localBusy);
        activityMonitor.Setup(a => a.IsBusy(RoutingTarget.Remote)).Returns(() => remoteBusy);
        activityMonitor.Setup(a => a.IsBusy(RoutingTarget.Cloud)).Returns(() => cloudBusy);

        var sut = CreateSut(catalogClient, gpuVramProvider, cloudModel: "deepseek-v3.1:671b-cloud", activityMonitor: activityMonitor);

        // Request 1: Local is idle -> routes to Local
        var r1 = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);
        Assert.Equal(RoutingTarget.Local, r1);
        localBusy = true; // Request 1 is now in progress on Local

        // Request 2: Local is busy, Remote is idle -> routes to Remote
        var r2 = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);
        Assert.Equal(RoutingTarget.Remote, r2);
        remoteBusy = true; // Request 2 is now in progress on Remote

        // Request 3: Local and Remote are busy -> routes to Cloud
        var r3 = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);
        Assert.Equal(RoutingTarget.Cloud, r3);
        cloudBusy = true; // Request 3 is now in progress on Cloud

        // Request 4: Local, Remote, and Cloud have requests in progress -> still routes to Cloud!
        var r4 = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);
        Assert.Equal(RoutingTarget.Cloud, r4);

        // Request 5: Still all busy -> still routes to Cloud!
        var r5 = await sut.DecideAsync(tokenCount: 1_000, modelName: ModelName);
        Assert.Equal(RoutingTarget.Cloud, r5);
    }
}

