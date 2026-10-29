using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaRouter.Options;
using OllamaRouter.Services;
using OllamaRouter.Transforms;

namespace OllamaRouter.Tests.Transforms;

/// <summary>
/// Two-bucket MoE ladder and dense clamp-only sizing tests (task #26).
/// </summary>
public class DynamicContextSizingTests
{
    private static DynamicContextOptions DefaultOptions() => new();

    // MoE capability helper: pre-configured (Modelfile) num_ctx, native context, CMax.
    private static TargetModelCapability MoeCap(int? modelfileNumCtx, int? native, int? cMaxOverride = null)
    {
        var cDefault = modelfileNumCtx ?? 4_096;
        var cMax = cMaxOverride ?? modelfileNumCtx ?? native ?? 131_072;
        return new TargetModelCapability("t", ModelArchitectureKind.Moe, ArchitectureDetectionSource.MoeExpertCount,
            cDefault, cMax, true, null, native);
    }

    private static TargetModelCapability DenseCap(int? modelfileNumCtx, int? native, ModelArchitectureKind kind = ModelArchitectureKind.LargeDense)
    {
        var cDefault = modelfileNumCtx ?? 4_096;
        var cMax = modelfileNumCtx ?? native ?? 131_072;
        return new TargetModelCapability("t", kind, ArchitectureDetectionSource.DenseParameterCount,
            cDefault, cMax, true, null, native);
    }

    // ================================================================================
    //  MoE two-bucket ladder (Decide)
    // ================================================================================

    [Theory]
    // Below baseline (184,320): requirement 100k+8,192=108,192 ≤ 184,320 → Baseline.
    [InlineData(100_000, null, 184_320, 262_144, ContextSizingMode.Baseline, 184_320)]
    // At baseline exactly: 176,128 + 8,192 = 184,320 ≤ 184,320 → Baseline.
    [InlineData(176_128, null, 184_320, 262_144, ContextSizingMode.Baseline, 184_320)]
    // Above baseline: 190k + 8,192 = 198,192 > 184,320 → Expanded to extensible 262,144.
    [InlineData(190_000, null, 184_320, 262_144, ContextSizingMode.Expanded, 262_144)]
    // Way above: 250k → Expanded.
    [InlineData(250_000, null, 184_320, 262_144, ContextSizingMode.Expanded, 262_144)]
    public void Decide_Moe_TwoBucketLadder(
        long tokens, int? clientNumCtx, int modelfile, int native, ContextSizingMode expectedMode, int expectedInjected)
    {
        var cap = MoeCap(modelfile, native);
        var d = DynamicContextSizingEngine.Decide(DefaultOptions(), cap, tokens, clientNumCtx, 8_192, null);

        Assert.Equal(expectedMode, d.Mode);
        Assert.Equal(expectedInjected, d.InjectedNumCtx);
        Assert.Equal("Moe", d.ProfileName);
        Assert.Equal(native, d.EffectiveCeiling); // extensible = min(native, ∞)
        Assert.Equal(modelfile, d.BaselineBucket);
        Assert.False(d.ClientRequestedClamped);
    }

    [Fact]
    public void Decide_Moe_ClientNumCtx_LowersRequirement_ToBaseline()
    {
        // 190k prompt + 8,192 margin = 198,192 > 184,320, but client num_ctx 150,000
        // lowers requirement to 150,000 ≤ 184,320 → Baseline.
        var cap = MoeCap(184_320, 262_144);
        var d = DynamicContextSizingEngine.Decide(DefaultOptions(), cap, 190_000, 150_000, 8_192, null);

        Assert.Equal(ContextSizingMode.Baseline, d.Mode);
        Assert.Equal(184_320, d.InjectedNumCtx);
    }

    [Fact]
    public void Decide_Moe_ClientNumCtxAboveInjected_IsReportedClamped()
    {
        // 100k prompt → baseline 184,320, but client requested 200,000 → clamped flag.
        var cap = MoeCap(184_320, 262_144);
        var d = DynamicContextSizingEngine.Decide(DefaultOptions(), cap, 100_000, 200_000, 8_192, null);

        Assert.Equal(ContextSizingMode.Baseline, d.Mode);
        Assert.Equal(184_320, d.InjectedNumCtx);
        Assert.True(d.ClientRequestedClamped);
    }

    [Fact]
    public void Decide_Moe_NoModelfile_UsesNativeAsBaseline()
    {
        // No Modelfile num_ctx, native 262,144: baseline = 262,144, extensible = 262,144.
        // Every request is Baseline (no expansion possible).
        var cap = MoeCap(null, 262_144);
        var d = DynamicContextSizingEngine.Decide(DefaultOptions(), cap, 100_000, null, 8_192, null);

        Assert.Equal(ContextSizingMode.Baseline, d.Mode);
        Assert.Equal(262_144, d.InjectedNumCtx);
        Assert.Equal(262_144, d.BaselineBucket);
        Assert.Equal(262_144, d.EffectiveCeiling);
    }

    [Fact]
    public void Decide_Moe_NoModelfileNoNative_FallsBackTo128k()
    {
        // No Modelfile, no native: baseline = 128k, extensible = 128k.
        var cap = MoeCap(null, null);
        var d = DynamicContextSizingEngine.Decide(DefaultOptions(), cap, 50_000, null, 8_192, null);

        Assert.Equal(ContextSizingMode.Baseline, d.Mode);
        Assert.Equal(131_072, d.InjectedNumCtx);
        Assert.Equal(131_072, d.BaselineBucket);
        Assert.Equal(131_072, d.EffectiveCeiling);
    }

    [Fact]
    public void Decide_Moe_MaxContextTokens_CapsExtensibleLimit()
    {
        // Modelfile 184,320, native 262,144, MaxContextTokens 200,000:
        // extensible = min(262,144, 200,000) = 200,000.
        // 190k + 8,192 = 198,192 > 184,320 → Expanded to 200,000.
        var cap = MoeCap(184_320, 262_144);
        var d = DynamicContextSizingEngine.Decide(DefaultOptions(), cap, 190_000, null, 8_192, 200_000);

        Assert.Equal(ContextSizingMode.Expanded, d.Mode);
        Assert.Equal(200_000, d.InjectedNumCtx);
        Assert.Equal(200_000, d.EffectiveCeiling);
    }

    [Fact]
    public void Decide_Moe_MaxContextTokensBelowModelfile_BaselineInjectsCap()
    {
        // Modelfile 200,000, native 262,144, MaxContextTokens 150,000:
        // extensible = min(262,144, 150,000) = 150,000.
        // baseline = 200,000. requirement 50k ≤ 200,000 → Baseline.
        // injected = min(200,000, 150,000) = 150,000.
        var cap = MoeCap(200_000, 262_144);
        var d = DynamicContextSizingEngine.Decide(DefaultOptions(), cap, 50_000, null, 8_192, 150_000);

        Assert.Equal(ContextSizingMode.Baseline, d.Mode);
        Assert.Equal(150_000, d.InjectedNumCtx);
        Assert.Equal(150_000, d.EffectiveCeiling);
        Assert.Equal(200_000, d.BaselineBucket);
    }

    // ================================================================================
    //  Dense clamp-only pass-through (Decide)
    // ================================================================================

    [Fact]
    public void Decide_Dense_ClientAboveBaseline_Clamped()
    {
        // Dense, Modelfile 49,152, client num_ctx 131,072 → Clamped to 49,152.
        var cap = DenseCap(49_152, 49_152);
        var d = DynamicContextSizingEngine.Decide(DefaultOptions(), cap, 2_000, 131_072, 8_192, null);

        Assert.Equal(ContextSizingMode.Clamped, d.Mode);
        Assert.Equal(49_152, d.InjectedNumCtx);
        Assert.Equal(49_152, d.EffectiveCeiling); // CMax
        Assert.True(d.ClientRequestedClamped);
        Assert.Equal(49_152, d.BaselineBucket);
    }

    [Fact]
    public void Decide_Dense_ClientBelowBaseline_PassThrough()
    {
        // Dense, Modelfile 49,152, client num_ctx 40,000 (≤ 49,152) → PassThrough.
        var cap = DenseCap(49_152, 49_152);
        var d = DynamicContextSizingEngine.Decide(DefaultOptions(), cap, 2_000, 40_000, 8_192, null);

        Assert.Equal(ContextSizingMode.PassThrough, d.Mode);
        Assert.Equal(0, d.InjectedNumCtx); // sentinel: no rewrite
        Assert.Equal(49_152, d.EffectiveCeiling);
        Assert.False(d.ClientRequestedClamped);
    }

    [Fact]
    public void Decide_Dense_NoClientNumCtx_PassThrough()
    {
        // Dense, Modelfile 49,152, no client num_ctx → PassThrough.
        var cap = DenseCap(49_152, 49_152);
        var d = DynamicContextSizingEngine.Decide(DefaultOptions(), cap, 2_000, null, 8_192, null);

        Assert.Equal(ContextSizingMode.PassThrough, d.Mode);
        Assert.Equal(0, d.InjectedNumCtx);
    }

    [Fact]
    public void Decide_Dense_DenseClampDisabled_ClientAboveBaseline_PassThrough()
    {
        // DenseClampEnabled = false → even oversized client value → PassThrough.
        var opts = new DynamicContextOptions { DenseClampEnabled = false };
        var cap = DenseCap(49_152, 49_152);
        var d = DynamicContextSizingEngine.Decide(opts, cap, 2_000, 131_072, 8_192, null);

        Assert.Equal(ContextSizingMode.PassThrough, d.Mode);
        Assert.Equal(0, d.InjectedNumCtx);
    }

    [Fact]
    public void Decide_SmallDense_Clamped()
    {
        // SmallDense also gets clamp-only.
        var cap = DenseCap(32_768, 32_768, ModelArchitectureKind.SmallDense);
        var d = DynamicContextSizingEngine.Decide(DefaultOptions(), cap, 5_000, 131_072, 8_192, null);

        Assert.Equal(ContextSizingMode.Clamped, d.Mode);
        Assert.Equal(32_768, d.InjectedNumCtx);
    }

    // ================================================================================
    //  SelectBucket (public two-bucket helper)
    // ================================================================================

    [Theory]
    [InlineData(100_000, null, ContextSizingMode.Baseline, 184_320)]
    [InlineData(190_000, null, ContextSizingMode.Expanded, 262_144)]
    [InlineData(250_000, null, ContextSizingMode.Expanded, 262_144)]
    public void SelectBucket_Moe_TwoBucket(
        long tokens, int? clientNumCtx, ContextSizingMode expectedMode, int expectedInjected)
    {
        var cap = MoeCap(184_320, 262_144);
        var (mode, injected, baseline, extensibleMax) =
            DynamicContextSizingEngine.SelectBucket(cap, tokens, clientNumCtx, 8_192, null);

        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedInjected, injected);
        Assert.Equal(184_320, baseline);
        Assert.Equal(262_144, extensibleMax);
    }

    [Fact]
    public void SelectBucket_Moe_MaxContextTokens_CapsExtensible()
    {
        var cap = MoeCap(184_320, 262_144);
        var (_, injected, _, extensibleMax) =
            DynamicContextSizingEngine.SelectBucket(cap, 190_000, null, 8_192, 200_000);

        Assert.Equal(200_000, injected);
        Assert.Equal(200_000, extensibleMax);
    }

    // ================================================================================
    //  BaselineBucket / ModelfileNumCtx / EffectiveNativeContext helpers
    // ================================================================================

    [Theory]
    [InlineData(184_320, 262_144, 184_320)]  // Modelfile present → uses it.
    [InlineData(null, 262_144, 262_144)]     // No Modelfile → native.
    [InlineData(null, null, 131_072)]         // Neither → 128k fallback.
    public void BaselineBucket_Resolution(int? modelfile, int? native, int expected)
    {
        var cap = MoeCap(modelfile, native);
        Assert.Equal(expected, DynamicContextSizingEngine.BaselineBucket(cap));
    }

    // ================================================================================
    //  IsHardwareSuitable: Stage-1 hard filter
    // ================================================================================

    [Fact]
    public void IsHardwareSuitable_27BDenseOn8GB_IsExcludedEvenWhenIdle()
    {
        var capability = new TargetModelCapability("t", ModelArchitectureKind.LargeDense,
            ArchitectureDetectionSource.DenseParameterCount, 4_096, 262_144, false, null, null);
        Assert.False(DynamicContextSizingEngine.IsHardwareSuitable(capability));
    }

    [Fact]
    public void IsHardwareSuitable_27BDenseOn16GB_IsSuitable()
    {
        var capability = new TargetModelCapability("t", ModelArchitectureKind.LargeDense,
            ArchitectureDetectionSource.DenseParameterCount, 4_096, 49_152, true, null, null);
        Assert.True(DynamicContextSizingEngine.IsHardwareSuitable(capability));
    }

    [Fact]
    public void IsHardwareSuitable_MoeOn8GB_IsSuitable()
    {
        var capability = new TargetModelCapability("t", ModelArchitectureKind.Moe,
            ArchitectureDetectionSource.MoeExpertCount, 4_096, 262_144, true, null, null);
        Assert.True(DynamicContextSizingEngine.IsHardwareSuitable(capability));
    }

    // ================================================================================
    //  DynamicContextOptions (task #26 changes)
    // ================================================================================

    [Fact]
    public void Options_Defaults_ShipOnlyMoeProfile()
    {
        var options = new DynamicContextOptions();

        Assert.False(options.Enabled);
        Assert.True(options.DenseClampEnabled);
        Assert.Equal(3, options.AutoShrinkCooldownTurns);
        Assert.Equal(60, options.AutoShrinkIdleTimeoutSeconds);

        // Only the Moe profile is built-in (task #26).
        Assert.Single(options.Profiles);
        Assert.True(options.Profiles.ContainsKey("Moe"));

        // Dense profiles are NOT restored.
#pragma warning disable CS0618
        Assert.False(options.Profiles.ContainsKey(DynamicContextOptions.DenseDesktop16gbProfileName));
        Assert.False(options.Profiles.ContainsKey(DynamicContextOptions.DenseServer96gbProfileName));
        Assert.False(options.Profiles.ContainsKey(DynamicContextOptions.SmallDenseProfileName));
#pragma warning restore CS0618
    }

    [Fact]
    public void Options_Normalize_RestoresOnlyMoeProfile()
    {
        // Simulates config binding that replaced the dictionary with an empty one.
        var options = new DynamicContextOptions
        {
            Profiles = new Dictionary<string, DynamicContextProfile>(StringComparer.OrdinalIgnoreCase)
        };

        options.Normalize();

        // The Moe profile is restored.
        Assert.True(options.Profiles.ContainsKey("Moe"));
        Assert.Equal(184_320, options.Profiles["Moe"].DefaultContext);
    }

    [Fact]
    public void Options_Normalize_LegacyDenseProfile_WarnsButKeeps()
    {
        var options = new DynamicContextOptions
        {
            Profiles = new Dictionary<string, DynamicContextProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Moe"] = new(184_320, null, 262_144),
#pragma warning disable CS0618
                ["DenseDesktop16gb"] = new(32_768, null, 49_152)
#pragma warning restore CS0618
            }
        };

        var warnings = new List<string>();
        var logger = new TestLogger(warnings);
        options.Normalize(logger);

        // Warning logged naming the legacy profile.
        Assert.Single(warnings);
        Assert.Contains("DenseDesktop16gb", warnings[0]);

        // The profile is still in the dictionary (bind-and-ignore).
        Assert.True(options.Profiles.ContainsKey("DenseDesktop16gb"));
        // Moe is also still there.
        Assert.True(options.Profiles.ContainsKey("Moe"));
    }

    [Fact]
    public void Options_Normalize_NoLegacyProfiles_NoWarning()
    {
        var options = new DynamicContextOptions();
        var warnings = new List<string>();
        var logger = new TestLogger(warnings);
        options.Normalize(logger);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Options_Normalize_MultipleLegacyProfiles_SingleWarning()
    {
        var options = new DynamicContextOptions
        {
            Profiles = new Dictionary<string, DynamicContextProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Moe"] = new(184_320, null, 262_144),
#pragma warning disable CS0618
                ["DenseDesktop16gb"] = new(32_768, null, 49_152),
                ["SmallDense"] = new(32_768, null, 131_072)
#pragma warning restore CS0618
            }
        };

        var warnings = new List<string>();
        var logger = new TestLogger(warnings);
        options.Normalize(logger);

        // ONE warning naming both profiles.
        Assert.Single(warnings);
        Assert.Contains("DenseDesktop16gb", warnings[0]);
        Assert.Contains("SmallDense", warnings[0]);
    }

    [Fact]
    public void Options_Normalize_ClampsNonPositiveBucketStep()
    {
        var options = new DynamicContextOptions();
#pragma warning disable CS0618
        options.BucketStepTokens = -5;
#pragma warning restore CS0618

        options.Normalize();

#pragma warning disable CS0618
        Assert.Equal(32_768, options.BucketStepTokens);
#pragma warning restore CS0618
    }

    [Fact]
    public void Options_Normalize_RepairsZeroValuedMoeProfile()
    {
        var options = new DynamicContextOptions();
        options.Profiles["Moe"] = new DynamicContextProfile(0, null, 0);

        options.Normalize();

        Assert.Equal(262_144, options.GetProfile("Moe").MaxContext);
    }

    [Fact]
    public void ModelThresholds_MaxContextTokens_DefaultsToNull()
    {
        var thresholds = new ModelThresholds();
        Assert.Null(thresholds.MaxContextTokens);
    }

    // ================================================================================
    //  ExtractClientNumCtx
    // ================================================================================

    [Theory]
    [InlineData("{ \"model\":\"x\",\"options\":{\"num_ctx\":131072}}", 131072)]
    [InlineData("{ \"model\":\"x\",\"options\":{\"num_ctx\":8192,\"temperature\":0.2}}", 8192)]
    [InlineData("{ \"model\":\"x\"}", null)]
    [InlineData("{ \"model\":\"x\",\"options\":{}}", null)]
    [InlineData("{ \"model\":\"x\",\"options\":{\"num_ctx\":\"8192\"}}", null)]   // Non-numeric.
    [InlineData("{ \"model\":\"x\",\"options\":{\"num_ctx\":0}}", null)]        // Non-positive.
    [InlineData("not-json", null)]
    [InlineData("[1,2,3]", null)]                                              // Non-object root.
    public void ExtractClientNumCtx_ReadsTolerantly(string body, int? expected)
    {
        Assert.Equal(expected, DynamicContextPayloadTransformer.ExtractClientNumCtx(body));
    }

    // ================================================================================
    //  Transformer Apply
    // ================================================================================

    [Fact]
    public void Apply_InjectsNumCtx_WhenOptionsAbsent()
    {
        const string body = """{"model":"qwen3.6-35b-a3b","messages":[{"role":"user","content":"hi"}]}""";
        var decision = new ContextSizingDecision("Moe", 262_144, 262_144, false, ContextSizingMode.Expanded, 184_320);

        var (newBody, injected) = DynamicContextPayloadTransformer.Apply(body, decision, new DynamicContextOptions { Enabled = true });

        Assert.Equal(262_144, injected);
        using var doc = JsonDocument.Parse(newBody);
        Assert.Equal("qwen3.6-35b-a3b", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal(262_144, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public void Apply_OverwritesExistingNumCtx_PreservesOtherOptionKeys()
    {
        const string body = """{"model":"m","options":{"num_ctx":131072,"temperature":0.1}}""";
        var decision = new ContextSizingDecision("Moe", 49_152, 49_152, true, ContextSizingMode.Clamped, 49_152);

        var (newBody, injected) = DynamicContextPayloadTransformer.Apply(body, decision, new DynamicContextOptions { Enabled = true });

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
        var decision = new ContextSizingDecision("Moe", 262_144, 262_144, false, ContextSizingMode.Expanded, 184_320);
        var options = new DynamicContextOptions { Enabled = false };

        var (newBody, injected) = DynamicContextPayloadTransformer.Apply(body, decision, options);

        Assert.Same(body, newBody);
        Assert.Null(injected);
    }

    [Fact]
    public void Apply_PassThroughZeroInjected_ReturnsBodyUnchanged()
    {
        // InjectedNumCtx = 0 (PassThrough sentinel): body is untouched.
        const string body = """{"model":"m","options":{"num_ctx":131072}}""";
        var decision = new ContextSizingDecision("Moe", 0, 49_152, false, ContextSizingMode.PassThrough, 49_152);

        var (newBody, injected) = DynamicContextPayloadTransformer.Apply(body, decision, new DynamicContextOptions { Enabled = true });

        Assert.Same(body, newBody);
        Assert.Null(injected);
    }

    [Fact]
    public void Apply_UnparseableBody_ReturnsBodyUnchanged()
    {
        const string body = "this is not json";
        var decision = new ContextSizingDecision("Moe", 262_144, 262_144, false, ContextSizingMode.Expanded, 184_320);

        var (newBody, injected) = DynamicContextPayloadTransformer.Apply(body, decision, new DynamicContextOptions());

        Assert.Same(body, newBody);
        Assert.Null(injected);
    }

    [Fact]
    public void Apply_NonObjectRoot_ReturnsBodyUnchanged()
    {
        const string body = "[1,2,3]";
        var decision = new ContextSizingDecision("Moe", 262_144, 262_144, false, ContextSizingMode.Expanded, 184_320);

        var (newBody, injected) = DynamicContextPayloadTransformer.Apply(body, decision, new DynamicContextOptions());

        Assert.Same(body, newBody);
        Assert.Null(injected);
    }

    // ================================================================================
    //  ApplyExplicitNumCtx
    // ================================================================================

    [Fact]
    public void ApplyExplicitNumCtx_InjectsOptionsAndNumCtx_WhenOptionsMissing()
    {
        const string body = """{"model":"qwen3.8-27b","prompt":"hello"}""";

        var (newBody, injected) = DynamicContextPayloadTransformer.ApplyExplicitNumCtx(body, 65_536);

        Assert.Equal(65_536, injected);
        using var doc = JsonDocument.Parse(newBody);
        Assert.Equal(65_536, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public void ApplyExplicitNumCtx_OverwritesNumCtx_WhenOptionsAlreadyHasNumCtx()
    {
        const string body = """{"model":"qwen3.8-27b","options":{"num_ctx":131072,"temperature":0.7}}""";

        var (newBody, injected) = DynamicContextPayloadTransformer.ApplyExplicitNumCtx(body, 49_152);

        Assert.Equal(49_152, injected);
        using var doc = JsonDocument.Parse(newBody);
        var options = doc.RootElement.GetProperty("options");
        Assert.Equal(49_152, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(0.7, options.GetProperty("temperature").GetDouble());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void ApplyExplicitNumCtx_NonPositiveValue_ReturnsBodyUnchanged(int invalidVal)
    {
        const string body = """{"model":"m"}""";
        var (newBody, injected) = DynamicContextPayloadTransformer.ApplyExplicitNumCtx(body, invalidVal);
        Assert.Same(body, newBody);
        Assert.Null(injected);
    }

    // ================================================================================
    //  GetMoeExtensibleCapacity
    // ================================================================================

    [Theory]
    [InlineData(262_144, null, 262_144)]   // native, no cap
    [InlineData(262_144, 200_000, 200_000)] // cap below native
    [InlineData(262_144, 300_000, 262_144)] // cap above native → native wins
    [InlineData(null, null, 131_072)]       // no native → 128k fallback
    [InlineData(null, 100_000, 100_000)]    // no native, cap → cap
    [InlineData(262_144, 0, 262_144)]       // cap ≤ 0 ignored
    [InlineData(262_144, -1, 262_144)]      // cap ≤ 0 ignored
    public void GetMoeExtensibleCapacity_Cases(int? native, int? cap, int expected)
    {
        var cap2 = new TargetModelCapability("t", ModelArchitectureKind.Moe,
            ArchitectureDetectionSource.MoeExpertCount, 4_096, native ?? 131_072, true, null, native);
        Assert.Equal(expected, ModelCapabilityEngine.GetMoeExtensibleCapacity(cap2, cap));
    }

    // ================================================================================
    //  Test helper logger
    // ================================================================================

    private sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly List<string> _messages;
        public TestLogger(List<string> messages) => _messages = messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
