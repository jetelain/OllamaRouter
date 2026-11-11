using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Recommendations;
using System.Text;
using System.Text.Json;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// Unit tests for the native role recommendation engine (task #66): the deterministic
/// assignment algorithm (terminal-status gate, largest-viable-row dense ceilings, MoE
/// baseline + native extensible limit, (ceiling, tps, catalog order) scoring, role
/// absence) and the live <see cref="RecommendationStore"/> (fail-soft load, ceiling
/// lookups with tag-stripped/case-insensitive model matching, atomic activation, JSON
/// round-trip). Pure in-memory fixtures — no daemons, no network.
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

    private static NodeHardwareProfile Profile(string name, long totalFastMemoryMB) => new(
        name, "linux", "x64", false, 32_768, SystemRamType.Ddr5,
        [new GpuDeviceProfile("Nvidia", "GeForce", (int)totalFastMemoryMB, "GDDR6", 256, 448)],
        totalFastMemoryMB, 448.0);

    private static readonly IReadOnlyList<CandidateModel> Catalog = CandidateModelCatalogOptions.DefaultModels;

    /// <summary>
    /// The spec example fleet: qwen3.8:27b viable to 49 152 on Desktop and 262 144 on
    /// Aiserver (evidence beyond the 131 072 catalog native); gemma4:e4b viable to 131 072
    /// everywhere; qwen3.6:35b-a3b (MoE) viable to 65 536 (next row non-viable).
    /// </summary>
    private static IReadOnlyList<BenchmarkTargetResult> DefaultFleetResults() =>
    [
        new BenchmarkTargetResult(Desktop, BenchmarkRunStatus.Succeeded,
            FixedStamp.AddMinutes(-20), FixedStamp.AddMinutes(-10),
        [
            Row("gemma4:e4b", 16_384, 90.0),
            Row("gemma4:e4b", 32_768, 84.0),
            Row("gemma4:e4b", 49_152, 77.0),
            Row("gemma4:e4b", 65_536, 70.0),
            Row("gemma4:e4b", 131_072, 48.0),
            Row("qwen3.8:27b", 16_384, 45.0),
            Row("qwen3.8:27b", 32_768, 40.0),
            Row("qwen3.8:27b", 49_152, 33.0),
            Row("qwen3.8:27b", 65_536, 12.0, viable: false, reason: "below-threshold"),
            Row("qwen3.6:35b-a3b", 16_384, 55.0),
            Row("qwen3.6:35b-a3b", 32_768, 51.0),
            Row("qwen3.6:35b-a3b", 65_536, 42.0),
            Row("qwen3.6:35b-a3b", 131_072, 14.0, viable: false, reason: "below-threshold")
        ]),
        new BenchmarkTargetResult(AiServer, BenchmarkRunStatus.Succeeded,
            FixedStamp.AddMinutes(-10), FixedStamp,
        [
            Row("gemma4:e4b", 16_384, 95.0),
            Row("gemma4:e4b", 32_768, 91.0),
            Row("gemma4:e4b", 49_152, 88.0),
            Row("gemma4:e4b", 65_536, 82.0),
            Row("gemma4:e4b", 131_072, 61.0),
            Row("qwen3.8:27b", 16_384, 30.0),
            Row("qwen3.8:27b", 32_768, 29.0),
            Row("qwen3.8:27b", 49_152, 28.0),
            Row("qwen3.8:27b", 65_536, 27.0),
            Row("qwen3.8:27b", 131_072, 25.5),
            Row("qwen3.8:27b", 184_320, 24.0),
            Row("qwen3.8:27b", 262_144, 22.5),
            Row("qwen3.6:35b-a3b", 16_384, 60.0),
            Row("qwen3.6:35b-a3b", 32_768, 57.0),
            Row("qwen3.6:35b-a3b", 65_536, 49.0),
            Row("qwen3.6:35b-a3b", 131_072, 38.0),
            Row("qwen3.6:35b-a3b", 131_072, 38.0)
        ])
    ];

    // ------------------------------------------------------------------
    //  Engine: per-node ceilings
    // ------------------------------------------------------------------

    [Fact]
    public void Generate_DensePerNodeCeilings_LargestViableRowWins_EvidenceMayExceedNative()
    {
        var engine = new RecommendationEngine();
        var matrix = engine.Generate(
            DefaultFleetResults(), Catalog,
            hardwareProfileLookup: name => name switch
            {
                Desktop => Profile(Desktop, 16_384),
                AiServer => Profile(AiServer, 98_304),
                _ => null
            },
            generatedAt: FixedStamp);

        var qwen = matrix.Models["qwen3.8:27b"];
        Assert.Equal("Dense", qwen.Architecture);
        Assert.Equal(131_072, qwen.NativeContext);

        // Fleet configuration order: Desktop first, then Aiserver.
        Assert.Equal([Desktop, AiServer], qwen.Nodes.Select(n => n.Target).ToList());

        var desktop = qwen.Nodes[0];
        Assert.Equal(49_152, desktop.ContextCeiling);        // evidence-driven, NOT capped by the catalog native
        Assert.Null(desktop.BaselineContext);                // dense: no baseline bucket
        Assert.Equal(33.0, desktop.TokensPerSecond);         // tps of the 49 152 row
        Assert.Equal(16_384, desktop.VramMB);

        var server = qwen.Nodes[1];
        Assert.Equal(262_144, server.ContextCeiling);        // evidence beyond the 131 072 native
        Assert.Null(server.BaselineContext);
        Assert.Equal(22.5, server.TokensPerSecond);          // tps of the 262 144 row
        Assert.Equal(98_304, server.VramMB);
    }

    [Fact]
    public void Generate_MoeShape_NativeContextCeiling_WithLargestViableBaseline()
    {
        var engine = new RecommendationEngine();
        var matrix = engine.Generate(DefaultFleetResults(), Catalog, generatedAt: FixedStamp);

        var moe = matrix.Models["qwen3.6:35b-a3b"];
        Assert.Equal("MoE", moe.Architecture);
        foreach (var node in moe.Nodes)
        {
            Assert.Equal(131_072, node.ContextCeiling);      // the model's native context (single extensible bucket)
        }

        // Largest viable rows: Desktop 65 536 (next row non-viable), Aiserver 131 072.
        Assert.Equal(65_536, moe.Nodes[0].BaselineContext);
        Assert.Equal(131_072, moe.Nodes[1].BaselineContext);
        Assert.Equal(42.0, moe.Nodes[0].TokensPerSecond);
        Assert.Equal(38.0, moe.Nodes[1].TokensPerSecond);
    }

    [Fact]
    public void Generate_MultiRoleUnifiedModel_ThreeRolesShareIdenticalNativeName()
    {
        var engine = new RecommendationEngine();
        var matrix = engine.Generate(DefaultFleetResults(), Catalog, generatedAt: FixedStamp);

        // The multi-role unified hot cache: the SAME native name for all three roles.
        var chat = matrix.Roles["agentic_chat"];
        var architect = matrix.Roles["software_architect"];
        var senior = matrix.Roles["senior_developer"];
        Assert.Equal("qwen3.8:27b", chat.Model);
        Assert.Equal("qwen3.8:27b", architect.Model);
        Assert.Equal("qwen3.8:27b", senior.Model);

        Assert.Equal("gemma4:e4b", matrix.Roles["basic"].Model);
        Assert.Equal("qwen3.6:35b-a3b", matrix.Roles["junior_developer"].Model);

        // RecommendedContextWindow = max node ceiling; Nodes in fleet configuration order.
        Assert.Equal(262_144, chat.RecommendedContextWindow);
        Assert.Equal([Desktop, AiServer], chat.Nodes.Select(n => n.Target).ToList());
        Assert.NotEmpty(chat.Description);
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

        var results = new List<BenchmarkTargetResult>
        {
            new(Desktop, BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
            [
                Row("small:7b", 16_384, 90.0),
                Row("small:7b", 32_768, 80.0),
                Row("big:14b", 16_384, 50.0),
                Row("big:14b", 32_768, 45.0),
                Row("big:14b", 49_152, 40.0),   // big wins: higher max ceiling (49 152 > 32 768)
                Row("sameA:7b", 16_384, 60.0),
                Row("sameA:7b", 32_768, 55.0),
                Row("sameB:7b", 16_384, 70.0),
                Row("sameB:7b", 32_768, 65.0)   // tie on ceiling (32 768) → sameB wins on tps (65 > 55)
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, catalog, generatedAt: FixedStamp);
        Assert.Equal("big:14b", matrix.Roles["senior_developer"].Model);
        Assert.Equal(49_152, matrix.Roles["senior_developer"].RecommendedContextWindow);
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
                Row("first:7b", 32_768, 55.0),
                Row("second:7b", 32_768, 55.0)   // full tie → catalog declaration order (first)
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
                Row("qwen3.8:27b", 262_144, 99.0)
            ]),
            // Completed target: gemma4:e4b is fully non-viable (dropped), qwen is viable only
            // on Aiserver; basic has NO viable model anywhere → the role is ABSENT.
            new BenchmarkTargetResult(AiServer, BenchmarkRunStatus.Succeeded, FixedStamp, FixedStamp,
            [
                Row("gemma4:e4b", 16_384, 10.0, viable: false, reason: "below-threshold"),
                Row("qwen3.8:27b", 16_384, 30.0),
                Row("qwen3.8:27b", 32_768, 28.0)
            ])
        };

        var matrix = new RecommendationEngine().Generate(results, Catalog, generatedAt: FixedStamp);

        Assert.False(matrix.Roles.ContainsKey("basic"));              // no viable candidate anywhere
        Assert.False(matrix.Roles.ContainsKey("junior_developer"));   // no MoE data at all
        var qwen = matrix.Roles["agentic_chat"];
        Assert.Equal(32_768, qwen.RecommendedContextWindow);
        // The node list contains ONLY Aiserver (Desktop was Running; its 262 144 row is gone).
        Assert.Equal([AiServer], qwen.Nodes.Select(n => n.Target).ToList());
        // The Models index covers only the winners.
        Assert.Single(matrix.Models);
        Assert.True(matrix.Models.ContainsKey("qwen3.8:27b"));
    }

    [Fact]
    public void Generate_ZeroTerminalResults_EmptyMatrix()
    {
        var results = new List<BenchmarkTargetResult>
        {
            new BenchmarkTargetResult(Desktop, BenchmarkRunStatus.Running, FixedStamp, null,
            [
                Row("qwen3.8:27b", 16_384, 30.0)
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
    [InlineData("qwen3.8:27b")]
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
