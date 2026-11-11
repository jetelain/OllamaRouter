using OllamaRouter.Tests.TestHelpers;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
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
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.ContextSizing;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;

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
        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(new TestOptionsMonitor<OllamaRouterOptions>(options ?? new OllamaRouterOptions
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
            Assert.Equal("-", local["contextAllocation"]?.GetValue<string>()); // no loaded model

            var cloud = fleet[1]!;
            Assert.Equal("Cloud", cloud["name"]?.GetValue<string>());
            Assert.Equal("cloud", cloud["location"]?.GetValue<string>()); // Cloud pseudo-target is always cloud
            Assert.True(cloud["url"] == null);
            Assert.False(cloud["enabled"]?.GetValue<bool>()); // availability flag false; cloud not configured
            Assert.Equal("Offline", cloud["liveness"]?.GetValue<string>()); // cloud models not configured
            Assert.Equal(0, cloud["inFlightRequests"]?.GetValue<int>());
            Assert.Equal("-", cloud["contextAllocation"]?.GetValue<string>());

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
    public async Task GetMonitorApi_ResidencyVisibility_ExposesPolicy_ResidentModelsAndObservedKeepAlive()
    {
        // Task #45 (acceptance 3): monitor fleet rows carry keepAliveSeconds + keepAliveSource
        // + residentModels + activeKeepAliveSeconds with the same values/omission rules as the
        // fleet API (Cloud: policy fields absent, residentModels: []), source-serialized
        // through OllamaRouterJsonSerializerContext (the test app injects it).
        var options = new OllamaRouterOptions
        {
            PreloadDefaultKeepAliveSeconds = 450, // CUSTOM router default → "default" source echoes it
            Targets =
            [
                new OllamaTargetOptions { Name = "Desktop", Url = "http://127.0.0.1:11435", MaxVramMB = 16384 },
                new OllamaTargetOptions { Name = "Server", Url = "http://192.168.1.10:11434", MaxVramMB = 98304 },
                new OllamaTargetOptions { Name = "Pinned", Url = "http://127.0.0.2:11435", MaxVramMB = 8192, KeepAliveSeconds = -1 },
                new OllamaTargetOptions { Name = "Mystery", Url = "http://192.168.1.200:11434" } // unclassifiable
            ]
        };

        var tracker = new Mock<ITargetExecutionTracker>();
        var now = DateTimeOffset.UtcNow;
        tracker.Setup(t => t.GetState(It.IsAny<RoutingTarget>())).Returns<RoutingTarget>(target => target.Value switch
        {
            "Desktop" => new TargetExecutionState(
                (RoutingTarget)"Desktop", 1,
                [
                    new TargetLoadedModel("llama3:8b", 4_000_000_000, 4096, null, -1),
                    new TargetLoadedModel("qwen:2b", 2_000_000_000, 8192, null, 300)
                ],
                6_000_000_000, now, false),
            "Pinned" => new TargetExecutionState(
                (RoutingTarget)"Pinned", 0,
                [new TargetLoadedModel("phi3:mini", 2_500_000_000, 4096, null, 900)],
                2_500_000_000, now, false),
            "Mystery" => new TargetExecutionState(
                (RoutingTarget)"Mystery", 0,
                [new TargetLoadedModel("mistral:7b", 4_500_000_000)], // no daemon-reported keep_alive
                4_500_000_000, now, false),
            _ => null // Server: idle
        });

        var (app, client, _) = await CreateTestAppAsync(options: options, executionTracker: tracker.Object);
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            var fleet = json!["fleet"]!.AsArray();
            var rowByName = fleet.ToDictionary(r => r!["name"]!.GetValue<string>(), r => r!);

            // Desktop: class convention 900 / "class"; FULL two-model resident list; the FIRST
            // model's observed keep_alive (-1) echoed.
            var desktop = rowByName["Desktop"];
            Assert.Equal(900, desktop["keepAliveSeconds"]!.GetValue<int>());
            Assert.Equal("class", desktop["keepAliveSource"]!.GetValue<string>());
            Assert.Equal(["llama3:8b", "qwen:2b"], desktop["residentModels"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
            Assert.Equal("llama3:8b", desktop["activeModel"]!.GetValue<string>()); // residentModels[0] == activeModel
            Assert.Equal(-1, desktop["activeKeepAliveSeconds"]!.GetValue<int>());

            // Server: server class → the literal -1 (never omitted) / "class"; idle → [].
            var server = rowByName["Server"];
            Assert.Equal(-1, server["keepAliveSeconds"]!.GetValue<int>());
            Assert.Equal("class", server["keepAliveSource"]!.GetValue<string>());
            Assert.Empty(server["residentModels"]!.AsArray());
            Assert.Null(server["activeKeepAliveSeconds"]); // no resident model

            // Pinned: explicit override wins verbatim → "explicit"; observed (900) ≠ configured (-1).
            var pinned = rowByName["Pinned"];
            Assert.Equal(-1, pinned["keepAliveSeconds"]!.GetValue<int>());
            Assert.Equal("explicit", pinned["keepAliveSource"]!.GetValue<string>());
            Assert.Equal(["phi3:mini"], pinned["residentModels"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
            Assert.Equal(900, pinned["activeKeepAliveSeconds"]!.GetValue<int>());

            // Mystery: unclassifiable → CUSTOM router default 450 / "default"; no daemon
            // keep_alive reported → key omitted.
            var mystery = rowByName["Mystery"];
            Assert.Equal(450, mystery["keepAliveSeconds"]!.GetValue<int>());
            Assert.Equal("default", mystery["keepAliveSource"]!.GetValue<string>());
            Assert.Equal(["mistral:7b"], mystery["residentModels"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
            Assert.Null(mystery["activeKeepAliveSeconds"]);

            // Cloud: policy fields ABSENT (keys omitted), residentModels: [].
            var cloud = rowByName["Cloud"];
            Assert.Null(cloud["keepAliveSeconds"]);
            Assert.Null(cloud["keepAliveSource"]);
            Assert.Null(cloud["activeKeepAliveSeconds"]);
            Assert.NotNull(cloud["residentModels"]); // ALWAYS serialized (even for Cloud)
            Assert.Empty(cloud["residentModels"]!.AsArray());

            // Existing monitor fields stay: in-flight and context allocation untouched.
            Assert.Equal(1, desktop["inFlightRequests"]?.GetValue<int>());
            Assert.Equal("4k", desktop["contextAllocation"]?.GetValue<string>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_ResidencyVisibility_NoTracker_ResidentModelsIsEmptyArray_AndPolicyStillResolved()
    {
        // Task #45: with NO execution tracker registered the rows still serialize residentModels
        // as an empty ARRAY (never null, never omitted) and the configured policy is still
        // resolved (snapshot-only enrichment, never a 5xx / hang).
        var options = new OllamaRouterOptions
        {
            Targets = [new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", MaxVramMB = 16384 }]
        };

        var (app, client, _) = await CreateTestAppAsync(options: options);
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            var fleet = json!["fleet"]!.AsArray();

            var local = fleet[0]!;
            Assert.NotNull(local["residentModels"]);
            Assert.Empty(local["residentModels"]!.AsArray());
            Assert.Equal(900, local["keepAliveSeconds"]!.GetValue<int>());
            Assert.Equal("class", local["keepAliveSource"]!.GetValue<string>());
            Assert.Null(local["activeKeepAliveSeconds"]);

            var cloud = fleet[1]!;
            Assert.Empty(cloud["residentModels"]!.AsArray());
            Assert.Null(cloud["keepAliveSeconds"]);
            Assert.Null(cloud["keepAliveSource"]);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(4096, "4k")]
    [InlineData(8192, "8k")]
    [InlineData(32768, "33k")]
    [InlineData(4500, "5k")] // midpoint: AwayFromZero (banker's rounding would give "4k")
    [InlineData(128000, "128k")]
    [InlineData(131072, "131k")]
    [InlineData(1000000, "1.0M")]
    [InlineData(1310720, "1.3M")]
    [InlineData(512, "512")]
    public async Task GetMonitorApi_ActualContext_FormatsDaemonReportedContextLength(int tokens, string expected)
    {
        var tracker = new Mock<ITargetExecutionTracker>();
        tracker.Setup(t => t.GetState(RoutingTarget.Local)).Returns(new TargetExecutionState(
            RoutingTarget.Local,
            InFlightRequests: 0,
            LoadedModels: [new TargetLoadedModel("llama3.2:latest", 4_000_000_000, ContextLength: tokens)],
            TotalVramBytes: 4_000_000_000,
            LastRefreshedAtUtc: DateTimeOffset.UtcNow,
            Stale: false));

        // A live auto-shrink lease on the SAME target/model must be ignored: the monitor now
        // shows only the daemon-reported context size.
        var (app, client, _) = await CreateTestAppAsync(
            executionTracker: tracker.Object,
            contextShrinkManager: new ContextAutoShrinkManager());
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            var fleet = json!["fleet"]!.AsArray();

            Assert.Equal("llama3.2:latest", fleet[0]!["activeModel"]?.GetValue<string>());
            Assert.Equal(expected, fleet[0]!["contextAllocation"]?.GetValue<string>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_LoadedModelWithoutContextLength_ShowsDash_LeaseIgnored()
    {
        // Legacy daemon: a model is loaded but the daemon did not report context_length
        // (ContextLength null). An EXPANDED auto-shrink lease is present on the same
        // target/model but must NOT be used as a fallback — the row shows "-".
        var tracker = new Mock<ITargetExecutionTracker>();
        tracker.Setup(t => t.GetState(RoutingTarget.Local)).Returns(new TargetExecutionState(
            RoutingTarget.Local,
            InFlightRequests: 0,
            LoadedModels: [new TargetLoadedModel("llama3.2:latest", 4_000_000_000)],
            TotalVramBytes: 4_000_000_000,
            LastRefreshedAtUtc: DateTimeOffset.UtcNow,
            Stale: false));

        // Give the shrink manager a genuinely expanded lease (same setup as the pre-change
        // lease-allocation test) so the "-" below proves the lease is ignored, not absent.
        var shrinkManager = new ContextAutoShrinkManager();
        var now = DateTimeOffset.UtcNow;
        var dynamic = new DynamicContextOptions();
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

        var (app, client, _) = await CreateTestAppAsync(
            executionTracker: tracker.Object,
            contextShrinkManager: shrinkManager);
        try
        {
            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            var fleet = json!["fleet"]!.AsArray();

            Assert.Equal("llama3.2:latest", fleet[0]!["activeModel"]?.GetValue<string>());
            Assert.Equal("-", fleet[0]!["contextAllocation"]?.GetValue<string>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetMonitorApi_MultipleModels_ContextMatchesFirstLoadedModel()
    {
        // Row consistency: with two models loaded the context shown belongs to the FIRST
        // loaded model — the same entry that drives the activeModel column.
        var tracker = new Mock<ITargetExecutionTracker>();
        tracker.Setup(t => t.GetState(RoutingTarget.Local)).Returns(new TargetExecutionState(
            RoutingTarget.Local,
            InFlightRequests: 0,
            LoadedModels:
            [
                new TargetLoadedModel("first:latest", 2_000_000_000, ContextLength: 4096),
                new TargetLoadedModel("second:latest", 2_000_000_000, ContextLength: 32768)
            ],
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

            Assert.Equal("first:latest", fleet[0]!["activeModel"]?.GetValue<string>());
            Assert.Equal("4k", fleet[0]!["contextAllocation"]?.GetValue<string>());
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

    // Builds a single-Local-target (no Cloud) last7Days dictionary for the cost-math table.
    private static Dictionary<RoutingTarget, ActivityStatisticsTotals> LocalOnly(int requests, int input, int output, int cached)
    {
        var dict = new Dictionary<RoutingTarget, ActivityStatisticsTotals>
        {
            [RoutingTarget.Local] = new(requests, input, output, cached)
        };
        if (RoutingTarget.Cloud != RoutingTarget.Local)
        {
            dict[RoutingTarget.Cloud] = new(0, 0, 0, 0);
        }
        return dict;
    }

    [Fact]
    public void CalculateCostStatus_NoCachedTokens_SameAsPreChangeMath()
    {
        var pricing = new PricingOptions { PromptPricePerMillion = 0.20, CachedPromptPricePerMillion = 0.05, CompletionPricePerMillion = 0.80 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(LocalOnly(1, 1_000, 100, 0), pricing);

        Assert.NotNull(result);
        // (1000 * 0.20 + 100 * 0.80) / 1e6 = 0.00028 -> 0.0003
        Assert.Equal(0.0003, result.EstimatedSavings);
    }

    [Fact]
    public void CalculateCostStatus_CachedPriceSet_SpecExampleRoundsTo0002()
    {
        var pricing = new PricingOptions { PromptPricePerMillion = 0.20, CachedPromptPricePerMillion = 0.05, CompletionPricePerMillion = 0.80 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(LocalOnly(1, 1_000, 100, 800), pricing);

        Assert.NotNull(result);
        // (200*0.20 + 800*0.05 + 100*0.80)/1e6 = 0.00016 -> 0.0002 (4-dec rounding).
        Assert.Equal(0.0002, result.EstimatedSavings);
        Assert.Equal(0.0002, result.PerTargetSavings["Local"]);
    }

    [Fact]
    public void CalculateCostStatus_CachedPriceSet_ScaledExact016()
    {
        var pricing = new PricingOptions { PromptPricePerMillion = 0.20, CachedPromptPricePerMillion = 0.05, CompletionPricePerMillion = 0.80 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(LocalOnly(1, 1_000_000, 100_000, 800_000), pricing);

        Assert.NotNull(result);
        // (200000*0.20 + 800000*0.05 + 100000*0.80)/1e6 = exactly 0.16.
        Assert.Equal(0.16, result.EstimatedSavings);
    }

    [Fact]
    public void CalculateCostStatus_CachedPriceUnset_FallsBackToInputPrice()
    {
        var pricing = new PricingOptions { PromptPricePerMillion = 0.20, CompletionPricePerMillion = 0.80 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(LocalOnly(1, 1_000, 100, 800), pricing);

        Assert.NotNull(result);
        // All 1000 input tokens valued at the input price: (1000*0.20 + 100*0.80)/1e6 = 0.00028 -> 0.0003.
        Assert.Equal(0.0003, result.EstimatedSavings);
    }

    [Fact]
    public void CalculateCostStatus_FlatPriceOnly_CachedFallsBackToFlat()
    {
        var pricing = new PricingOptions { PricePerMillion = 1.00 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(LocalOnly(1, 1_000, 100, 800), pricing);

        Assert.NotNull(result);
        // (200 + 800 + 100) * 1.0 / 1e6 = 0.0011.
        Assert.Equal(0.0011, result.EstimatedSavings);
    }

    [Fact]
    public void CalculateCostStatus_AllZeroPricing_ReturnsZeroCostObject()
    {
        var pricing = new PricingOptions { PromptPricePerMillion = 0.0, CachedPromptPricePerMillion = 0.0, CompletionPricePerMillion = 0.0 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(LocalOnly(1, 1_000, 100, 800), pricing);

        // IsConfigured true (all rates present and >= 0) => cost object present with zeros.
        Assert.NotNull(result);
        Assert.Equal(0.0, result.EstimatedSavings);
        Assert.Equal(0.0, result.CloudOverflowCost);
    }

    [Fact]
    public void CalculateCostStatus_CachedExceedsInput_ClampsAndNeverNegative()
    {
        var pricing = new PricingOptions { PromptPricePerMillion = 0.20, CachedPromptPricePerMillion = 0.05, CompletionPricePerMillion = 0.80 };

        // Corrupt stats: input 100, cached 150 -> clamped to 100; value = 100*0.05/1e6 = 0.000005 -> 0.0.
        var result = MonitorEndpointsExtensions.CalculateCostStatus(LocalOnly(1, 100, 0, 150), pricing);

        Assert.NotNull(result);
        Assert.Equal(0.0, result.EstimatedSavings);
    }

    [Fact]
    public void CalculateCostStatus_CachedPrice_AppliesToCloudOverflowCost()
    {
        var last7Days = new Dictionary<RoutingTarget, ActivityStatisticsTotals>
        {
            [RoutingTarget.Cloud] = new(1, 1_000_000, 100_000, 800_000)
        };

        var pricing = new PricingOptions { PromptPricePerMillion = 0.20, CachedPromptPricePerMillion = 0.05, CompletionPricePerMillion = 0.80 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(last7Days, pricing);

        Assert.NotNull(result);
        // Cloud overflow cost also uses the cached split: exactly 0.16.
        Assert.Equal(0.16, result.CloudOverflowCost);
        Assert.Equal(0.0, result.EstimatedSavings);
    }

    // ------------------------------------------------------------------
    //  Task #84: electricity-cost / actual-savings math table.
    //  Rounding contract: electricity4 = round(raw, 4); cloudValue4 = round(TokenCost, 4);
    //  savings4 = round(cloudValue4 − electricity4, 4) (never clamped); totals = round(Σ, 4).
    // ------------------------------------------------------------------

    private static IReadOnlyList<OllamaTargetOptions> Fleet(params (string Name, double? Watts)[] targets) =>
        targets.Select(t => new OllamaTargetOptions { Name = t.Name, Url = "http://127.0.0.1:11435", PowerWatts = t.Watts }).ToList();

    private static Dictionary<RoutingTarget, ActivityStatisticsTotals> ElapsedOnly(RoutingTarget target, double elapsedMs)
    {
        var dict = new Dictionary<RoutingTarget, ActivityStatisticsTotals> { [target] = new(0, 0, 0, 0, elapsedMs) };
        if (target != RoutingTarget.Cloud)
        {
            dict[RoutingTarget.Cloud] = new(0, 0, 0, 0, 0);
        }
        return dict;
    }

    [Fact]
    public void CalculateCostStatus_Electricity_DefaultWatts_OneHour150WAt020()
    {
        // 150 W = 0.15 kW × 1 h × 0.20/kWh = 0.03 (electricity price unset ⇒ 0.20 default).
        var pricing = new PricingOptions { PricePerMillion = 1.0 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(
            ElapsedOnly(RoutingTarget.Local, 3_600_000), pricing, Fleet(("Local", null)));

        Assert.NotNull(result);
        Assert.Equal(0.03, result.PerTargetElectricityCost["Local"]);
        Assert.Equal(0.03, result.ElectricityCost);
        // No tokens: actual savings = 0 − 0.03 (negative, reported as-is).
        Assert.Equal(-0.03, result.PerTargetSavings["Local"]);
        Assert.Equal(-0.03, result.EstimatedSavings);
    }

    [Fact]
    public void CalculateCostStatus_Electricity_PerTargetOverrideWatts_OneHour130WAt020()
    {
        // 130 W = 0.13 kW × 1 h × 0.20/kWh = exactly 0.026 (the spec's reference value).
        var pricing = new PricingOptions { PricePerMillion = 1.0 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(
            ElapsedOnly(RoutingTarget.Local, 3_600_000), pricing, Fleet(("Local", 130)));

        Assert.NotNull(result);
        Assert.Equal(0.026, result.PerTargetElectricityCost["Local"]);
        Assert.Equal(0.026, result.ElectricityCost);
    }

    [Fact]
    public void CalculateCostStatus_Electricity_ExplicitElectricityPrice_Applied()
    {
        // 150 W × 1 h × 0.30/kWh = 0.045.
        var pricing = new PricingOptions { PricePerMillion = 1.0, ElectricityPricePerKwh = 0.30 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(
            ElapsedOnly(RoutingTarget.Local, 3_600_000), pricing, Fleet(("Local", null)));

        Assert.NotNull(result);
        Assert.Equal(0.045, result.PerTargetElectricityCost["Local"]);
    }

    [Fact]
    public void CalculateCostStatus_Electricity_PricingDefaultNodePowerWatts_AppliesWithoutOverride()
    {
        // Pricing default 400 W (no per-target override): 0.4 kW × 1 h × 0.20 = 0.08.
        var pricing = new PricingOptions { PricePerMillion = 1.0, DefaultNodePowerWatts = 400 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(
            ElapsedOnly(RoutingTarget.Local, 3_600_000), pricing, Fleet(("Local", null)));

        Assert.NotNull(result);
        Assert.Equal(0.08, result.PerTargetElectricityCost["Local"]);
    }

    [Fact]
    public void CalculateCostStatus_Electricity_NoTargetsList_UsesPricingDefaultWatts()
    {
        // targets == null (unit-test default): the electricity term resolves against the
        // pricing default chain (200 W here) instead of the fleet per-target overrides.
        var pricing = new PricingOptions { PricePerMillion = 1.0, DefaultNodePowerWatts = 200 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(
            ElapsedOnly(RoutingTarget.Local, 3_600_000), pricing);

        Assert.NotNull(result);
        Assert.Equal(0.04, result.PerTargetElectricityCost["Local"]);
    }

    [Fact]
    public void CalculateCostStatus_Electricity_TargetAbsentFromFleet_UsesDefaultWatts()
    {
        // "Remote" has 7-day history but is no longer in the fleet → the 150 W default
        // (the 130 W Local override must NOT leak across).
        var last7Days = new Dictionary<RoutingTarget, ActivityStatisticsTotals>
        {
            [RoutingTarget.Remote] = new(0, 0, 0, 0, 3_600_000)
        };
        var pricing = new PricingOptions { PricePerMillion = 1.0 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(last7Days, pricing, Fleet(("Local", 130)));

        Assert.NotNull(result);
        Assert.Equal(0.03, result.PerTargetElectricityCost["Remote"]);
    }

    [Fact]
    public void CalculateCostStatus_Electricity_CloudTargetExempt()
    {
        // Cloud's huge elapsed time is never counted; no "Cloud" key in perTargetElectricityCost.
        var last7Days = new Dictionary<RoutingTarget, ActivityStatisticsTotals>
        {
            [RoutingTarget.Cloud] = new(1, 100_000, 0, 0, 3_600_000),
            [RoutingTarget.Local] = new(0, 0, 0, 0, 0)
        };
        var pricing = new PricingOptions { PricePerMillion = 1.0 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(last7Days, pricing, Fleet(("Local", 130)));

        Assert.NotNull(result);
        Assert.Equal(0.0, result.ElectricityCost);
        Assert.Equal("Local", result.PerTargetElectricityCost.Keys.Single());
        Assert.False(result.PerTargetElectricityCost.ContainsKey("Cloud")); // OrdinalIgnoreCase keys
        Assert.Equal(0.0, result.EstimatedSavings);
        // The Cloud OVERFLOW cost is untouched by the electricity term: 100k × 1.0 = 0.1.
        Assert.Equal(0.1, result.CloudOverflowCost);
    }

    [Fact]
    public void CalculateCostStatus_Electricity_ZeroElapsedOrZeroWatts_CostsZero()
    {
        var pricing = new PricingOptions { PricePerMillion = 1.0 };

        var zeroElapsed = MonitorEndpointsExtensions.CalculateCostStatus(
            ElapsedOnly(RoutingTarget.Local, 0), pricing, Fleet(("Local", 400)));
        Assert.NotNull(zeroElapsed);
        Assert.Equal(0.0, zeroElapsed!.ElectricityCost);
        Assert.Equal(0.0, zeroElapsed.PerTargetSavings["Local"]);

        var zeroWatts = MonitorEndpointsExtensions.CalculateCostStatus(
            ElapsedOnly(RoutingTarget.Local, 3_600_000), pricing, Fleet(("Local", 0)));
        Assert.NotNull(zeroWatts);
        Assert.Equal(0.0, zeroWatts!.ElectricityCost); // 0 W = zero-consumption node
        Assert.Equal(0.0, zeroWatts.PerTargetElectricityCost["Local"]);
    }

    [Fact]
    public void CalculateCostStatus_ActualSavings_CloudValueMinusElectricity()
    {
        // Local: 1M input tokens @ 0.20/1M → cloudValue 0.20; electricity 150 W × 1 h × 0.20
        // = 0.03; savings = round(0.20 − 0.03, 4) = 0.17.
        var last7Days = new Dictionary<RoutingTarget, ActivityStatisticsTotals>
        {
            [RoutingTarget.Local] = new(1, 1_000_000, 0, 0, 3_600_000)
        };
        var pricing = new PricingOptions { PromptPricePerMillion = 0.20, CompletionPricePerMillion = 0.80 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(last7Days, pricing, Fleet(("Local", 150)));

        Assert.NotNull(result);
        Assert.Equal(0.17, result.PerTargetSavings["Local"]);
        Assert.Equal(0.17, result.EstimatedSavings);
        Assert.Equal(0.03, result.PerTargetElectricityCost["Local"]);
    }

    [Fact]
    public void CalculateCostStatus_ActualSavings_NegativeReportedAsIs_NeverClamped()
    {
        // Tiny cloud value: 100 output tokens @ 0.80/1M = 0.00008 → 0.0001; electricity
        // 150 W × 1 h × 0.20 = 0.03 → savings = round(0.0001 − 0.03, 4) = −0.0299.
        var last7Days = new Dictionary<RoutingTarget, ActivityStatisticsTotals>
        {
            [RoutingTarget.Local] = new(1, 0, 100, 0, 3_600_000)
        };
        var pricing = new PricingOptions { CompletionPricePerMillion = 0.80 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(last7Days, pricing, Fleet(("Local", null)));

        Assert.NotNull(result);
        Assert.Equal(-0.0299, result.PerTargetSavings["Local"]);
        Assert.Equal(-0.0299, result.EstimatedSavings);
    }

    [Fact]
    public void CalculateCostStatus_Totals_AreRoundedSumsOfPerTargetValues()
    {
        // Local 150 W × 1 h → 0.03; Remote 130 W × 1 h → 0.026; each also carries tokens.
        var last7Days = new Dictionary<RoutingTarget, ActivityStatisticsTotals>
        {
            [RoutingTarget.Local] = new(1, 1_000_000, 0, 0, 3_600_000),   // cloudValue 0.20
            [RoutingTarget.Remote] = new(1, 500_000, 0, 0, 3_600_000)    // cloudValue 0.10
        };
        var pricing = new PricingOptions { PromptPricePerMillion = 0.20, CompletionPricePerMillion = 0.80 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(
            last7Days, pricing, Fleet(("Local", 150), ("Remote", 130)));

        Assert.NotNull(result);
        // Per-target: Local 0.20 − 0.03 = 0.17; Remote 0.10 − 0.026 = 0.074.
        Assert.Equal(0.17, result.PerTargetSavings["Local"]);
        Assert.Equal(0.074, result.PerTargetSavings["Remote"]);
        // Totals = rounded sums of the per-target values.
        Assert.Equal(0.17 + 0.074, result.EstimatedSavings);
        Assert.Equal(Math.Round(0.03 + 0.026, 4), result.ElectricityCost);
        Assert.Equal(result.PerTargetSavings.Values.Sum(), result.EstimatedSavings, 4);
        Assert.Equal(result.PerTargetElectricityCost.Values.Sum(), result.ElectricityCost, 4);
    }

    [Fact]
    public void CalculateCostStatus_Electricity_TargetNamesMatchedCaseInsensitively()
    {
        // The fleet name "local" (lowercase) still matches the Local statistics key.
        var pricing = new PricingOptions { PricePerMillion = 1.0 };

        var result = MonitorEndpointsExtensions.CalculateCostStatus(
            ElapsedOnly(RoutingTarget.Local, 3_600_000), pricing, Fleet(("local", 130)));

        Assert.NotNull(result);
        Assert.Equal(0.026, result.PerTargetElectricityCost["Local"]);
    }

    [Fact]
    public async Task GetMonitorApi_WithPricingAndPowerConfig_ActualSavingsAndElectricity()
    {
        // Integration (mirrors GetMonitorApi_WithPricing_*): known elapsed times flow through
        // the full monitor pipeline — statistics carry elapsedMs per target and in total, and
        // the cost status subtracts the observed-elapsed electricity term (Cloud exempt).
        var stats = new ActivityStatisticsSnapshot(
            Today: new Dictionary<RoutingTarget, ActivityStatisticsTotals>
            {
                [RoutingTarget.Local] = new(2, 1_000_000, 0, 0, 3_600_000),
                [RoutingTarget.Remote] = new(1, 0, 0, 0, 0),
                [RoutingTarget.Cloud] = new(0, 0, 0, 0, 7_200_000)
            },
            TodayTotal: new(3, 1_000_000, 0, 0, 10_800_000),
            Last7Days: new Dictionary<RoutingTarget, ActivityStatisticsTotals>
            {
                [RoutingTarget.Local] = new(10, 1_000_000, 0, 0, 3_600_000),
                [RoutingTarget.Remote] = new(5, 0, 0, 0, 0),
                [RoutingTarget.Cloud] = new(2, 100_000, 0, 0, 7_200_000)
            },
            Last7DaysTotal: new(17, 1_100_000, 0, 0, 10_800_000));

        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", PowerWatts = 130 },
                new OllamaTargetOptions { Name = "Remote", Url = "http://192.168.1.2:11435" }
            ],
            Pricing = new PricingOptions
            {
                PromptPricePerMillion = 0.20,
                CompletionPricePerMillion = 0.80,
                DefaultNodePowerWatts = 150
            }
        };

        var (app, client, _) = await CreateTestAppAsync(options: options, statisticsSnapshot: stats);
        try
        {
            var response = await client.GetAsync("/monitor/api");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.NotNull(json);

            // elapsedMs is exposed per target AND in the reserved "total" keys.
            var statistics = (JsonObject)json!["statistics"]!;
            Assert.Equal(3_600_000, ((JsonObject)statistics["today"]!)!["Local"]!["elapsedMs"]?.GetValue<double>());
            Assert.Equal(0, ((JsonObject)statistics["last7Days"]!)!["Remote"]!["elapsedMs"]?.GetValue<double>());
            Assert.Equal(10_800_000, ((JsonObject)statistics["last7Days"]!)!["total"]!["elapsedMs"]?.GetValue<double>());

            var cost = (JsonObject)json!["cost"]!;
            // Local: cloudValue 1M × 0.20 = 0.2; electricity 130 W × 1 h × 0.20 = 0.026;
            // actual savings = 0.2 − 0.026 = 0.174. Remote: zeros. Cloud: EXEMPT.
            Assert.Equal(0.174, cost["estimatedSavings"]?.GetValue<double>());
            Assert.Equal(0.026, cost["electricityCost"]?.GetValue<double>());
            Assert.Equal(0.174, cost["perTargetSavings"]?["Local"]?.GetValue<double>());
            Assert.Equal(0.0, cost["perTargetSavings"]?["Remote"]?.GetValue<double>());
            var perTargetElectricity = (JsonObject)cost["perTargetElectricityCost"]!;
            Assert.Equal(0.026, perTargetElectricity["Local"]?.GetValue<double>());
            Assert.Equal(0.0, perTargetElectricity["Remote"]?.GetValue<double>());
            Assert.False(perTargetElectricity.TryGetPropertyValue("Cloud", out _)); // OrdinalIgnoreCase keys
            // The Cloud overflow cost is untouched by the electricity term: 100k × 0.20 = 0.02.
            Assert.Equal(0.02, cost["cloudOverflowCost"]?.GetValue<double>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
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

    [Fact]
    public async Task GetMonitorApi_InProgressAndRecentRequests_IncludeReasoningSessionAndAgent_AndOmitNulls()
    {
        var inProgress = new List<InProgressRequest>
        {
            new(Guid.NewGuid(), DateTimeOffset.UtcNow, RoutingTarget.Local, "llama3", 42, "high", "s1", "a2")
        };
        var requests = new List<ActivityLogEntry>
        {
            new(DateTimeOffset.UtcNow, RoutingTarget.Local, "llama3", 42, 40, 100, 1500, 200, true, 38, "true", "s1", null)
        };

        var (app, client, mocks) = await CreateTestAppAsync();
        try
        {
            mocks.ActivityMonitor.Setup(m => m.GetSnapshot())
                .Returns(new ActivityMonitorSnapshot(new Dictionary<RoutingTarget, bool>(), inProgress, requests));

            var response = await client.GetAsync("/monitor/api");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.NotNull(json);

            var ip = Assert.Single(json["inProgress"]!.AsArray());
            Assert.Equal("high", ip?["reasoningEffort"]?.GetValue<string>());
            Assert.Equal("s1", ip?["sessionId"]?.GetValue<string>());
            Assert.Equal("a2", ip?["agentId"]?.GetValue<string>());

            var req = Assert.Single(json["requests"]!.AsArray());
            Assert.NotNull(req);
            Assert.Equal("true", req["reasoningEffort"]?.GetValue<string>());
            Assert.Equal("s1", req["sessionId"]?.GetValue<string>());
            Assert.Null(req["agentId"]); // null value ⇒ property omitted from JSON
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
