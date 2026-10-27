using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OllamaRouter.Options;
using OllamaRouter.Services;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OllamaRouter.Tests.Services;

public class FleetAggregationServiceTests : IDisposable
{
    public const string DesktopAuthority = "127.0.0.1:11435";
    public const string ServerAuthority = "192.168.1.25:11434";
    public const string LaptopAuthority = "10.0.0.9:11434";

    public const string DesktopUrl = $"http://{DesktopAuthority}";
    public const string ServerUrl = $"http://{ServerAuthority}";
    public const string LaptopUrl = $"http://{LaptopAuthority}";

    private readonly OllamaRouterOptions _options = new()
    {
        Targets =
        [
            new OllamaTargetOptions { Name = "Desktop", Url = DesktopUrl },
            new OllamaTargetOptions { Name = "Server", Url = ServerUrl },
            new OllamaTargetOptions { Name = "Laptop", Url = LaptopUrl }
        ]
    };

    private readonly FakeOllamaHttpHandler _handler = new();
    private readonly HttpClient _httpClient;
    private readonly OllamaModelCatalogClient _catalogClient;
    private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());
    private readonly ModelCatalogCacheService _catalogCache;

    private readonly Mock<ITargetLivenessService> _liveness = new();
    private readonly Mock<ITargetAvailabilityService> _availability = new();

    public FleetAggregationServiceTests()
    {
        _httpClient = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(4) };
        _catalogClient = new OllamaModelCatalogClient(_httpClient);
        _catalogCache = new ModelCatalogCacheService(_catalogClient, _memoryCache, NullLogger<ModelCatalogCacheService>.Instance);

        _liveness
            .Setup(t => t.EnsureKnownAsync(It.IsAny<RoutingTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TargetLivenessState.Online);
        _liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(true);
        _liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>(), It.IsAny<TimeSpan>())).Returns(true);
        _liveness.SetupGet(t => t.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddMinutes(1));
        _liveness
            .Setup(t => t.GetFleetTargets())
            .Returns(new[] { new RoutingTarget("Desktop"), new RoutingTarget("Server"), new RoutingTarget("Laptop") });
        _liveness
            .Setup(t => t.GetEntries())
            .Returns(OnlineEntries());

        _availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);
    }

    public void Dispose() => _httpClient.Dispose();

    private static RoutingTarget Dt = new("Desktop");
    private static RoutingTarget Sv = new("Server");
    private static RoutingTarget Lp = new("Laptop");

    private static Dictionary<RoutingTarget, TargetLivenessEntry> OnlineEntries() => new()
    {
        [Dt] = new(Dt, TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
        [Sv] = new(Sv, TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
        [Lp] = new(Lp, TargetLivenessState.Online, null, DateTimeOffset.UtcNow)
    };

    private FleetAggregationService CreateService(int? perTargetTimeoutMs = null)
    {
        var options = _options;
        if (perTargetTimeoutMs is not null)
        {
            options = new OllamaRouterOptions { Targets = _options.Targets };
            options.Aggregation.PerTargetTimeoutMs = perTargetTimeoutMs.Value;
        }

        return new FleetAggregationService(
            MsOptions.Create(options),
            _catalogClient,
            _catalogCache,
            _liveness.Object,
            _availability.Object,
            _memoryCache,
            NullLogger<FleetAggregationService>.Instance);
    }

    private static string TagsBody(params (string Name, int Size)[] models)
    {
        var arr = new JsonArray();
        foreach (var (name, size) in models)
        {
            arr.Add(new JsonObject { ["name"] = name, ["model"] = name, ["size"] = size });
        }

        return new JsonObject { ["models"] = arr }.ToJsonString();
    }

    private void SeedTags(string authority, params (string Name, int Size)[] models)
    {
        _handler.ForAuthority(authority, new FakeOllamaHttpHandler.TargetBehavior
        {
            Responses = { ["GET /api/tags"] = TagsBody(models) }
        });
    }

    [Fact]
    public async Task GetTagsFleet_ReturnsUnionInConfigOrder_WithoutDuplicates()
    {
        SeedTags(DesktopAuthority, ("llama3", 100), ("shared", 1));
        SeedTags(ServerAuthority, ("qwen3", 200), ("shared", 999));
        SeedTags(LaptopAuthority, ("gemma3", 300));

        var service = CreateService();
        var merged = await service.GetTagsFleetAsync(null);

        var names = merged.Select(m => m?["name"]?.GetValue<string>()).ToList();
        Assert.Equal(new[] { "llama3", "shared", "qwen3", "gemma3" }, names);

        // First-encountered (higher-priority) target wins on the duplicate key.
        var shared = merged.Single(m => m?["name"]?.GetValue<string>() == "shared");
        Assert.Equal(1, shared["size"]!.GetValue<long>());
    }

    [Fact]
    public async Task GetTagsFleet_SlowDaemon_ServedFromCache_WithinBudget()
    {
        // Seed the Lp's stale slot by serving it fast once.
        SeedTags(DesktopAuthority, ("llama3", 100));
        SeedTags(ServerAuthority, ("qwen3", 200));
        SeedTags(LaptopAuthority, ("gemma3", 300));
        var service = CreateService();
        await service.GetTagsFleetAsync(null);

        // Now the Lp becomes a slow (asleep) daemon: it misses the 500 ms budget.
        _handler.Behavior(LaptopAuthority).Latency = TimeSpan.FromSeconds(3);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var merged = await service.GetTagsFleetAsync(null);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1500),
            $"Aggregation took {sw.Elapsed.TotalMilliseconds:F0} ms; a slow daemon must not block beyond the budget + margin.");

        var names = merged.Select(m => m?["name"]?.GetValue<string>()).ToList();
        // The stale Lp catalog is still merged in (stale-while-revalidate).
        Assert.Contains("gemma3", names);
        Assert.Contains("llama3", names);
        Assert.Contains("qwen3", names);
    }

    [Fact]
    public async Task GetTagsFleet_NeverSeenSlowDaemon_ContributesNothing_WithoutHanging()
    {
        SeedTags(DesktopAuthority, ("llama3", 100));
        SeedTags(ServerAuthority, ("qwen3", 200));
        _handler.ForAuthority(LaptopAuthority, new FakeOllamaHttpHandler.TargetBehavior { Hang = true });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var merged = await CreateService().GetTagsFleetAsync(null);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1500),
            $"Aggregation took {sw.Elapsed.TotalMilliseconds:F0} ms; an unresponsive daemon must not hang the response.");

        var names = merged.Select(m => m?["name"]?.GetValue<string>()).ToList();
        Assert.Equal(new[] { "llama3", "qwen3" }, names);
    }

    [Fact]
    public async Task GetPsFleet_MergesEveryTarget_WithRouterTargetAndOwnVram()
    {
        // The same model ("llama3") runs on BOTH Dt and Sv with different pids/VRAM.
        _handler.ForAuthority(DesktopAuthority, new FakeOllamaHttpHandler.TargetBehavior
        {
            Responses =
            {
                ["GET /api/ps"] = """{"models":[{"name":"llama3","pid":100,"size_vram":1000},{"name":"gemma3","pid":101,"size_vram":500}]}"""
            }
        });
        _handler.ForAuthority(ServerAuthority, new FakeOllamaHttpHandler.TargetBehavior
        {
            Responses =
            {
                ["GET /api/ps"] = """{"models":[{"name":"llama3","pid":900,"size_vram":4000},{"name":"qwen3","pid":901,"size_vram":2000}]}"""
            }
        });
        SeedTags(LaptopAuthority, ("asleep-model", 1)); // Lp contributes nothing live; no ps slot.

        var merged = await CreateService().GetPsFleetAsync();

        Assert.Equal(4, merged.Count);

        // No cross-target dedupe: llama3 appears twice, each with its own pid + VRAM + target.
        var llamaEntries = merged
            .Where(m => m?["name"]?.GetValue<string>() == "llama3")
            .Cast<JsonObject>()
            .ToList();
        Assert.Equal(2, llamaEntries.Count);
        var fromDesktop = llamaEntries.Single(e => e["pid"]!.GetValue<long>() == 100);
        var fromServer = llamaEntries.Single(e => e["pid"]!.GetValue<long>() == 900);
        Assert.Equal("Desktop", fromDesktop["router_target"]!.GetValue<string>());
        Assert.Equal(1000, fromDesktop["size_vram"]!.GetValue<long>());
        Assert.Equal("Server", fromServer["router_target"]!.GetValue<string>());
        Assert.Equal(4000, fromServer["size_vram"]!.GetValue<long>());

        // No invented field leaks; the internal marker is removed.
        Assert.DoesNotContain(merged, m => m?["__router_source_target"] is not null);
    }

    [Fact]
    public async Task GetTagsFleet_OfflineTarget_NeverContacted_ButStaleSlotStillContributes()
    {
        SeedTags(DesktopAuthority, ("llama3", 100));
        SeedTags(ServerAuthority, ("qwen3", 200));
        SeedTags(LaptopAuthority, ("gemma3", 300));

        // Populate the Lp's stale slot while it is online.
        await CreateService().GetTagsFleetAsync(null);

        // The Lp goes Offline: it must not be contacted, yet its cached catalog
        // (60 s stale slot) is still served.
        _liveness.Setup(t => t.IsOnline(Lp)).Returns(false);
        _liveness.Setup(t => t.IsOnline(Lp, It.IsAny<TimeSpan>())).Returns(false);
        _liveness.Setup(t => t.GetEntries()).Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
        {
            [Dt] = new(Dt, TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
            [Sv] = new(Sv, TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
            [Lp] = new(Lp, TargetLivenessState.Offline, "connection error", DateTimeOffset.UtcNow)
        });
        _handler.Behavior(LaptopAuthority).Latency = TimeSpan.FromSeconds(5);

        var before = _handler.TotalCalls(LaptopAuthority);
        var merged = await CreateService().GetTagsFleetAsync(null);
        var after = _handler.TotalCalls(LaptopAuthority);

        Assert.Equal(before, after); // Offline target is never contacted.

        var names = merged.Select(m => m?["name"]?.GetValue<string>()).ToList();
        Assert.Contains("gemma3", names); // Stale slot still contributes.
        Assert.Contains("llama3", names);
        Assert.Contains("qwen3", names);
    }

    [Fact]
    public async Task GetShowFleet_ModelOnTwoTargets_ReachesHighestPriority_ThenServesFromCache()
    {
        SeedTags(DesktopAuthority, ("llama3", 100));
        SeedTags(ServerAuthority, ("llama3", 200), ("qwen3", 10));
        SeedTags(LaptopAuthority, ("gemma3", 300));

        const string showBody = """{"modelfile":"# Modelfile","details":{"family":"llama"},"model_info":{"param.size":"8B"}}""";
        foreach (var authority in new[] { DesktopAuthority, ServerAuthority })
        {
            _handler.Behavior(authority).Responses["POST /api/show"] = showBody;
        }

        var service = CreateService();

        var result = await service.GetShowFleetAsync("llama3", pinned: null);
        Assert.NotNull(result);
        Assert.Equal("llama", result!["details"]?["family"]?.GetValue<string>());

        // Highest-priority (earliest config-order) hoster is Dt, even though Sv
        // also hosts the model: the /api/show request must reach Dt only.
        Assert.Equal(1, _handler.CountCalls(DesktopAuthority, "POST /api/show"));
        Assert.Equal(0, _handler.CountCalls(ServerAuthority, "POST /api/show"));

        // Second call within the 30 s window: served from memory cache, zero new show traffic.
        var result2 = await service.GetShowFleetAsync("llama3", pinned: null);
        Assert.NotNull(result2);
        Assert.Equal(1, _handler.CountCalls(DesktopAuthority, "POST /api/show"));
        Assert.Equal(0, _handler.CountCalls(ServerAuthority, "POST /api/show"));
    }

    [Fact]
    public async Task GetShowFleet_PinnedTarget_HonoredOverPriority()
    {
        SeedTags(DesktopAuthority, ("llama3", 100));
        SeedTags(ServerAuthority, ("llama3", 200));
        SeedTags(LaptopAuthority, ("gemma3", 300));

        const string serverShow = """{"details":{"family":"llama-on-server"}}""";
        _handler.Behavior(ServerAuthority).Responses["POST /api/show"] = serverShow;

        var result = await CreateService().GetShowFleetAsync("llama3", pinned: Sv);
        Assert.NotNull(result);
        Assert.Equal("llama-on-server", result!["details"]?["family"]?.GetValue<string>());
        Assert.Equal(1, _handler.CountCalls(ServerAuthority, "POST /api/show"));
        Assert.Equal(0, _handler.CountCalls(DesktopAuthority, "POST /api/show"));
    }

    [Fact]
    public async Task GetShowFleet_PinnedTargetOffline_FallsBackToPrimary()
    {
        SeedTags(DesktopAuthority, ("llama3", 100));
        SeedTags(ServerAuthority, ("llama3", 200));
        _handler.Behavior(DesktopAuthority).Responses["POST /api/show"] = """{"details":{"family":"desktop"}}""";

        // The pinned target (Sv) is offline: the request must reach the primary target.
        // There is no "Local"-named target, so the primary is the first enabled effective
        // target, i.e. Dt.
        _liveness.Setup(t => t.IsOnline(Sv)).Returns(false);
        _liveness.Setup(t => t.IsOnline(Sv, It.IsAny<TimeSpan>())).Returns(false);
        _liveness.Setup(t => t.EnsureKnownAsync(Sv, It.IsAny<CancellationToken>())).ReturnsAsync(TargetLivenessState.Offline);

        var result = await CreateService().GetShowFleetAsync("llama3", pinned: Sv);
        Assert.NotNull(result);
        Assert.Equal("desktop", result!["details"]?["family"]?.GetValue<string>());
        Assert.Equal(0, _handler.CountCalls(ServerAuthority, "POST /api/show"));
    }

    [Fact]
    public async Task GetShowFleet_NoHosterKnown_FallsBackToPrimary()
    {
        SeedTags(DesktopAuthority, ("gemma3", 300));
        SeedTags(ServerAuthority, ("qwen3", 200));
        SeedTags(LaptopAuthority, ("llama3", 100));
        _handler.Behavior(DesktopAuthority).Responses["POST /api/show"] = """{"details":{"family":"desktop"}}""";

        // "mystery" is not in any cached catalog; primary (first effective target) = Dt.
        var result = await CreateService().GetShowFleetAsync("mystery", pinned: null);
        Assert.NotNull(result);
        Assert.Equal("desktop", result!["details"]?["family"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetTagsFleet_PerTargetBudgetOption_IsHonored()
    {
        SeedTags(DesktopAuthority, ("llama3", 100));
        SeedTags(ServerAuthority, ("qwen3", 200));
        SeedTags(LaptopAuthority, ("gemma3", 300));

        // Populate the Lp's stale slot first (all fast).
        var service = CreateService();
        await service.GetTagsFleetAsync(null);

        // Now shrink the budget so the (now slow) Lp definitely misses it.
        _handler.Behavior(LaptopAuthority).Latency = TimeSpan.FromMilliseconds(400);
        var fastService = CreateService(perTargetTimeoutMs: 300);

        var merged = await fastService.GetTagsFleetAsync(null);
        var names = merged.Select(m => m?["name"]?.GetValue<string>()).ToList();
        Assert.Contains("gemma3", names); // Stale fallback after the lowered budget fired.
        Assert.Contains("llama3", names);
    }

    [Fact]
    public void GetFanOutTargets_ExcludesOfflineTargets()
    {
        _liveness.Setup(t => t.IsOnline(Lp)).Returns(false);
        _liveness.Setup(t => t.IsOnline(Lp, It.IsAny<TimeSpan>())).Returns(false);
        _liveness.Setup(t => t.GetEntries()).Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
        {
            [Dt] = new(Dt, TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
            [Sv] = new(Sv, TargetLivenessState.Online, null, DateTimeOffset.UtcNow),
            [Lp] = new(Lp, TargetLivenessState.Offline, "down", DateTimeOffset.UtcNow)
        });

        var fanOut = CreateService().GetFanOutTargets();
        Assert.Equal(new[] { "Desktop", "Server" }, fanOut.Select(t => t.Name));
    }

    [Fact]
    public void GetFanOutTargets_ToleratesProbeOlderThanDefaultTtl_WhenWithinTolerantWindow()
    {
        _liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>())).Returns(false);
        _liveness.Setup(t => t.IsOnline(It.IsAny<RoutingTarget>(), ITargetLivenessService.DefaultTolerantCacheTtl)).Returns(true);
        _liveness.Setup(t => t.GetEntries()).Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>
        {
            [Dt] = new(Dt, TargetLivenessState.Online, null, DateTimeOffset.UtcNow.AddSeconds(-6)),
            [Sv] = new(Sv, TargetLivenessState.Online, null, DateTimeOffset.UtcNow.AddSeconds(-6)),
            [Lp] = new(Lp, TargetLivenessState.Online, null, DateTimeOffset.UtcNow.AddSeconds(-6))
        });

        var fanOut = CreateService().GetFanOutTargets();
        Assert.Equal(new[] { "Desktop", "Server", "Laptop" }, fanOut.Select(t => t.Name));
    }
}
