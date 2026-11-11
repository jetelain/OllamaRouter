using System.Text.Json.Serialization;

namespace OllamaRouter.Services.Hardware;

/// <summary>
/// Detected system (non-GPU) memory technology tier. Decode throughput is strictly
/// memory-bandwidth bound, so the tier (and an estimated bandwidth) decides whether a
/// node can keep a model above the ~20 tokens/s CPU-offload floor.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SystemRamType>))]
public enum SystemRamType
{
    Unknown = 0,
    Ddr3,
    Ddr4,
    Ddr5,
    LpDdr5X,
    AppleUnified,
}

/// <summary>
/// Per-GPU hardware detail of a fleet node: vendor, model name, total VRAM, the VRAM
/// technology tier (e.g. "GDDR6", "HBM3"), the memory bus width in bits (0 = unknown)
/// and the estimated effective bandwidth in GB/s (0 = unknown — never fabricated).
/// </summary>
public sealed record GpuDeviceProfile(
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("modelName")] string ModelName,
    [property: JsonPropertyName("vramMB")] int VramMB,
    [property: JsonPropertyName("vramType")] string VramType,
    [property: JsonPropertyName("busWidthBits")] int BusWidthBits,
    [property: JsonPropertyName("bandwidthGBs")] double BandwidthGBs);

/// <summary>
/// Full hardware profile of one fleet node: the OS, the CPU architecture, the system
/// memory tier and capacity, the GPU list and the derived fast-memory / bandwidth
/// metrics (computed once at profile build time by the server's <c>HardwareMetrics</c>)..
/// </summary>
public sealed record NodeHardwareProfile(
    [property: JsonPropertyName("nodeName")] string NodeName,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("isUnifiedMemory")] bool IsUnifiedMemory,
    [property: JsonPropertyName("systemRamMB")] long SystemRamMB,
    [property: JsonPropertyName("systemRamType")] SystemRamType SystemRamType,
    [property: JsonPropertyName("gpus")] IReadOnlyList<GpuDeviceProfile> Gpus,
    [property: JsonPropertyName("totalFastMemoryMB")] long TotalFastMemoryMB,
    [property: JsonPropertyName("effectiveBandwidthGBs")] double EffectiveBandwidthGBs);

/// <summary>
/// One node row of <c>GET /api/router/fleet/hardware</c>: the target's canonical name,
/// the profile provenance ("local"/"peer"/"persisted"/"manual"/"none"), the profile
/// (null when nothing is known and no probe succeeded) and the reachability flag.
/// </summary>
public sealed record FleetHardwareNode(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("profile")] NodeHardwareProfile? Profile,
    [property: JsonPropertyName("reachable")] bool Reachable);

/// <summary>
/// Response payload of <c>GET /api/router/fleet/hardware</c>: one row per effective
/// configured target (Cloud excluded) in configuration order.
/// </summary>
public sealed record RouterFleetHardwareResponse(
    [property: JsonPropertyName("nodes")] IReadOnlyList<FleetHardwareNode> Nodes);

/// <summary>
/// Persisted wrapper of one stored fleet hardware profile in <c>targets-hardware.json</c>:
/// the profile itself, the last update timestamp (ISO-8601) and the provenance
/// ("manual" for externally pushed profiles, "peer" for peer-daemon refreshes).
/// </summary>
public sealed record HardwareProfileState(
    [property: JsonPropertyName("profile")] NodeHardwareProfile Profile,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("source")] string Source);
