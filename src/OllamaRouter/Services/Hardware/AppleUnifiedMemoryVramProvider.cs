namespace OllamaRouter.Services.Hardware;
using System.Globalization;
using System.Text.RegularExpressions;
using OllamaRouter.Services.Process;

/// <summary>
/// Estimates the free GPU VRAM on macOS (Apple Silicon) from the machine's unified memory,
/// using <c>sysctl</c> (<c>hw.memsize</c>, <c>iogpu.wired_limit_mb</c>) and <c>vm_stat</c> via
/// <see cref="IProcessRunner"/>. The reported value is the smaller of the remaining GPU wired
/// budget and the free + inactive + speculative unified-memory pages. Returns 0 on any probe or
/// parsing failure (for example on a machine that is not macOS, or when a page statistic is
/// missing). Results are intentionally NOT cached: free unified memory varies as models and other
/// applications load and unload, and the routing decision needs a live value.
/// </summary>
public sealed partial class AppleUnifiedMemoryVramProvider : IGpuVramProvider
{
    private const long BytesPerMB = 1024L * 1024L;

    /// <summary>Total system RAM (MB) above which macOS allows the GPU to wire 3/4 of RAM (2/3 up to this size).</summary>
    private const long LargeMemoryThresholdMB = 36 * 1024;

    private const int ProbeTimeoutMs = 5000;

    private readonly IProcessRunner _processRunner;

    internal AppleUnifiedMemoryVramProvider(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public int GetFreeVramMB()
    {
        try
        {
            var memSizeResult = _processRunner.Run("sysctl", "-n hw.memsize", ProbeTimeoutMs);
            if (!memSizeResult.Succeeded ||
                !long.TryParse(memSizeResult.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalBytes) ||
                totalBytes <= 0)
            {
                return 0;
            }

            var totalMB = totalBytes / BytesPerMB;

            // iogpu.wired_limit_mb is unset by default on macOS (the probe fails); in that case
            // the default heuristic in ComputeFreeVramMB applies.
            var wiredLimitResult = _processRunner.Run("sysctl", "-n iogpu.wired_limit_mb", ProbeTimeoutMs);
            long.TryParse(wiredLimitResult.Succeeded ? wiredLimitResult.StdOut.Trim() : string.Empty,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var wiredLimitMB);

            var vmStatResult = _processRunner.Run("vm_stat", string.Empty, ProbeTimeoutMs);
            return ComputeFreeVramMB(totalMB, wiredLimitMB, vmStatResult.StdOut);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Computes the estimated free GPU VRAM (MB) on macOS from the total unified memory, the
    /// optional explicit GPU wired limit (MB), and the raw <c>vm_stat</c> output:
    /// <c>min(gpuLimitMB - wiredMB, free+inactive+speculative MB)</c>, clamped to
    /// <c>[0, int.MaxValue]</c>. Returns 0 when the input is invalid or unparseable.
    /// </summary>
    internal static int ComputeFreeVramMB(long totalMB, long wiredLimitMB, string vmStatOutput)
    {
        if (totalMB <= 0 || !TryParseVmStat(vmStatOutput, out var stats))
        {
            return 0;
        }

        var gpuLimitMB = wiredLimitMB > 0 ? wiredLimitMB : GetDefaultGpuLimitMB(totalMB);
        var wiredMB = stats.WiredPages * stats.PageSize / BytesPerMB;
        var availableMB = (stats.FreePages + stats.InactivePages + stats.SpeculativePages) * stats.PageSize / BytesPerMB;
        return (int)Math.Clamp(Math.Min(gpuLimitMB - wiredMB, availableMB), 0, int.MaxValue);
    }

    /// <summary>
    /// macOS default GPU wired budget: 2/3 of total RAM up to 36 GB, 3/4 above.
    /// </summary>
    internal static long GetDefaultGpuLimitMB(long totalMB)
    {
        return totalMB > LargeMemoryThresholdMB ? totalMB * 3 / 4 : totalMB * 2 / 3;
    }

    /// <summary>
    /// Parses the <c>vm_stat</c> output (page size plus per-class page counts). Fails (and
    /// resets <paramref name="stats"/>) when the page size line is missing or the required
    /// "free" / "wired down" counts are absent; "inactive" and "speculative" are optional
    /// (they default to 0).
    /// </summary>
    internal static bool TryParseVmStat(string output, out VmStat stats)
    {
        stats = default;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var pageSizeMatch = PageSizeRegex().Match(output);
        if (!pageSizeMatch.Success ||
            !long.TryParse(pageSizeMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pageSize) ||
            pageSize <= 0)
        {
            return false;
        }

        var pages = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PagesRegex().Matches(output))
        {
            if (long.TryParse(match.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                pages[match.Groups["name"].Value.Trim()] = count;
            }
        }

        if (!pages.TryGetValue("free", out var free) || !pages.TryGetValue("wired down", out var wired))
        {
            return false;
        }

        pages.TryGetValue("inactive", out var inactive);
        pages.TryGetValue("speculative", out var speculative);
        stats = new VmStat(pageSize, free, inactive, speculative, wired);
        return true;
    }

    /// <summary>Immutable page-statistics snapshot parsed from <c>vm_stat</c> output.</summary>
    internal readonly record struct VmStat(long PageSize, long FreePages, long InactivePages, long SpeculativePages, long WiredPages);

    [GeneratedRegex(@"page size of (\d+) bytes")]
    private static partial Regex PageSizeRegex();

    [GeneratedRegex(@"^Pages (?<name>[a-z ]+):\s+(?<count>\d+)\.", RegexOptions.Multiline)]
    private static partial Regex PagesRegex();
}
