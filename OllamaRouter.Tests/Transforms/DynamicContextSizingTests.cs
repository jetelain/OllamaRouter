using System.Text.Json;
using OllamaRouter.Options;
using OllamaRouter.Services;
using OllamaRouter.Transforms;

namespace OllamaRouter.Tests.Transforms;

public class DynamicContextSizingTests
{
    private static DynamicContextOptions DefaultOptions() => new();

    // --- SelectProfile matrix ---------------------------------------------------------

    [Theory]
    [InlineData(null, "Moe")]
    [InlineData(8192, "Moe")]
    [InlineData(98304, "Moe")]
    public void SelectProfile_Moe_AlwaysReturnsMoeProfile(int? maxVramMB, string expected)
    {
        Assert.Equal(expected, DynamicContextSizingEngine.SelectProfile(ModelArchitectureKind.Moe, maxVramMB));
    }

    [Theory]
    [InlineData(8192, "DenseDesktop16gb")]
    [InlineData(16384, "DenseDesktop16gb")]
    [InlineData(98303, "DenseDesktop16gb")]
    [InlineData(98304, "DenseServer96gb")]
    [InlineData(null, "DenseDesktop16gb")] // Unknown VRAM is conservative.
    public void SelectProfile_Dense_SplitAt96GBClass(int? maxVramMB, string expected)
    {
        Assert.Equal(expected, DynamicContextSizingEngine.SelectProfile(ModelArchitectureKind.LargeDense, maxVramMB));
    }

    [Theory]
    [InlineData(null, "SmallDense")]
    [InlineData(8192, "SmallDense")]
    [InlineData(98304, "SmallDense")]
    public void SelectProfile_SmallDense_AlwaysReturnsSmallDenseProfile(int? maxVramMB, string expected)
    {
        Assert.Equal(expected, DynamicContextSizingEngine.SelectProfile(ModelArchitectureKind.SmallDense, maxVramMB));
    }

    // --- SelectBucket: MoE table (184,320 / 221,184 / 262,144) ------------------------

    [Theory]
    // 200k/195k-token MoE prompts exceed the 221,184 intermediate bucket, but the effective
    // ceiling (262,144) caps the requirement — the 221,184 bucket is selected (the required
    // checklist value: 200k MoE prompt → options.num_ctx = 221184).
    [InlineData(200_000, null, 262_144, 221_184)]
    [InlineData(195_000, null, 262_144, 221_184)]
    [InlineData(185_000, null, 262_144, 221_184)]
    [InlineData(100_000, null, 262_144, 184_320)]
    // A 200k-token MoE prompt (35B-A3B class) with the effective ceiling capped at 221,184
    // (e.g. an explicit Modelfile num_ctx) stays at 221,184.
    [InlineData(200_000, null, 221_184, 221_184)]
    // A prompt beyond the ceiling is clamped to the ceiling (never rejected).
    [InlineData(240_000, null, 262_144, 262_144)]
    [InlineData(50_000, null, 262_144, 184_320)]
    public void SelectBucket_Moe_PicksSmallestBucketAboveRequirement(
        long tokens, int? clientNumCtx, int ceiling, int expected)
    {
        var options = DefaultOptions();

        var injected = DynamicContextSizingEngine.SelectBucket(options, "Moe", tokens, clientNumCtx, ceiling);

        Assert.Equal(expected, injected);
    }

    // --- SelectBucket: DenseDesktop16gb table (32,768 / 40,960 / 49,152) ---------------

    [Theory]
    // 2k prompt with a large static client num_ctx (131072, e.g. Continue/Zed): clamped
    // down to the 48k desktop ceiling — upper-bound preference, never a disqualifier.
    [InlineData(2_000, 131_072, 49_152, 49_152)]
    [InlineData(33_000, null, 49_152, 40_960)]
    [InlineData(2_000, null, 49_152, 32_768)]
    // A SMALL static client value never truncates a larger prompt: the estimate wins.
    [InlineData(100_000, 8_192, 49_152, 49_152)]
    [InlineData(41_000, null, 49_152, 49_152)]
    // Explicit Modelfile num_ctx BELOW the selected bucket clamps the injected value.
    [InlineData(2_000, null, 24_576, 24_576)]
    public void SelectBucket_DenseDesktop16gb_ClientValueIsUpperBoundPreference(
        long tokens, int? clientNumCtx, int ceiling, int expected)
    {
        var options = DefaultOptions();

        var injected = DynamicContextSizingEngine.SelectBucket(options, "DenseDesktop16gb", tokens, clientNumCtx, ceiling);

        Assert.Equal(expected, injected);
    }

    // --- SelectBucket: DenseServer96gb and SmallDense tables --------------------------

    [Theory]
    [InlineData(70_000, null, 262_144, 131_072)]
    [InlineData(200_000, null, 262_144, 262_144)]
    [InlineData(50_000, null, 262_144, 65_536)]
    public void SelectBucket_DenseServer96gb_FollowsProfileTable(long tokens, int? clientNumCtx, int ceiling, int expected)
    {
        var options = DefaultOptions();

        var injected = DynamicContextSizingEngine.SelectBucket(options, "DenseServer96gb", tokens, clientNumCtx, ceiling);

        Assert.Equal(expected, injected);
    }

    [Theory]
    [InlineData(50_000, null, 131_072, 65_536)]
    [InlineData(100_000, null, 131_072, 131_072)]
    [InlineData(10_000, null, 131_072, 32_768)]
    public void SelectBucket_SmallDense_FollowsProfileTable(long tokens, int? clientNumCtx, int ceiling, int expected)
    {
        var options = DefaultOptions();

        var injected = DynamicContextSizingEngine.SelectBucket(options, "SmallDense", tokens, clientNumCtx, ceiling);

        Assert.Equal(expected, injected);
    }

    // --- SelectBucket: custom profile with synthesized intermediate --------------------

    [Fact]
    public void SelectBucket_CustomProfile_SynthesizesIntermediateOnStepGrid()
    {
        // Custom profile: DefaultContext 32,768, MaxContext 131,072, no explicit
        // intermediate → snapUpToStep(ceil((32768 + 131072) / 2), 32768)
        // = snapUpToStep(81920, 32768) = 98,304.
        var options = DefaultOptions();
        options.Profiles["Custom"] = new DynamicContextProfile(32_768, null, 131_072);

        Assert.Equal(98_304, DynamicContextSizingEngine.SelectBucket(options, "Custom", 50_000, null, 131_072));
        // A requirement above the synthesized intermediate takes the next bucket up.
        Assert.Equal(131_072, DynamicContextSizingEngine.SelectBucket(options, "Custom", 100_000, null, 131_072));
        Assert.Equal(32_768, DynamicContextSizingEngine.SelectBucket(options, "Custom", 10_000, null, 131_072));
        // The smallest bucket above 40,000 is the synthesized intermediate (98,304).
        Assert.Equal(98_304, DynamicContextSizingEngine.SelectBucket(options, "Custom", 40_000, null, 131_072));
    }

    [Fact]
    public void SelectBucket_CustomProfile_NonStepAlignedMidpoint_IsClampedToMax()
    {
        // (1000 + 3300) / 2 = 2150 → snapUpToStep(2150, 1000) = 3000; 3000 < max 3300.
        var options = DefaultOptions();
        options.BucketStepTokens = 1_000;
        options.Profiles["Tiny"] = new DynamicContextProfile(1_000, null, 3_300);

        Assert.Equal(3_000, DynamicContextSizingEngine.SelectBucket(options, "Tiny", 2_500, null, 3_300));

        // (1000 + 2200) / 2 = 1600 → snapUpToStep(1600, 1000) = 2000 → clamped to max 2200?
        // No: 2000 ≤ 2200, so the ladder is 1000 / 2000 / 2200.
        options.Profiles["Tiny2"] = new DynamicContextProfile(1_000, null, 2_200);
        Assert.Equal(2_000, DynamicContextSizingEngine.SelectBucket(options, "Tiny2", 1_500, null, 2_200));
    }

    [Fact]
    public void SelectBucket_NonPositiveStep_FallsBackToDefaultGrid()
    {
        var options = DefaultOptions();
        options.BucketStepTokens = 0; // Guard: clamped to the default 32,768 grid.
        options.Profiles["Custom"] = new DynamicContextProfile(32_768, null, 131_072);

        // Midpoint 81,920 snaps to 98,304 on the default 32,768 grid.
        Assert.Equal(98_304, DynamicContextSizingEngine.SelectBucket(options, "Custom", 50_000, null, 131_072));
    }

    [Fact]
    public void SelectBucket_UnknownProfileName_FallsBackToConservativeDefaults()
    {
        var options = DefaultOptions();

        // Unknown profile name → the built-in conservative default (32,768 baseline).
        Assert.Equal(32_768, DynamicContextSizingEngine.SelectBucket(options, "DoesNotExist", 10_000, null, 131_072));
    }

    // --- Decide (full decision per (target, model, prompt)) ----------------------------

    [Fact]
    public void Decide_Moe200k_96GBClass_Ceiling221184_Emits221184()
    {
        // 200k-token MoE prompt on a 96 GB-class target whose effective ceiling is capped at
        // 221,184 (explicit Modelfile num_ctx): the bucket is clamped to the ceiling — the
        // required checklist value (200k-token MoE prompt → options.num_ctx = 221184).
        var decision = DynamicContextSizingEngine.Decide(
            DefaultOptions(), ModelArchitectureKind.Moe, 98_304, 221_184, estimatedTokens: 200_000, clientRequestedNumCtx: null);

        Assert.Equal("Moe", decision.ProfileName);
        Assert.Equal(221_184, decision.InjectedNumCtx);
        Assert.Equal(221_184, decision.EffectiveCeiling);
        Assert.False(decision.ClientRequestedClamped);
    }

    [Fact]
    public void Decide_Moe195k_96GBClass_FullCeiling_Emits221184()
    {
        var decision = DynamicContextSizingEngine.Decide(
            DefaultOptions(), ModelArchitectureKind.Moe, 98_304, 262_144, estimatedTokens: 195_000, clientRequestedNumCtx: null);

        Assert.Equal("Moe", decision.ProfileName);
        Assert.Equal(221_184, decision.InjectedNumCtx);
        Assert.Equal(262_144, decision.EffectiveCeiling);
        Assert.False(decision.ClientRequestedClamped);
    }
    [Fact]
    public void Decide_DenseDesktop2kClient131072_ClampedTo49152()
    {
        var decision = DynamicContextSizingEngine.Decide(
            DefaultOptions(), ModelArchitectureKind.LargeDense, 16_384, 49_152, estimatedTokens: 2_000, clientRequestedNumCtx: 131_072);

        Assert.Equal("DenseDesktop16gb", decision.ProfileName);
        Assert.Equal(49_152, decision.InjectedNumCtx);
        Assert.Equal(49_152, decision.EffectiveCeiling);
        Assert.True(decision.ClientRequestedClamped);
    }

    [Fact]
    public void Decide_CeilingBelowProfileMax_LimitsEffectiveCeiling()
    {
        // Hardware CMax (e.g. unknown-VRAM target → 4096) caps everything.
        var decision = DynamicContextSizingEngine.Decide(
            DefaultOptions(), ModelArchitectureKind.Moe, null, 4_096, estimatedTokens: 10_000, clientRequestedNumCtx: null);

        Assert.Equal(4_096, decision.EffectiveCeiling);
        Assert.Equal(4_096, decision.InjectedNumCtx);
    }

    // --- IsHardwareSuitable: Stage-1 hard filter ---------------------------------------

    private static TargetModelCapability Capability(ModelArchitectureKind architecture, int cDefault, int cMax, bool hardwareCapable)
    {
        return new TargetModelCapability("t", architecture, ArchitectureDetectionSource.DenseParameterCount, cDefault, cMax, hardwareCapable, null);
    }

    [Fact]
    public void IsHardwareSuitable_27BDenseOn8GB_IsExcludedEvenWhenIdle()
    {
        // LargeDense (27 B) on an 8 GB target: the veto is encoded in HardwareCapable and
        // ignores activity state entirely — the capability carries no notion of idle/loaded,
        // so the model is strictly excluded at qualification EVEN WHEN IDLE.
        var capability = Capability(ModelArchitectureKind.LargeDense, 4_096, 262_144, hardwareCapable: false);

        Assert.False(DynamicContextSizingEngine.IsHardwareSuitable(capability));
    }

    [Fact]
    public void IsHardwareSuitable_27BDenseOn16GB_IsSuitable()
    {
        var capability = Capability(ModelArchitectureKind.LargeDense, 4_096, 49_152, hardwareCapable: true);

        Assert.True(DynamicContextSizingEngine.IsHardwareSuitable(capability));
    }

    [Fact]
    public void IsHardwareSuitable_MoeOn8GB_IsSuitable()
    {
        // MoE is capable on all targets (plan §2.1): never VRAM-vetoed.
        var capability = Capability(ModelArchitectureKind.Moe, 4_096, 262_144, hardwareCapable: true);

        Assert.True(DynamicContextSizingEngine.IsHardwareSuitable(capability));
    }

    // --- DynamicContextOptions ---------------------------------------------------------

    [Fact]
    public void Options_Defaults_ShipBuiltInProfileTable()
    {
        var options = new DynamicContextOptions();

        Assert.True(options.Enabled);
        Assert.Equal(32_768, options.BucketStepTokens);
        Assert.Equal(3, options.AutoShrinkCooldownTurns);
        Assert.Equal(60, options.AutoShrinkIdleTimeoutSeconds);

        var moe = options.GetProfile("Moe");
        Assert.Equal(184_320, moe.DefaultContext);
        Assert.Equal(221_184, moe.IntermediateContext);
        Assert.Equal(262_144, moe.MaxContext);

        var desktop = options.GetProfile("DenseDesktop16gb");
        Assert.Equal(32_768, desktop.DefaultContext);
        Assert.Equal(40_960, desktop.IntermediateContext);
        Assert.Equal(49_152, desktop.MaxContext);

        var server = options.GetProfile("DenseServer96gb");
        Assert.Equal(65_536, server.DefaultContext);
        Assert.Equal(131_072, server.IntermediateContext);
        Assert.Equal(262_144, server.MaxContext);

        var small = options.GetProfile("SmallDense");
        Assert.Equal(32_768, small.DefaultContext);
        Assert.Equal(65_536, small.IntermediateContext);
        Assert.Equal(131_072, small.MaxContext);
    }

    [Fact]
    public void Options_GetProfile_IsCaseInsensitive()
    {
        var options = new DynamicContextOptions();

        Assert.Equal(262_144, options.GetProfile("moe").MaxContext);
        Assert.Equal(49_152, options.GetProfile("denseDESKTOP16GB").MaxContext);
    }

    [Fact]
    public void Options_Normalize_RestoresMissingBuiltInProfiles()
    {
        // Simulates config binding that replaced the dictionary with a partial override.
        var options = new DynamicContextOptions
        {
            Profiles = new Dictionary<string, DynamicContextProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Moe"] = new(184_320, 221_184, 300_000)
            }
        };

        options.Normalize();

        // The operator override is preserved...
        Assert.Equal(300_000, options.Profiles["Moe"].MaxContext);
        // ...and the missing built-in profiles are restored.
        Assert.Equal(49_152, options.Profiles["DenseDesktop16gb"].MaxContext);
        Assert.Equal(262_144, options.Profiles["DenseServer96gb"].MaxContext);
        Assert.Equal(131_072, options.Profiles["SmallDense"].MaxContext);
    }

    [Fact]
    public void Options_Normalize_ClampsNonPositiveBucketStep()
    {
        var options = new DynamicContextOptions { BucketStepTokens = -5 };

        options.Normalize();

        Assert.Equal(32_768, options.BucketStepTokens);
    }

    [Fact]
    public void Options_Normalize_RepairsZeroValuedBuiltInProfile()
    {
        var options = new DynamicContextOptions();
        options.Profiles["Moe"] = new DynamicContextProfile(0, null, 0);

        options.Normalize();

        Assert.Equal(262_144, options.GetProfile("Moe").MaxContext);
    }

    // --- ExtractClientNumCtx -----------------------------------------------------------

    [Theory]
    [InlineData("{\"model\":\"x\",\"options\":{\"num_ctx\":131072}}", 131072)]
    [InlineData("{\"model\":\"x\",\"options\":{\"num_ctx\":8192,\"temperature\":0.2}}", 8192)]
    [InlineData("{\"model\":\"x\"}", null)]
    [InlineData("{\"model\":\"x\",\"options\":{}}", null)]
    [InlineData("{\"model\":\"x\",\"options\":{\"num_ctx\":\"8192\"}}", null)]   // Non-numeric.
    [InlineData("{\"model\":\"x\",\"options\":{\"num_ctx\":0}}", null)]        // Non-positive.
    [InlineData("not-json", null)]
    [InlineData("[1,2,3]", null)]                                                 // Non-object root.
    public void ExtractClientNumCtx_ReadsTolerantly(string body, int? expected)
    {
        Assert.Equal(expected, DynamicContextPayloadTransformer.ExtractClientNumCtx(body));
    }

    // --- Transformer Apply -------------------------------------------------------------

    [Fact]
    public void Apply_InjectsNumCtx_WhenOptionsAbsent()
    {
        const string body = """{"model":"qwen3.6-35b-a3b","messages":[{"role":"user","content":"hi"}]}""";
        var decision = new ContextSizingDecision("Moe", 221_184, 262_144, false);

        var (newBody, injected) = DynamicContextPayloadTransformer.Apply(body, decision, new DynamicContextOptions());

        Assert.Equal(221_184, injected);
        using var doc = JsonDocument.Parse(newBody);
        Assert.Equal("qwen3.6-35b-a3b", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal(221_184, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public void Apply_OverwritesExistingNumCtx_PreservesOtherOptionKeys()
    {
        const string body = """{"model":"m","options":{"num_ctx":131072,"temperature":0.1}}""";
        var decision = new ContextSizingDecision("DenseDesktop16gb", 49_152, 49_152, true);

        var (newBody, injected) = DynamicContextPayloadTransformer.Apply(body, decision, new DynamicContextOptions());

        Assert.Equal(49_152, injected);
        using var doc = JsonDocument.Parse(newBody);
        var options = doc.RootElement.GetProperty("options");
        Assert.Equal(49_152, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(0.1, options.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal("m", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void Apply_Disabled_ReturnsBodyUnchanged()
    {
        const string body = """{"model":"m"}""";
        var decision = new ContextSizingDecision("Moe", 221_184, 262_144, false);
        var options = new DynamicContextOptions { Enabled = false };

        var (newBody, injected) = DynamicContextPayloadTransformer.Apply(body, decision, options);

        Assert.Same(body, newBody);
        Assert.Null(injected);
    }

    [Fact]
    public void Apply_UnparseableBody_ReturnsBodyUnchanged()
    {
        const string body = "this is not json";
        var decision = new ContextSizingDecision("Moe", 221_184, 262_144, false);

        var (newBody, injected) = DynamicContextPayloadTransformer.Apply(body, decision, new DynamicContextOptions());

        Assert.Same(body, newBody);
        Assert.Null(injected);
    }

    [Fact]
    public void Apply_NonObjectRoot_ReturnsBodyUnchanged()
    {
        const string body = "[1,2,3]";
        var decision = new ContextSizingDecision("Moe", 221_184, 262_144, false);

        var (newBody, injected) = DynamicContextPayloadTransformer.Apply(body, decision, new DynamicContextOptions());

        Assert.Same(body, newBody);
        Assert.Null(injected);
    }

    [Fact]
    public void Apply_NonPositiveInjectedValue_ReturnsBodyUnchanged()
    {
        const string body = """{"model":"m"}""";
        var decision = new ContextSizingDecision("Moe", 0, 0, false);

        var (newBody, injected) = DynamicContextPayloadTransformer.Apply(body, decision, new DynamicContextOptions());

        Assert.Same(body, newBody);
        Assert.Null(injected);
    }
}
