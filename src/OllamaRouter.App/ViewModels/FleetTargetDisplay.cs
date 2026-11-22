using System.Globalization;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// Pure display-formatting helpers for the Fleet &amp; Cloud Targets tab card (task #153,
/// maintainer-confirmed decisions 2026-11-21): human-readable memory units, the keep-alive
/// label, the one-line-per-topic hardware summary (OS / RAM+VRAM+bandwidth / GPU) and the
/// VRAM-override annotation. Pure and static — unit-testable without Avalonia. The row
/// (<see cref="FleetTargetRow"/>) calls these into read-only display projections.
/// </summary>
public static class FleetTargetDisplay
{
    /// <summary>
    /// Formats a memory size in MB for the card (task #153 decision 7, CONFIRMED variant A):
    /// null or ≤ 0 → <c>null</c> (hidden); &lt; 1024 → <c>"{mb} MB"</c>; otherwise GB =
    /// mb/1024 with at most 1 decimal when needed (e.g. 512 → <c>512 MB</c>, 1024 →
    /// <c>1 GB</c>, 1536 → <c>1.5 GB</c>, 83968 → <c>82 GB</c>, 93379 → <c>91.2 GB</c>).
    /// </summary>
    public static string? FormatMemoryMB(long? mb)
    {
        if (mb is null or <= 0)
        {
            return null;
        }

        var value = mb.Value;
        if (value < 1024)
        {
            return $"{value} MB";
        }

        return (value / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
    }

    /// <summary>
    /// The card's keep-alive chip text (task #153 decision 5): seconds null → <c>null</c>
    /// (hidden); -1 → <c>"indefinite"</c>; otherwise <c>"{n}s"</c>; the provenance suffix
    /// <c>" ({source})"</c> is appended only when <paramref name="source"/> is non-blank.
    /// Examples: (-1, "default") → <c>Keep alive: indefinite (default)</c>, (900, "class")
    /// → <c>Keep alive: 900s (class)</c>, (0, "explicit") → <c>Keep alive: 0s (explicit)</c>,
    /// (300, null) → <c>Keep alive: 300s</c>.
    /// </summary>
    public static string? FormatKeepAliveLabel(int? seconds, string? source)
    {
        if (seconds is null)
        {
            return null;
        }

        var value = seconds < 0
            ? "indefinite"
            : $"{seconds.Value}s";
        var suffix = string.IsNullOrWhiteSpace(source) ? string.Empty : $" ({source})";
        return $"Keep alive: {value}{suffix}";
    }

    /// <summary>
    /// The single-line GPU summary (task #153 decision 3): <c>"GPU: " </c> + the GPU display
    /// names joined by <c>", "</c>; empty list → <c>null</c>. Per GPU the display name is the
    /// model name when it already starts with the vendor (the <c>FormatGpuDevice</c> server
    /// precedent), otherwise <c>"{vendor} {modelName}"</c>. Sub-values are rendered in
    /// parentheses ONLY when known and joined by <c>", "</c>: VRAM =
    /// <c>FormatMemoryMB(vramMB) + " {vramType}"</c> (the type part only when the type is
    /// non-blank and not "Unknown", case-insensitive), bandwidth =
    /// <c>{bandwidthGBs:0.##} GB/s</c> when &gt; 0. All-unknown GPU → the name only, e.g.
    /// <c>GPU: AMD Strix Halo [Radeon Graphics / Radeon 8050S Graphics / Radeon 8060S Graphics]</c>;
    /// known sub-values → e.g. <c>GPU: AMD GPU (512 MB GDDR6, 341.2 GB/s)</c>.
    /// </summary>
    public static string? BuildGpuLine(IReadOnlyList<GpuDeviceProfile> gpus)
    {
        if (gpus is null || gpus.Count == 0)
        {
            return null;
        }

        return $"GPU: {string.Join(", ", gpus.Select(FormatGpu))}";
    }

    /// <summary>
    /// The VRAM-override predicate (task #153 decision 2, CONFIRMED Q2 A): true iff the
    /// PERSISTED config <c>MaxVramMB</c> is set, a hardware profile exists, and the value
    /// DIFFERS from the profile's <c>TotalFastMemoryMB</c> (the fleet endpoint's effective
    /// value falls back to the profile, so an override equal to it — or with no profile — is
    /// invisible and gets no annotation).
    /// </summary>
    public static bool HasVramOverride(int? configMaxVramMB, NodeHardwareProfile? profile) =>
        configMaxVramMB is not null
        && profile is not null
        && configMaxVramMB.Value != profile.TotalFastMemoryMB;

    /// <summary>
    /// The card's <c>VRAM override: {value}</c> annotation (task #153 decision 2): present
    /// only when <see cref="HasVramOverride"/> holds; otherwise <c>null</c> (hidden).
    /// </summary>
    public static string? BuildVramOverrideLabel(int? configMaxVramMB, NodeHardwareProfile? profile)
    {
        if (configMaxVramMB is not { } value || profile is null || value == profile.TotalFastMemoryMB)
        {
            return null;
        }

        return $"VRAM override: {FormatMemoryMB((long)value)}";
    }

    /// <summary>
    /// The merged OS line (task #153 decision 3): <c>"OS: " </c> + the non-blank parts of
    /// <paramref name="os"/> and <paramref name="arch"/> joined by a single space
    /// (e.g. <c>OS: linux x64</c>, <c>OS: linux</c> when the architecture is blank);
    /// both blank → <c>null</c> (hidden).
    /// </summary>
    public static string? BuildOsLine(string? os, string? arch)
    {
        var parts = new[] { os, arch }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .ToList();
        return parts.Count == 0 ? null : $"OS: {string.Join(' ', parts)}";
    }

    /// <summary>
    /// The merged memory line (task #153 decision 3; unified-memory correction, task #164
    /// 2026-11-22): the segments <c>RAM: {…}</c>, <c>VRAM: {…}</c> and
    /// <c>VRAM Speed: {bw:0.##} GB/s</c>, each present ONLY when its value is &gt; 0 (memory
    /// via <see cref="FormatMemoryMB"/>), joined by <c>" · "</c>; none present → <c>null</c>
    /// (the whole line hidden). For UNIFIED-memory profiles (<paramref name="isUnifiedMemory"/>
    /// true — the server's <c>HardwareMetrics</c> then sets <c>TotalFastMemoryMB</c> = system
    /// RAM, so a <c>VRAM:</c> segment would duplicate the RAM value), the <c>VRAM:</c> segment
    /// is NEVER emitted and the RAM segment carries the literal <c> (unified)</c> marker,
    /// using <paramref name="systemRamMB"/> when &gt; 0 with a defensive fallback to
    /// <paramref name="totalFastMemoryMB"/>. Example: non-unified →
    /// <c>RAM: 64 GB · VRAM: 16 GB · VRAM Speed: 468 GB/s</c>, unified (Strix Halo) →
    /// <c>RAM: 91.2 GB (unified) · VRAM Speed: 341.2 GB/s</c>.
    /// </summary>
    public static string? BuildMemoryLine(
        long? systemRamMB,
        long? totalFastMemoryMB,
        double? bandwidthGBs,
        bool isUnifiedMemory = false)
    {
        var segments = new List<string>();
        if (isUnifiedMemory)
        {
            // Unified: RAM IS the fast memory — mark it and never emit a VRAM segment
            // (the iGPU's dedicated VRAM stays visible on the GPU line only).
            var unifiedRam = systemRamMB is > 0
                ? systemRamMB
                : totalFastMemoryMB; // defensive fallback (identical for server-generated profiles)
            var ram = FormatMemoryMB(unifiedRam);
            if (ram is not null)
            {
                segments.Add($"RAM: {ram} (unified)");
            }
        }
        else
        {
            var ram = FormatMemoryMB(systemRamMB);
            if (ram is not null)
            {
                segments.Add($"RAM: {ram}");
            }

            var vram = FormatMemoryMB(totalFastMemoryMB);
            if (vram is not null)
            {
                segments.Add($"VRAM: {vram}");
            }
        }

        if (bandwidthGBs is > 0)
        {
            segments.Add(
                "VRAM Speed: " +
                bandwidthGBs.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                " GB/s");
        }

        return segments.Count == 0 ? null : string.Join(" · ", segments);
    }

    private static string FormatGpu(GpuDeviceProfile gpu)
    {
        var name = gpu.ModelName.StartsWith(gpu.Vendor, StringComparison.OrdinalIgnoreCase)
            ? gpu.ModelName
            : $"{gpu.Vendor} {gpu.ModelName}";

        var subValues = new List<string>();
        var vram = FormatMemoryMB(gpu.VramMB);
        if (vram is not null)
        {
            // The type part renders only when known (non-blank and not "Unknown").
            var typeKnown = !string.IsNullOrWhiteSpace(gpu.VramType)
                && !string.Equals(gpu.VramType, "Unknown", StringComparison.OrdinalIgnoreCase);
            subValues.Add(typeKnown ? $"{vram} {gpu.VramType}" : vram);
        }

        if (gpu.BandwidthGBs > 0)
        {
            subValues.Add(
                gpu.BandwidthGBs.ToString("0.##", CultureInfo.InvariantCulture) + " GB/s");
        }

        return subValues.Count == 0 ? name : $"{name} ({string.Join(", ", subValues)})";
    }
}
