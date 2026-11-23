using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace OllamaRouter.Options;

/// <summary>
/// Model class tier for downstream hardware-aware role selection (task #168).
/// Higher numeric value = higher class tier (<see cref="Expert"/> &gt; <see cref="MoE"/>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CandidateModelClass>))]
public enum CandidateModelClass
{
    /// <summary>Lightweight-active MoE (CPU-offload-eligible tier).</summary>
    MoE = 0,

    /// <summary>Flagship/expert tier (dense + hybrid frontiers; higher tier value).</summary>
    Expert = 1
}

/// <summary>
/// A single candidate model entry in the curated benchmark catalog.
/// One record per (name, quantization) pair; roles are data, not exclusive.
/// The trailing optional parameters (task #166) default to conservative values so every
/// pre-existing positional/named caller keeps compiling: unknown/synthetic models stay
/// dense-strict (<see cref="CandidateModelClass.Expert"/>, no CPU offload) and fall back
/// to the parametric sizing formulas (<see cref="EstimatedWeightsMB"/> /
/// <see cref="KvBytesPerToken"/> null).
/// </summary>
public sealed record CandidateModel(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("roles")] IReadOnlyList<UsageRole> Roles,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("totalParams")] long TotalParams,
    [property: JsonPropertyName("activeParams")] long? ActiveParams,
    [property: JsonPropertyName("quantization")] string Quantization,
    [property: JsonPropertyName("bitsPerWeight")] int BitsPerWeight,
    [property: JsonPropertyName("layers")] int Layers,
    [property: JsonPropertyName("kvHeads")] int KvHeads,
    [property: JsonPropertyName("headDim")] int HeadDim,
    [property: JsonPropertyName("nativeContext")] int NativeContext,
    [property: JsonPropertyName("defaultContext")] int DefaultContext,
    [property: JsonPropertyName("modelClass")] CandidateModelClass ModelClass = CandidateModelClass.Expert,
    [property: JsonPropertyName("allowCpuOffload")] bool AllowCpuOffload = false,
    [property: JsonPropertyName("estimatedWeightsMB")] double? EstimatedWeightsMB = null,
    [property: JsonPropertyName("kvBytesPerToken")] double? KvBytesPerToken = null,
    [property: JsonPropertyName("family")] string Family = "",
    [property: JsonPropertyName("qualityRank")] int QualityRank = 0);

/// <summary>
/// Options for the candidate model catalog used by the micro-benchmark engine (task #28).
/// Code-defined in v1 (not config-bound); the <see cref="Models"/> property carries the
/// curated seed catalog and <see cref="BenchmarkContextLadder"/> the default context sizes.
/// </summary>
public sealed class CandidateModelCatalogOptions
{
    /// <summary>Viability floor for decode throughput. Frozen constant, not user-configurable.</summary>
    public const double MinViableTokensPerSecond = 20.0;

    /// <summary>Fast-memory occupancy ceiling: weights + KV must stay ≤ 92% of TotalFastMemoryMB.</summary>
    public const double FastMemoryOccupancyLimit = 0.92;

    /// <summary>Default KV-cache dtype assumption (Ollama default); bytes per KV element.</summary>
    public const int KvCacheBytesPerElement = 2; // f16

    /// <summary>Default benchmark context ladder (tokens), ascending.</summary>
    public IReadOnlyList<int> BenchmarkContextLadder { get; set; } =
        new[] { 16384, 32768, 49152, 65536, 131072, 184320, 262144 };

    /// <summary>
    /// The curated seed catalog. Code-defined in v1; a config.json binding hook is out of scope.
    /// </summary>
    public IReadOnlyList<CandidateModel> Models { get; set; } = DefaultModels;

    /// <summary>
    /// The curated default catalog (task #166): the 7 <c>jetelain/*</c> models, verified
    /// against real Modelfiles and hardware benchmarks (8 GB + 64 GB RAM, 16 GB + 64 GB RAM,
    /// 64 GB unified, 96 GB unified / 88 GB TTM). Two MoE families (Flint, Qwen3.6-35B-A3B)
    /// stay CPU-offload-eligible and carry ALL five roles (junior/basic first per family);
    /// the Qwen3.8-27B dense flagships and the Ornith-1.5-122b hybrid frontier are strict
    /// (no CPU offload) and target the expert roles. Declaration order is the final
    /// catalog-order tie-break (MoE first: junior/basic-primary, then the dense flagships,
    /// then the frontier).
    /// </summary>
    public static readonly IReadOnlyList<CandidateModel> DefaultModels = new CandidateModel[]
    {
        new CandidateModel(
            Name: "jetelain/Flint:q4",
            Roles: [UsageRole.JuniorDeveloper, UsageRole.BasicModel, UsageRole.AgenticChat,
                UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper],
            Architecture: "MoE",
            TotalParams: 39_800_000_000,
            ActiveParams: 2_500_000_000,
            Quantization: "UD-Q4_K_XL",
            BitsPerWeight: 4,
            Layers: 49,
            KvHeads: 2,
            HeadDim: 128,
            NativeContext: 262_144,
            DefaultContext: 131_072,
            ModelClass: CandidateModelClass.MoE,
            AllowCpuOffload: true,
            // Resident footprint of the UD-Q4_K_XL mixed-precision GGUF (23.3 GB ≈ 23,859 MiB;
            // the parametric 39.8B × 4 bpw formula would understate the UD layout).
            EstimatedWeightsMB: 23_859,
            // Calibrated 2 KB/tok: 14 full-attention + 35 sliding-window (513 tok) layers;
            // 2048 × 262 144 / 2^20 = 512 MiB @ 256k (the measured "0.5 GiB KV" anchor).
            KvBytesPerToken: 2_048,
            Family: "Flint",
            QualityRank: 5),
        new CandidateModel(
            Name: "jetelain/Flint:q8",
            Roles: [UsageRole.JuniorDeveloper, UsageRole.BasicModel, UsageRole.AgenticChat,
                UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper],
            Architecture: "MoE",
            TotalParams: 39_800_000_000,
            ActiveParams: 2_500_000_000,
            Quantization: "Q8_0",
            BitsPerWeight: 8,
            Layers: 49,
            KvHeads: 2,
            HeadDim: 128,
            NativeContext: 262_144,
            DefaultContext: 131_072,
            ModelClass: CandidateModelClass.MoE,
            AllowCpuOffload: true,
            // Resident footprint of the Q8_0 GGUF (39.4 GB ≈ 40,346 MiB).
            EstimatedWeightsMB: 40_346,
            KvBytesPerToken: 2_048,
            Family: "Flint",
            QualityRank: 4),
        new CandidateModel(
            Name: "jetelain/Qwen3.6-35B-A3B:q4",
            Roles: [UsageRole.BasicModel, UsageRole.JuniorDeveloper, UsageRole.AgenticChat,
                UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper],
            Architecture: "MoE",
            TotalParams: 35_000_000_000,
            ActiveParams: 3_200_000_000,
            Quantization: "UD-Q4_K_XL",
            BitsPerWeight: 4,
            Layers: 41,
            KvHeads: 2,
            HeadDim: 128,
            NativeContext: 262_144,
            DefaultContext: 65_536,
            ModelClass: CandidateModelClass.MoE,
            AllowCpuOffload: true,
            // Resident footprint: 21.3 GB UD-Q4_K_XL model + bundled mmproj-BF16 (≈ 22,528 MiB).
            EstimatedWeightsMB: 22_528,
            // Calibrated 22 KB/tok (measured q8_0 KV, all 41 layers): 22 528 × 262 144 / 2^20 =
            // 5 632 MiB @ 256k.
            KvBytesPerToken: 22_528,
            Family: "Qwen3.6-35B-A3B",
            QualityRank: 5),
        new CandidateModel(
            Name: "jetelain/Qwen3.6-35B-A3B:code",
            Roles: [UsageRole.BasicModel, UsageRole.JuniorDeveloper, UsageRole.AgenticChat,
                UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper],
            Architecture: "MoE",
            TotalParams: 35_000_000_000,
            ActiveParams: 3_200_000_000,
            Quantization: "UD-Q8_K_XL",
            BitsPerWeight: 8,
            Layers: 41,
            KvHeads: 2,
            HeadDim: 128,
            NativeContext: 262_144,
            DefaultContext: 131_072,
            ModelClass: CandidateModelClass.MoE,
            AllowCpuOffload: true,
            // Resident footprint: 36.4 GB UD-Q8_K_XL model + bundled mmproj-BF16 (≈ 40,960 MiB).
            EstimatedWeightsMB: 40_960,
            KvBytesPerToken: 22_528,
            Family: "Qwen3.6-35B-A3B",
            QualityRank: 4),
        new CandidateModel(
            Name: "jetelain/Qwen3.8-27B:iq3",
            Roles: [UsageRole.AgenticChat, UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper],
            Architecture: "Dense",
            TotalParams: 27_800_000_000,
            ActiveParams: null,
            Quantization: "UD-IQ3_S",
            BitsPerWeight: 3,
            Layers: 65,
            KvHeads: 4,
            HeadDim: 256,
            NativeContext: 262_144,
            DefaultContext: 49_152,
            ModelClass: CandidateModelClass.Expert,
            AllowCpuOffload: false,
            // Resident footprint: 11.2 GB UD-IQ3_S model + bundled mmproj-BF16 (≈ 12,288 MiB).
            EstimatedWeightsMB: 12_288,
            // Calibrated 65 KB/tok (measured q8_0 KV): 65 536 × 262 144 / 2^20 = 16 384 MiB @ 256k.
            KvBytesPerToken: 65_536,
            Family: "Qwen3.8-27B",
            QualityRank: 3),
        new CandidateModel(
            Name: "jetelain/Qwen3.8-27B:latest",
            Roles: [UsageRole.AgenticChat, UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper],
            Architecture: "Dense",
            TotalParams: 27_800_000_000,
            ActiveParams: null,
            Quantization: "UD-Q4_K_XL",
            BitsPerWeight: 4,
            Layers: 65,
            KvHeads: 4,
            HeadDim: 256,
            NativeContext: 262_144,
            DefaultContext: 131_072,
            ModelClass: CandidateModelClass.Expert,
            AllowCpuOffload: false,
            // Resident footprint: 16.4 GB UD-Q4_K_XL model + bundled mmproj-BF16 (≈ 18,944 MiB).
            EstimatedWeightsMB: 18_944,
            KvBytesPerToken: 65_536,
            Family: "Qwen3.8-27B",
            QualityRank: 2),
        new CandidateModel(
            Name: "jetelain/Ornith-1.5-122b:latest",
            Roles: [UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper],
            Architecture: "Hybrid MoE",
            TotalParams: 124_600_000_000,
            ActiveParams: 10_000_000_000,
            Quantization: "UD-Q4_K_XL",
            BitsPerWeight: 4,
            Layers: 49,
            KvHeads: 2,
            HeadDim: 128,
            NativeContext: 262_144,
            DefaultContext: 131_072,
            ModelClass: CandidateModelClass.Expert,
            // Architectural MoE, but the ~80.6 GB resident weights are NEVER CPU-offloaded
            // (it requires ≥88 GB VRAM, 100% resident): strict dense sizing path.
            AllowCpuOffload: false,
            // MEASURED resident-footprint anchor (maintainer-confirmed, task #166 Q1):
            // observed 82.04 GB total resident @ 256k (78 239 MiB) minus the 256k KV
            // (6 656 MiB at 26 KB/tok). The on-disk figure is 82 534 MiB (80.6 GiB = 79.7 GB
            // split GGUF + 0.91 GB mmproj-BF16) — kept as the file-size reference only.
            // Ornith's ~0.6 GiB fixed SSM recurrent state is folded into this anchor.
            EstimatedWeightsMB: 71_583,
            // Calibrated 26 KB/tok: 12 full-attention layers + 36 SSM/recurrent layers;
            // 26 624 × 262 144 / 2^20 = 6 656 MiB @ 256k (the measured "~6.5 GiB KV" anchor).
            KvBytesPerToken: 26_624,
            Family: "Ornith-1.5",
            QualityRank: 1)
    };
}
