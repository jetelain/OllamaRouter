using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OllamaRouter.Services;

/// <summary>
/// Estimates the memory currently usable by the GPU on macOS (Apple Silicon unified memory),
/// using <c>sysctl</c> and <c>vm_stat</c>. Returns 0 if the tools are not available or their
/// output cannot be parsed.
/// </summary>
/// <remarks>
/// The GPU can only wire up to a fraction of the unified memory: <c>iogpu.wired_limit_mb</c> when
/// it has been set explicitly, otherwise the macOS default (about 2/3 of the RAM up to 36 GB, 3/4 above).
/// The result is the smallest of the remaining GPU budget (limit minus already wired memory) and the
/// memory the OS can hand out without swapping (free + inactive + speculative pages).
/// </remarks>
public sealed partial class AppleUnifiedMemoryVramProvider : IGpuVramProvider
{
    private const long BytesPerMB = 1024 * 1024;
    private const long LargeMemoryThresholdMB = 36 * 1024;

    public int GetFreeVramMB()
    {
        try
        {
            if (!long.TryParse(RunCommand("sysctl", "-n hw.memsize"), out long totalBytes))
            {
                return 0;
            }

            long.TryParse(RunCommand("sysctl", "-n iogpu.wired_limit_mb"), out long wiredLimitMB);

            return ComputeFreeVramMB(totalBytes / BytesPerMB, wiredLimitMB, RunCommand("vm_stat", string.Empty));
        }
        catch
        {
            return 0;
        }
    }

    internal static int ComputeFreeVramMB(long totalMB, long wiredLimitMB, string vmStatOutput)
    {
        if (totalMB <= 0 || !TryParseVmStat(vmStatOutput, out var stats))
        {
            return 0;
        }

        long gpuLimitMB = wiredLimitMB > 0 ? wiredLimitMB : GetDefaultGpuLimitMB(totalMB);
        long wiredMB = stats.WiredPages * stats.PageSize / BytesPerMB;
        long availableMB = (stats.FreePages + stats.InactivePages + stats.SpeculativePages) * stats.PageSize / BytesPerMB;

        long freeMB = Math.Min(gpuLimitMB - wiredMB, availableMB);
        return (int)Math.Clamp(freeMB, 0, int.MaxValue);
    }

    internal static long GetDefaultGpuLimitMB(long totalMB) =>
        totalMB > LargeMemoryThresholdMB ? totalMB * 3 / 4 : totalMB * 2 / 3;

    internal static bool TryParseVmStat(string output, out VmStat stats)
    {
        stats = default;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var pageSizeMatch = PageSizeRegex().Match(output);
        if (!pageSizeMatch.Success)
        {
            return false;
        }

        var pages = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PagesRegex().Matches(output))
        {
            pages[match.Groups["name"].Value.Trim()] = long.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
        }

        if (!pages.TryGetValue("free", out long free) || !pages.TryGetValue("wired down", out long wired))
        {
            return false;
        }

        stats = new VmStat(
            PageSize: long.Parse(pageSizeMatch.Groups[1].Value, CultureInfo.InvariantCulture),
            FreePages: free,
            InactivePages: pages.GetValueOrDefault("inactive"),
            SpeculativePages: pages.GetValueOrDefault("speculative"),
            WiredPages: wired);
        return true;
    }

    private static string RunCommand(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    [GeneratedRegex(@"page size of (\d+) bytes")]
    private static partial Regex PageSizeRegex();

    [GeneratedRegex(@"^Pages (?<name>[a-z ]+):\s+(?<count>\d+)\.", RegexOptions.Multiline)]
    private static partial Regex PagesRegex();

    internal readonly record struct VmStat(long PageSize, long FreePages, long InactivePages, long SpeculativePages, long WiredPages);
}
