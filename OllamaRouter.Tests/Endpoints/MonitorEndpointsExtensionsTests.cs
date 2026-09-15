using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Endpoints;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Endpoints;

public class MonitorEndpointsExtensionsTests
{
    private static async Task<(WebApplication App, HttpClient Client, Mocks Bag)> CreateTestAppAsync(
        string localUrl = "http://127.0.0.1:11435")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var mocks = new Mocks(
            new Mock<IActivityMonitorService>(),
            new Mock<IActivityStatisticsService>(),
            new Mock<ITargetAvailabilityService>(),
            new Mock<IOllamaModelCatalogClient>(),
            new Mock<IModelCatalogCacheService>());

        mocks.ActivityMonitor.Setup(m => m.GetSnapshot())
            .Returns(new ActivityMonitorSnapshot(new Dictionary<RoutingTarget, bool>(), [], []));

        mocks.ActivityStatistics.Setup(s => s.GetSnapshot())
            .Returns(new ActivityStatisticsSnapshot(
                new Dictionary<RoutingTarget, ActivityStatisticsTotals>
                {
                    [RoutingTarget.Local] = new(0, 0, 0),
                    [RoutingTarget.Remote] = new(0, 0, 0),
                    [RoutingTarget.Cloud] = new(0, 0, 0)
                },
                new(0, 0, 0),
                new Dictionary<RoutingTarget, ActivityStatisticsTotals>
                {
                    [RoutingTarget.Local] = new(0, 0, 0),
                    [RoutingTarget.Remote] = new(0, 0, 0),
                    [RoutingTarget.Cloud] = new(0, 0, 0)
                },
                new(0, 0, 0)));

        mocks.TargetAvailability.Setup(t => t.GetSnapshot())
            .Returns(new TargetAvailabilitySnapshot(true, true, false));

        builder.Services.AddSingleton(mocks.ActivityMonitor.Object);
        builder.Services.AddSingleton(mocks.ActivityStatistics.Object);
        builder.Services.AddSingleton(mocks.TargetAvailability.Object);
        builder.Services.AddSingleton(mocks.CatalogClient.Object);
        builder.Services.AddSingleton(mocks.ModelCatalogCache.Object);
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions
        {
            LocalUrl = localUrl
        }));

        var app = builder.Build();
        app.MapOllamaMonitorEndpoints();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client, mocks);
    }

    private sealed record Mocks(
        Mock<IActivityMonitorService> ActivityMonitor,
        Mock<IActivityStatisticsService> ActivityStatistics,
        Mock<ITargetAvailabilityService> TargetAvailability,
        Mock<IOllamaModelCatalogClient> CatalogClient,
        Mock<IModelCatalogCacheService> ModelCatalogCache);

    [Fact]
    public async Task GetMonitor_ReturnsHtmlWithReclaimVramButton()
    {
        var (app, client, _) = await CreateTestAppAsync();
        try
        {
            var response = await client.GetAsync("/monitor");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("🎮 Reclaim local VRAM", html);
            Assert.Contains("reclaimLocalVram", html);
            Assert.Contains("reclaimBtn", html);
            Assert.Contains("reclaimStatus", html);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostTargets_UpdatesTargetAvailability()
    {
        var (app, client, mocks) = await CreateTestAppAsync();
        try
        {
            var response = await client.PostAsJsonAsync("/targets", new { Local = false, Remote = true, Cloud = true });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            mocks.TargetAvailability.Verify(t => t.Update(false, true, true), Times.Once);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostReclaimVram_DisablesLocalTarget_AndUnloadsRunningModels()
    {
        var (app, client, mocks) = await CreateTestAppAsync("http://127.0.0.1:11435");
        try
        {
            mocks.TargetAvailability.Setup(t => t.GetSnapshot())
                .Returns(new TargetAvailabilitySnapshot(true, true, false));

            mocks.CatalogClient.Setup(c => c.StopRunningModelsAsync("http://127.0.0.1:11435", It.IsAny<CancellationToken>()))
                .ReturnsAsync(["llama3.2:latest", "qwen3:8b"]);

            var response = await client.PostAsync("/monitor/reclaim-vram", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();

            Assert.NotNull(json);
            Assert.True(json["success"]?.GetValue<bool>());
            Assert.True(json["localDisabled"]?.GetValue<bool>());
            Assert.Equal(2, json["count"]?.GetValue<int>());

            var unloadedArray = json["unloadedModels"]?.AsArray();
            Assert.NotNull(unloadedArray);
            Assert.Equal(2, unloadedArray.Count);
            Assert.Equal("llama3.2:latest", unloadedArray[0]?.ToString());
            Assert.Equal("qwen3:8b", unloadedArray[1]?.ToString());

            // Target availability must have been updated to disable Local (Local = false)
            mocks.TargetAvailability.Verify(t => t.Update(false, true, false), Times.Once);
            mocks.CatalogClient.Verify(c => c.StopRunningModelsAsync("http://127.0.0.1:11435", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostReclaimVram_WhenNoLocalUrlConfigured_StillDisablesLocalTarget()
    {
        var (app, client, mocks) = await CreateTestAppAsync(localUrl: "");
        try
        {
            mocks.TargetAvailability.Setup(t => t.GetSnapshot())
                .Returns(new TargetAvailabilitySnapshot(true, true, false));

            var response = await client.PostAsync("/monitor/reclaim-vram", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();

            Assert.NotNull(json);
            Assert.False(json["success"]?.GetValue<bool>());
            Assert.True(json["localDisabled"]?.GetValue<bool>());

            // Still disabled local
            mocks.TargetAvailability.Verify(t => t.Update(false, true, false), Times.Once);
            mocks.CatalogClient.Verify(c => c.StopRunningModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitor_ReturnsHtmlWithOfflineStylesAndScript()
    {
        var (app, client, _) = await CreateTestAppAsync();
        try
        {
            var response = await client.GetAsync("/monitor");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains(".card.offline", html);
            Assert.Contains(".offline .dot", html);
            Assert.Contains("statusText = 'Offline'", html);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_ReturnsOnlineStatus_ReflectingModelCatalogCache()
    {
        var (app, client, mocks) = await CreateTestAppAsync("http://127.0.0.1:11435");
        try
        {
            mocks.ModelCatalogCache.Setup(c => c.HasAnyModelsAsync("http://127.0.0.1:11435", It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();

            Assert.NotNull(json);
            var online = json["online"];
            Assert.NotNull(online);
            Assert.True(online["local"]?.GetValue<bool>());
            Assert.False(online["remote"]?.GetValue<bool>()); // remoteUrl not configured, so false
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
