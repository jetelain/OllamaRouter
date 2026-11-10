using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace OllamaRouter.Options;

/// <summary>
/// Primary usage role of a candidate model in the fleet benchmark matrix.
/// A single model record may list multiple roles (data, not exclusive).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UsageRole>))]
public enum UsageRole
{
    BasicModel = 0,
    AgenticChat,
    SoftwareArchitect,
    SeniorDeveloper,
    JuniorDeveloper
}

/// <summary>
/// A single candidate model entry in the curated benchmark catalog.
/// One record per (name, quantization) pair; roles are data, not exclusive.
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
    [property: JsonPropertyName("defaultContext")] int DefaultContext);

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
    /// The curated default catalog: gemma4:e4b (basic), qwen3.8:27b (agentic/architect/senior),
    /// qwen3.6:35b-a3b (junior MoE). All entries are approximate but internally consistent.
    /// </summary>
    public static readonly IReadOnlyList<CandidateModel> DefaultModels = new CandidateModel[]
    {
        new CandidateModel(
            Name: "gemma4:e4b",
            Roles: [UsageRole.BasicModel],
            Architecture: "Dense",
            TotalParams: 4_500_000_000,
            ActiveParams: null,
            Quantization: "q4",
            BitsPerWeight: 4,
            Layers: 36,
            KvHeads: 8,
            HeadDim: 256,
            NativeContext: 131_072,
            DefaultContext: 32_768),
        new CandidateModel(
            Name: "qwen3.8:27b",
            Roles: [UsageRole.AgenticChat, UsageRole.SoftwareArchitect, UsageRole.SeniorDeveloper],
            Architecture: "Dense",
            TotalParams: 27_800_000_000,
            ActiveParams: null,
            Quantization: "q4",
            BitsPerWeight: 4,
            Layers: 64,
            KvHeads: 8,
            HeadDim: 128,
            NativeContext: 131_072,
            DefaultContext: 65_536),
        new CandidateModel(
            Name: "qwen3.6:35b-a3b",
            Roles: [UsageRole.JuniorDeveloper],
            Architecture: "MoE",
            TotalParams: 35_000_000_000,
            ActiveParams: 3_300_000_000,
            Quantization: "q4",
            BitsPerWeight: 4,
            Layers: 64,
            KvHeads: 8,
            HeadDim: 128,
            NativeContext: 131_072,
            DefaultContext: 131_072)
    };
}
