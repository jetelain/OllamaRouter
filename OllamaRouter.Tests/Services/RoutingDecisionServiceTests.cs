using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// Tests for the two-stage hot-model affinity scoring pipeline (Phase 3, item 3):
/// Stage-1 candidate qualification + Stage-2 calibrated scoring
/// (Affinity +100, Hardware +20, Eviction -80, Concurrency (InFlight×40)+(cliff?100:0)),
/// declaration-order tie-break, and the Cloud fixed-score-0 terminal fallback.
/// </summary>
public class RoutingDecisionServiceTests
{
    private const string DesktopUrl = "http://desktop:11434";
    private const string LaptopUrl = "http://laptop:11434";
    private const string ServerUrl = "http://server:11434";

    // Fleet fixture (declaration order matters — Desktop first, then Laptop, then Server).
    private static readonly OllamaTargetOptions Desktop = new() { Name = "Desktop", Url = DesktopUrl, HardwareHint = "16gb", MaxParallel = 1 };
    private static readonly OllamaTargetOptions Laptop = new() { Name = "Laptop", Url = LaptopUrl, HardwareHint = "8gb", MaxParallel = 1 };
    private static readonly OllamaTargetOptions Server = new() { Name = "Server", Url = ServerUrl, HardwareHint = "96gb", MaxParallel = 2 };

    private static readonly RoutingTarget TDesktop = new("Desktop");
    private static readonly RoutingTarget TLaptop = new("Laptop");
    private static readonly RoutingTarget TServer = new("Server");

    // ------------------------------------------------------------------
    // Test harness
    // ------------------------------------------------------------------

    private sealed class Harness
    {
        public required RoutingDecisionService SUT { get; init; }
        public List<RoutingCandidateEvaluation> Evaluations { get; } = new();
        /// <summary>
        /// Mutable per-turn execution-state snapshot store (multi-turn simulations mutate
        /// this between turns; the tracker mock reads it live on every GetState call).
        /// </summary>
        public required Dictionary<RoutingTarget, TargetExecutionState> States { get; init; }

        public Task<RoutingTarget> Decide(int tokens, string model, int? numCtx = null)
            => SUT.DecideWithEvaluationsAsync(tokens, model, numCtx, e => Evaluations.Add(e));

        public Task<RoutingTarget> Decide(int tokens, string model, string sessionId)
            => SUT.DecideWithEvaluationsAsync(tokens, model, null, e => Evaluations.Add(e), sessionId: sessionId);

        public RoutingCandidateEvaluation? Eval(RoutingTarget t) => Evaluations.LastOrDefault(e => e.Target == t);
        }

    private sealed class MetaMap
    {
        private readonly Dictionary<string, (JsonObject Show, long SizeBytes)> _entries = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string url, string model, JsonObject show, long sizeBytes)
            => _entries[$"{url}|{model}"] = (show, sizeBytes);

        public JsonArray BuildTags(string url)
        {
            var array = new JsonArray();
            foreach (var (key, value) in _entries)
            {
                var idx = key.IndexOf('|');
                if (!key[..idx].Equals(url, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var model = key[(idx + 1)..];
                array.Add(new JsonObject { ["name"] = model, ["size"] = (JsonNode)(value.SizeBytes) });
            }

            return array;
        }

        public JsonObject? Show(string url, string model)
            => _entries.TryGetValue($"{url}|{model}", out var v) ? v.Show : null;
    }

    private static (Harness Harness, MetaMap Meta) Build(
        OllamaRouterOptions options,
        bool withEngine,
        HashSet<string> catalogModels,          // "url|model" entries present in the catalog
        HashSet<string> onlineTargets,          // target names that are Online
        Dictionary<RoutingTarget, TargetExecutionState>? states = null,
        HashSet<RoutingTarget>? disabled = null,
        MetaMap? meta = null,
        Mock<ITargetLivenessService>? livenessOverride = null,
        Func<RoutingTarget, int>? failureStreaks = null,
        ISessionAffinityService? sessionAffinity = null)
    {
        meta ??= new MetaMap();
        var metaMap = meta;
        var disabledSet = disabled ?? new HashSet<RoutingTarget>();
        var cacheService = new Mock<IModelCatalogCacheService>();
        cacheService
            .Setup(c => c.ModelExistsAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string? url, string model, CancellationToken _) =>
                Task.FromResult(catalogModels.Contains($"{url}|{model}")));
        cacheService
            .Setup(c => c.GetCurrentDigestAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(withEngine ? "digest-xyz" : "");

        Mock<ITargetLivenessService> liveness;
        if (livenessOverride is not null)
        {
            liveness = livenessOverride;
        }
        else
        {
            liveness = new Mock<ITargetLivenessService>();
            liveness.Setup(l => l.IsOnline(It.IsAny<RoutingTarget>()))
                .Returns((RoutingTarget t) => onlineTargets.Contains(t.Value));
            liveness.Setup(l => l.ServiceStartTimeUtc).Returns(DateTimeOffset.MinValue); // no startup grace
            liveness.Setup(l => l.GetEntries()).Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>());
        }

        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(a => a.IsEnabled(It.IsAny<RoutingTarget>()))
            .Returns((RoutingTarget t) => !disabledSet.Contains(t));

        var statesStore = new Dictionary<RoutingTarget, TargetExecutionState>(states ?? new Dictionary<RoutingTarget, TargetExecutionState>());
        var tracker = new Mock<ITargetExecutionTracker>();
        tracker.Setup(t => t.GetState(It.IsAny<RoutingTarget>()))
            .Returns((RoutingTarget t) => statesStore.TryGetValue(t, out var s) ? s : null);
        tracker.Setup(t => t.GetAllStates()).Returns(() => statesStore.Values.ToList());

        // The decision service consumes the per-target failure streak (task #8): a real
        // streak service is minted per harness and fed from the per-target streak function.
        var streakTracker = new TargetFailureStreakService();
        if (failureStreaks is not null)
        {
            var tracked = new HashSet<RoutingTarget>();
            tracked.UnionWith(onlineTargets.Select(n => new RoutingTarget(n)));
            tracked.Add(RoutingTarget.Cloud);
            foreach (var t in tracked)
            {
                var count = failureStreaks(t);
                for (var i = 0; i < count; i++)
                {
                    streakTracker.RecordFailure(t);
                }
            }
        }

        ModelCapabilityEngine? engine = null;
        if (withEngine)
        {
            var catalogClient = new Mock<IOllamaModelCatalogClient>();
            catalogClient
                .Setup(c => c.GetTagsWithDigestAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns((string? url, CancellationToken _) =>
                    Task.FromResult((url is null ? new JsonArray() : metaMap.BuildTags(url), "digest-xyz")));
            catalogClient
                .Setup(c => c.GetShowAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string? url, string model, CancellationToken _) =>
                {
                    JsonObject? show = metaMap.Show(url ?? "", model) ?? new JsonObject();
                    return Task.FromResult(show);
                });

            engine = new ModelCapabilityEngine(
                catalogClient.Object,
                new MemoryCache(new MemoryCacheOptions()),
                NullLogger<ModelCapabilityEngine>.Instance,
                cacheService.Object);
        }

        var cloudHandler = new CloudRoutingTargetHandler(availability.Object);
        var sut = new RoutingDecisionService(
            Microsoft.Extensions.Options.Options.Create(options),
            cacheService.Object,
            liveness.Object,
            availability.Object,
            tracker.Object,
            engine,
            cloudHandler,
            NullLogger<RoutingDecisionService>.Instance,
            streakTracker,
            sessionAffinity);

        return (new Harness { SUT = sut, States = statesStore }, meta);
        }

    // ---- option / metadata builders ----

    private static OllamaRouterOptions FleetOptions(string model, string? architecture = null, string? cloudModel = null)
    {
        var options = new OllamaRouterOptions
        {
            Targets = new List<OllamaTargetOptions> { Desktop, Laptop, Server },
            Models = new Dictionary<string, ModelThresholds>
            {
                [model] = new ModelThresholds { Architecture = architecture, CloudModel = cloudModel }
            }
        };
        return options;
    }

    private static TargetExecutionState State(
        RoutingTarget t,
        int inFlight,
        params (string Name, long Size)[] loaded)
    {
        var models = loaded.Select(m => new TargetLoadedModel(m.Name, m.Size)).ToList();
        return new TargetExecutionState(t, inFlight, models, models.Sum(m => (long)m.SizeVramBytes), DateTimeOffset.UtcNow, false);
    }

    private static readonly long Gb = 1_073_741_824;

    private static JsonObject DenseShow(long paramCount) => new()
    {
        ["model_info"] = new JsonObject { ["general.parameter_count"] = (JsonNode)paramCount },
        ["details"] = new JsonObject { ["family"] = "qwen" }
    };

    private static JsonObject MoeShow(long paramCount, long expertCount) => new()
    {
        ["model_info"] = new JsonObject
        {
            ["general.parameter_count"] = (JsonNode)paramCount,
            ["general.expert_count"] = (JsonNode)expertCount
        },
        ["details"] = new JsonObject { ["family"] = "moe" }
    };

    // ==================================================================
    // AC3 — concurrency spill: busy Desktop (-20) vs idle Server (0)
    // ==================================================================

    [Fact]
    public async Task ConcurrencySpill_BusyDesktop_SecondConcurrent27B_RoutesToServer()
    {
        var seedMeta = new MetaMap();
        var (h, meta) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, inFlight: 1, ("qwen3.8-27B", 16 * Gb)), // hot + busy, MaxParallel 1
                [TLaptop] = State(TLaptop, inFlight: 0),
                [TServer] = State(TServer, inFlight: 0)
            },
            meta: AddDense(seedMeta, "qwen3.8-27B", 27_800_000_000));

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(TServer, winner);

        var desktop = h.Eval(TDesktop)!;
        Assert.True(desktop.Qualified);
        Assert.Equal(100, desktop.AffinityBonus);  // 27B is hot on Desktop
        Assert.Equal(20, desktop.HardwareBonus);   // desktop-class: 30k + 8,192 = 38,192 ≤ the desktop's own 131,072 fallback CMax
        Assert.Equal(0, desktop.EvictionPenalty);  // requested model already loaded
        Assert.Equal(140, desktop.ConcurrencyPenalty); // 1 in-flight ×40 + cliff(1≥1)×100
        Assert.Equal(-20, desktop.Score);          // 120 − 140 (documented AC3 example)

        var server = h.Eval(TServer)!;
        Assert.True(server.Qualified);
        // 30k tokens (30,000 + 8,192 = 38,192) fits the desktop's OWN capacity (the 16 GB
        // unpinned target's 131,072 assumed fallback), so the +20 role bonus goes to the
        // desktop class, not the server class: Server scores 0 vs
        // Desktop's −20 (120 − 140, the documented AC3 example) ⇒ the second concurrent
        // 27B request spills immediately to the idle Server.
        Assert.Equal(0, server.HardwareBonus);
        Assert.Equal(0, server.Score);
    }

    // ==================================================================
    // AC1 — interleaved Architect(27B) / Developer(MoE), zero evictions
    // ==================================================================

    [Fact]
    public async Task InterleavedArchitectDeveloper_ZeroEvictions_AlternatesDesktopLaptop()
    {
        const string dense = "qwen3.8-27B";
        const string moe = "qwen3.6-35B-A3B";
        var meta = new MetaMap();
        meta = AddDense(meta, dense, 27_800_000_000);
        meta = AddMoe(meta, moe, 35_000_000_000);

        var options = new OllamaRouterOptions
        {
            Targets = new List<OllamaTargetOptions> { Desktop, Laptop }, // dual-agent: Desktop + Laptop only
            Models = new Dictionary<string, ModelThresholds> { [dense] = new(), [moe] = new() }
        };

        var catalog = new HashSet<string>
        {
            $"{DesktopUrl}|{dense}", $"{DesktopUrl}|{moe}",
            $"{LaptopUrl}|{dense}", $"{LaptopUrl}|{moe}"
        };
        var online = new HashSet<string> { "Desktop", "Laptop" };

        var (h, _) = Build(options, withEngine: true, catalogModels: catalog, onlineTargets: online, meta: meta);

        // Turn A: Architect 27B request. Desktop hot (27B), idle. → Desktop (+120).
        h.States[TDesktop] = State(TDesktop, inFlight: 0, (dense, 16 * Gb));
        h.States[TLaptop] = State(TLaptop, inFlight: 0);
        var a = await h.Decide(30_000, dense);
        Assert.Equal(TDesktop, a);
        Assert.Equal(120, h.Eval(TDesktop)!.Score); // 100 affinity + 20 desktop role
        Assert.Equal(0, h.Eval(TDesktop)!.EvictionPenalty);

        // Turn B (concurrent): Developer MoE request. Desktop busy with 27B (in-flight 1);
        // Laptop hot with MoE. → Laptop (100 affinity + 20 offload = +120).
        h.States[TDesktop] = State(TDesktop, inFlight: 1, (dense, 16 * Gb));
        h.States[TLaptop] = State(TLaptop, inFlight: 0, (moe, 24 * Gb));
        var b = await h.Decide(30_000, moe);
        Assert.Equal(TLaptop, b);
        Assert.Equal(120, h.Eval(TLaptop)!.Score);           // 100 affinity + 20 offload
        Assert.Equal(20, h.Eval(TLaptop)!.HardwareBonus);    // Desktop holds a dense model hot
        Assert.Equal(0, h.Eval(TLaptop)!.EvictionPenalty);   // no eviction on the winner
        Assert.Equal(80, h.Eval(TDesktop)!.EvictionPenalty); // MoE onto the 27B-holding Desktop WOULD evict

        // Turn A2: Architect 27B again → Desktop (no eviction on the winner).
        h.States[TDesktop] = State(TDesktop, inFlight: 0, (dense, 16 * Gb));
        h.States[TLaptop] = State(TLaptop, inFlight: 1, (moe, 24 * Gb));
        var a2 = await h.Decide(30_000, dense);
        Assert.Equal(TDesktop, a2);
        Assert.Equal(0, h.Eval(TDesktop)!.EvictionPenalty);

        // Turn B2: Developer MoE again → Laptop (no eviction on the winner).
        h.States[TDesktop] = State(TDesktop, inFlight: 1, (dense, 16 * Gb));
        h.States[TLaptop] = State(TLaptop, inFlight: 0, (moe, 24 * Gb));
        var b2 = await h.Decide(30_000, moe);
        Assert.Equal(TLaptop, b2);
        Assert.Equal(0, h.Eval(TLaptop)!.EvictionPenalty);

        // Zero unloads: the eviction penalty (−80) never hit a winner in any turn.
    }
    // ==================================================================
    // AC2 — standalone MoE on idle Desktop (no dense hot, all idle)
    // ==================================================================

    [Fact]
    public async Task StandaloneMoe_IdleFleet_RoutesToIdleDesktop_ByDeclarationOrder()
    {
        const string moe = "qwen3.6-35B-A3B";
        var meta = AddMoe(new MetaMap(), moe, 35_000_000_000);
        var (h, _) = Build(
            FleetOptions(moe),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|{moe}", $"{LaptopUrl}|{moe}", $"{ServerUrl}|{moe}" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, inFlight: 0), // idle, nothing loaded
                [TLaptop] = State(TLaptop, inFlight: 0),
                [TServer] = State(TServer, inFlight: 0)
            },
            meta: meta);

        var winner = await h.Decide(30_000, moe);

        // All three score 0 (no dense hot anywhere → Laptop gets NO offload bonus).
        Assert.Equal(TDesktop, winner);           // Desktop wins the 0-0-0 tie by declaration order
        Assert.Equal(0, h.Eval(TDesktop)!.Score);
        Assert.Equal(0, h.Eval(TLaptop)!.HardwareBonus); // Laptop did NOT receive the offload bonus
        Assert.Equal(0, h.Eval(TLaptop)!.Score);
        Assert.Equal(0, h.Eval(TServer)!.Score);
    }

    // ==================================================================
    // Stage-1 qualification: each rule individually disqualifies
    // ==================================================================

    [Fact]
    public async Task Stage1_AdminDisabled_DisqualifiesTarget()
    {
        var seedMeta = new MetaMap();
        var (h, meta) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            disabled: new() { TDesktop },
            meta: AddDense(seedMeta, "qwen3.8-27B", 27_800_000_000));

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.False(h.Eval(TDesktop)!.Qualified);
        Assert.Equal("disabled (administratively unavailable or Enabled=false)", h.Eval(TDesktop)!.DisqualifiedReason);
        Assert.NotEqual(TDesktop, winner);
    }

    [Fact]
    public async Task Stage1_Offline_DisqualifiesTarget()
    {
        var seedMeta = new MetaMap();
        var (h, meta) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop" }, // Server offline
            meta: AddDense(seedMeta, "qwen3.8-27B", 27_800_000_000));

        await h.Decide(30_000, "qwen3.8-27B");

        Assert.False(h.Eval(TServer)!.Qualified);
        Assert.StartsWith("offline", h.Eval(TServer)!.DisqualifiedReason);
    }

    [Fact]
    public async Task Stage1_MissingFromCatalog_DisqualifiesTarget()
    {
        var seedMeta = new MetaMap();
        var (h, meta) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" }, // not on Laptop
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: AddDense(seedMeta, "qwen3.8-27B", 27_800_000_000));

        await h.Decide(30_000, "qwen3.8-27B");

        Assert.False(h.Eval(TLaptop)!.Qualified);
        Assert.Equal("model not present in the target catalog", h.Eval(TLaptop)!.DisqualifiedReason);
    }

    [Fact]
    public async Task Stage1_HardwareIncapable_LargeDenseOn8Gblaptop_DisqualifiesTarget()
    {
        var seedMeta = new MetaMap();
        var (h, meta) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: AddDense(seedMeta, "qwen3.8-27B", 27_800_000_000));

        await h.Decide(30_000, "qwen3.8-27B");

        Assert.False(h.Eval(TLaptop)!.Qualified);
        Assert.StartsWith("hardware incapable", h.Eval(TLaptop)!.DisqualifiedReason);
    }

    [Fact]
    public async Task Stage1_ContextOverflow_TokensExceedCMax_DisqualifiesTarget()
    {
        // Modelfile num_ctx pins CMax at 4096; 30k tokens (+8,192 = 38,192) does not fit.
        var meta = new MetaMap();
        foreach (var url in new[] { DesktopUrl, LaptopUrl, ServerUrl })
        {
            meta.Add(url, "m", new JsonObject
            {
                ["model_info"] = new JsonObject { ["general.parameter_count"] = (JsonNode)27_800_000_000L },
                ["parameters"] = "num_ctx 4096"
            }, 16 * Gb);
        }

        var (h, _) = Build(
            FleetOptions("m"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|m", $"{LaptopUrl}|m", $"{ServerUrl}|m" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: meta);

        // Every target is disqualified (Desktop/Server context overflow, Laptop hardware
        // incapable) and no CloudModel is configured → the whole decision throws, but the
        // per-candidate evaluations survive the throw path via the reporting callback.
        await Assert.ThrowsAsync<NoAvailableTargetException>(() => h.Decide(30_000, "m"));

        // Desktop (CMax 4096): 30,000 + 8,192 = 38,192 > 4096 → context overflow.
        Assert.False(h.Eval(TDesktop)!.Qualified);
        Assert.StartsWith("context overflow", h.Eval(TDesktop)!.DisqualifiedReason);
    }

    [Fact]
    public async Task Stage1_ClientNumCtx_IsClampedDown_NotADisqualifier()
    {
        // Static client num_ctx 131072 is larger than CMax 49152; it clamps the requirement
        // down to the (small) token estimate, so the target still qualifies.
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: meta);

        var winner = await h.Decide(1_000, "qwen3.8-27B", numCtx: 131_072);

        // 1000 + 8,192 = 9,192 ≤ min(131072, ∞) and ≤ 49152 → Desktop qualifies and wins by order.
        Assert.True(h.Eval(TDesktop)!.Qualified);
        Assert.Equal(TDesktop, winner);
    }

    // ==================================================================
    // Stage-1 permissive fallback + pinned-Dense-on-small veto
    // ==================================================================

    [Fact]
    public async Task PermissiveFallback_NoCapabilityMetadata_StillQualifies()
    {
        // No engine ⇒ no metadata discoverable ⇒ permissively qualified (score 0).
        var (h, _) = Build(
            FleetOptions("mystery"),
            withEngine: false,
            catalogModels: new() { $"{DesktopUrl}|mystery", $"{LaptopUrl}|mystery", $"{ServerUrl}|mystery" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" });

        var winner = await h.Decide(1_000, "mystery");

        Assert.True(h.Eval(TDesktop)!.Qualified);
        Assert.Equal(0, h.Eval(TDesktop)!.Score);
        Assert.Equal(TDesktop, winner); // declaration-order tie-break at 0
    }

    [Fact]
    public async Task PinnedDense_NoCapabilityMetadata_SmallTargetIsVetoed()
    {
        // Operator pinned Architecture "Dense" (unknown count ⇒ LargeDense); the 8 GB Laptop
        // (< 12000 MB) is vetoed even with no capability metadata, while Desktop/Server pass.
        var (h, _) = Build(
            FleetOptions("mystery", architecture: "Dense"),
            withEngine: false,
            catalogModels: new() { $"{DesktopUrl}|mystery", $"{LaptopUrl}|mystery", $"{ServerUrl}|mystery" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" });

        var winner = await h.Decide(1_000, "mystery");

        Assert.False(h.Eval(TLaptop)!.Qualified);
        Assert.StartsWith("pinned-Dense", h.Eval(TLaptop)!.DisqualifiedReason);
        Assert.True(h.Eval(TDesktop)!.Qualified);
        Assert.Equal(TDesktop, winner);
    }

    // ==================================================================
    // Tie-breaker: equal scores resolve by declaration order
    // ==================================================================

    [Fact]
    public async Task TieBreaker_EqualScores_EarlierDeclarationWins()
    {
        var seedMeta = new MetaMap();
        var (h, meta) = Build(
            FleetOptions("mystery"),
            withEngine: false,
            catalogModels: new() { $"{DesktopUrl}|mystery", $"{ServerUrl}|mystery" }, // Desktop & Server only (no Laptop)
            onlineTargets: new() { "Desktop", "Laptop", "Server" });

        var winner = await h.Decide(1_000, "mystery");

        // Desktop (0) and Server (0) tie → Desktop (earlier declaration) wins.
        Assert.Equal(0, h.Eval(TDesktop)!.Score);
        Assert.Equal(0, h.Eval(TServer)!.Score);
        Assert.Equal(TDesktop, winner);
    }

    // ==================================================================
    // Cloud pseudo-target: fixed score 0, sorted after fleet targets
    // ==================================================================

    [Fact]
    public async Task Cloud_AllFleetOffline_CloudConfigured_CloudWins()
    {
        var seedMeta = new MetaMap();
        var (h, meta) = Build(
            FleetOptions("qwen3.8-27B", cloudModel: "deepseek-v3.1:671b-cloud"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { }, // all fleet offline
            meta: AddDense(seedMeta, "qwen3.8-27B", 27_800_000_000));

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(RoutingTarget.Cloud, winner);
        Assert.Equal(0, h.Eval(RoutingTarget.Cloud)!.Score);
        Assert.False(h.Eval(TDesktop)!.Qualified);
        Assert.False(h.Eval(TServer)!.Qualified);
    }

    [Fact]
    public async Task Cloud_FleetZeroScore_Tie_FleetWinsOverCloud()
    {
        var seedMeta = new MetaMap();
        var (h, meta) = Build(
            FleetOptions("qwen3.8-27B", cloudModel: "deepseek-v3.1:671b-cloud"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new() { [TDesktop] = State(TDesktop, 0), [TLaptop] = State(TLaptop, 0), [TServer] = State(TServer, 0) },
            meta: AddDense(seedMeta, "qwen3.8-27B", 27_800_000_000));

        var winner = await h.Decide(1_000, "qwen3.8-27B");

        // Desktop has affinity? No (nothing loaded) → 0. Cloud is 0. Desktop (0) beats Cloud (0).
        Assert.Equal(TDesktop, winner);
        Assert.NotEqual(RoutingTarget.Cloud, winner);
    }

    [Fact]
    public async Task Cloud_NoCloudModel_NothingQualifies_ThrowsNoAvailableTarget()
    {
        var seedMeta = new MetaMap();
        var (h, meta) = Build(
            FleetOptions("qwen3.8-27B"), // no CloudModel
            withEngine: true,
            catalogModels: new() { }, // model absent everywhere
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: AddDense(seedMeta, "qwen3.8-27B", 27_800_000_000));

        await Assert.ThrowsAsync<NoAvailableTargetException>(() => h.Decide(30_000, "qwen3.8-27B"));
    }

    // ==================================================================
    // Failure-streak switch over the ranked list
    // ==================================================================

    [Fact]
    public async Task FailureStreak_WinnerFailing_SwitchesToNextRankedCandidate()
    {
        // Desktop is top-ranked (27B hot) but has failed repeatedly; Server is next.
        var seedMeta = new MetaMap();
        var (h, meta) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, 0, ("qwen3.8-27B", 16 * Gb)), // Desktop hot → top ranked (120)
                [TLaptop] = State(TLaptop, 0),
                [TServer] = State(TServer, 0)
            },
            failureStreaks: t => t == TDesktop ? 5 : 0, // Desktop failing ≥ max (3)
            meta: AddDense(seedMeta, "qwen3.8-27B", 27_800_000_000));

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        // Desktop would win on score but its failure streak forces a switch to Server.
        Assert.Equal(TServer, winner);
    }

    // ==================================================================
    // Legacy two-tier synthesis regression
    // ==================================================================

    [Fact]
    public async Task LegacyTwoTier_SynthesizedFleet_UnconfiguredModel_FirstDeclaredWins()
    {
        // No Targets configured; LocalUrl/RemoteUrl synthesize Local(16gb) + Remote(16gb).
        var options = new OllamaRouterOptions
        {
            LocalUrl = "http://127.0.0.1:11435",
            RemoteUrl = "http://aiserver.local:11434"
            // Models empty ⇒ the requested model is unconfigured (no thresholds).
        };

        var (h, _) = Build(
            options,
            withEngine: false, // permissive path (no engine)
            catalogModels: new() { "http://127.0.0.1:11435|llama3", "http://aiserver.local:11434|llama3" },
            onlineTargets: new() { "Local", "Remote" });

        var winner = await h.Decide(1_000, "llama3");

        // Both synthesized targets qualify permissively at score 0 → Local (first declared).
        Assert.Equal(new RoutingTarget("Local"), winner);
    }

    // ==================================================================
    // DecideAsync overload without clientNumCtx still works
    // ==================================================================

    [Fact]
    public async Task DecideAsync_TwoArgOverload_Works()
    {
        var seedMeta = new MetaMap();
        var (h, meta) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: AddDense(seedMeta, "qwen3.8-27B", 27_800_000_000));

        var winner = await h.SUT.DecideAsync(1_000, "qwen3.8-27B");

        Assert.Equal(TDesktop, winner);
    }

    // ==================================================================
    // DI: single canonical constructor resolves without ambiguity
    // ==================================================================

    [Fact]
    public void ServiceCollection_CanResolveRoutingDecisionService_WithoutAmbiguousConstructors()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        // CloudOverflow disabled: used below to prove the Cloud handler actually receives the
        // options instance (a handler without options would report itself available).
        services.Configure<OllamaRouterOptions>(o => o.CloudOverflow.Enabled = false);
        services.AddSingleton(new Mock<IOllamaModelCatalogClient>().Object);
        services.AddSingleton(new Mock<IModelCatalogCacheService>().Object);
        services.AddSingleton(new Mock<ITargetLivenessService>().Object);
        // Everything enabled, so the Cloud handler's IsAvailable() outcome below is decided
        // SOLELY by the CloudOverflow.Enabled options switch (true with no options, false with them).
        var availability = new Mock<ITargetAvailabilityService>();
        availability.Setup(t => t.IsEnabled(It.IsAny<RoutingTarget>())).Returns(true);
        services.AddSingleton(availability.Object);
        services.AddSingleton(new Mock<ITargetExecutionTracker>().Object);
        services.AddSingleton(new ModelCapabilityEngine(
            new Mock<IOllamaModelCatalogClient>().Object,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ModelCapabilityEngine>.Instance));
        services.AddSingleton<ITargetFailureStreakService, TargetFailureStreakService>();

        // Mirrors Program.cs: the Cloud handler must be constructed with its options or the
        // CloudOverflow.Enabled switch / DefaultModel precedence would be silently ignored.
        services.AddTransient<CloudRoutingTargetHandler>(sp => new CloudRoutingTargetHandler(
            sp.GetRequiredService<ITargetAvailabilityService>(),
            sp.GetRequiredService<IOptions<OllamaRouterOptions>>(),
            sp.GetRequiredService<ILogger<CloudRoutingTargetHandler>>()));
        services.AddTransient<IRoutingTargetHandler>(sp => sp.GetRequiredService<CloudRoutingTargetHandler>());
        services.AddTransient<IRoutingDecisionService, RoutingDecisionService>();

        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var instance = serviceProvider.GetRequiredService<IRoutingDecisionService>();
        Assert.NotNull(instance);

        // The resolved Cloud handler must have received its options: with CloudOverflow.Enabled
        // = false (configured above) and the Cloud toggle on, it must report itself unavailable.
        var cloudHandler = serviceProvider.GetServices<IRoutingTargetHandler>()
            .Single(h => h.Target == RoutingTarget.Cloud);
        Assert.False(cloudHandler.IsAvailable());
    }

    // ==================================================================
    // Pipeline branch coverage: startup grace, offload conditions, eviction
    // VRAM-sum branches, role bonuses, failure streaks, tag normalization
    // ==================================================================

    private static Dictionary<RoutingTarget, TargetLivenessEntry> Entries(
        RoutingTarget target, TargetLivenessState state)
        => new() { [target] = new TargetLivenessEntry(target, state, null, DateTimeOffset.UtcNow, null) };

    private static JsonObject MoeShow(long paramCount) => new()
    {
        ["model_info"] = new JsonObject
        {
            ["general.parameter_count"] = (JsonNode)paramCount,
            ["general.expert_count"] = (JsonNode)64L
        },
        ["details"] = new JsonObject { ["family"] = "moe" }
    };

    [Fact]
    public async Task Stage1_StartupGrace_UnknownStateWithinGraceWindow_RemainsRoutable()
    {
        // Liveness prober started 1 s ago (within the 10 s grace) and the Desktop's raw state
        // is still Unknown: the pipeline must NOT disqualify it (boot-probe latency tolerance).
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { Desktop } };
        options.Models["mystery"] = new ModelThresholds();

        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.IsOnline(It.IsAny<RoutingTarget>())).Returns(false);
        liveness.Setup(l => l.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddSeconds(-1));
        liveness.Setup(l => l.GetEntries()).Returns(Entries(TDesktop, TargetLivenessState.Unknown));

        var (h, _) = Build(options, withEngine: false, catalogModels: new() { $"{DesktopUrl}|mystery" }, onlineTargets: new(), livenessOverride: liveness);

        var winner = await h.Decide(1_000, "mystery");

        Assert.True(h.Eval(TDesktop)!.Qualified);
        Assert.Equal(TDesktop, winner);
    }

    [Fact]
    public async Task Stage1_WithinStartupGrace_ButStateOffline_DisqualifiesTarget()
    {
        // Same grace window, but the raw state is already Offline (not Unknown): the grace
        // predicate must NOT rescue it.
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { Desktop } };
        options.Models["mystery"] = new ModelThresholds();

        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.IsOnline(It.IsAny<RoutingTarget>())).Returns(false);
        liveness.Setup(l => l.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddSeconds(-1));
        liveness.Setup(l => l.GetEntries()).Returns(Entries(TDesktop, TargetLivenessState.Offline));

        var (h, _) = Build(options, withEngine: false, catalogModels: new() { $"{DesktopUrl}|mystery" }, onlineTargets: new(), livenessOverride: liveness);

        await Assert.ThrowsAsync<NoAvailableTargetException>(() => h.Decide(1_000, "mystery"));
        Assert.False(h.Eval(TDesktop)!.Qualified);
        Assert.StartsWith("offline", h.Eval(TDesktop)!.DisqualifiedReason);
    }

    [Fact]
    public async Task Stage1_ConfigEnabledFalse_DisqualifiesTarget()
    {
        // The fleet entry's Enabled flag is false (not the availability state file): the
        // candidate is admin-disabled before any liveness/catalog check runs.
        var off = new OllamaTargetOptions { Name = "Desktop", Url = DesktopUrl, HardwareHint = "16gb", MaxParallel = 1, Enabled = false };
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { off } };
        options.Models["mystery"] = new ModelThresholds();

        var (h, _) = Build(options, withEngine: false, catalogModels: new() { $"{DesktopUrl}|mystery" }, onlineTargets: new() { "Desktop" });

        await Assert.ThrowsAsync<NoAvailableTargetException>(() => h.Decide(1_000, "mystery"));
        Assert.False(h.Eval(TDesktop)!.Qualified);
        Assert.StartsWith("disabled", h.Eval(TDesktop)!.DisqualifiedReason);
    }

    private static JsonObject PinnedDenseShow(long paramCount, int? modelfileNumCtx = null) => new()
    {
        ["model_info"] = new JsonObject { ["general.parameter_count"] = (JsonNode)paramCount },
        ["details"] = new JsonObject { ["family"] = "qwen" },
        ["parameters"] = modelfileNumCtx is { } n ? $"num_ctx {n}" : ""
    };

    [Fact]
    public async Task Stage1_ClientNumCtxSmallerThanRequired_StillDisqualifiesOnOverflow()
    {
        // The client's num_ctx is an upper-bound preference, never a disqualifier — but when
        // BOTH the token+margin estimate (5000 + 8,192 = 13,192) and the client num_ctx exceed
        // CMax 4096, the target is still disqualified: the clamp can only LOWER the requirement.
        var meta = new MetaMap();
        foreach (var url in new[] { DesktopUrl, LaptopUrl, ServerUrl })
        {
            // Modelfile pins CMax at 4096 on every target.
            meta.Add(url, "m", PinnedDenseShow(27_800_000_000, modelfileNumCtx: 4096), 16 * Gb);
        }

        var (h, _) = Build(
            FleetOptions("m"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|m", $"{LaptopUrl}|m", $"{ServerUrl}|m" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: meta);

        // 5000 tokens + 8,192 = 13,192; client num_ctx 8192 → clamped requirement
        // min(13,192, 8,192) = 8,192 > CMax 4096 → context overflow on the Desktop (the Laptop
        // is hardware-incapable for the 27B metadata as well) ⇒ nothing qualifies and no
        // CloudModel is configured.
        await Assert.ThrowsAsync<NoAvailableTargetException>(() => h.Decide(5_000, "m", numCtx: 8_192));
        Assert.False(h.Eval(TDesktop)!.Qualified);
        Assert.StartsWith("context overflow", h.Eval(TDesktop)!.DisqualifiedReason);
    }

    [Fact]
    public async Task Stage1_LargeDenseExceedingDesktopNodeCapacity_QualifiesOnServerClass()
    {
        // 60k tokens + 8,192 = 68,192 > 49,152 (the Desktop's own Modelfile-pin CMax): the
        // Desktop overflows and is disqualified, while the unpinned Server-class target (the
        // assumed 128k fallback CMax 131,072) qualifies and wins (the only survivor).
        var meta = new MetaMap();
        meta.Add(DesktopUrl, "qwen3.8-27B", PinnedDenseShow(27_800_000_000, modelfileNumCtx: 49_152), 16 * Gb);
        meta.Add(LaptopUrl, "qwen3.8-27B", PinnedDenseShow(27_800_000_000, modelfileNumCtx: 49_152), 16 * Gb);
        meta.Add(ServerUrl, "qwen3.8-27B", DenseShow(27_800_000_000), 16 * Gb);
        var (h, _) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: meta);

        var winner = await h.Decide(60_000, "qwen3.8-27B");

        Assert.False(h.Eval(TDesktop)!.Qualified);
        Assert.StartsWith("context overflow", h.Eval(TDesktop)!.DisqualifiedReason);
        Assert.True(h.Eval(TServer)!.Qualified);
        Assert.Equal(0, h.Eval(TServer)!.HardwareBonus); // request fits the server's own CMax ⇒ not the "over-capacity" server role
        Assert.Equal(TServer, winner);
    }

    [Fact]
    public async Task Stage1_ConfiguredMargin_Honored_FromOptions()
    {
        // ContextFitMarginTokens = 32,000: a 15k-token dense request requires 15,000 + 32,000
        // = 47,000 ≤ 49,152 → the Desktop (own CMax 49,152) qualifies and wins.
        var options = FleetOptions("qwen3.8-27B");
        options.ContextFitMarginTokens = 32_000;
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: meta);

        var winner = await h.Decide(15_000, "qwen3.8-27B");

        Assert.True(h.Eval(TDesktop)!.Qualified);
        Assert.Equal(20, h.Eval(TDesktop)!.HardwareBonus); // 47,000 ≤ desktop's own CMax 49,152
        Assert.Equal(TDesktop, winner);
    }

    [Fact]
    public async Task Stage1_ZeroMargin_AllowsTokensAlone()
    {
        // ContextFitMarginTokens = 0: 40,961 tokens alone ≤ 49,152 qualifies (the default
        // 8,192 margin would have disqualified 40,961 + 8,192 = 49,153 > 49,152).
        var options = FleetOptions("qwen3.8-27B");
        options.ContextFitMarginTokens = 0;
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: meta);

        await h.Decide(40_961, "qwen3.8-27B");

        Assert.True(h.Eval(TDesktop)!.Qualified);
    }

    [Fact]
    public async Task Stage1_VeryLongPrompt_FitsOnlyNodePreconfiguredAbove160k()
    {
        // A 150k-token dense request needs 150,000 + 8,192 = 158,192 tokens of context:
        // disqualified on a node pre-configured at 49,152 (Desktop) AND on an unpinned node
        // (the assumed 128k fallback CMax 131,072), but served by a node pre-configured at
        // 196,608.
        var big = new OllamaTargetOptions { Name = "BigCtx", Url = "http://bigctx:11434", HardwareHint = "96gb", MaxParallel = 2 };
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { Desktop, big } };
        options.Models["qwen3.8-27B"] = new ModelThresholds();

        var meta = new MetaMap();
        meta.Add(DesktopUrl, "qwen3.8-27B", PinnedDenseShow(27_800_000_000, modelfileNumCtx: 49_152), 16 * Gb);
        meta.Add(big.Url!, "qwen3.8-27B", PinnedDenseShow(27_800_000_000, modelfileNumCtx: 196_608), 16 * Gb);

        var tBig = new RoutingTarget("BigCtx");
        var (h, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{big.Url}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "BigCtx" },
            states: new() { [TDesktop] = State(TDesktop, 0), [tBig] = State(tBig, 0) },
            meta: meta);

        var winner = await h.Decide(150_000, "qwen3.8-27B");

        Assert.False(h.Eval(TDesktop)!.Qualified); // 158,192 > 49,152
        Assert.StartsWith("context overflow", h.Eval(TDesktop)!.DisqualifiedReason);
        Assert.True(h.Eval(tBig)!.Qualified);      // 158,192 ≤ 196,608 node pin
        Assert.Equal(tBig, winner);
    }

    [Fact]
    public async Task Stage1_VeryLongPrompt_ExceedsUnpinnedAssumed128kFallback()
    {
        // A 150k-token dense request needs 158,192 tokens: an unpinned Server-class node
        // (no Modelfile num_ctx, no native context_length) has CMax = the 131,072 assumed
        // fallback → context overflow (158,192 > 131,072).
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: meta);

        // Nothing qualifies (all targets overflow the 131,072 fallback) and no CloudModel is
        // configured → the decision throws, but the per-candidate evaluations survive.
        await Assert.ThrowsAsync<NoAvailableTargetException>(() => h.Decide(150_000, "qwen3.8-27B"));

        Assert.False(h.Eval(TDesktop)!.Qualified); // 158,192 > 131,072 assumed fallback (16 GB unpinned)
        Assert.False(h.Eval(TServer)!.Qualified);  // 158,192 > 131,072 assumed fallback (96 GB unpinned)
        Assert.StartsWith("context overflow", h.Eval(TServer)!.DisqualifiedReason);
        Assert.False(h.Eval(TLaptop)!.Qualified);  // 158,192 > 131,072 (8 GB unpinned)
    }

    [Fact]
    public async Task HardwareBonus_RoleDecidedByTargetOwnCapacity()
    {
        // Role rule (task #25): the LargeDense HardwareBonus is decided by the target's OWN
        // pre-configured capacity (CMax), never a hardcoded desktop ceiling: the desktop-class
        // target earns +20 when the margin-adjusted requirement fits ITS OWN CMax, the
        // server-class target earns +20 only when the requirement exceeds the target's own CMax
        // (the "over-capacity server role"). A 30k-token request needs 38,192, which the
        // desktop's own 49,152 pin covers → the desktop earns the bonus; a 60k-token request
        // needs 68,192, which does NOT fit the desktop's 49,152 (it is overflow-disqualified)
        // but fits the server's 131,072 (→ the server scores the 0, non-role, and still wins).
        var meta = new MetaMap();
        meta.Add(DesktopUrl, "qwen3.8-27B", PinnedDenseShow(27_800_000_000, modelfileNumCtx: 49_152), 16 * Gb);
        meta.Add(LaptopUrl, "qwen3.8-27B", PinnedDenseShow(27_800_000_000, modelfileNumCtx: 49_152), 16 * Gb);
        meta.Add(ServerUrl, "qwen3.8-27B", PinnedDenseShow(27_800_000_000, modelfileNumCtx: 131_072), 16 * Gb);
        var (h, _) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: meta);

        // 30k tokens (38,192 needed): the desktop's own 49,152 covers it → desktop role +20;
        // the server's 131,072 also covers it but a server that FITS is not the over-capacity
        // server role → 0. Desktop wins on the +20.
        var winner = await h.Decide(30_000, "qwen3.8-27B");
        Assert.True(h.Eval(TDesktop)!.Qualified);
        Assert.Equal(20, h.Eval(TDesktop)!.HardwareBonus); // desktop's own 49,152 covers 38,192
        Assert.Equal(0, h.Eval(TServer)!.HardwareBonus);   // server that fits is not the over-capacity role
        Assert.Equal(TDesktop, winner);

        // 60k tokens (68,192 needed): the desktop's 49,152 does NOT cover it → overflow-
        // disqualified (0, never scored); the server's 131,072 covers it → 0 (not the over-
        // capacity role) but it is the only qualifier → it wins.
        winner = await h.Decide(60_000, "qwen3.8-27B");
        Assert.False(h.Eval(TDesktop)!.Qualified);
        Assert.True(h.Eval(TServer)!.Qualified);
        Assert.Equal(0, h.Eval(TServer)!.HardwareBonus);
        Assert.Equal(TServer, winner);
    }

    [Fact]
    public async Task HardwareBonus_BusyDesktop_JustifiesMoeOffloadToLaptop()
    {
        // The Desktop holds NO dense model hot but HAS 1 in-flight request (busy per the Q2
        // resolution InFlight > 0) → the MoE offload bonus kicks in on the Laptop (+20), which
        // beats the busy Desktop's −140 concurrency penalty and the Server's 0.
        var meta = AddMoe(new MetaMap(), "moe1", 35_000_000_000);
        var (h, _) = Build(
            FleetOptions("moe1"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|moe1", $"{LaptopUrl}|moe1", $"{ServerUrl}|moe1" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, inFlight: 1), // busy, nothing loaded
                [TLaptop] = State(TLaptop, inFlight: 0),
                [TServer] = State(TServer, inFlight: 0)
            },
            meta: meta);

        var winner = await h.Decide(30_000, "moe1");

        Assert.Equal(20, h.Eval(TLaptop)!.HardwareBonus); // offload justified by busy desktop
        Assert.Equal(140, h.Eval(TDesktop)!.ConcurrencyPenalty);
        Assert.Equal(-140, h.Eval(TDesktop)!.Score);
        Assert.Equal(TLaptop, winner);
    }

    [Fact]
    public async Task HardwareBonus_MultipleSmallClassTargets_OnlyFirstClaimsOffloadSlot()
    {
        // Two small-class targets with a busy Desktop: only the FIRST declared small-class
        // target receives the +20 offload bonus (the role slot is single per decision).
        var s1 = new OllamaTargetOptions { Name = "S1", Url = "http://s1:11434", HardwareHint = "8gb", MaxParallel = 1 };
        var s2 = new OllamaTargetOptions { Name = "S2", Url = "http://s2:11434", HardwareHint = "8gb", MaxParallel = 1 };
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { Desktop, s1, s2 } };
        options.Models["moe1"] = new ModelThresholds();

        var ts1 = new RoutingTarget("S1");
        var ts2 = new RoutingTarget("S2");
        var meta = new MetaMap();
        foreach (var url in new[] { DesktopUrl, "http://s1:11434", "http://s2:11434" })
        {
            meta.Add(url, "moe1", MoeShow(35_000_000_000), 24 * Gb);
        }

        var (h, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|moe1", "http://s1:11434|moe1", "http://s2:11434|moe1" },
            onlineTargets: new() { "Desktop", "S1", "S2" },
            states: new()
            {
                [TDesktop] = State(TDesktop, inFlight: 1),
                [ts1] = State(ts1, inFlight: 0),
                [ts2] = State(ts2, inFlight: 0)
            },
            meta: meta);

        var winner = await h.Decide(30_000, "moe1");

        Assert.Equal(20, h.Eval(ts1)!.HardwareBonus);
        Assert.Equal(0, h.Eval(ts2)!.HardwareBonus); // slot already claimed by S1
        Assert.Equal(0, h.Eval(TDesktop)!.HardwareBonus); // offload bonus never lands on the desktop class
        Assert.Equal(ts1, winner);
    }

    [Fact]
    public async Task HardwareBonus_SmallDenseRequest_UsesOffloadSlotLikeMoe()
    {
        // A SmallDense request follows the same Laptop-offload rule as MoE: the +20 goes to
        // the first small-class target only when a desktop/server-class target is busy.
        var meta = AddDense(new MetaMap(), "small1", 4_000_000_000); // 4B ⇒ SmallDense
        var (h, _) = Build(
            FleetOptions("small1"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|small1", $"{LaptopUrl}|small1", $"{ServerUrl}|small1" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, inFlight: 1), // busy → offload justified
                [TLaptop] = State(TLaptop, inFlight: 0),
                [TServer] = State(TServer, inFlight: 0)
            },
            meta: meta);

        var winner = await h.Decide(30_000, "small1");

        Assert.Equal(20, h.Eval(TLaptop)!.HardwareBonus);
        Assert.Equal(TLaptop, winner);
    }

    [Fact]
    public async Task HardwareBonus_PinnedMoe_ArchitectureTreatedAsMoeForOffload()
    {
        // Operator pin "Moe" wins over auto-detection (4B dense metadata): the request is
        // handled as an MoE/SmallDense offload candidate.
        var meta = AddDense(new MetaMap(), "pinned1", 4_000_000_000);
        var (h, _) = Build(
            FleetOptions("pinned1", architecture: "Moe"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|pinned1", $"{LaptopUrl}|pinned1", $"{ServerUrl}|pinned1" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, inFlight: 1),
                [TLaptop] = State(TLaptop, inFlight: 0),
                [TServer] = State(TServer, inFlight: 0)
            },
            meta: meta);

        var winner = await h.Decide(30_000, "pinned1");

        Assert.Equal(20, h.Eval(TLaptop)!.HardwareBonus);
        Assert.Equal(TLaptop, winner);
    }

    [Fact]
    public async Task Eviction_StaleSnapshot_StillAppliesVramSumRule()
    {
        // A STALE tracker snapshot (refresh missed its budget) is still used for the eviction
        // rule — a stale snapshot is better than none for anti-thrashing: Desktop holds 10 GB,
        // loading the 16 GB model would overflow 16 GB ⇒ −80; the idle Server (0) wins.
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var staleDesktop = new TargetExecutionState(TDesktop, 0, [new TargetLoadedModel("other", 10 * Gb)], 10 * Gb, DateTimeOffset.UtcNow, Stale: true);

        var (h, _) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new()
            {
                [TDesktop] = staleDesktop,
                [TLaptop] = State(TLaptop, 0),
                [TServer] = State(TServer, 0)
            },
            meta: meta);

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(80, h.Eval(TDesktop)!.EvictionPenalty); // 10GB + 16GB > 16GB even though stale
        Assert.Equal(-60, h.Eval(TDesktop)!.Score); // 0 affinity + 20 role - 80 eviction
        Assert.Equal(0, h.Eval(TServer)!.EvictionPenalty);
        Assert.Equal(TServer, winner);
    }

    [Fact]
    public async Task Eviction_UnknownVramBudget_PessimisticOnNonServerClass()
    {
        // The Desktop has NO hardware hint (unknown VRAM budget) but holds a different model:
        // we cannot prove overflow, so the pessimistic anti-thrash fallback penalizes the
        // non-server-class candidate only. The Server (server-class, no penalty) wins.
        var desktopNoHint = new OllamaTargetOptions { Name = "Desktop", Url = DesktopUrl, MaxParallel = 1 }; // no hint ⇒ unknown
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { desktopNoHint, Server } };
        options.Models["mystery"] = new ModelThresholds();

        var meta = new MetaMap();
        foreach (var url in new[] { DesktopUrl, ServerUrl })
        {
            meta.Add(url, "mystery", MoeShow(35_000_000_000), 24 * Gb);
        }

        var (h, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|mystery", $"{ServerUrl}|mystery" },
            onlineTargets: new() { "Desktop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, inFlight: 0, ("other", 10 * Gb)),
                [TServer] = State(TServer, inFlight: 0)
            },
            meta: meta);

        var winner = await h.Decide(1_000, "mystery");

        Assert.Equal(80, h.Eval(TDesktop)!.EvictionPenalty); // unknown budget + non-server ⇒ penalize
        Assert.Equal(0, h.Eval(TServer)!.EvictionPenalty);  // server-class: cannot prove eviction
        Assert.Equal(TServer, winner);
    }

    [Fact]
    public async Task Eviction_UnknownWeight_WithKnownVram_PessimisticOnNonServerClass()
    {
        // Known VRAM budget but a degraded /api/tags entry (weight size 0): the conservative
        // fallback penalizes the non-server-class target holding a different model.
        var meta = new MetaMap();
        foreach (var url in new[] { DesktopUrl, LaptopUrl, ServerUrl })
        {
            meta.Add(url, "mystery", DenseShow(27_800_000_000), sizeBytes: 0); // catalog present, weight unknown
        }

        var (h, _) = Build(
            FleetOptions("mystery"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|mystery", $"{LaptopUrl}|mystery", $"{ServerUrl}|mystery" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, inFlight: 0, ("other", 10 * Gb)),
                [TLaptop] = State(TLaptop, inFlight: 0),
                [TServer] = State(TServer, inFlight: 0)
            },
            meta: meta);

        var winner = await h.Decide(1_000, "mystery");

        Assert.Equal(80, h.Eval(TDesktop)!.EvictionPenalty);
        Assert.Equal(0, h.Eval(TServer)!.EvictionPenalty);
        Assert.Equal(TServer, winner);
    }

    [Fact]
    public async Task Cloud_AllFleetNegativeScores_CloudBeatsNegativeFleet()
    {
        // Desktop saturated (1 in-flight, MaxParallel 1 ⇒ −140), Laptop/Server offline, Cloud
        // configured: the fixed score-0 Cloud candidate beats the negative fleet score.
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            FleetOptions("qwen3.8-27B", cloudModel: "deepseek-v3.1:671b-cloud"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop" },
            states: new() { [TDesktop] = State(TDesktop, inFlight: 1) },
            meta: meta);

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        // Desktop: 0 affinity + 20 role - 0 eviction - 140 concurrency = -120; Cloud's fixed
        // score 0 beats the negative fleet score.
        Assert.Equal(-120, h.Eval(TDesktop)!.Score);
        Assert.Equal(0, h.Eval(RoutingTarget.Cloud)!.Score);
        Assert.Equal(RoutingTarget.Cloud, winner);
    }

    [Fact]
    public async Task FailureStreak_AllCandidatesFailing_KeepsWinnerAndLogs()
    {
        // The winner's streak is ≥ max, but EVERY other ranked candidate is also failing: the
        // pipeline keeps the winner (existing behavior — a forced switch to another failing
        // target would be worse).
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, 0, ("qwen3.8-27B", 16 * Gb)), // top-ranked (120)
                [TLaptop] = State(TLaptop, 0),
                [TServer] = State(TServer, 0)
            },
            failureStreaks: _ => 5,
            meta: meta);

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(TDesktop, winner);
    }

    [Fact]
    public async Task FailureStreak_WinnerFailing_SwitchesToNextRankedFleetTarget()
    {
        // Desktop (top-ranked, SmallDense hot: 100 affinity) is failing; the next-ranked
        // candidate is the Laptop (+20 offload — the Desktop holds a dense model hot, and
        // the offload bonus lands on the small-class target). A 27B-class LargeDense would be
        // hardware-incapable on the 8 GB Laptop, so this scenario uses a 4B SmallDense model.
        var meta = AddDense(new MetaMap(), "small1", 4_000_000_000); // 4B ⇒ SmallDense
        var (h, _) = Build(
            FleetOptions("small1"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|small1", $"{LaptopUrl}|small1", $"{ServerUrl}|small1" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, 0, ("small1", 4 * Gb)), // top-ranked: 100 affinity
                [TLaptop] = State(TLaptop, 0),                      // +20 offload (desktop holds dense hot)
                [TServer] = State(TServer, 0)
            },
            failureStreaks: t => t == TDesktop ? 5 : 0,
            meta: meta);

        var winner = await h.Decide(30_000, "small1");

        // Desktop would win on score (100) but its failure streak forces a switch to the
        // next-ranked candidate (Laptop, 20).
        Assert.Equal(100, h.Eval(TDesktop)!.Score);
        Assert.Equal(20, h.Eval(TLaptop)!.Score);
        Assert.Equal(TLaptop, winner);
    }

    [Fact]
    public async Task FailureStreak_WinnerFailing_AllFleetFailing_SwitchesToCloud()
    {
        // Every fleet candidate is failing (the Desktop top-ranked winner's streak exceeds
        // the threshold), but the Cloud pseudo-target sorts last in the ranked list with no
        // failure streak of its own: the switch lands on Cloud.
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);

        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { Desktop } };
        options.Models["qwen3.8-27B"] = new ModelThresholds { CloudModel = "glm-5.3-flash:cloud" };

        var (h, _) = Build(options, withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop" },
            states: new() { [TDesktop] = State(TDesktop, 0, ("qwen3.8-27B", 16 * Gb)) },
            failureStreaks: t => t == RoutingTarget.Cloud ? 0 : 5, // Desktop failing, Cloud clean
            meta: meta);

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(RoutingTarget.Cloud, winner);
    }

    [Fact]
    public async Task Affinity_TaggedModelName_MatchesHotModelByNormalizedName()
    {
        // The request carries a tag ("qwen3.8-27B:latest") while /api/ps reports the model
        // WITHOUT a tag ("qwen3.8-27B"): the normalized-name comparison must still recognize
        // the model as hot (affinity +100). The /api/show for the tagged name degrades to the
        // permissive MoE classification, so no role bonus — only the affinity is asserted.
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new()
            {
                $"{DesktopUrl}|qwen3.8-27B:latest", $"{LaptopUrl}|qwen3.8-27B:latest", $"{ServerUrl}|qwen3.8-27B:latest"
            },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new() { [TDesktop] = State(TDesktop, 0, ("qwen3.8-27B", 16 * Gb)) }, // ps: no tag
            meta: meta);

        var winner = await h.Decide(30_000, "qwen3.8-27B:latest");

        Assert.Equal(100, h.Eval(TDesktop)!.AffinityBonus);
        Assert.Equal(0, h.Eval(TDesktop)!.EvictionPenalty);
        Assert.Equal(TDesktop, winner);
    }

    [Theory]
    [InlineData("Dense")]       // pinned Dense, unknown count ⇒ LargeDense ⇒ small-class veto
    [InlineData("dense")]       // case-insensitive pin
    [InlineData(null)]          // no pin + no metadata ⇒ permissively qualified (LargeDense-by-unknown-count is NOT inferred without metadata)
    [InlineData("Moe")]         // pinned Moe ⇒ permissive small-class (MoE never VRAM-vetoed)
    public async Task PinnedArchitecture_UnknownMetadata_VetoAppliesOnlyToDensePin(string? architecture)
    {
        // With no capability metadata discoverable, ONLY a Dense pin vetoes the 8 GB small-class
        // target; everything else stays permissively qualified (Q4 resolution).
        var expectLaptopQualified = architecture is null || string.Equals(architecture, "Moe", StringComparison.OrdinalIgnoreCase);

        var (h, _) = Build(
            FleetOptions("mystery", architecture: architecture),
            withEngine: false,
            catalogModels: new() { $"{DesktopUrl}|mystery", $"{LaptopUrl}|mystery", $"{ServerUrl}|mystery" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" });

        var winner = await h.Decide(1_000, "mystery");

        Assert.Equal(expectLaptopQualified, h.Eval(TLaptop)!.Qualified);
        Assert.True(h.Eval(TDesktop)!.Qualified);
        Assert.Equal(TDesktop, winner); // Desktop first in declaration order at the 0 tie
    }

    [Theory]
    [InlineData(2, 2, 180)]  // MaxParallel 2, 2 in-flight: 2×40 + cliff(2≥2)×100 = 180
    [InlineData(1, 2, 40)]   // MaxParallel 2, 1 in-flight: 1×40, no cliff = 40
    [InlineData(1, 1, 140)]  // MaxParallel 1, 1 in-flight: 1×40 + cliff = 140 (AC3)
    [InlineData(3, 2, 220)]  // MaxParallel 2, 3 in-flight: 3×40 + cliff = 220
    public async Task ConcurrencyPenalty_ScalesWithInFlightAndCliff(int inFlight, int maxParallel, int expectedPenalty)
    {
        // The concurrency penalty is (InFlight × 40) + (InFlight ≥ MaxParallel ? 100 : 0). A
        // single-target fleet isolates the term under test (nothing else scores).
        var single = new OllamaTargetOptions { Name = "S", Url = "http://s:11434", HardwareHint = "96gb", MaxParallel = maxParallel };
        var ts = new RoutingTarget("S");
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { single } };
        options.Models["mystery"] = new ModelThresholds();

        var (h, _) = Build(
            options,
            withEngine: true, // Stage 2 scoring runs (the permissive fixed-0 path skips it)
            catalogModels: new() { "http://s:11434|mystery" },
            onlineTargets: new() { "S" },
            states: new() { [ts] = State(ts, inFlight) });

        await h.Decide(1_000, "mystery");

        Assert.Equal(expectedPenalty, h.Eval(ts)!.ConcurrencyPenalty);
    }

    [Fact]
    public async Task Eviction_NoOtherModelLoaded_NoPenalty()
    {
        // The target has an empty /api/ps (nothing loaded): even though it is not
        // server-class and the model is not loaded, there is no OTHER model to evict, so
        // no penalty applies.
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, inFlight: 0), // empty /api/ps
                [TLaptop] = State(TLaptop, 0),
                [TServer] = State(TServer, 0)
            },
            meta: meta);

        await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(0, h.Eval(TDesktop)!.EvictionPenalty);
    }

    [Fact]
    public async Task Eviction_ServerClass_ProvableOverflow_StillPenalized()
    {
        // Per the Q1 VRAM-sum resolution: when the overflow is PROVABLE
        // (TotalVramBytes + weight > MaxVramMB, both known), the penalty applies to a
        // server-class target too — the server headroom exemption only kicks in when the
        // budget or the weight is UNKNOWN (we cannot prove eviction). The Server holds a
        // 90 GB model; loading the 16 GB request model overflows 96 GB (106 > 96) ⇒ −80.
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, 0),
                [TLaptop] = State(TLaptop, 0),
                [TServer] = State(TServer, inFlight: 0, ("other", 90 * Gb)) // server holds a big model
            },
            meta: meta);

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(80, h.Eval(TServer)!.EvictionPenalty); // provable overflow ⇒ penalized even server-class
        Assert.Equal(-80, h.Eval(TServer)!.Score);          // 0 + 0 role (≤48k ⇒ desktop-class) - 80 eviction
        Assert.Equal(0, h.Eval(TDesktop)!.EvictionPenalty);
        Assert.Equal(20, h.Eval(TDesktop)!.Score);
        Assert.Equal(TDesktop, winner);
    }

    [Fact]
    public async Task Stage1_StartupGraceExpired_NotYetOnline_DisqualifiesTarget()
    {
        // The liveness prober started 20 s ago (OUTSIDE the 10 s grace) and the Desktop's raw
        // state is still Unknown: the grace window has closed, so the target is offline and
        // disqualified.
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { Desktop } };
        options.Models["mystery"] = new ModelThresholds();

        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.IsOnline(It.IsAny<RoutingTarget>())).Returns(false);
        liveness.Setup(l => l.ServiceStartTimeUtc).Returns(DateTimeOffset.UtcNow.AddSeconds(-20));
        liveness.Setup(l => l.GetEntries()).Returns(Entries(TDesktop, TargetLivenessState.Unknown));

        var (h, _) = Build(options, withEngine: false, catalogModels: new() { $"{DesktopUrl}|mystery" }, onlineTargets: new(), livenessOverride: liveness);

        await Assert.ThrowsAsync<NoAvailableTargetException>(() => h.Decide(1_000, "mystery"));
        Assert.False(h.Eval(TDesktop)!.Qualified);
        Assert.StartsWith("offline", h.Eval(TDesktop)!.DisqualifiedReason);
    }

    // ==================================================================
    // Session affinity: sticky sessions (KV-cache), multi-agent isolation,
    // failover rebinding, Cloud never leased, 32 GB server class
    // ==================================================================

    private static SessionAffinityService CreateSessionAffinity() =>
        new SessionAffinityService(
            Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions { SessionAffinityTtlMinutes = 30 }),
            NullLogger<SessionAffinityService>.Instance);

    [Fact]
    public async Task SessionAffinity_Stickiness_OutRanksHotModelAffinity()
    {
        // Two equivalent idle server-class targets in declaration order; session "agent-1"
        // turn 1 wins aiserver1 (declaration order, score 0). Turn 2 the model is now HOT on
        // aiserver2 (+100) while aiserver1 is idle — agent-1 must STILL win aiserver1 with
        // the SessionAffinityBonus (+150), beating +100.
        var s1 = new OllamaTargetOptions { Name = "aiserver1", Url = "http://as1:11434", HardwareHint = "96gb", MaxParallel = 2 };
        var s2 = new OllamaTargetOptions { Name = "aiserver2", Url = "http://as2:11434", HardwareHint = "96gb", MaxParallel = 2 };
        var t1 = new RoutingTarget("aiserver1");
        var t2 = new RoutingTarget("aiserver2");
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { s1, s2 } };
        options.Models["qwen3.8-27B"] = new ModelThresholds();

        var meta = new MetaMap();
        meta.Add(s1.Url!, "qwen3.8-27B", DenseShow(27_800_000_000), 16 * Gb);
        meta.Add(s2.Url!, "qwen3.8-27B", DenseShow(27_800_000_000), 16 * Gb);

        var affinity = CreateSessionAffinity();
        var (h, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{s1.Url}|qwen3.8-27B", $"{s2.Url}|qwen3.8-27B" },
            onlineTargets: new() { "aiserver1", "aiserver2" },
            states: new() { [t1] = State(t1, 0), [t2] = State(t2, 0) },
            meta: meta,
            sessionAffinity: affinity);

        // Turn 1: no lease yet → 0-0 tie → aiserver1 by declaration order. The lease is written.
        var first = await h.Decide(30_000, "qwen3.8-27B", "agent-1");
        Assert.Equal(t1, first);
        Assert.True(affinity.TryGetAffinity("agent-1", out var pinned1));
        Assert.Equal(t1, pinned1);

        // Turn 2: the model is now HOT on aiserver2 (+100) but the session lease (+150)
        // keeps agent-1 on aiserver1.
        h.States[t1] = State(t1, inFlight: 0);
        h.States[t2] = State(t2, inFlight: 0, ("qwen3.8-27B", 16 * Gb));
        var second = await h.Decide(30_000, "qwen3.8-27B", "agent-1");

        Assert.Equal(t1, second);
        Assert.Equal(150, h.Eval(t1)!.SessionAffinityBonus);
        Assert.Equal(150, h.Eval(t1)!.Score);          // session only
        Assert.Equal(0, h.Eval(t2)!.SessionAffinityBonus);
        Assert.Equal(100, h.Eval(t2)!.AffinityBonus);  // hot on aiserver2, but 100 < 150
        Assert.Equal(100, h.Eval(t2)!.Score);
    }

    [Fact]
    public async Task SessionAffinity_MultiAgentIsolation_DistinctSessions_BindDistinctTargets()
    {
        // agent-1 (turn 1) binds aiserver1. agent-2 turn 1 with aiserver1 carrying in-flight
        // concurrency: agent-2's best is still aiserver2 (the pinned target of agent-1 does
        // NOT leak its bonus to agent-2; aiserver1's −140 concurrency makes aiserver2 win).
        // Interleaved turns keep each session on its own bound target.
        var s1 = new OllamaTargetOptions { Name = "aiserver1", Url = "http://as1:11434", HardwareHint = "96gb", MaxParallel = 1 };
        var s2 = new OllamaTargetOptions { Name = "aiserver2", Url = "http://as2:11434", HardwareHint = "96gb", MaxParallel = 1 };
        var t1 = new RoutingTarget("aiserver1");
        var t2 = new RoutingTarget("aiserver2");
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { s1, s2 } };
        options.Models["qwen3.8-27B"] = new ModelThresholds();

        var meta = new MetaMap();
        meta.Add(s1.Url!, "qwen3.8-27B", DenseShow(27_800_000_000), 16 * Gb);
        meta.Add(s2.Url!, "qwen3.8-27B", DenseShow(27_800_000_000), 16 * Gb);

        var affinity = CreateSessionAffinity();
        var (h, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{s1.Url}|qwen3.8-27B", $"{s2.Url}|qwen3.8-27B" },
            onlineTargets: new() { "aiserver1", "aiserver2" },
            states: new() { [t1] = State(t1, 0), [t2] = State(t2, 0) },
            meta: meta,
            sessionAffinity: affinity);

        // agent-1 turn 1: both idle → declaration order → aiserver1 (leased).
        var a1 = await h.Decide(30_000, "qwen3.8-27B", "agent-1");
        Assert.Equal(t1, a1);

        // agent-2 turn 1: aiserver1 is now busy (in-flight 1, MaxParallel 1 ⇒ −140);
        // agent-2 has no lease → aiserver2 (0) beats aiserver1 (−140) → aiserver2 (leased).
        h.States[t1] = State(t1, inFlight: 1);
        var b1 = await h.Decide(30_000, "qwen3.8-27B", "agent-2");
        Assert.Equal(t2, b1);
        Assert.Equal(0, h.Eval(t1)!.SessionAffinityBonus); // agent-2 sees no leak of agent-1's lease
        Assert.Equal(0, h.Eval(t2)!.SessionAffinityBonus);

        // Interleaved: both targets idle again. agent-1's +150 stays on aiserver1 and
        // agent-2's +150 on aiserver2 — no cross-talk, no bouncing.
        h.States[t1] = State(t1, inFlight: 0);
        h.States[t2] = State(t2, inFlight: 0);
        var a2 = await h.Decide(30_000, "qwen3.8-27B", "agent-1");
        Assert.Equal(t1, a2);
        Assert.Equal(150, h.Eval(t1)!.SessionAffinityBonus);
        Assert.Equal(0, h.Eval(t2)!.SessionAffinityBonus);

        var b2 = await h.Decide(30_000, "qwen3.8-27B", "agent-2");
        Assert.Equal(t2, b2);
        Assert.Equal(0, h.Eval(t1)!.SessionAffinityBonus);
        Assert.Equal(150, h.Eval(t2)!.SessionAffinityBonus);
    }

    [Fact]
    public async Task SessionAffinity_PinnedTargetGoesOffline_RebindsToFailoverTarget()
    {
        // agent-1 is bound to aiserver1; aiserver1 then goes OFFLINE (Stage-1 disqualified —
        // no affinity rescue): the session earns 0 there, wins aiserver2, and the lease is
        // rebound to aiserver2 on the same decision.
        var s1 = new OllamaTargetOptions { Name = "aiserver1", Url = "http://as1:11434", HardwareHint = "96gb", MaxParallel = 2 };
        var s2 = new OllamaTargetOptions { Name = "aiserver2", Url = "http://as2:11434", HardwareHint = "96gb", MaxParallel = 2 };
        var t1 = new RoutingTarget("aiserver1");
        var t2 = new RoutingTarget("aiserver2");
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { s1, s2 } };
        options.Models["qwen3.8-27B"] = new ModelThresholds();

        var meta = new MetaMap();
        meta.Add(s1.Url!, "qwen3.8-27B", DenseShow(27_800_000_000), 16 * Gb);
        meta.Add(s2.Url!, "qwen3.8-27B", DenseShow(27_800_000_000), 16 * Gb);

        var affinity = CreateSessionAffinity();
        var (h, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{s1.Url}|qwen3.8-27B", $"{s2.Url}|qwen3.8-27B" },
            onlineTargets: new() { "aiserver1", "aiserver2" },
            states: new() { [t1] = State(t1, 0), [t2] = State(t2, 0) },
            meta: meta,
            sessionAffinity: affinity);

        var first = await h.Decide(30_000, "qwen3.8-27B", "agent-1");
        Assert.Equal(t1, first);
        Assert.True(affinity.TryGetAffinity("agent-1", out var pinned));
        Assert.Equal(t1, pinned);

        // aiserver1 goes offline.
        var liveness = new Mock<ITargetLivenessService>();
        liveness.Setup(l => l.IsOnline(It.IsAny<RoutingTarget>())).Returns((RoutingTarget t) => t != t1);
        liveness.Setup(l => l.ServiceStartTimeUtc).Returns(DateTimeOffset.MinValue);
        liveness.Setup(l => l.GetEntries()).Returns(new Dictionary<RoutingTarget, TargetLivenessEntry>());

        var (h2, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{s1.Url}|qwen3.8-27B", $"{s2.Url}|qwen3.8-27B" },
            onlineTargets: new() { "aiserver1", "aiserver2" },
            states: new() { [t1] = State(t1, 0), [t2] = State(t2, 0) },
            meta: meta,
            livenessOverride: liveness,
            sessionAffinity: affinity);

        var second = await h2.Decide(30_000, "qwen3.8-27B", "agent-1");

        Assert.Equal(t2, second);
        Assert.False(h2.Eval(t1)!.Qualified); // offline ⇒ Stage-1 disqualified, no affinity rescue
        Assert.StartsWith("offline", h2.Eval(t1)!.DisqualifiedReason);
        Assert.Equal(0, h2.Eval(t2)!.SessionAffinityBonus); // bonus no longer applies (target dead)
        // The lease is rebound to the failover winner on the same decision.
        Assert.True(affinity.TryGetAffinity("agent-1", out var rebound));
        Assert.Equal(t2, rebound);
    }

    [Fact]
    public async Task SessionAffinity_FailureStreakSwitchOver_RebindsToSwitchedTarget()
    {
        // agent-1 is bound to aiserver1 (the score winner), but aiserver1's uncommitted
        // failure streak exceeds MaxConsecutiveFailures: the streak switch-over takes the
        // next-ranked target AND the lease is written on the FINAL (switched) winner.
        var s1 = new OllamaTargetOptions { Name = "aiserver1", Url = "http://as1:11434", HardwareHint = "96gb", MaxParallel = 2 };
        var s2 = new OllamaTargetOptions { Name = "aiserver2", Url = "http://as2:11434", HardwareHint = "96gb", MaxParallel = 2 };
        var t1 = new RoutingTarget("aiserver1");
        var t2 = new RoutingTarget("aiserver2");
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { s1, s2 } };
        options.Models["qwen3.8-27B"] = new ModelThresholds();

        var meta = new MetaMap();
        meta.Add(s1.Url!, "qwen3.8-27B", DenseShow(27_800_000_000), 16 * Gb);
        meta.Add(s2.Url!, "qwen3.8-27B", DenseShow(27_800_000_000), 16 * Gb);

        var affinity = CreateSessionAffinity();
        var (h, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{s1.Url}|qwen3.8-27B", $"{s2.Url}|qwen3.8-27B" },
            onlineTargets: new() { "aiserver1", "aiserver2" },
            states: new() { [t1] = State(t1, 0, ("qwen3.8-27B", 16 * Gb)), [t2] = State(t2, 0) },
            failureStreaks: t => t == t1 ? 5 : 0,
            meta: meta,
            sessionAffinity: affinity);

        var winner = await h.Decide(30_000, "qwen3.8-27B", "agent-1");

        // aiserver1 scores 250 (session 150 + affinity 100) but is streak-skipped; the lease
        // lands on the FINAL winner (aiserver2).
        Assert.Equal(t2, winner);
        Assert.True(affinity.TryGetAffinity("agent-1", out var rebound));
        Assert.Equal(t2, rebound);
    }

    [Fact]
    public async Task SessionAffinity_CloudWinner_IsNeverLeased()
    {
        // All fleet offline + Cloud configured + session present: the winner is the Cloud
        // pseudo-target and NO lease is created (the session re-decides next turn and rejoins
        // the fleet as soon as a node recovers).
        var s1 = new OllamaTargetOptions { Name = "aiserver1", Url = "http://as1:11434", HardwareHint = "96gb", MaxParallel = 2 };
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { s1 } };
        options.Models["qwen3.8-27B"] = new ModelThresholds { CloudModel = "deepseek-v3.1:671b-cloud" };

        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var affinity = CreateSessionAffinity();
        var (h, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{s1.Url}|qwen3.8-27B" },
            onlineTargets: new() { }, // all fleet offline
            meta: meta,
            sessionAffinity: affinity);

        var winner = await h.Decide(30_000, "qwen3.8-27B", "agent-1");

        Assert.Equal(RoutingTarget.Cloud, winner);
        Assert.False(affinity.TryGetAffinity("agent-1", out _)); // never leased
    }

    [Fact]
    public async Task SessionAffinity_NoSessionId_LegacyStatelessScores()
    {
        // sessionId: null ⇒ byte-identical legacy behavior: the SessionAffinityBonus field
        // is 0 on every candidate even when a lease exists for some other session.
        var s1 = new OllamaTargetOptions { Name = "aiserver1", Url = "http://as1:11434", HardwareHint = "96gb", MaxParallel = 2 };
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { s1 } };
        options.Models["qwen3.8-27B"] = new ModelThresholds();

        var meta = new MetaMap();
        meta.Add(s1.Url!, "qwen3.8-27B", DenseShow(27_800_000_000), 16 * Gb);

        var affinity = CreateSessionAffinity();
        affinity.SetAffinity("other-agent", new RoutingTarget("aiserver1"));
        var (h, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{s1.Url}|qwen3.8-27B" },
            onlineTargets: new() { "aiserver1" },
            states: new() { [new RoutingTarget("aiserver1")] = State(new RoutingTarget("aiserver1"), 0) },
            meta: meta,
            sessionAffinity: affinity);

        var winner = await h.Decide(30_000, "qwen3.8-27B", (string?)null);

        Assert.Equal(new RoutingTarget("aiserver1"), winner);
        Assert.Equal(0, h.Eval(new RoutingTarget("aiserver1"))!.SessionAffinityBonus);
        Assert.Equal(0, h.Eval(new RoutingTarget("aiserver1"))!.Score);
    }

    [Fact]
    public async Task SessionAffinity_NoAffinityService_LegacyStatelessScores()
    {
        // A null ISessionAffinityService (pre-affinity test wiring) ⇒ no bonus, no throw,
        // and the 9-arg construction stays source-compatible.
        var seedMeta = new MetaMap();
        var (h, meta) = Build(
            FleetOptions("qwen3.8-27B"),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{LaptopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Laptop", "Server" },
            meta: AddDense(seedMeta, "qwen3.8-27B", 27_800_000_000));

        var winner = await h.Decide(30_000, "qwen3.8-27B", "agent-1");

        Assert.Equal(TDesktop, winner);
        Assert.Equal(0, h.Eval(TDesktop)!.SessionAffinityBonus);
    }

    [Fact]
    public async Task HardwareBonus_64GBServerClass_QualifiedServerScoresNoRoleBonus()
    {
        // A 64 GB target (MaxVramMB 65536) is Server-class (≥ 32 GB threshold). A LargeDense
        // request of 60k tokens needs 68,192: the 16 GB Desktop pinned at 49,152 is
        // context-overflow disqualified, while both server-class targets (the assumed 131,072
        // fallback) qualify. Under the task #25 role rule a server that FITS the request in its
        // own CMax scores no role bonus (0, never the over-capacity role) — the bonus is no
        // longer awarded by the retired hardcoded desktop-ceiling comparison; the first
        // server-class target in declaration order wins the tie at 0.
        var s64 = new OllamaTargetOptions { Name = "aiserver2", Url = "http://as64:11434", MaxVramMB = 65_536, MaxParallel = 2 };
        var s32 = new OllamaTargetOptions { Name = "aiserver3", Url = "http://as32:11434", MaxVramMB = 32_768, MaxParallel = 2 };
        var options = new OllamaRouterOptions { Targets = new List<OllamaTargetOptions> { Desktop, s64, s32 } };
        options.Models["qwen3.8-27B"] = new ModelThresholds();

        var meta = new MetaMap();
        meta.Add(DesktopUrl, "qwen3.8-27B", PinnedDenseShow(27_800_000_000, modelfileNumCtx: 49_152), 16 * Gb);
        meta.Add(s64.Url!, "qwen3.8-27B", DenseShow(27_800_000_000), 16 * Gb);
        meta.Add(s32.Url!, "qwen3.8-27B", DenseShow(27_800_000_000), 16 * Gb);

        var t64 = new RoutingTarget("aiserver2");
        var t32 = new RoutingTarget("aiserver3");
        var (h, _) = Build(
            options,
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{s64.Url}|qwen3.8-27B", $"{s32.Url}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "aiserver2", "aiserver3" },
            states: new() { [TDesktop] = State(TDesktop, 0), [t64] = State(t64, 0), [t32] = State(t32, 0) },
            meta: meta);

        var winner = await h.Decide(60_000, "qwen3.8-27B");

        Assert.False(h.Eval(TDesktop)!.Qualified); // 68,192 > 49,152 own CMax ⇒ context overflow
        Assert.StartsWith("context overflow", h.Eval(TDesktop)!.DisqualifiedReason);
        Assert.True(h.Eval(t64)!.Qualified);
        Assert.Equal(0, h.Eval(t64)!.HardwareBonus); // 64 GB = Server class, but it FITS its own 131,072 ⇒ no over-capacity role
        Assert.True(h.Eval(t32)!.Qualified);
        Assert.Equal(0, h.Eval(t32)!.HardwareBonus); // 32 GB = Server class too
        Assert.Equal(t64, winner); // first server-class in declaration order at the 0 tie
    }

    [Fact]
    public void ServerClassThreshold_IsSharedConstant()
    {
        // Single source of truth: the 32 GB boundary constant is exposed by the sizing engine
        // and reused by the capability engine's ceiling classes.
        Assert.Equal(32_768, DynamicContextSizingEngine.ServerClassVramThresholdMB);
    }

    // ---- helpers ----

    // ==================================================================
    // Speed bonus (SpeedFactor): per-decision normalized Stage-2 SpeedBonus
    // ==================================================================

    private static OllamaRouterOptions SpeedOptions(string model, double desktopSpeed, double serverSpeed)
    {
        var options = new OllamaRouterOptions
        {
            Targets = new List<OllamaTargetOptions>
            {
                new() { Name = "Desktop", Url = DesktopUrl, HardwareHint = "16gb", MaxParallel = 1, SpeedFactor = desktopSpeed },
                new() { Name = "Server", Url = ServerUrl, HardwareHint = "96gb", MaxParallel = 2, SpeedFactor = serverSpeed }
            },
            Models = new Dictionary<string, ModelThresholds> { [model] = new ModelThresholds() }
        };
        return options;
    }

    [Fact]
    public async Task Speed_ColdFastNode_BeatsHotSlowerNode()
    {
        // Desktop (SpeedFactor 2.0) is cold; Server holds the model hot (+100). The
        // per-decision normalized speed bonus (maxHint 2.0 ⇒ full +150 on Desktop)
        // outranks the affinity: the faster cold node wins.
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            SpeedOptions("qwen3.8-27B", desktopSpeed: 2.0, serverSpeed: 1.0),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, 0),
                [TServer] = State(TServer, 0, ("qwen3.8-27B", 16 * Gb))
            },
            meta: meta);

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(TDesktop, winner);
        Assert.Equal(150, h.Eval(TDesktop)!.SpeedBonus);       // maxHint 2.0 ⇒ full bonus
        Assert.Equal(0, h.Eval(TServer)!.SpeedBonus);          // baseline factor ⇒ 0
        Assert.Equal(170, h.Eval(TDesktop)!.Score);            // 150 speed + 20 role + 0
        Assert.Equal(100, h.Eval(TServer)!.Score);             // hot, no speed
        Assert.Contains("speed=+150 (factor 2)", h.Eval(TDesktop)!.Detail!);
        Assert.DoesNotContain("speed=", h.Eval(TServer)!.Detail!);
    }

    [Fact]
    public async Task Speed_NoFactorsConfigured_AllSpeedBonusesZero_LegacyScores()
    {
        // Regression lock: with no SpeedFactor configured (all default 1.0) every
        // SpeedBonus is 0 and the scores are byte-identical to the legacy formula.
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            SpeedOptions("qwen3.8-27B", desktopSpeed: 1.0, serverSpeed: 1.0),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, 0),
                [TServer] = State(TServer, 0, ("qwen3.8-27B", 16 * Gb))
            },
            meta: meta);

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(TServer, winner); // hot +100 wins on the legacy formula
        Assert.Equal(0, h.Eval(TDesktop)!.SpeedBonus);
        Assert.Equal(0, h.Eval(TServer)!.SpeedBonus);
        Assert.Equal(20, h.Eval(TDesktop)!.Score);   // legacy: role only
        Assert.Equal(100, h.Eval(TServer)!.Score);   // legacy: affinity only
        Assert.DoesNotContain("speed=", h.Eval(TDesktop)!.Detail!);
        Assert.DoesNotContain("speed=", h.Eval(TServer)!.Detail!);
    }

    [Fact]
    public async Task Speed_IntermediateFactors_NormalizationScalesLinearly()
    {
        // Factors 1.5 (Desktop) vs 2.0 (Server): maxHint 2.0 ⇒ Desktop round(150 × 0.5 / 1.0) = 75,
        // Server full 150 (MidpointRounding.AwayFromZero).
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            SpeedOptions("qwen3.8-27B", desktopSpeed: 1.5, serverSpeed: 2.0),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, 0),
                [TServer] = State(TServer, 0)
            },
            meta: meta);

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(75, h.Eval(TDesktop)!.SpeedBonus);
        Assert.Equal(150, h.Eval(TServer)!.SpeedBonus);
        Assert.Equal(TServer, winner);
    }

    [Fact]
    public async Task Speed_FastEvictingNode_LosesToHotSlowerNode()
    {
        // Anti-thrash: Desktop (SpeedFactor 2.0) is cold and its load would evict the other
        // loaded model (16 GiB free, 16 GiB other model + 16 GiB new > 16 GiB ⇒ −80):
        // 150 − 80 = 70 + 20 role = 90 < Server hot 100 ⇒ the hot slower node wins.
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            SpeedOptions("qwen3.8-27B", desktopSpeed: 2.0, serverSpeed: 1.0),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, 0, ("llama3:8b", 16 * Gb)), // 16 GiB loaded ⇒ new 16 GiB load evicts
                [TServer] = State(TServer, 0, ("qwen3.8-27B", 16 * Gb))
            },
            meta: meta);

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(150, h.Eval(TDesktop)!.SpeedBonus);
        Assert.Equal(80, h.Eval(TDesktop)!.EvictionPenalty);
        Assert.Equal(90, h.Eval(TDesktop)!.Score);   // 150 + 20 role − 80 = 90
        Assert.Equal(100, h.Eval(TServer)!.Score);   // hot
        Assert.Equal(TServer, winner);               // 90 < 100: anti-thrash preserved
    }

    [Fact]
    public async Task Speed_FastNodeAtConcurrencyCliff_LosesToIdleHotSlowerNode()
    {
        // Desktop (SpeedFactor 2.0) at its MaxParallel cliff (1 in-flight ⇒ −140):
        // 150 + 20 role − 140 = 30 < Server hot 100 ⇒ the idle hot slower node wins.
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var (h, _) = Build(
            SpeedOptions("qwen3.8-27B", desktopSpeed: 2.0, serverSpeed: 1.0),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, inFlight: 1), // MaxParallel 1 ⇒ cliff −140
                [TServer] = State(TServer, 0, ("qwen3.8-27B", 16 * Gb))
            },
            meta: meta);

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(150, h.Eval(TDesktop)!.SpeedBonus);
        Assert.Equal(140, h.Eval(TDesktop)!.ConcurrencyPenalty);
        Assert.Equal(30, h.Eval(TDesktop)!.Score);   // 150 + 20 role − 140 = 30
        Assert.Equal(100, h.Eval(TServer)!.Score);
        Assert.Equal(TServer, winner);
    }

    [Fact]
    public async Task Speed_CloudPseudoTarget_NeverEarnsSpeedBonus()
    {
        // Cloud stays fixed-score-0 with SpeedBonus 0 and no speed suffix in its Detail;
        // the +150-speed fleet candidate still beats Cloud (150 > 0).
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var optionsWithCloud = SpeedOptions("qwen3.8-27B", 2.0, 1.0);
        optionsWithCloud.Models["qwen3.8-27B"].CloudModel = "glm-5.3-flash:cloud";
        var (h, _) = Build(
            optionsWithCloud,
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, 0),
                [TServer] = State(TServer, 0)
            },
            meta: meta);

        var winner = await h.Decide(30_000, "qwen3.8-27B");

        Assert.Equal(150, h.Eval(TDesktop)!.SpeedBonus);
        Assert.Equal(0, h.Eval(RoutingTarget.Cloud)!.Score);
        Assert.Equal(0, h.Eval(RoutingTarget.Cloud)!.SpeedBonus);
        Assert.DoesNotContain("speed=", h.Eval(RoutingTarget.Cloud)!.Detail!);
        Assert.Equal(TDesktop, winner); // 150 > 0: the fast fleet candidate beats Cloud
    }

    [Fact]
    public async Task Speed_SessionLeasedHotNode_BeatsFasterColdNode()
    {
        // Pins the 250 calibration: a session-leased HOT Server (150 + 100 = 250) still
        // outranks the faster cold Desktop (150 speed only) — stickiness beats speed.
        var meta = AddDense(new MetaMap(), "qwen3.8-27B", 27_800_000_000);
        var affinity = CreateSessionAffinity();
        affinity.SetAffinity("agent-1", TServer);
        var (h, _) = Build(
            SpeedOptions("qwen3.8-27B", desktopSpeed: 2.0, serverSpeed: 1.0),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|qwen3.8-27B", $"{ServerUrl}|qwen3.8-27B" },
            onlineTargets: new() { "Desktop", "Server" },
            states: new()
            {
                [TDesktop] = State(TDesktop, 0),
                [TServer] = State(TServer, 0, ("qwen3.8-27B", 16 * Gb)) // hot AND leased
            },
            meta: meta,
            sessionAffinity: affinity);

        var winner = await h.Decide(30_000, "qwen3.8-27B", "agent-1");

        Assert.Equal(TServer, winner);
        Assert.Equal(250, h.Eval(TServer)!.Score);              // 150 session + 100 affinity
        Assert.Equal(0, h.Eval(TServer)!.SpeedBonus);
        Assert.Equal(150, h.Eval(TDesktop)!.SpeedBonus);
        Assert.Equal(170, h.Eval(TDesktop)!.Score);             // 150 speed + 20 role
    }

    [Fact]
    public async Task Speed_Stage1Invariance_OverCeilingRequestStillDisqualifiesFastNode()
    {
        // The speed hint can never route an over-ceiling request to the faster node: the
        // Modelfile pins CMax at 4096, so Desktop (SpeedFactor 2.0) is disqualified on
        // context overflow BEFORE any speed bonus applies (no speed suffix in its Detail).
        var meta = new MetaMap();
        meta.Add(DesktopUrl, "m", new JsonObject
        {
            ["model_info"] = new JsonObject { ["general.parameter_count"] = (JsonNode)27_800_000_000L },
            ["parameters"] = "num_ctx 4096"
        }, 16 * Gb);
        meta.Add(ServerUrl, "m", DenseShow(27_800_000_000), 16 * Gb);

        var (h, _) = Build(
            SpeedOptions("m", desktopSpeed: 2.0, serverSpeed: 1.0),
            withEngine: true,
            catalogModels: new() { $"{DesktopUrl}|m", $"{ServerUrl}|m" },
            onlineTargets: new() { "Desktop", "Server" },
            states: new() { [TDesktop] = State(TDesktop, 0), [TServer] = State(TServer, 0) },
            meta: meta);

        var winner = await h.Decide(30_000, "m");

        Assert.False(h.Eval(TDesktop)!.Qualified);
        Assert.StartsWith("context overflow", h.Eval(TDesktop)!.DisqualifiedReason);
        Assert.Equal(0, h.Eval(TDesktop)!.SpeedBonus);
        Assert.DoesNotContain("speed=", h.Eval(TDesktop)!.Detail!);
        Assert.True(h.Eval(TServer)!.Qualified);
        Assert.Equal(0, h.Eval(TServer)!.SpeedBonus); // only Desktop had a factor ⇒ maxHint over QUALIFIED set is 1.0
        Assert.Equal(TServer, winner);
    }

    // ==================================================================
    // Speed bonus tests end; shared helpers below
    // ==================================================================

    [Fact]
    public void NormalizeModelName_StripsTagSuffix()
    {
        Assert.Equal("llama3", RoutingDecisionService.NormalizeModelName("llama3:8b"));
        Assert.Equal("Qwen3.8-27B", RoutingDecisionService.NormalizeModelName("Qwen3.8-27B:latest"));
        Assert.Equal("plain", RoutingDecisionService.NormalizeModelName("plain"));
    }

    private static MetaMap AddDense(MetaMap meta, string model, long paramCount)
    {
        foreach (var url in new[] { DesktopUrl, LaptopUrl, ServerUrl })
        {
            meta.Add(url, model, DenseShow(paramCount), 16 * Gb);
        }

        return meta;
    }

    private static MetaMap AddMoe(MetaMap meta, string model, long paramCount)
    {
        foreach (var url in new[] { DesktopUrl, LaptopUrl, ServerUrl })
        {
            meta.Add(url, model, MoeShow(paramCount, expertCount: 64), 24 * Gb);
        }

        return meta;
    }
}
