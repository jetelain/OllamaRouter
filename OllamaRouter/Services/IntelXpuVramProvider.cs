using System.Text.RegularExpressions;

namespace OllamaRouter.Services;

/// <summary>
/// Queries Intel XPU free VRAM (Linux). Tries <c>xpu-smi stat -d mem</c> first (columnar
/// <c>MemUsed</c>/<c>MemSize</c> output, summed across devices), then falls back to
/// <c>intel_gpu_top -L --no-color -s 1</c> (one-shot sample; the process is expected to be
/// killed by the timeout, and its <c>Total:</c>/<c>Used:</c> lines are parsed from the
/// partial output). Returns 0 if no source succeeds or on any parsing failure.
/// </summary>
internal sealed class IntelXpuVramProvider : IGpuVramProvider
{
    private const int TimeoutMs = 5000;

    private static readonly Regex TotalUsedRegex = new(
        @"(total|used)\s*:\s*([\d.]+)\s*(?:MiB|MB|mb|Gib|GB|gb)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IProcessRunner _processRunner;

    public IntelXpuVramProvider(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public int GetFreeVramMB()
    {
        int freeMB = TryGetFreeVramFromXpuSmi();
        return freeMB > 0 ? freeMB : TryGetFreeVramFromIntelGpuTop();
    }

    private int TryGetFreeVramFromXpuSmi()
    {
        try
        {
            var result = _processRunner.Run("xpu-smi", "stat -d mem", TimeoutMs);
            if (!result.Succeeded)
            {
                return 0;
            }

            int? memUsedColumn = null;
            int? memSizeColumn = null;
            long totalFreeMB = 0;

            foreach (var rawLine in result.StdOut.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (memUsedColumn is null || memSizeColumn is null)
                {
                    // Header line: locate the MemUsed / MemSize columns.
                    bool isHeader = false;
                    for (int i = 0; i < columns.Length; i++)
                    {
                        if (string.Equals(columns[i], "MemUsed", StringComparison.Ordinal))
                        {
                            memUsedColumn = i;
                            isHeader = true;
                        }
                        else if (string.Equals(columns[i], "MemSize", StringComparison.Ordinal))
                        {
                            memSizeColumn = i;
                            isHeader = true;
                        }
                    }

                    if (!isHeader)
                    {
                        return 0;
                    }

                    continue;
                }

                // Device row, e.g. `0  1024 MB  16384 MB  6.25`. Value/unit pairs can shift
                // relative to the header, so scan forward from the header column positions:
                // first number at/after MemUsed column = MemUsed, next number = MemSize.
                if (ScanValue(columns, memUsedColumn.Value, out long memUsed) &&
                    ScanValue(columns, memUsedColumn.Value + 1, out long memSize))
                {
                    totalFreeMB += Math.Max(0, memSize - memUsed);
                }
            }

            return (int)totalFreeMB;
        }
        catch
        {
            return 0;
        }
    }

    private int TryGetFreeVramFromIntelGpuTop()
    {
        try
        {
            // Expected-kill path: the one-shot sample is killed on timeout, and the partial
            // output is still usable. A clean exit is accepted as well.
            var result = _processRunner.Run("intel_gpu_top", "-L --no-color -s 1", TimeoutMs);
            if (!result.Succeeded && !result.TimedOut)
            {
                return 0;
            }

            long? totalMiB = null;
            long? usedMiB = null;
            foreach (Match match in TotalUsedRegex.Matches(result.StdOut))
            {
                if (!double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double value))
                {
                    continue;
                }

                long rounded = (long)Math.Round(value);
                if (match.Groups[1].Value.Equals("total", StringComparison.OrdinalIgnoreCase))
                {
                    totalMiB = rounded;
                }
                else
                {
                    usedMiB = rounded;
                }
            }

            if (totalMiB is null || usedMiB is null)
            {
                return 0;
            }

            return (int)Math.Max(0, totalMiB.Value - usedMiB.Value);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Finds the first number at or after <paramref name="startIndex"/> and converts it (together with
    /// its optional unit token) to MB.
    /// </summary>
    private static bool ScanValue(string[] columns, int startIndex, out long mb)
    {
        mb = 0;
        for (int i = Math.Max(0, startIndex); i < columns.Length; i++)
        {
            if (TryParseDoubleInvariant(columns[i], out double value))
            {
                string unit = i + 1 < columns.Length ? columns[i + 1] : string.Empty;
                mb = ToMb(value, unit);
                return true;
            }
        }

        return false;
    }

    private static bool TryParseDoubleInvariant(string s, out double value)
    {
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static long ToMb(double value, string unit)
    {
        string normalized = unit?.Trim().ToUpperInvariant() ?? string.Empty;
        double factor = normalized switch
        {
            "KB" or "KIB" => 1.0 / 1024.0,
            "GB" or "GIB" => 1024.0,
            _ => 1.0 // bare numbers are MB per xpu-smi
        };

        return (long)Math.Round(value * factor);
    }
}
