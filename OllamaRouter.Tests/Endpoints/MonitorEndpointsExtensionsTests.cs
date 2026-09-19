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
using OllamaRouter.Serialization;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Endpoints;

public class MonitorEndpointsExtensionsTests
{
    private static async Task<(WebApplication App, HttpClient Client, Mocks Bag)> CreateTestAppAsync(
        string localUrl = "http://127.0.0.1:11435",
        PricingOptions? pricing = null,
        ActivityStatisticsSnapshot? statisticsSnapshot = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        var mocks = new Mocks(
            new Mock<IActivityMonitorService>(),
            new Mock<IActivityStatisticsService>(),
            new Mock<ITargetAvailabilityService>(),
            new Mock<IOllamaModelCatalogClient>(),
            new Mock<IModelCatalogCacheService>());

        mocks.ActivityMonitor.Setup(m => m.GetSnapshot())
            .Returns(new ActivityMonitorSnapshot(new Dictionary<RoutingTarget, bool>(), [], []));

        var defaultSnapshot = statisticsSnapshot ?? new ActivityStatisticsSnapshot(
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
            new(0, 0, 0));

        mocks.ActivityStatistics.Setup(s => s.GetSnapshot())
            .Returns(defaultSnapshot);

        mocks.TargetAvailability.Setup(t => t.GetSnapshot())
            .Returns(new TargetAvailabilitySnapshot(true, true, false));

        builder.Services.AddSingleton(mocks.ActivityMonitor.Object);
        builder.Services.AddSingleton(mocks.ActivityStatistics.Object);
        builder.Services.AddSingleton(mocks.TargetAvailability.Object);
        builder.Services.AddSingleton(mocks.CatalogClient.Object);
        builder.Services.AddSingleton(mocks.ModelCatalogCache.Object);
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions
        {
            LocalUrl = localUrl,
            Pricing = pricing
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
    public async Task PostTargets_WithCamelCaseJson_ReturnsUpdatedTargets()
    {
        var (app, client, mocks) = await CreateTestAppAsync();
        try
        {
            var content = new StringContent("""{"local": true, "remote": false, "cloud": true}""", System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/targets", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.NotNull(json);
            Assert.True(json["local"]?.GetValue<bool>());
            Assert.False(json["remote"]?.GetValue<bool>());
            Assert.True(json["cloud"]?.GetValue<bool>());

            mocks.TargetAvailability.Verify(t => t.Update(true, false, true), Times.Once);
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

    [Fact]
    public async Task GetMonitor_ReturnsHtmlWithProgressBarAndCostElements()
    {
        var (app, client, _) = await CreateTestAppAsync();
        try
        {
            var response = await client.GetAsync("/monitor");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("statsSummaryRow", html);
            Assert.Contains("usageRatioContainer", html);
            Assert.Contains("usageProgressBar", html);
            Assert.Contains("usageProgressLegend", html);
            Assert.Contains("renderUsageRatio", html);
            Assert.Contains("costContainer", html);
            Assert.Contains("has-cloud", html);
            Assert.Contains("estimatedSavingsValue", html);
            Assert.Contains("cloudCostValue", html);
            Assert.Contains("renderCost", html);
            Assert.Contains("formatCost", html);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_WithoutPricing_CostIsNull()
    {
        var (app, client, _) = await CreateTestAppAsync();
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();

            Assert.NotNull(json);
            Assert.True(json["cost"] == null, "Cost should be null or omitted when pricing is not configured.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_WithPricing_ReturnsEstimatedSavingsAndCloudCost()
    {
        var stats = new ActivityStatisticsSnapshot(
            Today: new Dictionary<RoutingTarget, ActivityStatisticsTotals>
            {
                [RoutingTarget.Local] = new(0, 0, 0),
                [RoutingTarget.Remote] = new(0, 0, 0),
                [RoutingTarget.Cloud] = new(0, 0, 0)
            },
            TodayTotal: new(0, 0, 0),
            Last7Days: new Dictionary<RoutingTarget, ActivityStatisticsTotals>
            {
                [RoutingTarget.Local] = new(Requests: 10, InputTokens: 200_000, OutputTokens: 50_000),
                [RoutingTarget.Remote] = new(Requests: 5, InputTokens: 800_000, OutputTokens: 150_000),
                [RoutingTarget.Cloud] = new(Requests: 2, InputTokens: 100_000, OutputTokens: 20_000)
            },
            Last7DaysTotal: new(17, 1_100_000, 220_000));

        var pricing = new PricingOptions
        {
            PromptPricePerMillion = 0.20,
            CompletionPricePerMillion = 0.80,
            Currency = "$"
        };

        var (app, client, _) = await CreateTestAppAsync(pricing: pricing, statisticsSnapshot: stats);
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();

            Assert.NotNull(json);
            var cost = json["cost"];
            Assert.NotNull(cost);

            // Local + Remote input = 1,000,000; output = 200,000
            // Savings = (1.0 * 0.20) + (0.2 * 0.80) = 0.20 + 0.16 = 0.36
            Assert.Equal(0.36, cost["estimatedSavings"]?.GetValue<double>());

            // Cloud input = 100,000; output = 20,000
            // Cloud cost = (0.1 * 0.20) + (0.02 * 0.80) = 0.02 + 0.016 = 0.036
            Assert.Equal(0.036, cost["cloudOverflowCost"]?.GetValue<double>());
            Assert.Equal("$", cost["currency"]?.GetValue<string>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void CalculateCostStatus_NullPricing_ReturnsNull()
    {
        var last7Days = new Dictionary<RoutingTarget, ActivityStatisticsTotals>
        {
            [RoutingTarget.Local] = new(10, 100_000, 50_000)
        };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(last7Days, null);

        Assert.Null(result);
    }

    [Fact]
    public void CalculateCostStatus_UnconfiguredPricing_ReturnsNull()
    {
        var last7Days = new Dictionary<RoutingTarget, ActivityStatisticsTotals>
        {
            [RoutingTarget.Local] = new(10, 100_000, 50_000)
        };

        var pricing = new PricingOptions();
        var result = MonitorEndpointsExtensions.CalculateCostStatus(last7Days, pricing);

        Assert.Null(result);
    }

    [Fact]
    public void CalculateCostStatus_FlatRate_CalculatesSavingsAndCost()
    {
        var last7Days = new Dictionary<RoutingTarget, ActivityStatisticsTotals>
        {
            [RoutingTarget.Local] = new(10, 500_000, 500_000),
            [RoutingTarget.Remote] = new(5, 500_000, 500_000),
            [RoutingTarget.Cloud] = new(2, 200_000, 100_000)
        };

        var pricing = new PricingOptions
        {
            PricePerMillion = 1.0,
            Currency = "€"
        };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(last7Days, pricing);

        Assert.NotNull(result);
        // Local + Remote total tokens = 2,000,000 -> 2.0 * 1.0 = 2.0
        Assert.Equal(2.0, result.EstimatedSavings);
        // Cloud total tokens = 300,000 -> 0.3 * 1.0 = 0.3
        Assert.Equal(0.3, result.CloudOverflowCost);
        Assert.Equal("€", result.Currency);
    }

    [Fact]
    public void CalculateCostStatus_InputOutputAliases_WorksCorrectly()
    {
        var last7Days = new Dictionary<RoutingTarget, ActivityStatisticsTotals>
        {
            [RoutingTarget.Local] = new(1, 1_000_000, 0),
            [RoutingTarget.Remote] = new(0, 0, 0),
            [RoutingTarget.Cloud] = new(1, 0, 1_000_000)
        };

        var pricing = new PricingOptions
        {
            InputPricePerMillion = 0.50,
            OutputPricePerMillion = 2.00
        };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(last7Days, pricing);

        Assert.NotNull(result);
        Assert.Equal(0.50, result.EstimatedSavings);
        Assert.Equal(2.00, result.CloudOverflowCost);
    }

    [Fact]
    public void CalculateOverheadStatus_NoValidSamples_ReturnsNullOverallFactor()
    {
        var requests = new List<ActivityLogEntry>
        {
            new(DateTimeOffset.UtcNow, RoutingTarget.Local, "qwen3:8b", 100, null, 50, 200, 200, true),
            new(DateTimeOffset.UtcNow, RoutingTarget.Local, "qwen3:8b", 100, 0, 50, 200, 200, true),
            new(DateTimeOffset.UtcNow, RoutingTarget.Local, "qwen3:8b", 100, 100, 50, 200, 500, false)
        };

        var result = MonitorEndpointsExtensions.CalculateOverheadStatus(requests, 1.10, "Tiktoken");

        Assert.NotNull(result);
        Assert.Equal(1.10, result.ConfiguredFactor);
        Assert.Equal("Tiktoken", result.Estimator);
        Assert.Null(result.OverallObservedFactor);
        Assert.Equal(0, result.SampleCount);
        Assert.Empty(result.Models);
    }

    [Fact]
    public void CalculateOverheadStatus_WithExplicitRawTokens_ComputesAccurateFactors()
    {
        var requests = new List<ActivityLogEntry>
        {
            new(DateTimeOffset.UtcNow, RoutingTarget.Local, "qwen3:8b", 110, 109, 50, 200, 200, true, RawPromptTokens: 100),
            new(DateTimeOffset.UtcNow, RoutingTarget.Local, "qwen3:8b", 220, 218, 50, 200, 200, true, RawPromptTokens: 200),
            new(DateTimeOffset.UtcNow, RoutingTarget.Remote, "llama3.2:latest", 100, 100, 30, 150, 200, true, RawPromptTokens: 100)
        };

        var result = MonitorEndpointsExtensions.CalculateOverheadStatus(requests, 1.0, "Heuristic");

        Assert.NotNull(result);
        Assert.Equal(1.0, result.ConfiguredFactor);
        Assert.Equal("Heuristic", result.Estimator);
        Assert.Equal(3, result.SampleCount);
        // Total raw = 400, total actual = 427 -> 427 / 400 = 1.0675 -> 1.07
        Assert.Equal(1.07, result.OverallObservedFactor);
        Assert.Equal(2, result.Models.Count);

        var qwen = result.Models.Single(m => m.Model == "qwen3:8b");
        Assert.Equal(2, qwen.SampleCount);
        Assert.Equal(1.09, qwen.ObservedFactor); // 327 / 300 = 1.09

        var llama = result.Models.Single(m => m.Model == "llama3.2:latest");
        Assert.Equal(1, llama.SampleCount);
        Assert.Equal(1.00, llama.ObservedFactor);
    }

    [Fact]
    public void CalculateOverheadStatus_FallbackWhenRawPromptTokensNotAvailable_CalculatesFromEstimatedAndConfigured()
    {
        var requests = new List<ActivityLogEntry>
        {
            // Estimated is 110, ConfiguredFactor is 1.10 -> raw = round(110 / 1.10) = 100
            // Actual is 115 -> factor = 115 / 100 = 1.15
            new(DateTimeOffset.UtcNow, RoutingTarget.Local, "qwen3:8b", 110, 115, 50, 200, 200, true, RawPromptTokens: null)
        };

        var result = MonitorEndpointsExtensions.CalculateOverheadStatus(requests, 1.10, "Tiktoken");

        Assert.NotNull(result);
        Assert.Equal(1, result.SampleCount);
        Assert.Equal(1.15, result.OverallObservedFactor);
        Assert.Single(result.Models);
        Assert.Equal(1.15, result.Models[0].ObservedFactor);
    }

    [Fact]
    public async Task GetMonitorApi_IncludesOverheadStatus_InResponse()
    {
        var requests = new List<ActivityLogEntry>
        {
            new(DateTimeOffset.UtcNow, RoutingTarget.Local, "qwen3:8b", 110, 109, 50, 200, 200, true, RawPromptTokens: 100)
        };

        var (app, client, mocks) = await CreateTestAppAsync();
        try
        {
            mocks.ActivityMonitor.Setup(m => m.GetSnapshot())
                .Returns(new ActivityMonitorSnapshot(new Dictionary<RoutingTarget, bool>(), [], requests));

            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();

            Assert.NotNull(json);
            var overhead = json["overhead"];
            Assert.NotNull(overhead);
            Assert.Equal(1.0, overhead["configuredFactor"]?.GetValue<double>());
            Assert.Equal("Heuristic", overhead["estimator"]?.GetValue<string>());
            Assert.Equal(1.09, overhead["overallObservedFactor"]?.GetValue<double>());
            Assert.Equal(1, overhead["sampleCount"]?.GetValue<int>());

            var models = overhead["models"]?.AsArray();
            Assert.NotNull(models);
            Assert.Single(models);
            Assert.Equal("qwen3:8b", models[0]?["model"]?.GetValue<string>());
            Assert.Equal(1.09, models[0]?["observedFactor"]?.GetValue<double>());
            Assert.Equal(1, models[0]?["sampleCount"]?.GetValue<int>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
