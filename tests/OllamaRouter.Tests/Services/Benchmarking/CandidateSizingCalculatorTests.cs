using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Tests.Services.Benchmarking;

/// <summary>
/// Exact-value math pins for the pre-flight candidate sizing calculator (task #28, reworked
/// for the curated jetelain/* catalog + hybrid/UD sizing in task #166). MB = MiB (2^20)
/// throughout. The 7×5 hardware matrix (A7 of the task spec) is pinned as Theory rows; the
/// parametric-path boundary and fail-open semantics keep their original synthetic models.
/// </summary>
public class CandidateSizingCalculatorTests
{
    // ------------------------------------------------------------------
    //  Fixed-value math pins
    // ------------------------------------------------------------------

    [Fact]
    public void ComputeWeightsMB_27bDenseQ4_IsApproximately13900MB()
    {
        // 27.8e9 × 4 / 8 / 2^20 = 13 256.07 MB (MB = MiB, 2^20 — documented choice).
        var mb = CandidateSizingCalculator.ComputeWeightsMB(27_800_000_000, 4);
        Assert.Equal(13_256.073, mb, precision: 3);
    }

    [Fact]
    public void ComputeWeightsMB_4b5Q4_IsApproximately2146MB()
    {
        // 4.5e9 × 4 / 8 / 2^20 = 2145.767 MB (MB = MiB, 2^20).
        var mb = CandidateSizingCalculator.ComputeWeightsMB(4_500_000_000, 4);
        Assert.Equal(2145.767, mb, precision: 3);
    }

    [Fact]
    public void ComputeWeightsMB_35bQ8_IsApproximately33379MB()
    {
        // 35e9 × 8 / 8 / 2^20 = 33378.601 MB.
        var mb = CandidateSizingCalculator.ComputeWeightsMB(35_000_000_000, 8);
        Assert.Equal(33_378.601, mb, precision: 3);
    }

    [Fact]
    public void ComputeActiveWeightsMB_MoE_UsesActiveParams()
    {
        // 3.3e9 × 4 / 8 / 2^20 ≈ 1573.56 MB (~1.5 GiB decode buffer per the spec).
        var model = new CandidateModel(
            "moe-test", [UsageRole.JuniorDeveloper], "MoE",
            TotalParams: 35_000_000_000, ActiveParams: 3_300_000_000,
            "q4", 4, 64, 8, 128, 131_072, 131_072);

        var mb = CandidateSizingCalculator.ComputeActiveWeightsMB(model);
        Assert.Equal(1573.563, mb, precision: 3);

        // And it is NOT the full-weights size.
        Assert.NotEqual(CandidateSizingCalculator.ComputeWeightsMB(35_000_000_000, 4), mb);
    }

    [Fact]
    public void ComputeActiveWeightsMB_Dense_EqualstotalParams()
    {
        var model = new CandidateModel(
            "dense-test", [UsageRole.AgenticChat], "Dense",
            TotalParams: 27_800_000_000, ActiveParams: null,
            "q4", 4, 64, 8, 128, 131_072, 65_536);

        Assert.Equal(
            CandidateSizingCalculator.ComputeWeightsMB(27_800_000_000, 4),
            CandidateSizingCalculator.ComputeActiveWeightsMB(model));
    }

    [Fact]
    public void ComputeActiveWeightsMB_ActiveParamsSet_IsUsedRegardlessOfArchitectureString()
    {
        // Task #166 rule change: the gate is ActiveParams ?? TotalParams, NOT
        // Architecture == "MoE" — a synthetic "Dense" model with explicit active params
        // sizes its decode buffer from the active params.
        var model = new CandidateModel(
            "hybrid-test", [UsageRole.SeniorDeveloper], "Hybrid MoE",
            TotalParams: 124_600_000_000, ActiveParams: 10_000_000_000,
            "q4", 4, 49, 2, 128, 262_144, 131_072);

        // 10e9 × 4 / 8 / 2^20 = 4768.371582...
        Assert.Equal(4_768.3716, CandidateSizingCalculator.ComputeActiveWeightsMB(model), precision: 3);
    }

    [Fact]
    public void ComputeActiveWeightsMB_CuratedFlintQ4_Is1192MB()
    {
        // 2.5e9 × 4 / 8 / 2^20 = 1192.0928955... (parametric — NOT the 23 859 MiB resident
        // anchor, which only applies to the full-weights/decode-residency math).
        var flint = Default("jetelain/Flint:q4");
        Assert.Equal(1_192.093, CandidateSizingCalculator.ComputeActiveWeightsMB(flint), precision: 3);
    }

    [Fact]
    public void ComputeActiveWeightsMB_CuratedOrnith_Uses10BActiveParams()
    {
        // 10e9 × 4 / 8 / 2^20 = 4768.371582... — correct for Ornith's 10B active params even
        // though Architecture is "Hybrid MoE" (the old string gate missed it).
        var ornith = Default("jetelain/Ornith-1.5-122b:latest");
        Assert.Equal(4_768.3716, CandidateSizingCalculator.ComputeActiveWeightsMB(ornith), precision: 3);
    }

    [Fact]
    public void ComputeKvCacheMB_131k_Ctx_Is32768MB()
    {
        // 131072 × 64 layers × 8 kvHeads × 128 headDim × 2 (K+V) × 2 (f16) / 2^20 = 32768 MB.
        var mb = CandidateSizingCalculator.ComputeKvCacheMB(131_072, 64, 8, 128);
        Assert.Equal(32_768.0, mb, precision: 3);
    }

    [Fact]
    public void ComputeKvCacheMB_16k_Ctx_Is4096MB()
    {
        // 16384 × 64 × 8 × 128 × 2 × 2 / 2^20 = 4096 MB.
        var mb = CandidateSizingCalculator.ComputeKvCacheMB(16_384, 64, 8, 128);
        Assert.Equal(4_096.0, mb, precision: 3);
    }

    [Fact]
    public void ComputeKvCacheMB_262k_Ctx_Is65536MB()
    {
        var mb = CandidateSizingCalculator.ComputeKvCacheMB(262_144, 64, 8, 128);
        Assert.Equal(65_536.0, mb, precision: 3);
    }

    // ------------------------------------------------------------------
    //  Catalog shape (task #166, A3): exactly the 7 curated jetelain/* entries
    // ------------------------------------------------------------------

    private static CandidateModel Default(string name) =>
        CandidateModelCatalogOptions.DefaultModels.First(m => m.Name == name);

    [Fact]
    public void DefaultModels_ContainsExactlyTheSevenCuratedEntries_InDeclarationOrder()
    {
        var models = CandidateModelCatalogOptions.DefaultModels;
        Assert.Equal(
            [
                "jetelain/Flint:q4",
                "jetelain/Flint:q8",
                "jetelain/Qwen3.6-35B-A3B:q4",
                "jetelain/Qwen3.6-35B-A3B:code",
                "jetelain/Qwen3.8-27B:iq3",
                "jetelain/Qwen3.8-27B:latest",
                "jetelain/Ornith-1.5-122b:latest"
            ],
            models.Select(m => m.Name).ToList());
        Assert.All(models, m => Assert.Equal(262_144, m.NativeContext));
    }

    [Fact]
    public void DefaultModels_Roles_MoECarriesAllFiveRoles_DenseAndOrnith_CarryExpertRolesOnly()
    {
        var moeRoles = new[]
        {
            UsageRole.JuniorDeveloper, UsageRole.BasicModel, UsageRole.AgenticChat,
            UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper
        };
        var basicFirstMoeRoles = new[]
        {
            UsageRole.BasicModel, UsageRole.JuniorDeveloper, UsageRole.AgenticChat,
            UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper
        };
        var expertRoles = new[]
        {
            UsageRole.AgenticChat, UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper
        };

        // Roles[0] is the PRIMARY role (the unfiltered benchmark row tag).
        Assert.Equal(moeRoles, Default("jetelain/Flint:q4").Roles);
        Assert.Equal(moeRoles, Default("jetelain/Flint:q8").Roles);
        Assert.Equal(basicFirstMoeRoles, Default("jetelain/Qwen3.6-35B-A3B:q4").Roles);
        Assert.Equal(basicFirstMoeRoles, Default("jetelain/Qwen3.6-35B-A3B:code").Roles);
        Assert.Equal(expertRoles, Default("jetelain/Qwen3.8-27B:iq3").Roles);
        Assert.Equal(expertRoles, Default("jetelain/Qwen3.8-27B:latest").Roles);
        Assert.Equal(
            new[] { UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper },
            Default("jetelain/Ornith-1.5-122b:latest").Roles);
    }

    [Theory]
    [InlineData("jetelain/Flint:q4", "MoE", "UD-Q4_K_XL", 4, 39_800_000_000L, 2_500_000_000L, 49, 2, 128, 131_072,
        CandidateModelClass.MoE, true, 23_859.0, 2_048.0, "Flint", 5)]
    [InlineData("jetelain/Flint:q8", "MoE", "Q8_0", 8, 39_800_000_000L, 2_500_000_000L, 49, 2, 128, 131_072,
        CandidateModelClass.MoE, true, 40_346.0, 2_048.0, "Flint", 4)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", "MoE", "UD-Q4_K_XL", 4, 35_000_000_000L, 3_200_000_000L, 41, 2, 128, 65_536,
        CandidateModelClass.MoE, true, 22_528.0, 22_528.0, "Qwen3.6-35B-A3B", 5)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:code", "MoE", "UD-Q8_K_XL", 8, 35_000_000_000L, 3_200_000_000L, 41, 2, 128, 131_072,
        CandidateModelClass.MoE, true, 40_960.0, 22_528.0, "Qwen3.6-35B-A3B", 4)]
    [InlineData("jetelain/Qwen3.8-27B:iq3", "Dense", "UD-IQ3_S", 3, 27_800_000_000L, null, 65, 4, 256, 49_152,
        CandidateModelClass.Expert, false, 12_288.0, 65_536.0, "Qwen3.8-27B", 3)]
    [InlineData("jetelain/Qwen3.8-27B:latest", "Dense", "UD-Q4_K_XL", 4, 27_800_000_000L, null, 65, 4, 256, 131_072,
        CandidateModelClass.Expert, false, 18_944.0, 65_536.0, "Qwen3.8-27B", 2)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", "Hybrid MoE", "UD-Q4_K_XL", 4, 124_600_000_000L, 10_000_000_000L, 49, 2, 128, 131_072,
        CandidateModelClass.Expert, false, 71_583.0, 26_624.0, "Ornith-1.5", 1)]
    public void DefaultModels_EntryValues_MatchTheCuratedSpec(
        string name, string architecture, string quantization, int bitsPerWeight, long totalParams,
        long? activeParams, int layers, int kvHeads, int headDim, int defaultContext,
        CandidateModelClass modelClass, bool allowCpuOffload, double estimatedWeightsMB,
        double kvBytesPerToken, string family, int qualityRank)
    {
        var model = Default(name);
        Assert.Equal(architecture, model.Architecture);
        Assert.Equal(quantization, model.Quantization);
        Assert.Equal(bitsPerWeight, model.BitsPerWeight);
        Assert.Equal(totalParams, model.TotalParams);
        Assert.Equal(activeParams, model.ActiveParams);
        Assert.Equal(layers, model.Layers);
        Assert.Equal(kvHeads, model.KvHeads);
        Assert.Equal(headDim, model.HeadDim);
        Assert.Equal(defaultContext, model.DefaultContext);
        Assert.Equal(modelClass, model.ModelClass);
        Assert.Equal(allowCpuOffload, model.AllowCpuOffload);
        Assert.Equal(estimatedWeightsMB, model.EstimatedWeightsMB);
        Assert.Equal(kvBytesPerToken, model.KvBytesPerToken);
        Assert.Equal(family, model.Family);
        Assert.Equal(qualityRank, model.QualityRank);
    }

    // ------------------------------------------------------------------
    //  Effective weights + effective KV helper pins (A7)
    // ------------------------------------------------------------------

    [Fact]
    public void ComputeEffectiveWeightsMB_CuratedModels_UseEstimatedWeightsMB()
    {
        // The UD mixed-precision resident footprints replace the parametric formula.
        Assert.Equal(23_859.0, CandidateSizingCalculator.ComputeEffectiveWeightsMB(Default("jetelain/Flint:q4")));
        Assert.Equal(40_346.0, CandidateSizingCalculator.ComputeEffectiveWeightsMB(Default("jetelain/Flint:q8")));
        Assert.Equal(22_528.0, CandidateSizingCalculator.ComputeEffectiveWeightsMB(Default("jetelain/Qwen3.6-35B-A3B:q4")));
        Assert.Equal(40_960.0, CandidateSizingCalculator.ComputeEffectiveWeightsMB(Default("jetelain/Qwen3.6-35B-A3B:code")));
        Assert.Equal(12_288.0, CandidateSizingCalculator.ComputeEffectiveWeightsMB(Default("jetelain/Qwen3.8-27B:iq3")));
        Assert.Equal(18_944.0, CandidateSizingCalculator.ComputeEffectiveWeightsMB(Default("jetelain/Qwen3.8-27B:latest")));
        Assert.Equal(71_583.0, CandidateSizingCalculator.ComputeEffectiveWeightsMB(Default("jetelain/Ornith-1.5-122b:latest")));
    }

    [Fact]
    public void ComputeEffectiveWeightsMB_SyntheticModelWithoutAnchor_FallsBackToParametric()
    {
        var synthetic = new CandidateModel(
            "synthetic", [UsageRole.AgenticChat], "Dense",
            TotalParams: 27_800_000_000, ActiveParams: null,
            "q4", 4, 65, 4, 256, 262_144, 131_072);

        Assert.Equal(
            CandidateSizingCalculator.ComputeWeightsMB(27_800_000_000, 4),
            CandidateSizingCalculator.ComputeEffectiveWeightsMB(synthetic));
    }

    [Theory]
    // A7 KV pins: calibrated bytes/token × ctx / 2^20 (exact on these rungs).
    [InlineData("jetelain/Flint:q4", 16_384, 32.0)]
    [InlineData("jetelain/Flint:q4", 32_768, 64.0)]
    [InlineData("jetelain/Flint:q4", 49_152, 96.0)]
    [InlineData("jetelain/Flint:q4", 131_072, 256.0)]
    [InlineData("jetelain/Flint:q4", 262_144, 512.0)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", 16_384, 352.0)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", 32_768, 704.0)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", 49_152, 1_056.0)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", 131_072, 2_816.0)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", 262_144, 5_632.0)]
    [InlineData("jetelain/Qwen3.8-27B:latest", 16_384, 1_024.0)]
    [InlineData("jetelain/Qwen3.8-27B:latest", 32_768, 2_048.0)]
    [InlineData("jetelain/Qwen3.8-27B:latest", 49_152, 3_072.0)]
    [InlineData("jetelain/Qwen3.8-27B:latest", 131_072, 8_192.0)]
    [InlineData("jetelain/Qwen3.8-27B:latest", 262_144, 16_384.0)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", 16_384, 416.0)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", 32_768, 832.0)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", 49_152, 1_248.0)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", 131_072, 3_328.0)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", 262_144, 6_656.0)]
    public void ComputeEffectiveKvCacheMB_CalibratedBytesPerToken_PinsA7Values(string name, int ctx, double expectedMB)
    {
        Assert.Equal(
            expectedMB,
            CandidateSizingCalculator.ComputeEffectiveKvCacheMB(Default(name), ctx),
            precision: 6);
    }

    [Fact]
    public void ComputeEffectiveKvCacheMB_SyntheticModelWithoutCalibration_FallsBackToParametric()
    {
        var synthetic = new CandidateModel(
            "synthetic", [UsageRole.AgenticChat], "Dense",
            TotalParams: 27_800_000_000, ActiveParams: null,
            "q4", 4, 65, 4, 256, 262_144, 131_072);

        Assert.Equal(
            CandidateSizingCalculator.ComputeKvCacheMB(262_144, 65, 4, 256),
            CandidateSizingCalculator.ComputeEffectiveKvCacheMB(synthetic, 262_144));
    }

    // ------------------------------------------------------------------
    //  Hardware test profiles (A6)
    // ------------------------------------------------------------------

    private static NodeHardwareProfile Profile(long fastMB, long systemRamMB = 32_768, bool unified = false) =>
        new("node", "linux", "x64", unified, systemRamMB,
            unified ? SystemRamType.AppleUnified : SystemRamType.Ddr5,
            [], fastMB, 0);

    /// <summary>P8: 8 GB discrete GPU + 64 GB DDR5 RAM.</summary>
    private static NodeHardwareProfile P8() => Profile(8_192, systemRamMB: 65_536);

    /// <summary>P16: 16 GB discrete GPU + 64 GB DDR5 RAM.</summary>
    private static NodeHardwareProfile P16() => Profile(16_384, systemRamMB: 65_536);

    /// <summary>U64: 64 GB unified memory.</summary>
    private static NodeHardwareProfile U64() => Profile(65_536, systemRamMB: 65_536, unified: true);

    /// <summary>U96: 96 GB unified memory.</summary>
    private static NodeHardwareProfile U96() => Profile(98_304, systemRamMB: 98_304, unified: true);

    /// <summary>TTM88: 88 GB TTM VRAM pool (90 112 MiB) on a 96 GB server.</summary>
    private static NodeHardwareProfile Ttm88() => Profile(90_112, systemRamMB: 98_304);

    private static NodeHardwareProfile ProfileById(string id) => id switch
    {
        "P8" => P8(),
        "P16" => P16(),
        "U64" => U64(),
        "U96" => U96(),
        "TTM88" => Ttm88(),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    // ------------------------------------------------------------------
    //  The A7 7-model × 5-profile verdict matrix
    // ------------------------------------------------------------------
    // Limits: P8 7 536.64 / P16 15 073.28 / U64 60 293.12 / U96 90 439.68 / TTM88 82 903.04 MiB.
    // Rungs: P8 @16k/131k/256k, P16 @32k/48k/128k(131 072), U64/U96/TTM88 @256k.

    [Theory]
    [InlineData("jetelain/Flint:q4", "P8", 16_384, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q4", "P8", 131_072, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q4", "P8", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q4", "P16", 32_768, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q4", "P16", 49_152, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q4", "P16", 131_072, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q4", "P16", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q4", "U64", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q4", "U96", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q4", "TTM88", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q8", "P8", 16_384, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q8", "P8", 131_072, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q8", "P8", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q8", "P16", 32_768, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q8", "P16", 49_152, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q8", "P16", 131_072, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q8", "P16", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q8", "U64", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q8", "U96", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Flint:q8", "TTM88", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", "P8", 16_384, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", "P8", 131_072, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", "P8", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", "P16", 32_768, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", "P16", 49_152, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", "P16", 131_072, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", "P16", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", "U64", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", "U96", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:q4", "TTM88", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:code", "P8", 16_384, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:code", "P8", 131_072, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:code", "P8", 262_144, MemoryFitStatus.PrunedKvCache)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:code", "P16", 32_768, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:code", "P16", 49_152, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:code", "P16", 131_072, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:code", "P16", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:code", "U64", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:code", "U96", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.6-35B-A3B:code", "TTM88", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.8-27B:iq3", "P8", 16_384, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Qwen3.8-27B:iq3", "P8", 131_072, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Qwen3.8-27B:iq3", "P8", 262_144, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Qwen3.8-27B:iq3", "P16", 32_768, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.8-27B:iq3", "P16", 49_152, MemoryFitStatus.PrunedKvCache)]
    [InlineData("jetelain/Qwen3.8-27B:iq3", "P16", 131_072, MemoryFitStatus.PrunedKvCache)]
    [InlineData("jetelain/Qwen3.8-27B:iq3", "U64", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.8-27B:iq3", "U96", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.8-27B:iq3", "TTM88", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.8-27B:latest", "P8", 16_384, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Qwen3.8-27B:latest", "P8", 131_072, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Qwen3.8-27B:latest", "P8", 262_144, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Qwen3.8-27B:latest", "P16", 32_768, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Qwen3.8-27B:latest", "P16", 49_152, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Qwen3.8-27B:latest", "P16", 131_072, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Qwen3.8-27B:latest", "U64", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.8-27B:latest", "U96", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Qwen3.8-27B:latest", "TTM88", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", "P8", 16_384, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", "P8", 131_072, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", "P8", 262_144, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", "P16", 32_768, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", "P16", 49_152, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", "P16", 131_072, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", "P16", 262_144, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", "U64", 262_144, MemoryFitStatus.PrunedWeights)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", "U96", 262_144, MemoryFitStatus.Fits)]
    [InlineData("jetelain/Ornith-1.5-122b:latest", "TTM88", 262_144, MemoryFitStatus.Fits)]
    public void Evaluate_CuratedMatrix_MatchesTheVerifiedA7Verdicts(
        string modelName, string profileId, int ctx, MemoryFitStatus expected)
    {
        var estimate = CandidateSizingCalculator.Evaluate(Default(modelName), ctx, ProfileById(profileId));
        Assert.Equal(expected, estimate.Status);
        if (expected is MemoryFitStatus.Fits)
        {
            Assert.Null(estimate.Reason);
        }
        else
        {
            Assert.NotNull(estimate.Reason);
        }
    }

    // ------------------------------------------------------------------
    //  A7 exact-value pins: decode-bound fits/prunes + the ≥88 GB VRAM guarantee
    // ------------------------------------------------------------------

    [Fact]
    public void Qwen36Q4_P8_256k_DecodeFitsTight_With378MiBHeadroom()
    {
        // decode = active 3.2e9×4/8/2^20 = 1 525.879 + KV 5 632.0 = 7 157.879 ≤ 7 536.64
        // (378.76 MiB headroom — keep, do NOT "fix"); residency 22 528 + 5 632 = 28 160.0 ≤
        // 0.92 × (8 192 + 65 536) = 67 829.76 → Fits.
        var estimate = CandidateSizingCalculator.Evaluate(Default("jetelain/Qwen3.6-35B-A3B:q4"), 262_144, P8());
        Assert.Equal(MemoryFitStatus.Fits, estimate.Status);
        Assert.Equal(22_528.0, estimate.WeightsMB, precision: 3);
        Assert.Equal(1_525.879, estimate.ActiveWeightsMB, precision: 3);
        Assert.Equal(5_632.0, estimate.KvCacheMB, precision: 3);
        Assert.Equal(28_160.0, estimate.TotalRequiredMB, precision: 3);
        Assert.Equal(8_192, estimate.FastMemoryMB);
    }

    [Fact]
    public void Qwen36Code_P8_256k_DecodeBoundPrunedKvCache_WhileResidencyFits()
    {
        // decode = 3 051.758 + 5 632.0 = 8 683.758 > 7 536.64 → PrunedKvCache (the active
        // buffer alone, 3 051.758, is below the limit — decode-bound, NOT weights).
        // Residency still fits (40 960 + 5 632 = 46 592.0 ≤ 67 829.76) — the "fits memory,
        // evaluated empirically at runtime" case: the model is pulled, the 256k rung prunes.
        var estimate = CandidateSizingCalculator.Evaluate(Default("jetelain/Qwen3.6-35B-A3B:code"), 262_144, P8());
        Assert.Equal(MemoryFitStatus.PrunedKvCache, estimate.Status);
        Assert.Contains("active MoE weights + KV cache", estimate.Reason);
        Assert.Equal(40_960.0, estimate.WeightsMB, precision: 3);
        Assert.Equal(3_051.758, estimate.ActiveWeightsMB, precision: 3);
        Assert.Equal(46_592.0, estimate.TotalRequiredMB, precision: 3);
    }

    [Fact]
    public void Qwen36Code_P8_131k_Fits_LargestSurvivingRung()
    {
        // decode = 3 051.758 + 2 816.0 = 5 867.758 ≤ 7 536.64 → Fits (largest surviving rung).
        var estimate = CandidateSizingCalculator.Evaluate(Default("jetelain/Qwen3.6-35B-A3B:code"), 131_072, P8());
        Assert.Equal(MemoryFitStatus.Fits, estimate.Status);
    }

    [Fact]
    public void Qwen38Iq3_P16_48k_PrunedKvCache_ConservativeDenseRule()
    {
        // 12 288 + 3 072 = 15 360.0 > 15 073.28 (92% of 16 384) → PrunedKvCache. Deliberately
        // conservative (2026-11-22 product decision): empirical reports put the real boundary
        // at ~48–64k on a dedicated 16 GiB node; the 92% rule prunes one rung early.
        var estimate = CandidateSizingCalculator.Evaluate(Default("jetelain/Qwen3.8-27B:iq3"), 49_152, P16());
        Assert.Equal(MemoryFitStatus.PrunedKvCache, estimate.Status);
        Assert.Contains("KV", estimate.Reason);
    }

    [Fact]
    public void Qwen38Latest_U64_256k_Fits()
    {
        // 18 944 + 16 384 = 35 328.0 ≤ 60 293.12 → Fits.
        var estimate = CandidateSizingCalculator.Evaluate(Default("jetelain/Qwen3.8-27B:latest"), 262_144, U64());
        Assert.Equal(MemoryFitStatus.Fits, estimate.Status);
        Assert.Equal(35_328.0, estimate.TotalRequiredMB, precision: 3);
    }

    [Fact]
    public void Ornith_U64_256k_PrunedWeights_WeightsAloneExceed()
    {
        // Weights 71 583 alone exceed 60 293.12 (and 71 583 + 6 656 = 78 239.0 > 60 293.12).
        // Dense wording on every pruned profile (AllowCpuOffload = false), never the MoE one.
        var estimate = CandidateSizingCalculator.Evaluate(Default("jetelain/Ornith-1.5-122b:latest"), 262_144, U64());
        Assert.Equal(MemoryFitStatus.PrunedWeights, estimate.Status);
        Assert.Contains("alone exceed", estimate.Reason);
        Assert.DoesNotContain("full MoE weights", estimate.Reason);
        Assert.Equal(78_239.0, estimate.TotalRequiredMB, precision: 3);
    }

    [Fact]
    public void Ornith_U96_256k_Fits_78239Within90439()
    {
        // 71 583 + 6 656 = 78 239.0 ≤ 90 439.68 (92% of 98 304) → Fits (100% VRAM run).
        var estimate = CandidateSizingCalculator.Evaluate(Default("jetelain/Ornith-1.5-122b:latest"), 262_144, U96());
        Assert.Equal(MemoryFitStatus.Fits, estimate.Status);
        Assert.Equal(78_239.0, estimate.TotalRequiredMB, precision: 3);
    }

    [Fact]
    public void Ornith_TTM88_256k_Fits_The88GBVramGuarantee()
    {
        // 78 239.0 ≤ 82 903.04 (92% of the 90 112 MiB TTM pool) → Fits — the "requires
        // ≥88 GB VRAM" guarantee at 86.8% occupancy.
        var estimate = CandidateSizingCalculator.Evaluate(Default("jetelain/Ornith-1.5-122b:latest"), 262_144, Ttm88());
        Assert.Equal(MemoryFitStatus.Fits, estimate.Status);
    }

    [Fact]
    public void Ornith_P8_PrunedWeights_UsesDenseWording_NeverTheMoEOffloadWording()
    {
        // AllowCpuOffload = false → strict dense path even though ModelClass/Architecture say
        // MoE: the reason is the dense wording ("weights … alone exceed"), NOT "full MoE
        // weights do not fit in fast+system memory".
        var estimate = CandidateSizingCalculator.Evaluate(Default("jetelain/Ornith-1.5-122b:latest"), 16_384, P8());
        Assert.Equal(MemoryFitStatus.PrunedWeights, estimate.Status);
        Assert.Contains("weights", estimate.Reason);
        Assert.Contains("alone exceed", estimate.Reason);
        Assert.DoesNotContain("full MoE weights", estimate.Reason);
    }

    // ------------------------------------------------------------------
    //  Dense pruning (synthetic + curated)
    // ------------------------------------------------------------------

    private static readonly CandidateModel Qwen27b =
        Default("jetelain/Qwen3.8-27B:latest");

    [Fact]
    public void Dense_27bPrunedOn8GBProfile_WeightsDoNotFit()
    {
        // 8 GB fast, 8192 × 0.92 = 7536.64 limit; the 18 944 MiB resident anchor alone exceeds it.
        var estimate = CandidateSizingCalculator.Evaluate(Qwen27b, 16_384, Profile(8192));
        Assert.Equal(MemoryFitStatus.PrunedWeights, estimate.Status);
        Assert.NotNull(estimate.Reason);
        Assert.Contains("weights", estimate.Reason);
    }

    [Fact]
    public void Dense_27bFitsOn32GBProfile_16kCtx()
    {
        // 32768 × 0.92 = 30146.6 limit; 18 944 + 1 024 = 19 968 ≤ limit → Fits.
        var estimate = CandidateSizingCalculator.Evaluate(Qwen27b, 16_384, Profile(32_768));
        Assert.Equal(MemoryFitStatus.Fits, estimate.Status);
        Assert.Null(estimate.Reason);
        Assert.Equal(32_768, estimate.FastMemoryMB);
    }

    [Fact]
    public void Dense_27bPrunedKvCache_WhenOnlySumExceeds()
    {
        // Fast memory sized so weights fit but weights + 131k KV does not:
        // limit must be in (18 944, 27 136). Use fast = 22 000 → limit = 20 240: weights
        // 18 944 ≤ 20 240, sum 18 944 + 8 192 = 27 136 > 20 240.
        var estimate = CandidateSizingCalculator.Evaluate(Qwen27b, 131_072, Profile(22_000));
        Assert.Equal(MemoryFitStatus.PrunedKvCache, estimate.Status);
        Assert.Contains("KV", estimate.Reason);
    }

    [Fact]
    public void Dense_ExactlyAt92PercentLimit_Fits()
    {
        // Boundary semantics: prune only when STRICTLY greater than the limit.
        // Build a tiny synthetic model so the required total lands exactly on the limit.
        // weights = 90 MB exactly: p × 4 / 8 / 2^20 = 90 → p = 90 × 2 × 2^20 = 188743680.
        // KV = 2 MB: ctx = 524288, layers = 1, kvHeads = 1, headDim = 1, bpe = 2 →
        // 524288 × 1 × 1 × 1 × 2 × 2 / 2^20 = 2 MB. total = 92.0 exactly.
        // fast = 10000 → limit = 0.92 × 10000 = 92.0 → NOT strictly greater → Fits.
        var exact = new CandidateModel(
            "exact-fit", [UsageRole.BasicModel], "Dense",
            TotalParams: 188_743_680, ActiveParams: null,
            "q4", 4, Layers: 1, KvHeads: 1, HeadDim: 1,
            NativeContext: 131_072, DefaultContext: 131_072);
        var weights = CandidateSizingCalculator.ComputeWeightsMB(exact.TotalParams, 4);
        var kv = CandidateSizingCalculator.ComputeKvCacheMB(524_288, 1, 1, 1);
        Assert.Equal(90.0, weights, precision: 3);
        Assert.Equal(2.0, kv, precision: 3);

        var estimate = CandidateSizingCalculator.Evaluate(exact, 524_288, Profile(10_000));
        Assert.Equal(MemoryFitStatus.Fits, estimate.Status);
    }

    [Fact]
    public void Dense_JustAbove92PercentLimit_IsPruned()
    {
        // Same model, slightly smaller fast memory: limit drops below the 92 MB total.
        var exact = new CandidateModel(
            "exact-fit", [UsageRole.BasicModel], "Dense",
            TotalParams: 188_743_680, ActiveParams: null,
            "q4", 4, Layers: 1, KvHeads: 1, HeadDim: 1,
            NativeContext: 131_072, DefaultContext: 131_072);
        // fast = 99 → limit = 0.92 × 99 = 91.08 < 92 → PrunedKvCache
        // (weights 90 ≤ 91.08, but the sum 92 is strictly above the limit).
        var estimate = CandidateSizingCalculator.Evaluate(exact, 524_288, Profile(99));
        Assert.Equal(MemoryFitStatus.PrunedKvCache, estimate.Status);
    }

    // ------------------------------------------------------------------
    //  MoE rules (CPU-offload path, AllowCpuOffload = true)
    // ------------------------------------------------------------------

    private static readonly CandidateModel Qwen35bA3b =
        Default("jetelain/Qwen3.6-35B-A3B:q4");

    /// <summary>
    /// A fresh synthetic offloadable MoE with the ORIGINAL 64/8/128 geometry, so the
    /// 38 147.0 / 3 814.7 / 4 096 / 32 768 parametric pins stay exact (a <c>with</c>-override
    /// of the curated 41/2/128 geometry would drift the KV values).
    /// </summary>
    private static CandidateModel BigQ8Moe() => new(
        "moe:40b-q8", [UsageRole.AgenticChat], "MoE",
        TotalParams: 40_000_000_000, ActiveParams: 4_000_000_000,
        "q8", 8, Layers: 64, KvHeads: 8, HeadDim: 128,
        NativeContext: 131_072, DefaultContext: 131_072,
        ModelClass: CandidateModelClass.MoE,
        AllowCpuOffload: true);

    [Fact]
    public void Moe_ActiveBufferFits24GB_Fits()
    {
        // Active 3.2B q4 = 1 525.879 MB + KV(65k) 1 408.0 MB = 2 933.879 ≤ 0.92 × 24576 =
        // 22609.9 → decode fits. Full 22 528 + 1 408 = 23 936.0 ≤ 0.92 × (24576 + 65536 sys)
        // = 82 903.04 → residency fits.
        var estimate = CandidateSizingCalculator.Evaluate(Qwen35bA3b, 65_536, Profile(24_576, systemRamMB: 65_536));
        Assert.Equal(MemoryFitStatus.Fits, estimate.Status);
        Assert.Equal(1_525.879, estimate.ActiveWeightsMB, precision: 3);
        Assert.Equal(22_528.0, estimate.WeightsMB, precision: 3);
    }

    [Fact]
    public void Moe_FullWeightsDoNotFitFastPlusSystem_NonUnified_Pruned()
    {
        // Decode (active buffer + KV) fits in fast memory, but the FULL weights + KV cannot
        // be resident in any tier: 40B q8 MoE with 4B active params:
        // weights = 40e9 × 8 / 8 / 2^20 = 38147.0, active = 3814.7, KV(16k) = 4096.
        // fast = 16384 → limit 15069.1: decode 3814.7 + 4096 = 7910.7 ≤ limit → decode fits.
        // Full: 38147.0 + 4096 = 42243.0 > 0.92 × (16384 + 0 system) = 15069.1 →
        // PrunedWeights ("full MoE weights do not fit").
        var big = BigQ8Moe();
        var estimate = CandidateSizingCalculator.Evaluate(big, 16_384, Profile(16_384, systemRamMB: 0));
        Assert.Equal(MemoryFitStatus.PrunedWeights, estimate.Status);
        Assert.Contains("full MoE weights", estimate.Reason);
    }

    [Fact]
    public void Moe_Unified_FullWeightsMustFitFast_Pruned()
    {
        // Unified: the fast tier is the only tier. Same 40B q8 MoE model on a 40 GB unified
        // node: decode 7910.7 ≤ 0.92 × 40960 = 37683.2 → fits; full 42243.0 > 37683.2 →
        // PrunedWeights.
        var big = BigQ8Moe();
        var estimate = CandidateSizingCalculator.Evaluate(big, 16_384, Profile(40_960, unified: true));
        Assert.Equal(MemoryFitStatus.PrunedWeights, estimate.Status);
        Assert.Contains("full MoE weights", estimate.Reason);
    }

    [Fact]
    public void Moe_DecodeBufferExceedsFastMemory_PrunedKvCache()
    {
        // 40B q8 MoE: active 3814.7 + KV(131k) 32768 = 36582.7; fast 30720 → limit 28262.4 →
        // active alone (3814.7) ≤ limit, but the decode sum exceeds → PrunedKvCache.
        var big = BigQ8Moe();
        var estimate = CandidateSizingCalculator.Evaluate(big, 131_072, Profile(30_720, systemRamMB: 65_536));
        Assert.Equal(MemoryFitStatus.PrunedKvCache, estimate.Status);
    }

    [Fact]
    public void Moe_ActiveBufferAloneExceedsLimit_PrunedWeights_Attribution()
    {
        // Active buffer ALONE exceeds the fast limit → the prune is attributed to the
        // weights (PrunedWeights), not the KV cache.
        var big = BigQ8Moe(); // active = 3814.7 MB
        // fast = 3000 → limit = 2760.0 < 3814.7 (active alone).
        var estimate = CandidateSizingCalculator.Evaluate(big, 16_384, Profile(3_000, systemRamMB: 65_536));
        Assert.Equal(MemoryFitStatus.PrunedWeights, estimate.Status);
    }

    // ------------------------------------------------------------------
    //  Fail-open (unknown profile)
    // ------------------------------------------------------------------

    [Fact]
    public void UnknownProfile_IsAssumed_WithNoPruning()
    {
        var estimate = CandidateSizingCalculator.Evaluate(Qwen27b, 262_144, profile: null);
        Assert.Equal(MemoryFitStatus.Assumed, estimate.Status);
        Assert.Null(estimate.Reason);
        Assert.Null(estimate.FastMemoryMB);
        // All sizes are still computed (effective values):
        // weights = the 18 944 MiB resident anchor (NOT the parametric 13 256.073);
        // KV = the calibrated 65 KB/tok × 262 144 / 2^20 = 16 384.0 (NOT the parametric 65 536).
        Assert.Equal(18_944.0, estimate.WeightsMB, precision: 3);
        Assert.Equal(16_384.0, estimate.KvCacheMB, precision: 3);
        // Active weights stay PURELY PARAMETRIC per the A4 rule (ActiveParams ?? Total ×
        // bits / 8 / 2^20): 27.8e9 × 4 / 8 / 2^20 = 13 256.073 — the 18 944 MiB anchor
        // (UD layout + mmproj) never applies to the decode buffer.
        Assert.Equal(13_256.073, estimate.ActiveWeightsMB, precision: 3);
    }

    [Fact]
    public void ZeroFastMemoryProfile_IsAssumed_WithNoPruning()
    {
        var estimate = CandidateSizingCalculator.Evaluate(Qwen27b, 262_144, Profile(0));
        Assert.Equal(MemoryFitStatus.Assumed, estimate.Status);
        Assert.Null(estimate.FastMemoryMB);
    }
}
