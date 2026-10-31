using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
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
        ActivityStatisticsSnapshot? statisticsSnapshot = null,
        OllamaRouterOptions? options = null,
        ITargetLivenessService? liveness = null,
        ITargetExecutionTracker? executionTracker = null,
        IContextAutoShrinkManager? contextShrinkManager = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
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
            .Returns(new TargetAvailabilitySnapshot(new Dictionary<RoutingTarget, bool>
            {
                [RoutingTarget.Local] = true,
                [RoutingTarget.Remote] = true,
                [RoutingTarget.Cloud] = false
            }));

        builder.Services.AddSingleton(mocks.ActivityMonitor.Object);
        builder.Services.AddSingleton(mocks.ActivityStatistics.Object);
        builder.Services.AddSingleton(mocks.TargetAvailability.Object);
        builder.Services.AddSingleton(mocks.CatalogClient.Object);
        builder.Services.AddSingleton(mocks.ModelCatalogCache.Object);
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options ?? new OllamaRouterOptions
        {
            LocalUrl = localUrl,
            Pricing = pricing
        }));

        // Optional fleet-enrichment services (null = not registered: minimal test app).
        if (liveness != null)
        {
            builder.Services.AddSingleton(liveness);
        }
        if (executionTracker != null)
        {
            builder.Services.AddSingleton(executionTracker);
        }
        if (contextShrinkManager != null)
        {
            builder.Services.AddSingleton(contextShrinkManager);
        }

        var app = builder.Build();
        app.MapOllamaMonitorEndpoints();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client, mocks);
    }

    private static TargetAvailabilitySnapshot TargetFlags(params (RoutingTarget Target, bool Enabled)[] entries) =>
        new(entries.ToDictionary(pair => pair.Target, pair => pair.Enabled));

    private static Dictionary<string, bool> StringFlags(params (string Name, bool Value)[] entries) =>
        entries.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

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
            Assert.Contains("\uD83C\uDFAE Reclaim local VRAM", html);
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
            mocks.TargetAvailability.Verify(t => t.Update(It.Is<IReadOnlyDictionary<string, bool>>(f =>
                    f.ContainsKey("Local") && !f["Local"] && f.ContainsKey("Remote") && f["Remote"] &&
                    f.ContainsKey("Cloud") && f["Cloud"])), Times.Once);
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

            // Target names are matched case-insensitively, so the applied flags come back keyed
            // by the requested names.
            var lookup = StringFlags();
            foreach (var (key, value) in json)
            {
                lookup[key] = (bool)value!;
            }
            Assert.True(lookup["local"]);
            Assert.False(lookup["remote"]);
            Assert.True(lookup["cloud"]);

            mocks.TargetAvailability.Verify(t => t.Update(It.Is<IReadOnlyDictionary<string, bool>>(f =>
                    f.ContainsKey("local") && f["local"] && f.ContainsKey("remote") && !f["remote"] &&
                    f.ContainsKey("cloud") && f["cloud"])), Times.Once);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostTargets_WithFleetName_UpdatesTargetAvailability()
    {
        var (app, client, mocks) = await CreateTestAppAsync();
        try
        {
            var content = new StringContent("""{"desktop": false}""", System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/targets", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // The web toggle for a fleet name flows into the availability service under that key.
            mocks.TargetAvailability.Verify(t => t.Update(It.Is<IReadOnlyDictionary<string, bool>>(f =>
                    f.ContainsKey("desktop") && !f["desktop"] && f.Count == 1)), Times.Once);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void TogglePersistence_RoundTrip_PersistsDisabledFleetTargetAcrossInstances()
    {
        var tempDir = Directory.CreateTempSubdirectory("OllamaRouterTests");
        var stateFilePath = Path.Combine(tempDir.FullName, "targets.json");
        try
        {
            var fleet = new[] { new RoutingTarget("desktop"), new RoutingTarget("laptop"), new RoutingTarget("server1"), RoutingTarget.Cloud };

            var first = new TargetAvailabilityService(stateFilePath, fleet, NullLogger<TargetAvailabilityService>.Instance);
            first.Update(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["desktop"] = false
            });

            // A fresh instance constructed from the same persisted file sees the disabled target;
            // all other targets remain enabled.
            var second = new TargetAvailabilityService(stateFilePath, fleet, NullLogger<TargetAvailabilityService>.Instance);
            Assert.False(second.IsEnabled(new RoutingTarget("desktop")));
            Assert.True(second.IsEnabled(new RoutingTarget("laptop")));
            Assert.True(second.IsEnabled(new RoutingTarget("server1")));
            Assert.True(second.IsEnabled(RoutingTarget.Cloud));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetMonitorApi_FleetIsOrderedArray_WithCloudLast_AndLegacyDictionariesRemoved()
    {
        var (app, client, _) = await CreateTestAppAsync();
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();

            Assert.NotNull(json);

            // The legacy name-keyed dictionaries are gone.
            Assert.True(json["targets"] == null);
            Assert.True(json["online"] == null);
            Assert.True(json["busy"] == null);

            // The new ordered fleet array: "Local" (no RemoteUrl in this test app), Cloud last.
            var fleet = json["fleet"]!.AsArray();
            Assert.Equal(2, fleet.Count);

            var local = fleet[0]!;
            Assert.Equal("Local", local["name"]?.GetValue<string>());
            Assert.Equal("http://127.0.0.1:11435", local["url"]?.GetValue<string>());
            Assert.Equal("local", local["location"]?.GetValue<string>()); // loopback URL
            Assert.True(local["enabled"]?.GetValue<bool>());
            Assert.Equal("Offline", local["liveness"]?.GetValue<string>()); // no models in the cache mock
            Assert.Equal(0, local["inFlightRequests"]?.GetValue<int>());
            Assert.Equal("-", local["hardwareProfile"]?.GetValue<string>()); // legacy synthesized hint
            Assert.Equal("-", local["contextAllocation"]?.GetValue<string>()); // no leases

            var cloud = fleet[1]!;
            Assert.Equal("Cloud", cloud["name"]?.GetValue<string>());
            Assert.Equal("cloud", cloud["location"]?.GetValue<string>()); // Cloud pseudo-target is always cloud
            Assert.True(cloud["url"] == null);
            Assert.False(cloud["enabled"]?.GetValue<bool>()); // availability flag false; cloud not configured
            Assert.Equal("Offline", cloud["liveness"]?.GetValue<string>()); // cloud models not configured
            Assert.Equal(0, cloud["inFlightRequests"]?.GetValue<int>());
            Assert.Equal("baseline", cloud["contextAllocation"]?.GetValue<string>());

            // statistics periods contain one entry per target plus the reserved "total" key.
            var today = json["statistics"]?["today"]!;
            var last7 = json["statistics"]?["last7Days"]!;
            Assert.NotNull(today["Local"]);
            Assert.NotNull(today["Cloud"]);
            Assert.NotNull(today["total"]);
            Assert.NotNull(last7["Local"]);
            Assert.NotNull(last7["Cloud"]);
            Assert.NotNull(last7["total"]);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_WithLivenessService_MapsRawStatesToProbingAndOffline()
    {
        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.GetEntries()).Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
        {
            [RoutingTarget.Local] = new(RoutingTarget.Local, TargetLivenessState.Unknown, null, DateTimeOffset.UtcNow),
            [new RoutingTarget("desktop")] = new(new RoutingTarget("desktop"), TargetLivenessState.Offline, "boom", DateTimeOffset.UtcNow)
        });

        var (app, client, _) = await CreateTestAppAsync(
            options: new OllamaRouterOptions
            {
                Targets =
                [
                    new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", MaxVramMB = 16384 },
                    new OllamaTargetOptions { Name = "desktop", Url = "http://127.0.0.1:11436", MaxVramMB = 16384 }
                ]
            },
            liveness: liveness.Object);
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            var fleet = json!["fleet"]!.AsArray();

            // Unknown state maps to "Probing"; Offline stays "Offline".
            Assert.Equal("Probing", fleet[0]!["liveness"]?.GetValue<string>());
            Assert.Equal("Offline", fleet[1]!["liveness"]?.GetValue<string>());

            // Cloud remains driven by cloudEnabled (not configured here).
            Assert.Equal("Offline", fleet[2]!["liveness"]?.GetValue<string>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_WithCloudOverflowDefaultModel_ReportsCloudOnline()
    {
        var (app, client, _) = await CreateTestAppAsync(
            options: new OllamaRouterOptions
            {
                Targets =
                [
                    new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", MaxVramMB = 16384 }
                ],
                CloudOverflow = new CloudOverflowOptions
                {
                    Enabled = true,
                    DefaultModel = "glm-5.3-flash:cloud"
                }
            });
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            var fleet = json!["fleet"]!.AsArray();

            var cloud = fleet.Single(f => f!["name"]?.GetValue<string>() == "Cloud");
            Assert.Equal("Online", cloud!["liveness"]?.GetValue<string>());
            Assert.True(json["cloudEnabled"]?.GetValue<bool>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_WithCloudOverflowDisabled_ReportsCloudOffline()
    {
        var (app, client, _) = await CreateTestAppAsync(
            options: new OllamaRouterOptions
            {
                Targets =
                [
                    new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", MaxVramMB = 16384 }
                ],
                CloudOverflow = new CloudOverflowOptions
                {
                    Enabled = false,
                    DefaultModel = "glm-5.3-flash:cloud"
                }
            });
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            var fleet = json!["fleet"]!.AsArray();

            var cloud = fleet.Single(f => f!["name"]?.GetValue<string>() == "Cloud");
            Assert.Equal("Offline", cloud!["liveness"]?.GetValue<string>());
            Assert.False(json["cloudEnabled"]?.GetValue<bool>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_WithoutLivenessService_FallsBackToCatalogCheck()
    {
        var (app, client, mocks) = await CreateTestAppAsync("http://127.0.0.1:11435");
        try
        {
            // No liveness service registered: catalog check drives the liveness.
            mocks.ModelCatalogCache.Setup(c => c.HasAnyModelsAsync("http://127.0.0.1:11435", It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            var fleet = json!["fleet"]!.AsArray();

            Assert.Equal("Online", fleet[0]!["liveness"]?.GetValue<string>()); // HasAnyModelsAsync == true
            Assert.Equal("Offline", fleet[1]!["liveness"]?.GetValue<string>()); // cloud not configured
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_WithExecutionTracker_ExposesActiveModelAndInFlightPerTarget()
    {
        var tracker = new Mock<ITargetExecutionTracker>();
        tracker.Setup(t => t.GetState(RoutingTarget.Local)).Returns(new TargetExecutionState(
            RoutingTarget.Local,
            InFlightRequests: 2,
            LoadedModels: [new TargetLoadedModel("llama3.2:latest", 4_000_000_000)],
            TotalVramBytes: 4_000_000_000,
            LastRefreshedAtUtc: DateTimeOffset.UtcNow,
            Stale: false));

        var (app, client, _) = await CreateTestAppAsync(executionTracker: tracker.Object);
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            var fleet = json!["fleet"]!.AsArray();

            Assert.Equal("llama3.2:latest", fleet[0]!["activeModel"]?.GetValue<string>());
            Assert.Equal(2, fleet[0]!["inFlightRequests"]?.GetValue<int>());

            // Cloud never has an active model or in-flight requests.
            Assert.True(fleet[1]!["activeModel"] == null);
            Assert.Equal(0, fleet[1]!["inFlightRequests"]?.GetValue<int>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_WithContextLeases_ShowsExpandedAndBaselineAllocations()
    {
        var shrinkManager = new ContextAutoShrinkManager();
        var now = DateTimeOffset.UtcNow;
        var dynamic = new DynamicContextOptions();

        // "Local" has an expanded lease (MoE two-bucket ladder, task #26: the pre-configured
        // baseline is 184,320 and the single extensible bucket is the native limit 262,144;
        // a 195,000-token prompt exceeds the baseline so the engine expands to 262,144).
        var moeCapability = new TargetModelCapability(
            RoutingTarget.Local.Value,
            ModelArchitectureKind.Moe,
            ArchitectureDetectionSource.MoeExpertCount,
            184_320,
            262_144,
            true,
            null,
            262_144);
        shrinkManager.Decide(
            RoutingTarget.Local,
            "llama3.2:latest",
            DynamicContextSizingEngine.Decide(dynamic, moeCapability, 195_000, null, 8_192, null),
            dynamic,
            195_000,
            now);

        var (app, client, _) = await CreateTestAppAsync(contextShrinkManager: shrinkManager);
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            var fleet = json!["fleet"]!.AsArray();

            // Expanded lease: "<bucket>k (expanded)".
            var localContext = fleet[0]!["contextAllocation"]?.GetValue<string>()!;
            Assert.EndsWith(" (expanded)", localContext);
            Assert.StartsWith("262.1k", localContext);

            // No leases: baseline implied ("-"). Cloud: always "baseline".
            // (The legacy synthesized fleet only has Local in this test app.)
            Assert.Equal("baseline", fleet[1]!["contextAllocation"]?.GetValue<string>());
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

            // Target availability must have been updated to disable the target named "Local".
            mocks.TargetAvailability.Verify(t => t.Update(It.Is<IReadOnlyDictionary<string, bool>>(f =>
                    f.ContainsKey("Local") && !f["Local"])), Times.Once);
            mocks.CatalogClient.Verify(c => c.StopRunningModelsAsync("http://127.0.0.1:11435", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostReclaimVram_WhenDisableTargetFalse_UnloadsModels_AndLeavesLocalTargetEnabled()
    {
        var (app, client, mocks) = await CreateTestAppAsync("http://127.0.0.1:11435");
        try
        {
            mocks.CatalogClient.Setup(c => c.StopRunningModelsAsync("http://127.0.0.1:11435", It.IsAny<CancellationToken>()))
                .ReturnsAsync(["llama3.2:latest"]);

            var response = await client.PostAsync("/monitor/reclaim-vram?disableTarget=false", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();

            Assert.NotNull(json);
            Assert.True(json["success"]?.GetValue<bool>());
            Assert.False(json["localDisabled"]?.GetValue<bool>());
            Assert.Equal(1, json["count"]?.GetValue<int>());

            // Target availability must NOT have been updated
            mocks.TargetAvailability.Verify(t => t.Update(It.IsAny<IReadOnlyDictionary<string, bool>>()), Times.Never);
            mocks.CatalogClient.Verify(c => c.StopRunningModelsAsync("http://127.0.0.1:11435", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostModelsUnload_DefaultLeavesLocalTargetEnabled_AndUnloadsRunningModels()
    {
        var (app, client, mocks) = await CreateTestAppAsync("http://127.0.0.1:11435");
        try
        {
            mocks.CatalogClient.Setup(c => c.StopRunningModelsAsync("http://127.0.0.1:11435", It.IsAny<CancellationToken>()))
                .ReturnsAsync(["qwen3:8b"]);

            var response = await client.PostAsync("/api/models/unload", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();

            Assert.NotNull(json);
            Assert.True(json["success"]?.GetValue<bool>());
            Assert.False(json["localDisabled"]?.GetValue<bool>());
            Assert.Equal(1, json["count"]?.GetValue<int>());

            // Local must NOT have been disabled
            mocks.TargetAvailability.Verify(t => t.Update(It.IsAny<IReadOnlyDictionary<string, bool>>()), Times.Never);
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
            var response = await client.PostAsync("/monitor/reclaim-vram", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();

            Assert.NotNull(json);
            Assert.False(json["success"]?.GetValue<bool>());
            Assert.True(json["localDisabled"]?.GetValue<bool>());

            // Still disabled the target named "Local" (skipped silently when absent from the fleet).
            mocks.TargetAvailability.Verify(t => t.Update(It.Is<IReadOnlyDictionary<string, bool>>(f =>
                    f.ContainsKey("Local") && !f["Local"])), Times.Once);
            mocks.CatalogClient.Verify(c => c.StopRunningModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitor_ReturnsHtmlWithFleetTableStylesAndScript()
    {
        var (app, client, _) = await CreateTestAppAsync();
        try
        {
            var response = await client.GetAsync("/monitor");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("fleetTable", html);
            Assert.Contains("fleetRows", html);
            Assert.Contains("lv-offline", html);
            Assert.Contains("lv-probing", html);
            Assert.Contains("lv-disabled", html);
            Assert.Contains("statusText = 'Offline'", html);
            Assert.Contains("statusText = 'Probing'", html);
            Assert.Contains("renderFleet", html);
            Assert.Contains("lv-offline", html);
            Assert.Contains("lv-probing", html);
            Assert.Contains("lv-disabled", html);
            Assert.Contains("statusText = 'Offline'", html);
            Assert.Contains("statusText = 'Probing'", html);
            Assert.Contains("renderFleet", html);
            // Location-based rendering: location-driven CSS classes and the JS lookup built
            // from data.fleet (t.location), not the legacy name-keyed targetIcons map.
            Assert.Contains(".target-local { color: #3f9eff; }", html);
            Assert.Contains(".target-remote { color: #34e0a1; }", html);
            Assert.Contains(".target-cloud { color: #c48bff; }", html);
            Assert.Contains("locationIcons", html);
            Assert.Contains("updateLocationByName", html);
            Assert.Contains("t.location", html);
            // The fleet-row span must derive its color class from the location lookup, not the raw name.
            Assert.Contains("span.className = 'target-' + targetColorClass(target);", html);
            Assert.DoesNotContain("const targetIcons", html);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_FleetLocationIsClassifiedPerUrl_AndOverrideWins()
    {
        var (app, client, _) = await CreateTestAppAsync(
            options: new OllamaRouterOptions
            {
                Targets =
                [
                    new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" },
                    new OllamaTargetOptions { Name = "desktop", Url = "http://192.168.1.25:11434" },
                    new OllamaTargetOptions { Name = "edge", Url = "http://example.com:11434" },
                    // Explicit override: a public URL classified as Remote instead of Cloud.
                    new OllamaTargetOptions { Name = "lan-relay", Url = "http://example.net:11434", Location = TargetLocation.Remote },
                    new OllamaTargetOptions { Name = "mDNS", Url = "http://aiserver.local:11434" }
                ]
            });
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            var fleet = json!["fleet"]!.AsArray();

            var byName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fleet)
            {
                byName[f!["name"]!.GetValue<string>()!] = (JsonObject)f!;
            }

            Assert.Equal("local", byName["Local"]["location"]?.GetValue<string>());
            Assert.Equal("remote", byName["desktop"]["location"]?.GetValue<string>());
            Assert.Equal("cloud", byName["edge"]["location"]?.GetValue<string>());
            Assert.Equal("remote", byName["lan-relay"]["location"]?.GetValue<string>()); // override wins
            Assert.Equal("remote", byName["mDNS"]["location"]?.GetValue<string>()); // .local hostname
            Assert.Equal("cloud", byName["Cloud"]["location"]?.GetValue<string>()); // pseudo-target always cloud
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
            var fleet = json["fleet"]!.AsArray();
            Assert.Equal("Online", fleet[0]!["liveness"]?.GetValue<string>());
            Assert.Equal("Offline", fleet[1]!["liveness"]?.GetValue<string>()); // cloud models not configured
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
    public async Task GetMonitorApi_WithPricing_ReturnsPerTargetSavings()
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

            var cost = json!["cost"]!;
            var perTarget = cost["perTargetSavings"]!.AsObject();

            // Local: (0.2 * 0.20) + (0.05 * 0.80) = 0.04 + 0.04 = 0.08
            Assert.Equal(0.08, perTarget["Local"]!.GetValue<double>());
            // Remote: (0.8 * 0.20) + (0.15 * 0.80) = 0.16 + 0.12 = 0.28
            Assert.Equal(0.28, perTarget["Remote"]!.GetValue<double>());
            // Cloud is excluded from the savings (it is an overflow cost).
            Assert.True(perTarget.ContainsKey("Cloud") == false);

            // Per-target values sum to the estimated savings total.
            var perTargetSum = perTarget.Select(pair => pair.Value!.GetValue<double>()).Sum();
            Assert.Equal(cost["estimatedSavings"]!.GetValue<double>(), perTargetSum, 4);
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
        // Per-target: Local 1,000,000 -> 1.0; Remote 1,000,000 -> 1.0; sum == total, Cloud excluded.
        Assert.Equal(1.0, result.PerTargetSavings["Local"]);
        Assert.Equal(1.0, result.PerTargetSavings["Remote"]);
        Assert.DoesNotContain("Cloud", result.PerTargetSavings.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(result.EstimatedSavings, result.PerTargetSavings.Values.Sum(), 4);
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
