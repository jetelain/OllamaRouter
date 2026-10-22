namespace OllamaRouter.Services;

/// <summary>
/// Windows fallback provider that reports the <b>total</b> GPU VRAM (sum of
/// <c>Win32_VideoController.AdapterRAM</c> across all adapters, via <c>wmic</c> with a
/// <c>Get-CimInstance</c> PowerShell fallback) as "free" VRAM.
/// </summary>
/// <remarks>
/// Degraded semantics: WMI only exposes <b>total</b> VRAM, never free VRAM. This provider is
/// deliberately placed last in the composite probe order so it is only reached when no exact
/// free-VRAM source (e.g. <c>nvidia-smi</c>) reported a non-zero value. The first successful
/// non-zero result is cached for the lifetime of the process because total VRAM is static.
/// </remarks>
internal sealed class WindowsVramProvider : IGpuVramProvider
{
    private const string AdapterRamPrefix = "AdapterRAM=";

    private const int WmicTimeoutMs = 5000;
    private const int PowerShellTimeoutMs = 15000;

    private readonly IProcessRunner _processRunner;
    private readonly object _lock = new();
    private int _cachedFreeMB; // 0 = not yet cached

    public WindowsVramProvider(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public int GetFreeVramMB()
    {
        lock (_lock)
        {
            if (_cachedFreeMB > 0)
            {
                return _cachedFreeMB;
            }

            int freeMB = TryGetTotalVramFromWmic();
            if (freeMB <= 0)
            {
                freeMB = TryGetTotalVramFromPowerShell();
            }

            if (freeMB > 0)
            {
                _cachedFreeMB = freeMB;
            }

            return freeMB;
        }
    }

    private int TryGetTotalVramFromWmic()
    {
        try
        {
            var result = _processRunner.Run(
                "wmic", "path win32_videocard get AdapterRAM /format:list", WmicTimeoutMs);
            if (!result.Succeeded)
            {
                return 0;
            }

            long totalBytes = 0;
            foreach (var rawLine in result.StdOut.Split('\n'))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith(AdapterRamPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line.Substring(AdapterRamPrefix.Length).Trim();
                if (long.TryParse(value, out long bytes))
                {
                    totalBytes += bytes;
                }
            }

            return BytesToMb(totalBytes);
        }
        catch
        {
            return 0;
        }
    }

    private int TryGetTotalVramFromPowerShell()
    {
        try
        {
            var result = _processRunner.Run(
                "powershell",
                "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_VideoController).AdapterRAM\"",
                PowerShellTimeoutMs);
            if (!result.Succeeded)
            {
                return 0;
            }

            long totalBytes = 0;
            foreach (var rawLine in result.StdOut.Split('\n', '\r'))
            {
                var line = rawLine.Trim();
                if (long.TryParse(line, out long bytes))
                {
                    totalBytes += bytes;
                }
            }

            return BytesToMb(totalBytes);
        }
        catch
        {
            return 0;
        }
    }

    private static int BytesToMb(long bytes) => (int)Math.Max(0, bytes / 1024 / 1024);
}
