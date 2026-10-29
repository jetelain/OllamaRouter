using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// Exact-value math pins for the pre-flight candidate sizing calculator (task #28).
/// MB = MiB (2^20) throughout.
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
    //  Dense pruning
    // ------------------------------------------------------------------

    private static NodeHardwareProfile Profile(long fastMB, long systemRamMB = 32_768, bool unified = false) =>
        new("node", "linux", "x64", unified, systemRamMB,
            unified ? SystemRamType.AppleUnified : SystemRamType.Ddr5,
            [], fastMB, 0);

    private static readonly CandidateModel Qwen27b =
        CandidateModelCatalogOptions.DefaultModels.First(m => m.Name == "qwen3.8:27b");

    [Fact]
    public void Dense_27bPrunedOn8GBProfile_WeightsDoNotFit()
    {
        // 8 GB fast, 8192 × 0.92 = 7526.4 limit; weights alone are 13256.1 MB.
        var estimate = CandidateSizingCalculator.Evaluate(Qwen27b, 16_384, Profile(8192));
        Assert.Equal(MemoryFitStatus.PrunedWeights, estimate.Status);
        Assert.NotNull(estimate.Reason);
        Assert.Contains("weights", estimate.Reason);
    }

    [Fact]
    public void Dense_27bFitsOn32GBProfile_16kCtx()
    {
        // 32768 × 0.92 = 30146.6 limit; 13256.1 + 4096 = 17352.1 ≤ limit → Fits.
        var estimate = CandidateSizingCalculator.Evaluate(Qwen27b, 16_384, Profile(32_768));
        Assert.Equal(MemoryFitStatus.Fits, estimate.Status);
        Assert.Null(estimate.Reason);
        Assert.Equal(32_768, estimate.FastMemoryMB);
    }

    [Fact]
    public void Dense_27bPrunedKvCache_WhenOnlySumExceeds()
    {
        // Fast memory sized so weights fit but weights + 131k KV does not:
        // limit must be in (13256.1, 46024.1). Use fast = 15500 → limit = 14260: weights
        // 13256.1 ≤ 14260, sum 13256.1 + 32768 = 46024.1 > 14260.
        var estimate = CandidateSizingCalculator.Evaluate(Qwen27b, 131_072, Profile(15_500));
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
    //  MoE rules
    // ------------------------------------------------------------------

    private static readonly CandidateModel Qwen35bA3b =
        CandidateModelCatalogOptions.DefaultModels.First(m => m.Name == "qwen3.6:35b-a3b");

    [Fact]
    public void Moe_ActiveBufferFits24GB_Fits()
    {
        // Active 3.3B q4 ≈ 1573.6 MB + KV(65k) 16384 MB = 17957.6 ≤ 0.92 × 24576 = 22609.9 →
        // decode fits. Full weights 16689.3 + 16384 = 33073.3 ≤ 0.92 × (24576 + 65536 sys) →
        // residency fits.
        var estimate = CandidateSizingCalculator.Evaluate(Qwen35bA3b, 65_536, Profile(24_576, systemRamMB: 65_536));
        Assert.Equal(MemoryFitStatus.Fits, estimate.Status);
        Assert.Equal(1573.563, estimate.ActiveWeightsMB, precision: 3);
        Assert.Equal(16_689.301, estimate.WeightsMB, precision: 3);
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
        var big = Qwen35bA3b with
        {
            TotalParams = 40_000_000_000,
            ActiveParams = 4_000_000_000,
            Quantization = "q8",
            BitsPerWeight = 8
        };
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
        var big = Qwen35bA3b with
        {
            TotalParams = 40_000_000_000,
            ActiveParams = 4_000_000_000,
            Quantization = "q8",
            BitsPerWeight = 8
        };
        var estimate = CandidateSizingCalculator.Evaluate(big, 16_384, Profile(40_960, unified: true));
        Assert.Equal(MemoryFitStatus.PrunedWeights, estimate.Status);
        Assert.Contains("full MoE weights", estimate.Reason);
    }

    [Fact]
    public void Moe_DecodeBufferExceedsFastMemory_PrunedKvCache()
    {
        // 40B q8 MoE: active 3814.7 + KV(131k) 32768 = 36582.7; fast 30720 → limit 28262.4 →
        // active alone (3814.7) ≤ limit, but the decode sum exceeds → PrunedKvCache.
        var big = Qwen35bA3b with
        {
            TotalParams = 40_000_000_000,
            ActiveParams = 4_000_000_000,
            Quantization = "q8",
            BitsPerWeight = 8
        };
        var estimate = CandidateSizingCalculator.Evaluate(big, 131_072, Profile(30_720, systemRamMB: 65_536));
        Assert.Equal(MemoryFitStatus.PrunedKvCache, estimate.Status);
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
        // All sizes are still computed.
        Assert.Equal(13_256.073, estimate.WeightsMB, precision: 3);
        Assert.Equal(65_536.0, estimate.KvCacheMB, precision: 3); // 262k ctx
    }

    [Fact]
    public void ZeroFastMemoryProfile_IsAssumed_WithNoPruning()
    {
        var estimate = CandidateSizingCalculator.Evaluate(Qwen27b, 262_144, Profile(0));
        Assert.Equal(MemoryFitStatus.Assumed, estimate.Status);
        Assert.Null(estimate.FastMemoryMB);
    }
}
