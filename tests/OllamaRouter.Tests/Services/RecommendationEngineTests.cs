using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Recommendations;
using System.Text;
using System.Text.Json;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// Unit tests for the native role recommendation engine (task #66, hardware-capability-aware
/// since task #168): the deterministic assignment algorithm (terminal-status gate, the 128k
/// per-(model, node) eligibility floor, largest-viable-row dense ceilings, MoE baseline +
/// native extensible limit, class-staged role pools with the QualityRank / role-family /
/// max-ceiling / max-tps / catalog-order comparator, role absence) and the live
/// <see cref="RecommendationStore"/> (fail-soft load, ceiling lookups with
/// tag-stripped/case-insensitive model matching, atomic activation, JSON round-trip).
/// Pure in-memory fixtures — no daemons, no network.
/// </summary>
public class RecommendationEngineTests
{
    private static readonly DateTimeOffset FixedStamp = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private const string Desktop = "Desktop";
    private const string AiServer = "Aiserver";

    // ------------------------------------------------------------------
    //  Fixture builders
    // ------------------------------------------------------------------

    private static BenchmarkContextResult Row(string model, int numCtx, double tps, bool viable = true, string? reason = null) =>
        new(model, UsageRole.AgenticChat, numCtx, 64, (long)(64e9 / tps), tps, 5_000, Viable: viable, Reason: reason);

    /// <summary>
    /// A sizing-pruned row in the exact shape the runner records for a model that cannot fit
    /// on the node (all-zero measurements, non-viable, "sizing-pruned" reason, task #166).
    /// The engine only reads Viable == true rows, so this is behaviorally identical to zero
    /// rows (task #168 decision 6) — used where it documents intent.
    /// </summary>
    private static BenchmarkContextResult PrunedRow(string model, int numCtx = 32_768) =>
        new(model, UsageRole.AgenticChat, numCtx, 0, 0, 0, 0, Viable: false, Reason: "sizing-pruned");

    /// <summary>
    /// A synthetic all-5-role MoE model (the catalog's universal-fallback shape) for the
    /// single-model boundary catalogs (task #168 floor-boundary facts).
    /// </summary>
    private static CandidateModel AllRoleMoe(string name) => new(
        name,
        [UsageRole.BasicModel, UsageRole.JuniorDeveloper, UsageRole.AgenticChat,
         UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper],
        "MoE", 35_000_000_000, 3_200_000_000, "q4", 4, 41, 2, 128, 262_144, 131_072,
        ModelClass: CandidateModelClass.MoE);

    private static NodeHardwareProfile Profile(string name, long totalFastMemoryMB) => new(
        name, "linux", "x64", false, 32_768, SystemRamType.Ddr5,
        [new GpuDeviceProfile("Nvidia", "GeForce", (int)totalFastMemoryMB, "GDDR6", 256, 448)],
        totalFastMemoryMB, 448.0);

    private static readonly IReadOnlyList<CandidateModel> Catalog = CandidateModelCatalogOptions.DefaultModels;

    /// <summary>
    /// The spec example fleet (task #166 names): jetelain/Qwen3.8-27B:latest viable to
    /// 49 152 on Desktop and 262 144 on Aiserver (evidence at the 262 144 catalog native);
    /// jetelain/Qwen3.6-35B-A3B:q4 viable to 131 072 everywhere; jetelain/Flint:q4 (MoE)
    /// viable to 65 536 on Desktop (next row non-viable) / 131 072 on Aiserver.
    /// </summary>
    private static IReadOnlyList<BenchmarkTargetResult> DefaultFleetResults() =>
    [
        new BenchmarkTargetResult(Desktop, BenchmarkRunStatus.Succeeded,
            FixedStamp.AddMinutes(-20), FixedStamp.AddMinutes(-10),
        [
            Row("jetelain/Qwen3.6-35B-A3B:q4", 16_384, 90.0),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 32_768, 84.0),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 49_152, 77.0),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 65_536, 70.0),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 131_072, 48.0),
            Row("jetelain/Qwen3.8-27B:latest", 16_384, 45.0),
            Row("jetelain/Qwen3.8-27B:latest", 32_768, 40.0),
            Row("jetelain/Qwen3.8-27B:latest", 49_152, 33.0),
            Row("jetelain/Qwen3.8-27B:latest", 65_536, 12.0, viable: false, reason: "below-threshold"),
            Row("jetelain/Flint:q4", 16_384, 55.0),
            Row("jetelain/Flint:q4", 32_768, 51.0),
            Row("jetelain/Flint:q4", 65_536, 42.0),
            Row("jetelain/Flint:q4", 131_072, 14.0, viable: false, reason: "below-threshold")
        ]),
        new BenchmarkTargetResult(AiServer, BenchmarkRunStatus.Succeeded,
            FixedStamp.AddMinutes(-10), FixedStamp,
        [
            Row("jetelain/Qwen3.6-35B-A3B:q4", 16_384, 95.0),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 32_768, 91.0),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 49_152, 88.0),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 65_536, 82.0),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 131_072, 61.0),
            Row("jetelain/Qwen3.8-27B:latest", 16_384, 30.0),
            Row("jetelain/Qwen3.8-27B:latest", 32_768, 29.0),
            Row("jetelain/Qwen3.8-27B:latest", 49_152, 28.0),
            Row("jetelain/Qwen3.8-27B:latest", 65_536, 27.0),
            Row("jetelain/Qwen3.8-27B:latest", 131_072, 25.5),
            Row("jetelain/Qwen3.8-27B:latest", 184_320, 24.0),
            Row("jetelain/Qwen3.8-27B:latest", 262_144, 22.5),
            Row("jetelain/Flint:q4", 16_384, 60.0),
            Row("jetelain/Flint:q4", 32_768, 57.0),
            Row("jetelain/Flint:q4", 65_536, 49.0),
            Row("jetelain/Flint:q4", 131_072, 38.0),
            Row("jetelain/Flint:q4", 131_072, 38.0)
        ])
    ];

    // ------------------------------------------------------------------
    //  Engine: per-node ceilings
    // ------------------------------------------------------------------

    [Fact]
    public void Generate_DensePerNodeCeilings_LargestViableRowWins_EvidenceMayExceedNative()
    {
        var engine = new RecommendationEngine();
        // Catalog restricted to the curated dense flagship so its per-node shape is exercised
        // through the roles it carries alone (it would win the expert roles anyway — Expert
        // stage, rank 2 — with the full catalog).
        var denseCatalog = new[]
        {
            CandidateModelCatalogOptions.DefaultModels.First(m => m.Name == "jetelain/Qwen3.8-27B:latest")
        };
        var matrix = engine.Generate(
            DefaultFleetResults(), denseCatalog,
            hardwareProfileLookup: name => name switch
            {
                Desktop => Profile(Desktop, 16_384),
                AiServer => Profile(AiServer, 98_304),
                _ => null
            },
            generatedAt: FixedStamp);

        var qwen = matrix.Models["jetelain/Qwen3.8-27B:latest"];
        Assert.Equal("Dense", qwen.Architecture);
        Assert.Equal(262_144, qwen.NativeContext);

        // Desktop's 49 152 viable ceiling is BELOW the 128k role-eligibility floor (task #168)
        // → the (model, Desktop) pair is dropped; only the eligible Aiserver node remains in
        // the winner's Nodes list.
        Assert.Equal([AiServer], qwen.Nodes.Select(n => n.Target).ToList());

        var server = qwen.Nodes[0];
        Assert.Equal(262_144, server.ContextCeiling);        // evidence at the 262 144 native
        Assert.Null(server.BaselineContext);                 // dense: no baseline bucket
        Assert.Equal(22.5, server.TokensPerSecond);          // tps of the 262 144 row
        Assert.Equal(98_304, server.VramMB);
    }

    [Fact]
    public void Generate_MoeShape_NativeContextCeiling_WithLargestViableBaseline()
    {
        var engine = new RecommendationEngine();
        // Catalog restricted to the curated MoE model so the MoE node shape is exercised
        // through the roles it carries alone.
        var moeCatalog = new[]
        {
            CandidateModelCatalogOptions.DefaultModels.First(m => m.Name == "jetelain/Flint:q4")
        };
        var matrix = engine.Generate(DefaultFleetResults(), moeCatalog, generatedAt: FixedStamp);

        var moe = matrix.Models["jetelain/Flint:q4"];
        Assert.Equal("MoE", moe.Architecture);

        // Desktop's 65 536 viable ceiling is below the 128k floor (task #168) → pair dropped;
        // only Aiserver (largest viable row 131 072) survives.
        Assert.Equal([AiServer], moe.Nodes.Select(n => n.Target).ToList());
        var node = moe.Nodes[0];
        Assert.Equal(262_144, node.ContextCeiling);      // the model's native context (single extensible bucket)
        Assert.Equal(131_072, node.BaselineContext);     // largest viable row, ≥ floor by construction
        Assert.Equal(38.0, node.TokensPerSecond);
    }

    [Fact]
    public void Generate_DefaultFixture_Post168_HardwareAwareWinners()
    {
        // Post-#168 hardware-aware winners for the default fixture: the 128k floor drops the
        // Desktop nodes (Qwen3.8-27B:latest ceiling 49 152, Flint:q4 ceiling 65 536), leaving
        // eligible pairs: Qwen3.6-35B-A3B:q4 on BOTH nodes, Qwen3.8-27B:latest + Flint:q4 on
        // Aiserver only. basic → Qwen3.6:q4 (Q4-tier tie, Qwen3.6 family preferred);
        // junior → Flint:q4 (Flint family preferred); all three expert roles →
        // Qwen3.8-27B:latest (Expert stage, rank 2).
        var engine = new RecommendationEngine();
        var matrix = engine.Generate(DefaultFleetResults(), Catalog, generatedAt: FixedStamp);

        Assert.Equal(5, matrix.Roles.Count);
        Assert.Equal("jetelain/Qwen3.6-35B-A3B:q4", matrix.Roles["basic"].Model);
        Assert.Equal(262_144, matrix.Roles["basic"].RecommendedContextWindow);
        Assert.Equal([Desktop, AiServer], matrix.Roles["basic"].Nodes.Select(n => n.Target).ToList());

        Assert.Equal("jetelain/Flint:q4", matrix.Roles["junior_developer"].Model);
        Assert.Equal(262_144, matrix.Roles["junior_developer"].RecommendedContextWindow);
        Assert.Equal([AiServer], matrix.Roles["junior_developer"].Nodes.Select(n => n.Target).ToList());

        foreach (var alias in new[] { "agentic_chat", "software_architect", "senior_developer" })
        {
            Assert.Equal("jetelain/Qwen3.8-27B:latest", matrix.Roles[alias].Model);
            Assert.Equal(262_144, matrix.Roles[alias].RecommendedContextWindow);
            Assert.Equal([AiServer], matrix.Roles[alias].Nodes.Select(n => n.Target).ToList());
        }

        // The Models index covers exactly the three winners.
        Assert.Equal(3, matrix.Models.Count);
        Assert.All(matrix.Roles.Values, r => Assert.False(string.IsNullOrWhiteSpace(r.Description)));
    }

    [Fact]
    public void Generate_VramMBNull_WhenNoHardwareProfileKnown()
    {
        var engine = new RecommendationEngine();
        var matrix = engine.Generate(DefaultFleetResults(), Catalog, generatedAt: FixedStamp);
        Assert.All(matrix.Models.Values, m => Assert.All(m.Nodes, n => Assert.Null(n.VramMB)));
    }

    // ------------------------------------------------------------------
    //  Engine: selection scoring + tie-breaks
    // ------------------------------------------------------------------

    private static CandidateModel Model(string name, IReadOnlyList<UsageRole> roles, string architecture = "Dense", int nativeContext = 131_072) =>
        new(name, roles, architecture, 8_000_000_000, null, "q4", 4, 36, 8, 128, nativeContext, 32_768);

    [Fact]
    public void Generate_Scoring_HighestMaxCeilingWins_ThenTps_ThenCatalogOrder()
    {
        var catalog = new List<CandidateModel>
        {
            // Two candidates tagged to the same role (SeniorDeveloper).
            Model("small:7b", [UsageRole.SeniorDeveloper]),
            Model("big:14b", [UsageRole.SeniorDeveloper]),
            Model("sameA:7b", [UsageRole.SoftwareArchitect]),
            Model("sameB:7b", [UsageRole.SoftwareArchitect])
        };

        // Synthetic rows all clear the 128k eligibility floor (task #168).
        var results = new List<BenchmarkTargetResult>
        {
            new(Desktop, BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
            [
                Row("small:7b", 131_072, 80.0),
                Row("big:14b", 131_072, 45.0),
                Row("big:14b", 163_840, 40.0),   // big wins: higher max ceiling (163 840 > 131 072)
                Row("sameA:7b", 131_072, 55.0),
                Row("sameB:7b", 131_072, 65.0)   // tie on ceiling (131 072) → sameB wins on tps (65 > 55)
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, catalog, generatedAt: FixedStamp);
        Assert.Equal("big:14b", matrix.Roles["senior_developer"].Model);
        Assert.Equal(163_840, matrix.Roles["senior_developer"].RecommendedContextWindow);
        Assert.Equal("sameB:7b", matrix.Roles["software_architect"].Model);
    }

    [Fact]
    public void Generate_Scoring_TieOnCeilingAndTps_CatalogOrderWins()
    {
        var catalog = new List<CandidateModel>
        {
            Model("first:7b", [UsageRole.SeniorDeveloper]),
            Model("second:7b", [UsageRole.SeniorDeveloper])
        };
        var results = new List<BenchmarkTargetResult>
        {
            new(Desktop, BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
            [
                Row("first:7b", 131_072, 55.0),
                Row("second:7b", 131_072, 55.0)   // full tie → catalog declaration order (first)
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, catalog, generatedAt: FixedStamp);
        Assert.Equal("first:7b", matrix.Roles["senior_developer"].Model);
    }

    // ------------------------------------------------------------------
    //  Engine: exclusions
    // ------------------------------------------------------------------

    [Fact]
    public void Generate_RunningTargetsIgnored_NoViableNodesDropped_RoleAbsentWhenNoViableModel()
    {
        var results = new List<BenchmarkTargetResult>
        {
            // Running target: its ladder is incomplete → excluded entirely (its only viable
            // qwen row must not contribute).
            new BenchmarkTargetResult(Desktop, BenchmarkRunStatus.Running, FixedStamp, null,
            [
                Row("jetelain/Qwen3.8-27B:latest", 262_144, 99.0)
            ]),
            // Completed target: Qwen3.6-35B-A3B:q4 is fully non-viable (dropped), the dense
            // flagship is eligible on Aiserver only (131 072 @ 28.0 clears the 128k floor);
            // basic + junior have NO eligible MoE anywhere → the roles are ABSENT.
            new BenchmarkTargetResult(AiServer, BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
            [
                Row("jetelain/Qwen3.6-35B-A3B:q4", 16_384, 10.0, viable: false, reason: "below-threshold"),
                Row("jetelain/Qwen3.8-27B:latest", 131_072, 28.0)
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, Catalog, generatedAt: FixedStamp);

        Assert.False(matrix.Roles.ContainsKey("basic"));              // no eligible MoE anywhere
        Assert.False(matrix.Roles.ContainsKey("junior_developer"));   // no MoE data at all
        var qwen = matrix.Roles["agentic_chat"];
        Assert.Equal("jetelain/Qwen3.8-27B:latest", qwen.Model);
        Assert.Equal(131_072, qwen.RecommendedContextWindow);
        // The node list contains ONLY Aiserver (Desktop was Running; its 262 144 row is gone).
        Assert.Equal([AiServer], qwen.Nodes.Select(n => n.Target).ToList());
        // The Models index covers only the winners.
        Assert.Single(matrix.Models);
        Assert.True(matrix.Models.ContainsKey("jetelain/Qwen3.8-27B:latest"));
    }

    [Fact]
    public void Generate_ZeroTerminalResults_EmptyMatrix()
    {
        var results = new List<BenchmarkTargetResult>
        {
            new BenchmarkTargetResult(Desktop, BenchmarkRunStatus.Running, FixedStamp, null,
            [
                Row("jetelain/Qwen3.8-27B:latest", 16_384, 30.0)
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, Catalog, generatedAt: FixedStamp);
        Assert.Empty(matrix.Roles);
        Assert.Empty(matrix.Models);
        Assert.Equal(FixedStamp, matrix.GeneratedAt);
    }

    [Fact]
    public void Generate_GeneratedAtNullDefaultsToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var matrix = new RecommendationEngine().Generate(
            DefaultFleetResults(), Catalog, generatedAt: null);
        var after = DateTimeOffset.UtcNow;
        Assert.NotNull(matrix.GeneratedAt);
        Assert.True(matrix.GeneratedAt >= before && matrix.GeneratedAt <= after);
    }

    // ------------------------------------------------------------------
    //  Engine: fleet topologies (task #168, synthetic ladders anchored to the measured
    //  8 GB / 16 GB / 64 GB / 96 GB breakpoints)
    // ------------------------------------------------------------------

    [Fact]
    public void Generate_Fleet8Gb_BothQ4MoeEligible_AllFiveRolesViable_FlintWinsArchitectJunior()
    {
        // 8 GB GPU + 64 GB RAM: all three Expert models are sizing-pruned, the Q8
        // Qwen3.6-35B-A3B:code peaks at 98 304 (112k boundary case — the 131 072 row drops to
        // 17.8 t/s) → dropped by the 128k floor. Both Q4 MoEs are ≥128k-eligible, so per the
        // decision-5 binding rule: basic + agentic_chat → Qwen3.6-35B-A3B:q4 (family
        // preference), junior + architect + senior → Flint:q4 (family preference).
        // ZERO empty expert roles.
        var results = new List<BenchmarkTargetResult>
        {
            new("Gpu8", BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
            [
                PrunedRow("jetelain/Qwen3.8-27B:latest"),
                PrunedRow("jetelain/Qwen3.8-27B:iq3"),
                PrunedRow("jetelain/Ornith-1.5-122b:latest"),
                Row("jetelain/Qwen3.6-35B-A3B:code", 32_768, 20.5),
                Row("jetelain/Qwen3.6-35B-A3B:code", 49_152, 20.4),
                Row("jetelain/Qwen3.6-35B-A3B:code", 65_536, 20.35),
                Row("jetelain/Qwen3.6-35B-A3B:code", 98_304, 20.3),
                Row("jetelain/Qwen3.6-35B-A3B:code", 131_072, 17.8, viable: false, reason: "below-threshold"),
                Row("jetelain/Qwen3.6-35B-A3B:q4", 131_072, 30.6),
                Row("jetelain/Qwen3.6-35B-A3B:q4", 163_840, 23.5),
                Row("jetelain/Qwen3.6-35B-A3B:q4", 196_608, 21.8),
                Row("jetelain/Flint:q4", 131_072, 22.0)
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, Catalog, generatedAt: FixedStamp);

        Assert.Equal(5, matrix.Roles.Count);
        Assert.Equal("jetelain/Qwen3.6-35B-A3B:q4", matrix.Roles["basic"].Model);
        Assert.Equal("jetelain/Flint:q4", matrix.Roles["junior_developer"].Model);
        // Expert stage empty → MoE fallback; Q4-tier tie → role-specific family preference.
        Assert.Equal("jetelain/Qwen3.6-35B-A3B:q4", matrix.Roles["agentic_chat"].Model);
        Assert.Equal("jetelain/Flint:q4", matrix.Roles["software_architect"].Model);
        Assert.Equal("jetelain/Flint:q4", matrix.Roles["senior_developer"].Model);
        Assert.All(matrix.Roles.Values, r =>
        {
            Assert.Equal(262_144, r.RecommendedContextWindow);   // MoE ceiling = NativeContext
            Assert.Equal(["Gpu8"], r.Nodes.Select(n => n.Target).ToList());
        });

        // The floor-dropped Q8 model wins NOTHING: no role value, no Models key.
        Assert.DoesNotContain(matrix.Roles.Values, r => r.Model == "jetelain/Qwen3.6-35B-A3B:code");
        Assert.False(matrix.Models.ContainsKey("jetelain/Qwen3.6-35B-A3B:code"));
        Assert.Equal(2, matrix.Models.Count);
    }

    [Fact]
    public void Generate_Fleet8Gb_FlintQ4BelowFloor_EveryRoleQwen36Q4()
    {
        // Same 8 GB fleet as the previous fact, but Flint:q4 is viable only to 98 304 (the
        // 131 072 row is non-viable) → the "Flint:q4 if ≥128k else Qwen3.6-35B-A3B:q4"
        // sub-case: ALL five roles fall to the single remaining eligible MoE.
        var results = new List<BenchmarkTargetResult>
        {
            new("Gpu8", BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
            [
                PrunedRow("jetelain/Qwen3.8-27B:latest"),
                PrunedRow("jetelain/Qwen3.8-27B:iq3"),
                PrunedRow("jetelain/Ornith-1.5-122b:latest"),
                Row("jetelain/Qwen3.6-35B-A3B:code", 32_768, 20.5),
                Row("jetelain/Qwen3.6-35B-A3B:code", 49_152, 20.4),
                Row("jetelain/Qwen3.6-35B-A3B:code", 65_536, 20.35),
                Row("jetelain/Qwen3.6-35B-A3B:code", 98_304, 20.3),
                Row("jetelain/Qwen3.6-35B-A3B:code", 131_072, 17.8, viable: false, reason: "below-threshold"),
                Row("jetelain/Qwen3.6-35B-A3B:q4", 131_072, 30.6),
                Row("jetelain/Qwen3.6-35B-A3B:q4", 163_840, 23.5),
                Row("jetelain/Qwen3.6-35B-A3B:q4", 196_608, 21.8),
                Row("jetelain/Flint:q4", 32_768, 26.0),
                Row("jetelain/Flint:q4", 98_304, 22.5),
                Row("jetelain/Flint:q4", 131_072, 19.0, viable: false, reason: "below-threshold")
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, Catalog, generatedAt: FixedStamp);

        Assert.Equal(5, matrix.Roles.Count);
        Assert.All(matrix.Roles.Values, r =>
        {
            Assert.Equal("jetelain/Qwen3.6-35B-A3B:q4", r.Model);
            Assert.Equal(262_144, r.RecommendedContextWindow);
        });
        Assert.Single(matrix.Models);
        Assert.True(matrix.Models.ContainsKey("jetelain/Qwen3.6-35B-A3B:q4"));
    }

    [Fact]
    public void Generate_Fleet16Gb_Iq3BelowFloor_ExpertsFallBackToQ8Moe()
    {
        // 16 GB GPU + 64 GB RAM: Qwen3.8-27B:iq3 peaks at 49 152 (the 65 536 row drops to
        // 14.7 t/s — the 48k/56k boundary case) → floor-dropped; Qwen3.8-27B:latest is
        // sizing-pruned (18 944 MiB > 92% of 16 384). The Expert stages are empty → the Q8
        // MoE tier (QualityRank 4) beats both Q4 MoEs (rank 5): basic → Qwen3.6:code (family),
        // junior → Flint:q8, agentic_chat → Qwen3.6:code (family), architect/senior →
        // Flint:q8 (family).
        var results = new List<BenchmarkTargetResult>
        {
            new("Gpu16", BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
            [
                Row("jetelain/Qwen3.8-27B:iq3", 32_768, 45.0),
                Row("jetelain/Qwen3.8-27B:iq3", 49_152, 40.0),
                Row("jetelain/Qwen3.8-27B:iq3", 65_536, 14.7, viable: false, reason: "below-threshold"),
                PrunedRow("jetelain/Qwen3.8-27B:latest"),
                Row("jetelain/Qwen3.6-35B-A3B:code", 131_072, 55.0),
                Row("jetelain/Qwen3.6-35B-A3B:code", 163_840, 54.0),
                Row("jetelain/Flint:q8", 131_072, 44.0),
                Row("jetelain/Flint:q8", 163_840, 43.0),
                Row("jetelain/Qwen3.6-35B-A3B:q4", 131_072, 70.0),
                Row("jetelain/Flint:q4", 131_072, 45.0)
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, Catalog, generatedAt: FixedStamp);

        Assert.Equal(5, matrix.Roles.Count);
        Assert.Equal("jetelain/Qwen3.6-35B-A3B:code", matrix.Roles["basic"].Model);
        Assert.Equal("jetelain/Flint:q8", matrix.Roles["junior_developer"].Model);
        Assert.Equal("jetelain/Qwen3.6-35B-A3B:code", matrix.Roles["agentic_chat"].Model);
        Assert.Equal("jetelain/Flint:q8", matrix.Roles["software_architect"].Model);
        Assert.Equal("jetelain/Flint:q8", matrix.Roles["senior_developer"].Model);
        Assert.All(matrix.Roles.Values, r => Assert.Equal(262_144, r.RecommendedContextWindow));
        Assert.All(matrix.Roles.Values, r => Assert.Equal(["Gpu16"], r.Nodes.Select(n => n.Target).ToList()));
    }

    [Fact]
    public void Generate_Fleet64GbUnified_ExpertQ4Wins_AllFiveRoles()
    {
        // 64 GB unified memory node (alone): Ornith-1.5-122b is sizing-pruned (71 583 MiB
        // > 92% of 65 536 = 60 293); Qwen3.8-27B:latest (Q4, rank 2) beats the ELIGIBLE
        // Qwen3.8-27B:iq3 (rank 3) on QualityRank and wins all three expert roles; the Q8 MoE
        // tier wins basic (Qwen3.6:code, family) and junior (Flint:q8, family).
        var results = new List<BenchmarkTargetResult>
        {
            new("Unified64", BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
            [
                PrunedRow("jetelain/Ornith-1.5-122b:latest"),
                Row("jetelain/Qwen3.8-27B:latest", 131_072, 24.5),
                Row("jetelain/Qwen3.8-27B:latest", 163_840, 24.0),
                Row("jetelain/Qwen3.8-27B:latest", 196_608, 23.5),
                Row("jetelain/Qwen3.8-27B:latest", 262_144, 23.0),
                Row("jetelain/Qwen3.8-27B:iq3", 131_072, 23.0),
                Row("jetelain/Qwen3.8-27B:iq3", 163_840, 22.5),
                Row("jetelain/Qwen3.8-27B:iq3", 262_144, 21.5),
                Row("jetelain/Qwen3.6-35B-A3B:code", 262_144, 40.0),
                Row("jetelain/Flint:q8", 262_144, 35.0),
                Row("jetelain/Qwen3.6-35B-A3B:q4", 262_144, 55.0),
                Row("jetelain/Flint:q4", 262_144, 50.0)
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, Catalog, generatedAt: FixedStamp);

        Assert.Equal(5, matrix.Roles.Count);
        foreach (var alias in new[] { "agentic_chat", "software_architect", "senior_developer" })
        {
            var role = matrix.Roles[alias];
            Assert.Equal("jetelain/Qwen3.8-27B:latest", role.Model);
            Assert.Equal(262_144, role.RecommendedContextWindow);
            // The winner's node list is restricted to the eligible node.
            Assert.Equal(["Unified64"], role.Nodes.Select(n => n.Target).ToList());
        }
        Assert.Equal("jetelain/Qwen3.6-35B-A3B:code", matrix.Roles["basic"].Model);
        Assert.Equal("jetelain/Flint:q8", matrix.Roles["junior_developer"].Model);
    }

    [Fact]
    public void Generate_Fleet96GbUnified_OrnithWinsArchitectSenior_LatestWinsAgenticChat()
    {
        // 96 GB unified node (88 GB VRAM pool): Ornith-1.5-122b (rank 1) is eligible and wins
        // software_architect + senior_developer; it is EXCLUDED from agentic_chat by its
        // catalog Roles (the key scenario-4 assertion) EVEN THOUGH it is eligible on the node
        // → Qwen3.8-27B:latest (rank 2) wins agentic_chat. The Models index holds the 4
        // distinct winners (Ornith wins two roles — union, no duplicates).
        var results = new List<BenchmarkTargetResult>
        {
            new("Unified96", BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
            [
                Row("jetelain/Ornith-1.5-122b:latest", 131_072, 31.0),
                Row("jetelain/Ornith-1.5-122b:latest", 163_840, 27.0),
                Row("jetelain/Ornith-1.5-122b:latest", 196_608, 24.0),
                Row("jetelain/Ornith-1.5-122b:latest", 262_144, 22.0),
                Row("jetelain/Qwen3.8-27B:latest", 131_072, 25.5),
                Row("jetelain/Qwen3.8-27B:latest", 163_840, 25.0),
                Row("jetelain/Qwen3.8-27B:latest", 196_608, 25.0),
                Row("jetelain/Qwen3.8-27B:latest", 262_144, 25.0),
                Row("jetelain/Qwen3.8-27B:iq3", 262_144, 24.0),
                Row("jetelain/Qwen3.6-35B-A3B:code", 262_144, 50.0),
                Row("jetelain/Flint:q8", 262_144, 45.0)
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, Catalog, generatedAt: FixedStamp);

        Assert.Equal(5, matrix.Roles.Count);
        Assert.Equal("jetelain/Ornith-1.5-122b:latest", matrix.Roles["software_architect"].Model);
        Assert.Equal("jetelain/Ornith-1.5-122b:latest", matrix.Roles["senior_developer"].Model);
        Assert.Equal("jetelain/Qwen3.8-27B:latest", matrix.Roles["agentic_chat"].Model);
        Assert.Equal("jetelain/Qwen3.6-35B-A3B:code", matrix.Roles["basic"].Model);
        Assert.Equal("jetelain/Flint:q8", matrix.Roles["junior_developer"].Model);
        Assert.All(matrix.Roles.Values, r => Assert.Equal(262_144, r.RecommendedContextWindow));

        // The Models index: union of the role winners — 4 distinct models, each winner
        // exactly once.
        Assert.Equal(4, matrix.Models.Count);
        Assert.Equal(
            [
                "jetelain/Flint:q8",
                "jetelain/Ornith-1.5-122b:latest",
                "jetelain/Qwen3.6-35B-A3B:code",
                "jetelain/Qwen3.8-27B:latest"
            ],
            matrix.Models.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Generate_MixedFleet8GbPlus64Gb_WinnerNodesRestrictedToEligibleNodes()
    {
        // Mixed fleet (the fact-8 Gpu8 ladder + the fact-11 Unified64 ladder): Qwen3.6-35B-A3B:
        // code is floor-dropped on Gpu8 (ceiling 98 304) but eligible on Unified64 → it wins
        // basic with its Nodes list restricted to [Unified64]; the Q4 MoEs are eligible on
        // BOTH nodes but never win when a Q8-tier model is eligible anywhere.
        var gpu8 = new BenchmarkTargetResult("Gpu8", BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
        [
            PrunedRow("jetelain/Qwen3.8-27B:latest"),
            PrunedRow("jetelain/Qwen3.8-27B:iq3"),
            PrunedRow("jetelain/Ornith-1.5-122b:latest"),
            Row("jetelain/Qwen3.6-35B-A3B:code", 32_768, 20.5),
            Row("jetelain/Qwen3.6-35B-A3B:code", 49_152, 20.4),
            Row("jetelain/Qwen3.6-35B-A3B:code", 65_536, 20.35),
            Row("jetelain/Qwen3.6-35B-A3B:code", 98_304, 20.3),
            Row("jetelain/Qwen3.6-35B-A3B:code", 131_072, 17.8, viable: false, reason: "below-threshold"),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 131_072, 30.6),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 163_840, 23.5),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 196_608, 21.8),
            Row("jetelain/Flint:q4", 131_072, 22.0)
        ]);
        var unified64 = new BenchmarkTargetResult("Unified64", BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
        [
            PrunedRow("jetelain/Ornith-1.5-122b:latest"),
            Row("jetelain/Qwen3.8-27B:latest", 131_072, 24.5),
            Row("jetelain/Qwen3.8-27B:latest", 163_840, 24.0),
            Row("jetelain/Qwen3.8-27B:latest", 196_608, 23.5),
            Row("jetelain/Qwen3.8-27B:latest", 262_144, 23.0),
            Row("jetelain/Qwen3.8-27B:iq3", 131_072, 23.0),
            Row("jetelain/Qwen3.8-27B:iq3", 163_840, 22.5),
            Row("jetelain/Qwen3.8-27B:iq3", 262_144, 21.5),
            Row("jetelain/Qwen3.6-35B-A3B:code", 262_144, 40.0),
            Row("jetelain/Flint:q8", 262_144, 35.0),
            Row("jetelain/Qwen3.6-35B-A3B:q4", 262_144, 55.0),
            Row("jetelain/Flint:q4", 262_144, 50.0)
        ]);

        var matrix = new RecommendationEngine().Generate([gpu8, unified64], Catalog, generatedAt: FixedStamp);

        Assert.Equal(5, matrix.Roles.Count);
        Assert.Equal("jetelain/Qwen3.6-35B-A3B:code", matrix.Roles["basic"].Model);
        Assert.Equal(["Unified64"], matrix.Roles["basic"].Nodes.Select(n => n.Target).ToList());
        Assert.Equal("jetelain/Flint:q8", matrix.Roles["junior_developer"].Model);
        Assert.Equal(["Unified64"], matrix.Roles["junior_developer"].Nodes.Select(n => n.Target).ToList());
        foreach (var alias in new[] { "agentic_chat", "software_architect", "senior_developer" })
        {
            Assert.Equal("jetelain/Qwen3.8-27B:latest", matrix.Roles[alias].Model);
            Assert.Equal(["Unified64"], matrix.Roles[alias].Nodes.Select(n => n.Target).ToList());
        }
        // The Q4 MoEs (eligible on both nodes) never win when the Q8 tier is eligible.
        Assert.DoesNotContain(
            matrix.Roles.Values, r => r.Model is "jetelain/Qwen3.6-35B-A3B:q4" or "jetelain/Flint:q4");
    }

    [Fact]
    public void Generate_FloorBoundary_Exactly128kAtExactly20Tps_Eligible()
    {
        // Both boundaries are INCLUSIVE: exactly 131 072 tokens at exactly 20.0 t/s is viable
        // AND eligible (>= on both).
        var catalog = new[] { AllRoleMoe("edge/moe:q4") };
        var results = new List<BenchmarkTargetResult>
        {
            new(Desktop, BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
            [
                Row("edge/moe:q4", 131_072, 20.0)
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, catalog, generatedAt: FixedStamp);

        Assert.Equal(5, matrix.Roles.Count);
        Assert.All(matrix.Roles.Values, r =>
        {
            Assert.Equal("edge/moe:q4", r.Model);
            Assert.Equal(262_144, r.RecommendedContextWindow);   // MoE ceiling = NativeContext
            Assert.Equal(131_072, r.Nodes[0].BaselineContext);
            Assert.Equal(20.0, r.Nodes[0].TokensPerSecond);
        });
        Assert.Single(matrix.Models);
    }

    [Fact]
    public void Generate_FloorBoundary_CeilingBelow128k_Disqualified_AllRolesAbsent()
    {
        // The 131 072 row is non-viable (19.0 t/s < the 20.0 floor) and the largest viable
        // row (98 304) is below the 128k floor → the (model, node) pair is dropped → no role
        // is assignable → the matrix is empty.
        var catalog = new[] { AllRoleMoe("edge/moe:q4") };
        var results = new List<BenchmarkTargetResult>
        {
            new(Desktop, BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
            [
                Row("edge/moe:q4", 98_304, 25.0),
                Row("edge/moe:q4", 131_072, 19.0, viable: false, reason: "below-threshold")
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, catalog, generatedAt: FixedStamp);

        Assert.Empty(matrix.Roles);
        Assert.Empty(matrix.Models);
    }

    // ------------------------------------------------------------------
    //  RoleAlias vocabulary
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(UsageRole.BasicModel, "basic")]
    [InlineData(UsageRole.AgenticChat, "agentic_chat")]
    [InlineData(UsageRole.SoftwareArchitect, "software_architect")]
    [InlineData(UsageRole.SeniorDeveloper, "senior_developer")]
    [InlineData(UsageRole.JuniorDeveloper, "junior_developer")]
    public void RoleAlias_ToAlias_CanonicalSnakeCase(UsageRole role, string alias)
    {
        Assert.Equal(alias, RoleAlias.ToAlias(role));
    }

    [Theory]
    [InlineData("senior_developer", UsageRole.SeniorDeveloper)]
    [InlineData("Software_Architect", UsageRole.SoftwareArchitect)]
    [InlineData("BASIC", UsageRole.BasicModel)]
    public void RoleAlias_TryFromAlias_OrdinalIgnoreCase(string alias, UsageRole role)
    {
        Assert.True(RoleAlias.TryFromAlias(alias, out var parsed));
        Assert.Equal(role, parsed);
    }

    [Theory]
    [InlineData("qwen3.8:27b")] // a model name that is NOT a role alias
    [InlineData("nonexistent_role")]
    [InlineData("")]
    [InlineData(null)]
    public void RoleAlias_TryFromAlias_NonAlias_ReturnsFalse(string? alias)
    {
        Assert.False(RoleAlias.TryFromAlias(alias, out _));
    }

    // ------------------------------------------------------------------
    //  Store: fail-soft load + activation
    // ------------------------------------------------------------------

    private FleetRecommendations SampleMatrix() => new(
        FixedStamp,
        new Dictionary<string, RoleRecommendation>(StringComparer.OrdinalIgnoreCase)
        {
            ["senior_developer"] = new RoleRecommendation(
                "senior_developer", "qwen3.8:27b", 262_144, "desc",
                [
                    new NodeRoleProfile(Desktop, 49_152, null, 33.0, 16_384),
                    new NodeRoleProfile(AiServer, 262_144, null, 22.5, 98_304)
                ])
        },
        new Dictionary<string, ModelNodeProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["qwen3.8:27b"] = new ModelNodeProfile(
                "qwen3.8:27b", "Dense", 131_072, "q4",
                [
                    new NodeRoleProfile(Desktop, 49_152, null, 33.0, 16_384),
                    new NodeRoleProfile(AiServer, 262_144, null, 22.5, 98_304)
                ])
        });

    private sealed class TempFile : IDisposable
    {
        public TempFile()
        {
            var tempDir = System.IO.Directory.CreateTempSubdirectory("recstore-").FullName;
            Path = System.IO.Path.Combine(tempDir, "recommended.json");
        }
        public string Path { get; }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void Store_MissingFile_EmptyMatrix_NoThrow()
    {
        using var file = new TempFile();
        var store = new RecommendationStore(file.Path);
        Assert.Empty(store.GetMatrix().Roles);
        Assert.Empty(store.GetMatrix().Models);
        Assert.Null(store.GetMatrix().GeneratedAt);
        Assert.Null(store.GetRole("senior_developer"));
        Assert.False(store.TryActivateFromFile(out var matrix));
        Assert.Empty(matrix.Models);
    }

    [Fact]
    public void Store_CorruptFile_EmptyMatrix_NoThrow()
    {
        using var file = new TempFile();
        File.WriteAllText(file.Path, "{ not valid json !!!!!");
        var store = new RecommendationStore(file.Path);
        Assert.Empty(store.GetMatrix().Roles);
        Assert.False(store.TryActivateFromFile(out _));
    }

    [Fact]
    public void Store_Activate_SwapsAtomically()
    {
        using var file = new TempFile();
        var store = new RecommendationStore(file.Path);
        var matrix = SampleMatrix();
        store.Activate(matrix);

        Assert.Same(matrix, store.GetMatrix());
        Assert.NotNull(store.GetRole("SENIOR_DEVELOPER")); // alias lookup is case-insensitive
        Assert.Null(store.GetRole("junior_developer"));    // absent role
    }

    [Fact]
    public void Store_TryGetNodeContextCeiling_TagStrippedAndCaseInsensitiveMatching()
    {
        using var file = new TempFile();
        var store = new RecommendationStore(file.Path);
        store.Activate(SampleMatrix());

        // Exact native name, tag-stripped and case variants all resolve.
        Assert.True(store.TryGetNodeContextCeiling("Desktop", "qwen3.8:27b", out var exact));
        Assert.Equal(49_152, exact);
        Assert.True(store.TryGetNodeContextCeiling("Desktop", "qwen3.8", out var stripped));
        Assert.Equal(49_152, stripped);
        Assert.True(store.TryGetNodeContextCeiling("DESKTOP", "QWEN3.8:27B", out var caseless));
        Assert.Equal(49_152, caseless);

        // Unknown (model, node) pairs → false (fail-open).
        Assert.False(store.TryGetNodeContextCeiling("Desktop", "gemma4:e4b", out _));
        Assert.False(store.TryGetNodeContextCeiling("Nope", "qwen3.8:27b", out _));
        Assert.False(store.TryGetNodeContextCeiling("desktop ", "qwen3.8:27b", out int ceilingMiss)); // not a real target name
        Assert.Equal(0, ceilingMiss);
    }

    [Fact]
    public void Store_TryActivateFromFile_ReloadsPersistedMatrix()
    {
        using var file = new TempFile();
        var store = new RecommendationStore(file.Path);
        var matrix = SampleMatrix();
        File.WriteAllText(file.Path, JsonSerializer.Serialize(matrix, OllamaRouterJsonSerializerContext.Default.FleetRecommendations));

        Assert.True(store.TryActivateFromFile(out var reloaded));
        Assert.Equal(FixedStamp, reloaded.GeneratedAt);
        Assert.Equal(262_144, store.GetMatrix().Roles["senior_developer"].RecommendedContextWindow);
        Assert.True(store.TryGetNodeContextCeiling("Aiserver", "qwen3.8", out var ceiling));
        Assert.Equal(262_144, ceiling);
    }

    // ------------------------------------------------------------------
    //  JSON round-trip through the source-generated context
    // ------------------------------------------------------------------

    [Fact]
    public void FleetRecommendations_JsonRoundTrip_FieldForFieldEqual()
    {
        var matrix = SampleMatrix();
        var json = JsonSerializer.Serialize(matrix, OllamaRouterJsonSerializerContext.Default.FleetRecommendations);
        var roundTripped = JsonSerializer.Deserialize(json, OllamaRouterJsonSerializerContext.Default.FleetRecommendations);

        Assert.NotNull(roundTripped);
        Assert.Equal(matrix.GeneratedAt, roundTripped!.GeneratedAt);
        Assert.Equal(matrix.Roles.Count, roundTripped.Roles.Count);
        foreach (var (alias, role) in matrix.Roles)
        {
            var parsed = roundTripped.Roles[alias];
            Assert.Equal(role.Role, parsed.Role);
            Assert.Equal(role.Model, parsed.Model);
            Assert.Equal(role.RecommendedContextWindow, parsed.RecommendedContextWindow);
            Assert.Equal(role.Description, parsed.Description);
            Assert.Equal(role.Nodes.Count, parsed.Nodes.Count);
            for (var i = 0; i < role.Nodes.Count; i++)
            {
                Assert.Equal(role.Nodes[i].Target, parsed.Nodes[i].Target);
                Assert.Equal(role.Nodes[i].ContextCeiling, parsed.Nodes[i].ContextCeiling);
                Assert.Equal(role.Nodes[i].BaselineContext, parsed.Nodes[i].BaselineContext);
                Assert.Equal(role.Nodes[i].TokensPerSecond, parsed.Nodes[i].TokensPerSecond);
                Assert.Equal(role.Nodes[i].VramMB, parsed.Nodes[i].VramMB);
            }
        }

        foreach (var (model, profile) in matrix.Models)
        {
            var parsed = roundTripped.Models[model];
            Assert.Equal(profile.Model, parsed.Model);
            Assert.Equal(profile.Architecture, parsed.Architecture);
            Assert.Equal(profile.NativeContext, parsed.NativeContext);
            Assert.Equal(profile.Quantization, parsed.Quantization);
            Assert.Equal(profile.Nodes.Count, parsed.Nodes.Count);
        }
    }
}
