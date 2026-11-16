using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OllamaRouter.Options;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.ModelUninstall;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Services.ModelUninstall;

/// <summary>
/// Unit tests for the <see cref="ModelUninstallService"/> (task #126): the validation ORDER
/// (fleet model check, then the target roster — both BEFORE any daemon delete call), the
/// case-insensitive target matching with canonical-name echoes, the request-order fail-soft
/// per-target rows (a failing target never aborts the rest) and the duplicate collapse.
/// The catalog client is strict-mocked: any unscripted delete call fails the test.
/// </summary>
public class ModelUninstallServiceTests
{
    private const string DesktopUrl = "http://127.0.0.1:11435";
    private const string ServerUrl = "http://192.168.1.10:11434";
    private const string Model = "llama3:8b";

    private static OllamaRouterOptions TwoTargetOptions() => new()
    {
        Targets =
        [
            new OllamaTargetOptions { Name = "Desktop", Url = DesktopUrl },
            new OllamaTargetOptions { Name = "Server", Url = ServerUrl }
        ],
        Models = new Dictionary<string, ModelThresholds>(),
        CloudOverflow = new CloudOverflowOptions { Enabled = false }
    };

    private static (ModelUninstallService Service, Mock<IOllamaModelCatalogClient> Catalog) CreateService(
        string fleetModel = Model)
    {
        var catalog = new Mock<IOllamaModelCatalogClient>(MockBehavior.Strict);
        var cache = new Mock<IModelCatalogCacheService>(MockBehavior.Strict);
        cache
            .Setup(c => c.RefreshAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<string>>([fleetModel]));

        return (new ModelUninstallService(
            new TestOptionsMonitor<OllamaRouterOptions>(TwoTargetOptions()),
            catalog.Object,
            cache.Object,
            fleetAggregation: null,
            availability: null,
            NullLogger<ModelUninstallService>.Instance), catalog);
    }

    [Fact]
    public async Task UninstallAsync_UnknownFleetModel_ReturnsModelError_WithZeroDeleteCalls()
    {
        // Strict catalog mock: no DeleteModelAsync scripted → any daemon call throws.
        var (service, _) = CreateService(fleetModel: "qwen2:72b");

        var result = await service.UninstallAsync("ghost:1b", ["Desktop"]);

        Assert.NotNull(result);
        Assert.Equal("model not found: ghost:1b", result!.Error);
        Assert.Empty(result.Targets);
    }

    [Fact]
    public async Task UninstallAsync_UnknownTarget_ReturnsTargetError_WithZeroDeleteCalls()
    {
        var (service, _) = CreateService();

        var result = await service.UninstallAsync(Model, ["Desktop", "Bogus"]);

        Assert.NotNull(result);
        Assert.Equal("target not found: Bogus", result!.Error);
        Assert.Empty(result.Targets);
    }

    [Fact]
    public async Task UninstallAsync_CloudPseudoTarget_IsNotAnEffectiveTarget_404Shape()
    {
        var (service, _) = CreateService();

        var result = await service.UninstallAsync(Model, ["Cloud"]);

        Assert.NotNull(result);
        Assert.Equal("target not found: Cloud", result!.Error);
    }

    [Fact]
    public async Task UninstallAsync_HappyPath_DeletesPerTargetInRequestOrder_WithCanonicalNames()
    {
        var (service, catalog) = CreateService();
        catalog
            .Setup(c => c.DeleteModelAsync(DesktopUrl, Model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDeleteOutcome(true));
        catalog
            .Setup(c => c.DeleteModelAsync(ServerUrl, Model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDeleteOutcome(true));

        // Case-insensitive request names; order reversed vs. configuration order.
        var result = await service.UninstallAsync(Model, ["server", "desktop"]);

        Assert.NotNull(result);
        Assert.Null(result!.Error);
        Assert.Equal(Model, result.Model);
        Assert.Equal(2, result.Targets.Count);
        Assert.Equal([("Server", true), ("Desktop", true)],
            result.Targets.Select(t => (t.Target, t.Success)).ToArray());
        Assert.All(result.Targets, t => Assert.Null(t.Error));
    }

    [Fact]
    public async Task UninstallAsync_MixedResults_FailSoftRows_DoNotAbortRemainingTargets()
    {
        var (service, catalog) = CreateService();
        catalog
            .Setup(c => c.DeleteModelAsync(DesktopUrl, Model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDeleteOutcome(false, "http 404: model 'llama3:8b' not found"));
        catalog
            .Setup(c => c.DeleteModelAsync(ServerUrl, Model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDeleteOutcome(true));

        var result = await service.UninstallAsync(Model, ["Desktop", "Server"]);

        Assert.NotNull(result);
        Assert.Null(result!.Error);
        Assert.Equal(2, result.Targets.Count);
        var desktop = result.Targets[0];
        var server = result.Targets[1];
        Assert.False(desktop.Success);
        Assert.Equal("http 404: model 'llama3:8b' not found", desktop.Error);
        Assert.True(server.Success);
        Assert.Null(server.Error);
    }

    [Fact]
    public async Task UninstallAsync_DuplicateNames_CollapseCaseInsensitively_ToOneCanonicalRow()
    {
        var (service, catalog) = CreateService();
        var calls = 0;
        catalog
            .Setup(c => c.DeleteModelAsync(DesktopUrl, Model, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                return Task.FromResult(new ModelDeleteOutcome(true));
            });

        var result = await service.UninstallAsync(Model, ["Desktop", "desktop", "DESKTOP"]);

        Assert.NotNull(result);
        Assert.Null(result!.Error);
        var row = Assert.Single(result.Targets);
        Assert.Equal("Desktop", row.Target);
        Assert.True(row.Success);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UninstallAsync_BlankNames_AreSkipped_SingleValidTargetStillDeletes()
    {
        var (service, catalog) = CreateService();
        catalog
            .Setup(c => c.DeleteModelAsync(ServerUrl, Model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDeleteOutcome(true));

        var result = await service.UninstallAsync(Model, ["  ", "Server"]);

        Assert.NotNull(result);
        Assert.Null(result!.Error);
        var row = Assert.Single(result.Targets);
        Assert.Equal("Server", row.Target);
        Assert.True(row.Success);
    }
}
