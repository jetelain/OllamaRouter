using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json.Nodes;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class ModelCapabilityEngineTests
{
    private const string TargetUrl = "http://127.0.0.1:11435";
    private const string Digest = "digest-abc123";

    private static ModelMetadata Metadata(
        long? parameterCount = null,
        long expertCount = 0,
        string? family = null,
        bool hasModelInfo = true,
        int? numCtx = null,
        long weightBytes = 0,
        long? nativeContextLength = null)
    {
        return new ModelMetadata("test-model:latest", weightBytes, parameterCount, expertCount, family, numCtx, hasModelInfo, numCtx is null ? null : $"num_ctx {numCtx}\n", nativeContextLength);
    }

    private static OllamaTargetOptions Target(string? hint, int? maxVram = null)
    {
        return new OllamaTargetOptions { Name = "t", Url = TargetUrl, HardwareHint = hint, MaxVramMB = maxVram };
    }

    // --- Architecture classification matrix -------------------------------------------

    [Theory]
    // Dense below the 14B threshold → SmallDense.
    [InlineData(4_002_082_688L, null, 0, ModelArchitectureKind.SmallDense, ArchitectureDetectionSource.DenseParameterCount)]
    [InlineData(9_000_000_000L, null, 0, ModelArchitectureKind.SmallDense, ArchitectureDetectionSource.DenseParameterCount)]
    // Dense at/above the threshold → LargeDense.
    [InlineData(27_800_000_000L, "qwen3", 0, ModelArchitectureKind.LargeDense, ArchitectureDetectionSource.DenseParameterCount)]
    // DBRX: dense ≥14B, family has no "moe", no expert keys → LargeDense.
    [InlineData(132_000_000_000L, "dbrx", 0, ModelArchitectureKind.LargeDense, ArchitectureDetectionSource.DenseParameterCount)]
    public void Classify_DenseByParameterCount_ReturnsExpectedKindAndSource(
        long parameterCount, string? family, long expertCount, ModelArchitectureKind expected, ArchitectureDetectionSource expectedSource)
    {
        var engine = CreateEngine();
        var metadata = Metadata(parameterCount, expertCount, family, hasModelInfo: true);

        var (architecture, source) = engine.ClassifyArchitecture(metadata, null);

        Assert.Equal(expected, architecture);
        Assert.Equal(expectedSource, source);
    }

    [Fact]
    public void Classify_MoeViaExpertCount_ReturnsMoe()
    {
        var engine = CreateEngine();
        // 35B-A3B MoE: expert_count present.
        var metadata = Metadata(35_000_000_000L, expertCount: 128, family: "qwen3", hasModelInfo: true);

        var (architecture, source) = engine.ClassifyArchitecture(metadata, null);

        Assert.Equal(ModelArchitectureKind.Moe, architecture);
        Assert.Equal(ArchitectureDetectionSource.MoeExpertCount, source);
    }

    [Fact]
    public void Classify_MoeViaFamily_ReturnsMoe()
    {
        var engine = CreateEngine();
        var metadata = Metadata(parameterCount: null, expertCount: 0, family: "qwen3moe", hasModelInfo: true);

        var (architecture, source) = engine.ClassifyArchitecture(metadata, null);

        Assert.Equal(ModelArchitectureKind.Moe, architecture);
        Assert.Equal(ArchitectureDetectionSource.MoeFamily, source);
    }

    [Fact]
    public void Classify_OldOllamaNoModelInfo_PermissiveFallback_ReturnsMoe()
    {
        var engine = CreateEngine();
        // No model_info, family has no "moe" → permissive MoE, never a false LargeDense veto.
        var metadata = Metadata(parameterCount: null, expertCount: 0, family: "qwen2", hasModelInfo: false);

        var (architecture, source) = engine.ClassifyArchitecture(metadata, null);

        Assert.Equal(ModelArchitectureKind.Moe, architecture);
        Assert.Equal(ArchitectureDetectionSource.PermissiveFallback, source);
        Assert.NotEqual(ModelArchitectureKind.LargeDense, architecture);
    }

    [Fact]
    public void Classify_DensePin_OverridesPermissiveFallback_ReturnsLargeDense()
    {
        var engine = CreateEngine();
        var metadata = Metadata(parameterCount: null, expertCount: 0, family: "qwen2", hasModelInfo: false);

        var (architecture, source) = engine.ClassifyArchitecture(metadata, "Dense");

        Assert.Equal(ModelArchitectureKind.LargeDense, architecture);
        Assert.Equal(ArchitectureDetectionSource.Pinned, source);
    }

    [Fact]
    public void Classify_MoePin_OverridesDenseDetection_ReturnsMoe()
    {
        var engine = CreateEngine();
        var metadata = Metadata(27_800_000_000L, expertCount: 0, family: "qwen3", hasModelInfo: true);

        var (architecture, source) = engine.ClassifyArchitecture(metadata, "Moe");

        Assert.Equal(ModelArchitectureKind.Moe, architecture);
        Assert.Equal(ArchitectureDetectionSource.Pinned, source);
    }

    [Fact]
    public void Classify_DensePin_UnknownParamCount_ReturnsLargeDense()
    {
        var engine = CreateEngine();
        var metadata = Metadata(parameterCount: null, hasModelInfo: true);

        var (architecture, _) = engine.ClassifyArchitecture(metadata, "Dense");

        Assert.Equal(ModelArchitectureKind.LargeDense, architecture);
    }

    [Fact]
    public void Classify_GarbagePin_IgnoredProceedsToDetection()
    {
        var engine = CreateEngine();
        var metadata = Metadata(27_800_000_000L, expertCount: 0, family: "qwen3", hasModelInfo: true);

        var (architecture, source) = engine.ClassifyArchitecture(metadata, "Foo");

        Assert.Equal(ModelArchitectureKind.LargeDense, architecture);
        Assert.Equal(ArchitectureDetectionSource.DenseParameterCount, source);
    }

    // --- Ceiling derivation -----------------------------------------------------------

    [Fact]
    public void DeriveCapability_ExplicitNumCtx16gb_WinsOutright()
    {
        var engine = CreateEngine();
        var metadata = Metadata(27_800_000_000L, family: "qwen3", numCtx: 49152, weightBytes: 16L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target("16gb"), null);

        Assert.Equal(49152, capability.CDefault);
        Assert.Equal(49152, capability.CMax);
        Assert.True(capability.HardwareCapable);
    }

    [Fact]
    public void DeriveCapability_ExplicitNumCtx96gb_WinsOutright()
    {
        var engine = CreateEngine();
        var metadata = Metadata(27_800_000_000L, family: "qwen3", numCtx: 262144, weightBytes: 16L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target("96gb"), null);

        Assert.Equal(262144, capability.CDefault);
        Assert.Equal(262144, capability.CMax);
    }

    [Fact]
    public void DeriveCapability_NoNumCtx_NativeContextBeatsFallback()
    {
        var engine = CreateEngine();
        // No Modelfile pin, native GGUF context 65,536 → CMax is the native length (NOT the
        // 131,072 assumed fallback) on ANY target class.
        var metadata = Metadata(27_800_000_000L, family: "qwen3", nativeContextLength: 65_536, weightBytes: 16L * 1_048_576);

        var desktop = engine.DeriveCapability(metadata, "t", Target("16gb"), null);
        var server = engine.DeriveCapability(metadata, "t", Target("96gb"), null);

        Assert.Equal(65_536, desktop.CMax);
        Assert.Equal(65_536, server.CMax);
        Assert.True(desktop.HardwareCapable);
    }

    [Fact]
    public void DeriveCapability_NoNumCtx_NoNative_ReturnsAssumed128kFallbackOnAnyTargetClass()
    {
        var engine = CreateEngine();
        var metadata = Metadata(27_800_000_000L, family: "qwen3", weightBytes: 16L * 1_048_576);

        var server = engine.DeriveCapability(metadata, "t", Target("96gb"), null);
        var desktop = engine.DeriveCapability(metadata, "t", Target("16gb"), null);
        var laptop = engine.DeriveCapability(metadata, "t", Target("8gb"), null);
        var unknown = engine.DeriveCapability(metadata, "t", Target(null), null);

        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, server.CMax);
        Assert.Equal(131_072, server.CMax);
        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, desktop.CMax);
        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, laptop.CMax);
        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, unknown.CMax);
    }

    [Fact]
    public void DeriveCapability_ExplicitNumCtxBeatsNativeContext()
    {
        var engine = CreateEngine();
        // Modelfile pin 49,152 beats the model's (larger) native 131,072 context.
        var metadata = Metadata(27_800_000_000L, family: "qwen3", numCtx: 49_152, nativeContextLength: 131_072, weightBytes: 16L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target("16gb"), null);

        Assert.Equal(49_152, capability.CMax);
    }

    [Fact]
    public void DeriveCapability_NoNumCtx_27BDense_96gb_ReturnsFallbackWindow()
    {
        var engine = CreateEngine();
        var metadata = Metadata(27_800_000_000L, family: "qwen3", weightBytes: 16L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target("96gb"), null);

        Assert.Equal(ModelCapabilityEngine.OllamaDefaultContext, capability.CDefault);
        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, capability.CMax);
        Assert.True(capability.HardwareCapable);
    }

    [Fact]
    public void DeriveCapability_NoNumCtx_27BDense_16gb_ReturnsFallbackContextWindow()
    {
        var engine = CreateEngine();
        var metadata = Metadata(27_800_000_000L, family: "qwen3", weightBytes: 16L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target("16gb"), null);

        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, capability.CMax);
        Assert.True(capability.HardwareCapable);
    }

    [Fact]
    public void DeriveCapability_NoNumCtx_27BDense_8gb_IsHardVetoed()
    {
        var engine = CreateEngine();
        var metadata = Metadata(27_800_000_000L, family: "qwen3", weightBytes: 16_500L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target("8gb"), null);

        Assert.Equal(ModelArchitectureKind.LargeDense, capability.Architecture);
        Assert.False(capability.HardwareCapable);
    }

    [Fact]
    public void DeriveCapability_Moe35BA3B_WeightsExceed16gbBudget_StillCapable()
    {
        var engine = CreateEngine();
        // MoE is capable on all targets (plan §2.1): the weight exceeds the pure-VRAM budget,
        // but MoE is never VRAM-vetoed. availableMB is negative; CMax follows the 16gb class rule.
        var metadata = Metadata(35_000_000_000L, expertCount: 128, family: "qwen3", weightBytes: 21_500L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target("16gb"), null);

        Assert.Equal(ModelArchitectureKind.Moe, capability.Architecture);
        Assert.True(capability.HardwareCapable);
        Assert.True(capability.AvailableVramForKVCacheMB is < 0);
        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, capability.CMax);
    }

    [Fact]
    public void DeriveCapability_Moe35BA3B_96gb_IsCapable_WithFallbackWindow()
    {
        var engine = CreateEngine();
        var metadata = Metadata(35_000_000_000L, expertCount: 128, family: "qwen3", weightBytes: 21_500L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target("96gb"), null);

        Assert.True(capability.HardwareCapable);
        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, capability.CMax);
    }

    [Fact]
    public void DeriveCapability_SmallMoeOn8gb_ElasticFallbackWindow()
    {
        var engine = CreateEngine();
        // Small MoE whose weights fit on an 8gb target → assumed 128k fallback window.
        var metadata = Metadata(4_000_000_000L, expertCount: 64, family: "qwen3moe", weightBytes: 4L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target("8gb"), null);

        Assert.Equal(ModelArchitectureKind.Moe, capability.Architecture);
        Assert.True(capability.HardwareCapable);
        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, capability.CMax);
    }

    [Fact]
    public void DeriveCapability_NoNumCtx_CDefaultIsOllamaDefault()
    {
        var engine = CreateEngine();
        var metadata = Metadata(4_002_082_688L, family: "llama", weightBytes: 2L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target("16gb"), null);

        Assert.Equal(ModelCapabilityEngine.OllamaDefaultContext, capability.CDefault);
    }

    [Fact]
    public void DeriveCapability_UnknownVram_FallsBackToAssumed128kNoHeadroom()
    {
        var engine = CreateEngine();
        var metadata = Metadata(4_002_082_688L, family: "llama", weightBytes: 2L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target(null), null);

        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, capability.CMax);
        Assert.Null(capability.AvailableVramForKVCacheMB);
    }

    [Fact]
    public void DeriveCapability_AvailableVramForKVCacheReflected()
    {
        var engine = CreateEngine();
        // 16gb (16384 MB) budget, 6 GB weights → 16384 - 6144 = 10240 MB for KV cache.
        var metadata = Metadata(4_002_082_688L, family: "llama", weightBytes: 6144L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target("16gb"), null);

        Assert.Equal(10240, capability.AvailableVramForKVCacheMB);
    }

    // --- Client context clamping ------------------------------------------------------

    // --- Assumed-128k fallback (task #25) -----------------------------------------

    [Theory]
    // 32 GB, 64 GB and 96 GB unpinned targets with no discoverable native context all get
    // the assumed 128k fallback window (hardware-derived ceilings are no longer capacity
    // sources).
    [InlineData(32_768)]
    [InlineData(65_536)]
    [InlineData(98_304)]
    public void DeriveCapability_NoNumCtx_ServerClassBudgets_LargeDenseGets128kFallback(int maxVramMB)
    {
        var engine = CreateEngine();
        var metadata = Metadata(27_800_000_000L, family: "qwen3", weightBytes: 16L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target(null, maxVramMB), null);

        Assert.True(capability.HardwareCapable);
        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, capability.CMax);
        Assert.Equal(131_072, capability.CMax);
    }

    [Fact]
    public void DeriveCapability_NoNumCtx_16GB_LargeDense_GetsFallbackWindow()
    {
        var engine = CreateEngine();
        var metadata = Metadata(27_800_000_000L, family: "qwen3", weightBytes: 16L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target(null, 16_384), null);

        Assert.True(capability.HardwareCapable);
        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, capability.CMax);
        Assert.Equal(131_072, capability.CMax);
    }

    [Fact]
    public void DeriveCapability_NoNumCtx_8GB_LargeDense_StillVetoed()
    {
        var engine = CreateEngine();
        var metadata = Metadata(27_800_000_000L, family: "qwen3", weightBytes: 16_500L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target(null, 8_192), null);

        Assert.False(capability.HardwareCapable);
    }

    [Theory]
    // MoE unpinned on 16 GB and 8 GB → assumed 128k fallback window on BOTH (the capacity
    // no longer depends on the hardware class; the < 12 GB LargeDense veto is unchanged).
    [InlineData(16_384)]
    [InlineData(8_192)]
    public void DeriveCapability_Moe_Unpinned_FallbackWindow(int maxVramMB)
    {
        var engine = CreateEngine();
        var metadata = Metadata(4_000_000_000L, expertCount: 64, family: "qwen3moe", weightBytes: 4L * 1_048_576);

        var capability = engine.DeriveCapability(metadata, "t", Target(null, maxVramMB), null);

        Assert.Equal(ModelCapabilityEngine.FallbackContextWindow, capability.CMax);
    }

    // --- Client context clamping ------------------------------------------------------

    [Fact]
    public void EvaluateClientContext_ClientNumCtxClampedToCMax()
    {
        // 2000 tokens, client requests 131072, CMax 49152 → 2000 + 8192 = 10192 ≤ 49152
        // qualifies; the injected value clamps to min(131072, 49152) = 49152.
        var (qualified, injected) = ModelCapabilityEngine.EvaluateClientContext(2000, 131072, 49152);

        Assert.True(qualified);
        Assert.Equal(49152, injected);
    }

    [Fact]
    public void EvaluateClientContext_NoClientNumCtx_AddsMargin()
    {
        // 2000 tokens, no client num_ctx → 2000 + 8192 = 10192, well below CMax.
        var (qualified, injected) = ModelCapabilityEngine.EvaluateClientContext(2000, null, 49152);

        Assert.True(qualified);
        Assert.Equal(10_192, injected);
    }

    [Fact]
    public void EvaluateClientContext_BoundaryFit_Qualifies()
    {
        // 40,960 + 8,192 = 49,152 ≤ 49,152 → fits exactly at the boundary.
        var (qualified, _) = ModelCapabilityEngine.EvaluateClientContext(40_960, null, 49_152);

        Assert.True(qualified);
    }

    [Fact]
    public void EvaluateClientContext_BoundaryPlusOneToken_NotQualified()
    {
        // 40,961 + 8,192 = 49,153 > 49,152 → disqualified by a single token.
        var (qualified, _) = ModelCapabilityEngine.EvaluateClientContext(40_961, null, 49_152);

        Assert.False(qualified);
    }

    [Fact]
    public void EvaluateClientContext_OversizedRequest_NotQualified()
    {
        // 150,000 + 8,192 = 158,192 required, exceeds CMax 49,152.
        var (qualified, injected) = ModelCapabilityEngine.EvaluateClientContext(150_000, null, 49152);

        Assert.False(qualified);
        Assert.Equal(49152, injected); // Injected value still clamped to CMax.
    }

    [Fact]
    public void EvaluateClientContext_SmallClientNumCtx_LowersRequirement()
    {
        // 150,000 tokens would overflow CMax 49,152, but the client's static num_ctx 8,192 is
        // an upper-bound preference: min(158,192, 8,192) = 8,192 ≤ 49,152 → qualified.
        var (qualified, injected) = ModelCapabilityEngine.EvaluateClientContext(150_000, 8_192, 49_152);

        Assert.True(qualified);
        Assert.Equal(8_192, injected);
    }

    [Fact]
    public void EvaluateClientContext_CustomMargin_Honored()
    {
        // Margin 32,000: 17,000 + 32,000 = 49,000 ≤ 49,152 qualifies…
        var (qualifiedLarge, _) = ModelCapabilityEngine.EvaluateClientContext(17_000, null, 49_152, contextFitMarginTokens: 32_000);
        Assert.True(qualifiedLarge);

        // …and at the boundary: 17,152 + 32,000 = 49,152 qualifies, 17,153 + 32,000 = 49,153
        // > 49,152 → disqualified, proving the margin is the configured value (not 1.25).

        var (qualified, _) = ModelCapabilityEngine.EvaluateClientContext(17_153, null, 49_152, contextFitMarginTokens: 32_000);
        Assert.False(qualified);
    }

    [Fact]
    public void EvaluateClientContext_ZeroMargin_TokensAlone()
    {
        // Margin 0: the raw estimate is the requirement (40,960 ≤ 49,152 qualifies; the
        // default 8,192 margin would have disqualified 40,961+).
        var (qualified, injected) = ModelCapabilityEngine.EvaluateClientContext(40_960, null, 49_152, contextFitMarginTokens: 0);

        Assert.True(qualified);
        Assert.Equal(40_960, injected);
    }

    [Fact]
    public void EvaluateClientContext_NegativeMargin_ClampsToZero()
    {
        // A negative margin is treated as 0 (defensive clamp inside the engine).
        var (qualified, injected) = ModelCapabilityEngine.EvaluateClientContext(2_000, null, 49_152, contextFitMarginTokens: -100);

        Assert.True(qualified);
        Assert.Equal(2_000, injected);
    }

    // --- Caching / single-flight ------------------------------------------------------

    [Fact]
    public async Task GetMetadataAsync_SameDigest_ReturnsCachedMetadataWithoutSecondShow()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient
            .Setup(c => c.GetTagsWithDigestAsync(TargetUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new System.Text.Json.Nodes.JsonArray(), "digest-a"));
        catalogClient
            .Setup(c => c.GetShowAsync(TargetUrl, "m:latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Text.Json.Nodes.JsonObject { ["model_info"] = new System.Text.Json.Nodes.JsonObject { ["general.parameter_count"] = (System.Text.Json.Nodes.JsonNode)4L } });

        var engine = CreateEngine(catalogClient: catalogClient);

        var first = await engine.GetMetadataAsync(TargetUrl, "m:latest", "digest-a");
        var second = await engine.GetMetadataAsync(TargetUrl, "m:latest", "digest-a");

        Assert.Same(first, second);
        catalogClient.Verify(c => c.GetShowAsync(TargetUrl, "m:latest", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetMetadataAsync_EmptyDigest_ReturnsDegradedMetadataWithoutNetwork()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        var engine = CreateEngine(catalogClient: catalogClient);

        var metadata = await engine.GetMetadataAsync(TargetUrl, "m:latest", "");

        Assert.Null(metadata.ParameterCount);
        Assert.False(metadata.HasModelInfo);
        Assert.Null(metadata.ModelfileNumCtx);
        catalogClient.Verify(c => c.GetShowAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetMetadataAsync_NetworkFailure_ReturnsDegradedMetadata()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient
            .Setup(c => c.GetTagsWithDigestAsync(TargetUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new System.Text.Json.Nodes.JsonArray(), "digest-a"));
        catalogClient
            .Setup(c => c.GetShowAsync(TargetUrl, "m:latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync((System.Text.Json.Nodes.JsonObject?)null);

        var engine = CreateEngine(catalogClient: catalogClient);

        var metadata = await engine.GetMetadataAsync(TargetUrl, "m:latest", "digest-a");

        Assert.Null(metadata.ParameterCount);
        Assert.False(metadata.HasModelInfo);
        // Degrades to permissive fallback classification rather than blocking routing.
        var (architecture, source) = engine.ClassifyArchitecture(metadata, null);
        Assert.Equal(ModelArchitectureKind.Moe, architecture);
        Assert.Equal(ArchitectureDetectionSource.PermissiveFallback, source);
    }

    [Fact]
    public async Task GetMetadataAsync_RecordsCacheKeyForDigestInvalidation()
    {
        var catalogClient = new Mock<IOllamaModelCatalogClient>();
        catalogClient
            .Setup(c => c.GetTagsWithDigestAsync(TargetUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new System.Text.Json.Nodes.JsonArray(), Digest));
        catalogClient
            .Setup(c => c.GetShowAsync(TargetUrl, "m:latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Text.Json.Nodes.JsonObject());

        var cache = new MemoryCache(new MemoryCacheOptions());
        var registry = new RecordingRegistry();
        var engine = new ModelCapabilityEngine(catalogClient.Object, cache, NullLogger<ModelCapabilityEngine>.Instance, registry);

        await engine.GetMetadataAsync(TargetUrl, "m:latest", Digest);

        Assert.Contains($"{ModelCatalogCacheService.CapabilityKeyPrefix}{TargetUrl}:{Digest}:m:latest", registry.RegisteredKeys);
    }

    private sealed class RecordingRegistry : IModelCatalogCacheService
    {
        public List<string> RegisteredKeys { get; } = [];

        public Task<bool> ModelExistsAsync(string? baseUrl, string modelName, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> HasAnyModelsAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<IReadOnlyList<string>> RefreshAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<(IReadOnlyList<string> Names, string Digest)> RefreshWithDigestAsync(string? baseUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult((new List<string>() as IReadOnlyList<string>, string.Empty));

        public Task<string> GetCurrentDigestAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult("");

        public string CurrentDigest(string? baseUrl) => "";

        public void RegisterCapabilityCacheKeys(string baseUrl, IEnumerable<string> keys, string digest)
        {
            RegisteredKeys.AddRange(keys);
        }

        public Task<JsonArray?> GetStaleTagsAsync(string url) => Task.FromResult<JsonArray?>(null);
        public void SetStaleTags(string url, JsonArray tags) { }
        public Task<JsonArray?> GetStaleV1ModelsAsync(string url) => Task.FromResult<JsonArray?>(null);
        public void SetStaleV1Models(string url, JsonArray data) { }
        public Task<JsonArray?> GetStalePsAsync(string url) => Task.FromResult<JsonArray?>(null);
        public void SetStalePs(string url, JsonArray models) { }
    }

    private static ModelCapabilityEngine CreateEngine(Mock<IOllamaModelCatalogClient>? catalogClient = null, IMemoryCache? memoryCache = null)
    {
        return new ModelCapabilityEngine(
            catalogClient?.Object ?? new Mock<IOllamaModelCatalogClient>().Object,
            memoryCache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ModelCapabilityEngine>.Instance,
            null);
    }
}
