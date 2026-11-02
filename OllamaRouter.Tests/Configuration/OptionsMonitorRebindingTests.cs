using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Configuration;
using OllamaRouter.Endpoints;
using OllamaRouter.Extensions;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Configuration;

/// <summary>
/// Task #24 acceptance coverage: every consumer reads <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c>
/// so a configuration change (unit-level <c>TestOptionsMonitor.SetValue</c> or a real on-disk
/// <c>config/config.json</c> reload) changes service/endpoint behavior WITHOUT restarting the
/// DI container, and the <see cref="OllamaRouterOptionsNormalizer"/> re-applies the bind-time
/// clamps on every bind/rebind.
/// </summary>
public class OptionsMonitorRebindingTests
{
    private const string DesktopUrl = "http://desktop:11434";

    // ==================================================================
    // 1. OllamaRouterOptionsNormalizer unit tests
    // ==================================================================

    [Fact]
    public void Normalizer_PostConfigure_ClampsNegativeValues_IsIdempotent()
    {
        var normalizer = new OllamaRouterOptionsNormalizer();
        var options = new OllamaRouterOptions
        {
            ContextFitMarginTokens = -5,
            DynamicContext = new DynamicContextOptions
            {
                AutoShrinkCooldownTurns = -1,
                AutoShrinkIdleTimeoutSeconds = -2
            },
            Recording = new RecordingOptions
            {
                TokenMetricsCsv = new TokenMetricsCsvOptions { MaxFiles = -9 }
            }
        };

        normalizer.PostConfigure(null, options);

        Assert.Equal(0, options.ContextFitMarginTokens);
        Assert.Equal(3, options.DynamicContext.AutoShrinkCooldownTurns);
        Assert.Equal(60, options.DynamicContext.AutoShrinkIdleTimeoutSeconds);
        Assert.Equal(0, options.Recording!.TokenMetricsCsv!.MaxFiles);

        // Second pass (a reload rebind): idempotent, nothing further changes.
        normalizer.PostConfigure(null, options);
        Assert.Equal(0, options.ContextFitMarginTokens);
        Assert.Equal(3, options.DynamicContext.AutoShrinkCooldownTurns);
        Assert.Equal(60, options.DynamicContext.AutoShrinkIdleTimeoutSeconds);
        Assert.Equal(0, options.Recording.TokenMetricsCsv.MaxFiles);
    }

    [Fact]
    public void OptionsInfrastructure_BindsSectionAndNormalizes_OnFirstAccess()
    {
        // Bare ServiceCollection + AddOptions() + Configure<T>(section) + the normalizer —
        // exactly what the configuration-driven AddOllamaRouter overload registers.
        var services = new ServiceCollection();
        services.AddOptions();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OllamaRouter:ContextFitMarginTokens"] = "-10",
            ["OllamaRouter:MaxConsecutiveFailures"] = "5",
            ["OllamaRouter:DynamicContext:AutoShrinkCooldownTurns"] = "-1"
        }).Build();
        services.Configure<OllamaRouterOptions>(configuration.GetSection(OllamaRouterOptions.SectionName));
        services.AddSingleton<IPostConfigureOptions<OllamaRouterOptions>, OllamaRouterOptionsNormalizer>();
        using var provider = services.BuildServiceProvider();

        var value = provider.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>().CurrentValue;

        Assert.Equal(5, value.MaxConsecutiveFailures);
        Assert.Equal(0, value.ContextFitMarginTokens);      // clamped by the normalizer
        Assert.Equal(3, value.DynamicContext.AutoShrinkCooldownTurns); // clamped by the normalizer
    }

    // ==================================================================
    // 2. Registration wiring (both overloads)
    // ==================================================================

    [Fact]
    public void InstanceOverload_ResolvesLiveByReferenceMonitor_SameInstance_MutationsObserved()
    {
        var services = new ServiceCollection();
        var options = new OllamaRouterOptions { MaxConsecutiveFailures = 3 };
        services.AddOllamaRouter(options, stateDirectory: Path.Combine(Path.GetTempPath(), $"t24-{Guid.NewGuid():N}"), enableTui: false);
        using var provider = services.BuildServiceProvider();

        var monitor = provider.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>();

        Assert.Same(options, monitor.CurrentValue);
        options.MaxConsecutiveFailures = 9; // live-by-reference: the same instance is observed
        Assert.Equal(9, monitor.CurrentValue.MaxConsecutiveFailures);
    }

    [Fact]
    public void InstanceOverload_TryAdd_CallerRegisteredMonitorWins()
    {
        var services = new ServiceCollection();
        var options = new OllamaRouterOptions { MaxConsecutiveFailures = 3 };
        var callerMonitor = new TestOptionsMonitor<OllamaRouterOptions>(options);
        services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(callerMonitor);
        services.AddOllamaRouter(options, stateDirectory: Path.Combine(Path.GetTempPath(), $"t24-{Guid.NewGuid():N}"), enableTui: false);
        using var provider = services.BuildServiceProvider();

        // The TryAddSingleton<IOptionsMonitor> in the instance overload must not shadow the
        // caller-registered TestOptionsMonitor.
        Assert.Same(callerMonitor, provider.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>());
    }

    // ==================================================================
    // 3. Host-based real file reload (layered factory appsettings.json +
    //    user config/config.json, reloadOnChange)
    // ==================================================================

    [Fact]
    public async Task Host_FileReload_RebindsMonitor_AndReappliesNormalizer()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"t24-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(contentRoot, "config"));
        try
        {
            // Factory layer: read-only defaults (the router's real appsettings.json shape, trimmed).
            File.WriteAllText(
                Path.Combine(contentRoot, "appsettings.json"),
                """
                {
                  "OllamaRouter": {
                    "MaxConsecutiveFailures": 0,
                    "ContextFitMarginTokens": 5000,
                    "TerminalMonitoring": { "Enabled": false },
                    "Targets": [ { "Name": "Local", "Url": "http://127.0.0.1:11435" } ]
                  }
                }
                """);

            // User layer: overrides the factory values.
            File.WriteAllText(
                Path.Combine(contentRoot, "config", "config.json"),
                """
                {
                  "OllamaRouter": {
                    "MaxConsecutiveFailures": 7,
                    "CloudOverflow": { "DefaultModel": "initial-cloud-model" }
                  }
                }
                """);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = "OllamaRouter",
                ContentRootPath = contentRoot,
                Args = Array.Empty<string>()
            });
            var status = RouterConfigurationSources.AddUserConfiguration(builder.Configuration, contentRoot);
            Assert.Equal(UserConfigLoadStatus.Loaded, status);
            builder.Services.AddOllamaRouter(builder.Configuration, stateDirectory: contentRoot, enableTui: false);
            var app = builder.Build();
            using var _ = app;

            var monitor = app.Services.GetRequiredService<IOptionsMonitor<OllamaRouterOptions>>();

            // Layered INITIAL value: the user layer wins over the factory layer.
            var initial = monitor.CurrentValue;
            Assert.Equal(7, initial.MaxConsecutiveFailures);
            Assert.Equal(5000, initial.ContextFitMarginTokens); // factory value (not overridden)
            Assert.Equal("initial-cloud-model", initial.CloudOverflow.DefaultModel);

            // Wait (bounded) for the reload-driven rebind via OnChange.
            var rebindTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = monitor.OnChange(_ => rebindTcs.TrySetResult());

            // Rewrite the user file: a lower failure threshold, a NEGATIVE context margin
            // (must be re-clamped to 0 by the normalizer on rebind) and a new cloud model.
            var updated = """
                {
                  "OllamaRouter": {
                    "MaxConsecutiveFailures": 3,
                    "ContextFitMarginTokens": -100,
                    "CloudOverflow": { "DefaultModel": "reloaded-cloud-model" }
                  }
                }
                """;
            var tempFile = Path.Combine(contentRoot, "config", "config.json.tmp");
            File.WriteAllText(tempFile, updated);
            File.Move(tempFile, Path.Combine(contentRoot, "config", "config.json"), overwrite: true);

            var completed = await Task.WhenAny(rebindTcs.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.True(completed == rebindTcs.Task, "the reloadOnChange change token must fire within 20 s");

            var reloaded = monitor.CurrentValue;
            Assert.Equal(3, reloaded.MaxConsecutiveFailures);
            Assert.Equal(0, reloaded.ContextFitMarginTokens); // normalizer re-applied on the reload
            Assert.Equal("reloaded-cloud-model", reloaded.CloudOverflow.DefaultModel);
        }
        finally
        {
            try
            {
                Directory.Delete(contentRoot, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    // ==================================================================
    // 4. Service live rebinding (no DI restart)
    // ==================================================================

    [Fact]
    public async Task CloudRoutingTargetHandler_LiveRebind_FlipsAvailabilityWithoutReconstruction()
    {
        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(a => a.IsEnabled(RoutingTarget.Cloud)).Returns(true);
        var cache = new Mock<IModelCatalogCacheService>();

        var monitor = new TestOptionsMonitor<OllamaRouterOptions>(
            new OllamaRouterOptions { CloudOverflow = new CloudOverflowOptions { Enabled = false, DefaultModel = "gpt-x" } });
        var handler = new CloudRoutingTargetHandler(availability.Object, monitor);

        var context = new RoutingContext(10, "mystery", "mystery", null, cache.Object);
        Assert.False(handler.IsAvailable());
        Assert.False(await handler.CanProcessAsync(context));

        // Live flip of CloudOverflow.Enabled + default model — no handler reconstruction.
        monitor.SetValue(new OllamaRouterOptions { CloudOverflow = new CloudOverflowOptions { Enabled = true, DefaultModel = "gpt-x" } });

        Assert.True(handler.IsAvailable());
        Assert.True(await handler.CanProcessAsync(context));
    }

    [Fact]
    public async Task RoutingDecisionService_LiveRebind_TargetDisabledChangesDecision()
    {
        var options = new TestOptionsMonitor<OllamaRouterOptions>(
            new OllamaRouterOptions { Targets = [Target("Desktop")] });
        options.CurrentValue.Models["mystery"] = new ModelThresholds();

        var cache = new Mock<IModelCatalogCacheService>();
        cache.Setup(c => c.ModelExistsAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        cache.Setup(c => c.GetCurrentDigestAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");

        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        liveness.Setup(l => l.ServiceStartTimeUtc).Returns(DateTimeOffset.MinValue);
        liveness.Setup(l => l.GetEntries()).Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>());

        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(a => a.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);

        var tracker = new Mock<ITargetExecutionTracker>();
        tracker.Setup(t => t.GetState(It.IsAny<RoutingTarget>())).Returns((TargetExecutionState?)null);
        tracker.Setup(t => t.GetAllStates()).Returns((System.Collections.Generic.IReadOnlyList<TargetExecutionState>)Array.Empty<TargetExecutionState>());

        var cloudHandler = new CloudRoutingTargetHandler(availability.Object); // no options: Cloud never resolves a model here
        var sut = new RoutingDecisionService(
            options,
            cache.Object,
            liveness.Object,
            availability.Object,
            tracker.Object,
            null, // no capability engine (permissive path)
            cloudHandler,
            NullLogger<RoutingDecisionService>.Instance,
            new TargetFailureStreakService(),
            null);

        // Initial: the single online target with the configured model wins.
        var winner = await sut.DecideAsync(1_000, "mystery");
        Assert.Equal(new RoutingTarget("Desktop"), winner);

        // Live rebind: the SAME target is now admin-disabled in config → no target qualifies
        // (no Cloud model resolvable) → the decision flips to NoAvailableTarget, with the
        // service instance and DI container untouched.
        var disabledOptions = new OllamaRouterOptions
        {
            Targets = [new OllamaTargetOptions { Name = "Desktop", Url = DesktopUrl, MaxVramMB = 16384, MaxParallel = 1, Enabled = false }]
        };
        disabledOptions.Models["mystery"] = new ModelThresholds();
        options.SetValue(disabledOptions);

        await Assert.ThrowsAsync<NoAvailableTargetException>(() => sut.DecideAsync(1_000, "mystery"));
    }

    private static OllamaTargetOptions Target(string name, bool enabled = true) =>
        new() { Name = name, Url = DesktopUrl, MaxVramMB = 16384, MaxParallel = 1, Enabled = enabled };

    // ==================================================================
    // 5. Endpoint output reflects a monitor change without host restart
    // ==================================================================

    [Fact]
    public async Task MonitorEndpoint_ReflectsMonitorChange_WithoutHostRestart()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(opt =>
        {
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
        });

        var monitor = new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions());
        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(monitor);

        var activityMonitor = new Mock<IActivityMonitorService>();
        activityMonitor.Setup(m => m.GetSnapshot())
            .Returns(new ActivityMonitorSnapshot(new Dictionary<RoutingTarget, bool>(), [], []));
        var activityStatistics = new Mock<IActivityStatisticsService>();
        activityStatistics.Setup(s => s.GetSnapshot())
            .Returns(new ActivityStatisticsSnapshot(
                new Dictionary<RoutingTarget, ActivityStatisticsTotals>(),
                new ActivityStatisticsTotals(0, 0, 0),
                new Dictionary<RoutingTarget, ActivityStatisticsTotals>(),
                new ActivityStatisticsTotals(0, 0, 0)));
        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.GetSnapshot())
            .Returns(new TargetAvailabilitySnapshot(new Dictionary<RoutingTarget, bool> { [RoutingTarget.Cloud] = true }));
        var catalogCache = new Mock<IModelCatalogCacheService>();

        builder.Services.AddSingleton(activityMonitor.Object);
        builder.Services.AddSingleton(activityStatistics.Object);
        builder.Services.AddSingleton(availability.Object);
        builder.Services.AddSingleton(catalogCache.Object);

        var app = builder.Build();
        app.MapOllamaMonitorEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        try
        {
            Assert.False((bool?)ReadCloudEnabled(await GetMonitorApiAsync(client)));

            // Live flip: the /monitor/api cloud-enabled flag must change on the NEXT request
            // without restarting the host or the DI container.
            monitor.SetValue(new OllamaRouterOptions
            {
                CloudOverflow = new CloudOverflowOptions { Enabled = true, DefaultModel = "gpt-x" }
            });

            Assert.True((bool?)ReadCloudEnabled(await GetMonitorApiAsync(client)));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<string> GetMonitorApiAsync(HttpClient client) =>
        await client.GetStringAsync("/monitor/api");

    private static JsonNode? ReadCloudEnabled(string body)
    {
        var root = JsonNode.Parse(body);
        return root?["cloudEnabled"];
    }
}
