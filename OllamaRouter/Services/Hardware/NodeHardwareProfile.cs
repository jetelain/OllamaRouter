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
/// metrics (computed once at profile build time by <see cref="HardwareMetrics"/>).
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

/// <summary>
/// Pure derivation helpers for the node hardware profile metrics. No I/O: every method
/// is deterministic and fail-soft (unknown inputs → <see cref="SystemRamType.Unknown"/> /
/// zero bandwidth — numbers are never fabricated).
/// </summary>
public static class HardwareMetrics
{
    /// <summary>
    /// Unified-memory detection: an Apple Silicon SoC (Apple in the SoC/CPU brand string
    /// on arm64) is always unified; an AMD APU with LPDDR5X system RAM and no discrete
    /// GPU (Strix Halo / Point class) is treated as unified. Anything else → false.
    /// </summary>
    public static bool DetectUnifiedMemory(string os, string architecture, string cpuBrand, SystemRamType ramType, IReadOnlyList<GpuDeviceProfile> gpus)
    {
        if (string.Equals(os, "macos", StringComparison.OrdinalIgnoreCase) &&
            cpuBrand.Contains("Apple", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ramType == SystemRamType.LpDdr5X &&
               (gpus is null || gpus.Count == 0) &&
               cpuBrand.Contains("AMD", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Total "fast" memory: unified → system RAM; discrete → sum of GPU VRAM; a
    /// CPU-only node (no GPU and no VRAM) still counts its system RAM as the tier.
    /// </summary>
    public static long ComputeTotalFastMemoryMB(bool isUnifiedMemory, long systemRamMB, IReadOnlyList<GpuDeviceProfile>? gpus)
    {
        if (isUnifiedMemory)
        {
            return systemRamMB;
        }

        long vram = 0;
        if (gpus is not null)
        {
            foreach (var gpu in gpus)
            {
                vram += Math.Max(0, gpu.VramMB);
            }
        }

        return vram > 0 ? vram : systemRamMB;
    }

    /// <summary>
    /// Estimated effective bandwidth in GB/s (conservative estimation tables):
    /// unified → RAM bandwidth estimate (Apple: SoC-class table, 100 GB/s fallback);
    /// discrete → max GPU bandwidth estimate; unknown hardware → 0.
    /// </summary>
    public static double ComputeEffectiveBandwidthGBs(bool isUnifiedMemory, SystemRamType ramType, string cpuBrand, int ramChannels, IReadOnlyList<GpuDeviceProfile>? gpus)
    {
        if (isUnifiedMemory)
        {
            if (ramType == SystemRamType.AppleUnified)
            {
                return EstimateAppleUnifiedBandwidthGBs(cpuBrand);
            }

            return EstimateRamBandwidthGBs(ramType, ramChannels);
        }

        if (gpus is null || gpus.Count == 0)
        {
            return EstimateRamBandwidthGBs(ramType, ramChannels);
        }

        double max = 0;
        foreach (var gpu in gpus)
        {
            var estimate = gpu.BandwidthGBs > 0
                ? gpu.BandwidthGBs
                : EstimateGpuBandwidthGBs(gpu.VramType, gpu.BusWidthBits);
            max = Math.Max(max, estimate);
        }

        return max;
    }

    /// <summary>
    /// Conservative RAM bandwidth estimates (GB/s): DDR3-8192 dual ≈ 12.8, DDR4-3200 dual ≈
    /// 25.6, DDR5-4800 dual ≈ 38.4 (2× multiplier for 4-channel servers), LPDDR5X-8533 ≈
    /// 85.3 GB/s per channel (× 4 channels default on Strix Halo ≈ 341 GB/s). Unknown → 0.
    /// </summary>
    public static double EstimateRamBandwidthGBs(SystemRamType ramType, int channels)
    {
        if (ramType == SystemRamType.LpDdr5X)
        {
            // 85.3 GB/s per channel; 4 channels by default on Strix Halo (≈ 341 GB/s).
            return 85.3 * Math.Max(1, channels <= 0 ? 4 : channels);
        }

        if (ramType == SystemRamType.AppleUnified)
        {
            return 100;
        }

        if (ramType is SystemRamType.Ddr3 or SystemRamType.Ddr4 or SystemRamType.Ddr5)
        {
            // Dual-channel baselines; 4-channel server multiplier (×2) when detectable.
            return ramType switch
            {
                SystemRamType.Ddr3 => 12.8,
                SystemRamType.Ddr4 => 25.6,
                _ => 38.4
            } * (channels >= 4 ? 2 : 1);
        }

        return 0;
    }

    /// <summary>
    /// Apple unified-memory bandwidth by SoC class (GB/s): M3 Ultra 819, M2 Ultra 400,
    /// M1 Max / M2 Pro / M2 Max 307, M3 / M4 family 546 (default when a generation is
    /// detected), M1 / M2 base 153, unknown SoC → conservative 100 GB/s fallback.
    /// </summary>
    public static double EstimateAppleUnifiedBandwidthGBs(string cpuBrand)
    {
        var brand = cpuBrand ?? string.Empty;
        if (brand.Contains("M3", StringComparison.OrdinalIgnoreCase) && brand.Contains("Ultra", StringComparison.OrdinalIgnoreCase))
        {
            return 819;
        }

        if (brand.Contains("M2", StringComparison.OrdinalIgnoreCase) && brand.Contains("Ultra", StringComparison.OrdinalIgnoreCase))
        {
            return 400;
        }

        if (brand.Contains("M1", StringComparison.OrdinalIgnoreCase) && brand.Contains("Max", StringComparison.OrdinalIgnoreCase))
        {
            return 307;
        }

        if (brand.Contains("M2", StringComparison.OrdinalIgnoreCase) &&
            (brand.Contains("Pro", StringComparison.OrdinalIgnoreCase) || brand.Contains("Max", StringComparison.OrdinalIgnoreCase)))
        {
            return 307;
        }

        if (brand.Contains("M3", StringComparison.OrdinalIgnoreCase) || brand.Contains("M4", StringComparison.OrdinalIgnoreCase))
        {
            return 546;
        }

        if (brand.Contains("M1", StringComparison.OrdinalIgnoreCase) || brand.Contains("M2", StringComparison.OrdinalIgnoreCase))
        {
            return 153;
        }

        return 100;
    }

    /// <summary>
    /// Conservative GPU bandwidth estimate from the VRAM tier + bus width (GB/s):
    /// GDDR6 512-bit ≈ 512, GDDR6X 384-bit ≈ 936, HBM2E/HBM3 512-bit ≈ 1555. Unknown
    /// tiers or missing bus width → 0 (never fabricated).
    /// </summary>
    public static double EstimateGpuBandwidthGBs(string? vramType, int busWidthBits)
    {
        if (string.IsNullOrWhiteSpace(vramType))
        {
            return 0;
        }

        var type = vramType.Trim().ToUpperInvariant();
        double bandwidth = type switch
        {
            "GDDR6" => 512,
            "GDDR6X" => 936,
            "HBM2E" or "HBM2" or "HBM3" or "HBM3E" => 1555,
            _ => 0
        };

        return busWidthBits > 0 ? bandwidth : bandwidth * 0.5;
    }
}
