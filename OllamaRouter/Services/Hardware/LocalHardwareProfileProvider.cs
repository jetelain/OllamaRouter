using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OllamaRouter.Services;

namespace OllamaRouter.Services.Hardware;

/// <summary>
/// Fail-soft local workstation hardware profiler. Every sub-probe (RAM size, RAM type,
/// CPU brand, GPU details) degrades independently to <see cref="SystemRamType.Unknown"/>
/// / zero on any exception or timeout — the profile itself is ALWAYS produced. The
/// profile is probed lazily on the first call and cached in a single-flight
/// <see cref="Task{TResult}"/> for the process lifetime (hardware is static), so the
/// cached read path is a pure memory access (the fast <c>GET /api/router/hardware</c> read). GPU identity reuses <see cref="IGpuVramProvider.GetLocalGpuSnapshotAsync"/>.
/// </summary>
public sealed class LocalHardwareProfileProvider : IHardwareProfileProvider
{
    private const int DmidecodeTimeoutMs = 5000;
    private const int WmicTimeoutMs = 5000;
    private const int PowerShellTimeoutMs = 15000;
    private const int SysctlTimeoutMs = 2000;
    private const int SystemProfilerTimeoutMs = 60000;

    private readonly IProcessRunner _processRunner;
    private readonly IGpuVramProvider _gpuVramProvider;
    private readonly ILogger<LocalHardwareProfileProvider> _logger;
    private readonly Func<NodeHardwareProfile>? _testOverride;
    private readonly Func<string, string?>? _memInfoReader;
    private readonly Func<Task<NodeHardwareProfile>> _probe;
    private Task<NodeHardwareProfile>? _cached;
    private readonly object _cacheGate = new();

    /// <summary>
    /// Creates the provider. <paramref name="testOverride"/> short-circuits the probe
    /// entirely (tests inject a canned profile); <paramref name="memInfoReader"/>
    /// overrides the <c>/proc/meminfo</c> file read (tests inject canned content,
    /// null = the real file read).
    /// </summary>
    internal LocalHardwareProfileProvider(
        IProcessRunner processRunner,
        IGpuVramProvider gpuVramProvider,
        ILogger<LocalHardwareProfileProvider> logger,
        Func<NodeHardwareProfile>? testOverride = null,
        Func<string, string?>? memInfoReader = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _gpuVramProvider = gpuVramProvider ?? throw new ArgumentNullException(nameof(gpuVramProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _testOverride = testOverride;
        _memInfoReader = memInfoReader;
        _probe = _testOverride is not null
            ? () => Task.FromResult(_testOverride())
            : () => ProbeAsync();
    }

    public Task<NodeHardwareProfile> GetLocalProfileAsync(CancellationToken cancellationToken = default)
    {
        // Single-flight lazy cache: the hardware is static for the process lifetime.
        lock (_cacheGate)
        {
            return _cached ??= _probe();
        }
    }

    // ------------------------------------------------------------------
    //  Probe orchestration (fail-soft everywhere)
    // ------------------------------------------------------------------

    private async Task<NodeHardwareProfile> ProbeAsync()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        string cpuBrand = "";
        long systemRamMB = 0;
        SystemRamType ramType = SystemRamType.Unknown;
        int channels = 0;
        IReadOnlyList<GpuDeviceProfile> gpus = Array.Empty<GpuDeviceProfile>();

        try
        {
            if (OperatingSystem.IsWindows())
            {
                cpuBrand = ProbeWindowsCpuBrand();
                (systemRamMB, ramType, channels) = ProbeWindowsRam();
                gpus = ProbeDiscreteGpus();
            }
            else if (OperatingSystem.IsLinux())
            {
                cpuBrand = ProbeLinuxCpuBrand();
                systemRamMB = ProbeLinuxRamMB();
                (ramType, channels) = ProbeLinuxRamType();
                gpus = ProbeDiscreteGpus();
            }
            else if (OperatingSystem.IsMacOS())
            {
                cpuBrand = ProbeSysctl("machdep.cpu.brand_string");
                systemRamMB = ProbeMacOSRamMB();
                gpus = ProbeMacOSGpus();
                // Apple Silicon (arm64, or an "Apple" SoC brand string): the system RAM is
                // the unified high-bandwidth tier (integrated or "discrete-looking" GPU
                // entries share it). Intel Macs → unknown RAM type.
                ramType = string.Equals(architecture, "arm64", StringComparison.OrdinalIgnoreCase) ||
                           cpuBrand.Contains("Apple", StringComparison.OrdinalIgnoreCase)
                    ? SystemRamType.AppleUnified
                    : SystemRamType.Unknown;
            }
            else
            {
                gpus = await ProbeVendorGpuSnapshotAsync();
            }
        }
        catch (Exception ex)
        {
            // Fail-soft contract: an unexpected probe bug must never break the profile.
            _logger.LogWarning(ex, "Hardware profile probe degraded to unknown values.");
        }

        if (gpus.Count == 0)
        {
            // The vendor CLI snapshot is the last-resort GPU identity source on any OS.
            gpus = await ProbeVendorGpuSnapshotAsync();
        }

        var nodeName = Environment.MachineName;
        var isUnified = HardwareMetrics.DetectUnifiedMemory(os, architecture, cpuBrand, ramType, gpus);
        var totalFastMemoryMB = HardwareMetrics.ComputeTotalFastMemoryMB(isUnified, systemRamMB, gpus);
        var bandwidth = HardwareMetrics.ComputeEffectiveBandwidthGBs(isUnified, ramType, cpuBrand, channels, gpus);

        return new NodeHardwareProfile(
            nodeName,
            os,
            architecture,
            isUnified,
            systemRamMB,
            ramType,
            gpus,
            totalFastMemoryMB,
            bandwidth);
    }

    private async Task<IReadOnlyList<GpuDeviceProfile>> ProbeVendorGpuSnapshotAsync()
    {
        try
        {
            var snapshot = await _gpuVramProvider.GetLocalGpuSnapshotAsync().ConfigureAwait(false);
            if (snapshot is not { TotalVramMB: > 0 })
            {
                return Array.Empty<GpuDeviceProfile>();
            }

            var vendor = NormalizeVendor(snapshot.Vendor);
            var gpus = new List<GpuDeviceProfile>(snapshot.Devices.Count);
            foreach (var device in snapshot.Devices)
            {
                var modelName = CleanGpuModelName(vendor, ParseDeviceName(device));
                var (vramType, bandwidth) = vendor switch
                {
                    "Nvidia" => ClassifyNvidiaGpu(modelName, 0),
                    "AMD" => ClassifyAmdGpu(modelName, snapshot.TotalVramMB),
                    "Intel" => ClassifyIntelGpu(modelName),
                    _ => ("Unknown", 0.0)
                };

                gpus.Add(new GpuDeviceProfile(vendor, modelName, 0, vramType, 0, bandwidth));
            }

            // The snapshot only carries the SUMMED total VRAM; attribute it all to the
            // first device when a single GPU was detected (the common case), otherwise
            // leave per-device VRAM unknown (0) — never fabricate a per-device split.
            if (gpus.Count == 1)
            {
                gpus[0] = gpus[0] with { VramMB = snapshot.TotalVramMB };
            }

            return gpus;
        }
        catch
        {
            // Fail-soft: unknown GPU list.
        }

        return Array.Empty<GpuDeviceProfile>();
    }

    private static string NormalizeVendor(string vendor) =>
        vendor.Contains("Nvidia", StringComparison.OrdinalIgnoreCase) ? "Nvidia"
            : vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
            : vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
            : "Unknown";

    private static string ParseDeviceName(string device)
    {
        // e.g. "NVIDIA GeForce RTX 4080 (16384 MB)" → "GeForce RTX 4080".
        var open = device.IndexOf('(');
        var name = open > 0 ? device[..open] : device;
        return name.Trim();
    }

    private static string CleanGpuModelName(string vendor, string rawName)
    {
        var name = rawName.Trim();
        while (name.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase))
        {
            name = name[7..].Trim();
        }
        while (name.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase))
        {
            name = name[4..].Trim();
        }
        return name;
    }

    // ------------------------------------------------------------------
    //  Windows probes (wmic + Get-CimInstance PowerShell fallback, mirroring
    //  WindowsVramProvider's pattern)
    // ------------------------------------------------------------------

    private (long RamMB, SystemRamType Type, int Channels) ProbeWindowsRam()
    {
        long ramMB = 0;
        SystemRamType type = SystemRamType.Unknown;
        int channels = 0;

        try
        {
            var ps = _processRunner.Run(
                "powershell",
                "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_PhysicalMemory | Select-Object Capacity,MemoryType,SMBIOSMemoryType,Speed,DeviceLocator | Format-List\"",
                PowerShellTimeoutMs);
            if (ps.Succeeded && ps.StdOut.Length > 0)
            {
                var parsed = ParseWin32PhysicalMemory(ps.StdOut);
                if (parsed.RamMB > 0 || parsed.Type != SystemRamType.Unknown)
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // Fall through to wmic.
        }

        try
        {
            var wmic = _processRunner.Run(
                "wmic", "path win32_physicalmemory get Capacity,MemoryType,SMBIOSMemoryType,Speed,DeviceLocator /format:list", WmicTimeoutMs);
            if (wmic.Succeeded && wmic.StdOut.Length > 0)
            {
                var parsed = ParseWin32PhysicalMemory(wmic.StdOut);
                if (parsed.RamMB > 0 || parsed.Type != SystemRamType.Unknown)
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // Fall through to legacy PowerShell fallback.
        }

        try
        {
            var ps = _processRunner.Run(
                "powershell",
                "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_PhysicalMemory | ForEach-Object { '$($_.MemoryType),$($_.Capacity)' }\"",
                PowerShellTimeoutMs);
            if (ps.Succeeded && ps.StdOut.Length > 0)
            {
                (ramMB, type) = ParseCimInstanceMemoryOutput(ps.StdOut, ramMB);
            }
        }
        catch
        {
            // Fail-soft: unknown RAM type.
        }

        if (ramMB == 0 && OperatingSystem.IsWindows())
        {
            ramMB = GetWindowsPhysicalMemoryMB();
        }

        return (ramMB, type, channels);
    }

    /// <summary>
    /// Parses <c>Get-CimInstance Win32_PhysicalMemory</c> fallback output: one
    /// <c>MemoryType,Capacity</c> line per physical module (older CIM scripts may emit
    /// a bare <c>MemoryType</c> line, which still updates the type). Accumulates the
    /// incoming <paramref name="ramMB"/> baseline (bytes → MB) and returns the
    /// highest-severity type code seen.
    /// </summary>
    internal static (long RamMB, SystemRamType Type) ParseCimInstanceMemoryOutput(string output, long ramMBBaseline = 0)
    {
        var ramMB = ramMBBaseline;
        SystemRamType type = SystemRamType.Unknown;

        foreach (var rawLine in output.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Trim().Split(',', 2);
            if (parts.Length == 0 || parts[0].Length == 0)
            {
                continue;
            }

            var typeCode = parts[0].Trim();
            var parsedType = MaxSeverity(ParseWmiMemoryType(typeCode), ParseSmbiosMemoryType(typeCode));
            type = MaxSeverity(type, parsedType);
            if (parts.Length == 2 && long.TryParse(parts[1].Trim(), out var bytes))
            {
                ramMB += Math.Max(0, bytes / 1024 / 1024);
            }
        }

        return (ramMB, type);
    }

    /// <summary>
    /// Parses <c>Win32_PhysicalMemory</c> WMI list-format output: RAM MB = Σ <c>Capacity</c>
    /// lines, RAM type from the highest-severity <c>MemoryType</c> code, and the channel
    /// count as the number of distinct DeviceLocator bank prefixes ("DIMM A1"/"DIMM A2" → bank A).
    /// </summary>
    internal static (long RamMB, SystemRamType Type, int Channels) ParseWin32PhysicalMemory(string wmicOutput)
    {
        long ramMB = 0;
        SystemRamType type = SystemRamType.Unknown;
        var locators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentLocator = "";
        var maxSpeed = 0;

        foreach (var rawLine in wmicOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            string key;
            string value;
            var eq = line.IndexOf('=');
            var colon = line.IndexOf(':');
            if (eq >= 0 && (colon < 0 || eq < colon))
            {
                key = line[..eq].Trim();
                value = line[(eq + 1)..].Trim();
            }
            else if (colon >= 0)
            {
                key = line[..colon].Trim();
                value = line[(colon + 1)..].Trim();
            }
            else
            {
                continue;
            }

            if (key.Equals("Capacity", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(value, out var bytes))
            {
                ramMB += Math.Max(0, bytes / 1024 / 1024);
            }
            else if (key.Equals("MemoryType", StringComparison.OrdinalIgnoreCase))
            {
                type = MaxSeverity(type, ParseWmiMemoryType(value));
            }
            else if (key.Equals("SMBIOSMemoryType", StringComparison.OrdinalIgnoreCase))
            {
                type = MaxSeverity(type, ParseSmbiosMemoryType(value));
            }
            else if (key.Equals("Speed", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(value, out var speed) && speed > 0)
            {
                maxSpeed = Math.Max(maxSpeed, speed);
            }
            else if (key.Equals("DeviceLocator", StringComparison.OrdinalIgnoreCase))
            {
                currentLocator = value;
                // Channel = distinct bank prefix (strip the trailing DIMM slot digit):
                // "DIMM A1" and "DIMM A2" share bank "A".
                // "Controller0-DIMM1" and "Controller1-DIMM1" share "Controller0-DIMM" / "Controller1-DIMM".
                var bank = currentLocator.TrimEnd();
                var digitStart = bank.Length;
                while (digitStart > 0 && char.IsDigit(bank[digitStart - 1]))
                {
                    digitStart--;
                }

                if (digitStart > 0)
                {
                    locators.Add(bank[..digitStart]);
                }
            }
        }

        if (type == SystemRamType.Unknown && maxSpeed > 0)
        {
            if (maxSpeed >= 4400)
            {
                type = SystemRamType.Ddr5;
            }
            else if (maxSpeed >= 1866)
            {
                type = SystemRamType.Ddr4;
            }
        }

        return (ramMB, type, locators.Count);
    }

    internal static SystemRamType ParseSmbiosMemoryType(string value)
    {
        if (!int.TryParse(value, out var code))
        {
            return SystemRamType.Unknown;
        }

        // DMTF SMBIOS DSP0134 Type 17 Memory Device - Memory Type codes:
        // 24 = DDR3, 26 = DDR4, 34 = DDR5, 35 = LPDDR5
        return code switch
        {
            24 => SystemRamType.Ddr3,
            26 => SystemRamType.Ddr4,
            34 => SystemRamType.Ddr5,
            35 => SystemRamType.LpDdr5X,
            _ => SystemRamType.Unknown
        };
    }

    internal static SystemRamType ParseWmiMemoryType(string value)
    {
        if (!int.TryParse(value, out var code))
        {
            return SystemRamType.Unknown;
        }

        // Win32_PhysicalMemory.MemoryType codes (MS-MQX):
        // 19/20 = DDR3, 22 = LPDDR3, 24 = DDR5, 25 = LPDDR5, 26 = DDR4, 27 = LPDDR5X.
        return code switch
        {
            19 or 20 => SystemRamType.Ddr3,
            26 => SystemRamType.Ddr4,
            24 => SystemRamType.Ddr5,
            25 or 27 => SystemRamType.LpDdr5X,
            _ => SystemRamType.Unknown
        };
    }

    private static SystemRamType MaxSeverity(SystemRamType a, SystemRamType b) =>
        Math.Max((int)a, (int)b) == (int)a ? a : b;

    // ------------------------------------------------------------------
    //  Linux probes (direct /proc/meminfo read + dmidecode + vendor CLIs)
    // ------------------------------------------------------------------

    private long ProbeLinuxRamMB()
    {
        try
        {
            var content = _memInfoReader is not null
                ? _memInfoReader("/proc/meminfo")
                : File.Exists("/proc/meminfo") ? File.ReadAllText("/proc/meminfo") : null;
            if (content is null)
            {
                return 0;
            }

            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.TrimStart();
                if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                {
                    var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length >= 2 && long.TryParse(fields[1], out var kb))
                    {
                        return Math.Max(0, kb / 1024);
                    }
                }
            }
        }
        catch
        {
            // Fail-soft: unknown RAM size.
        }

        return 0;
    }

    private (SystemRamType, int) ProbeLinuxRamType()
    {
        try
        {
            var result = _processRunner.Run("dmidecode", "-t memory", DmidecodeTimeoutMs);
            if (result.Succeeded)
            {
                return ParseDmidecodeMemory(result.StdOut);
            }
        }
        catch
        {
            // Fail-soft: unknown RAM type.
        }

        return (SystemRamType.Unknown, 0);
    }

    /// <summary>
    /// Parses <c>dmidecode -t memory</c> output: the RAM type from the first populated
    /// memory device ("Type: DDR5" etc.) and the channel count as the number of distinct
    /// per-device "Locator:" bank prefixes (DIMM A1 / DIMM A2 → bank A). The array-level
    /// "Bank Locator:" lines are ignored (they repeat per device, not per channel).
    /// </summary>
    internal static (SystemRamType Type, int Channels) ParseDmidecodeMemory(string output)
    {
        SystemRamType type = SystemRamType.Unknown;
        var banks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inDevice = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Memory Device", StringComparison.OrdinalIgnoreCase))
            {
                inDevice = true;
                continue;
            }

            if (!inDevice)
            {
                continue;
            }

            if (line.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line.AsSpan(5).ToString().Trim();
                if (value.Length > 0 && !string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    type = ParseDmidecodeType(value);
                }
            }
            else if (line.StartsWith("Locator:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line.AsSpan(8).ToString().Trim();
                if (value.Length > 0 && !string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    var digitStart = value.Length;
                    while (digitStart > 0 && char.IsDigit(value[digitStart - 1]))
                    {
                        digitStart--;
                    }

                    if (digitStart > 0)
                    {
                        banks.Add(value[..digitStart]);
                    }
                }
            }
            else if (line.StartsWith("Size:", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(line.AsSpan(5).ToString().Trim(), "No Module Installed", StringComparison.OrdinalIgnoreCase))
            {
                inDevice = false;
            }
        }

        return (type, banks.Count);
    }

    internal static SystemRamType ParseDmidecodeType(string value)
    {
        var v = value.Trim().ToUpperInvariant();
        return v.Contains("LPDDR5") ? SystemRamType.LpDdr5X
            : v.Contains("DDR5") ? SystemRamType.Ddr5
            : v.Contains("DDR4") ? SystemRamType.Ddr4
            : v.Contains("DDR3") ? SystemRamType.Ddr3
            : SystemRamType.Unknown;
    }

    private IReadOnlyList<GpuDeviceProfile> ProbeLinuxGpus() => ProbeDiscreteGpus();

    private IReadOnlyList<GpuDeviceProfile> ProbeDiscreteGpus()
    {
        var gpus = new List<GpuDeviceProfile>();

        // NVIDIA: nvidia-smi (cross-platform on Windows and Linux).
        try
        {
            var nvidia = _processRunner.Run(
                "nvidia-smi",
                "--query-gpu=name,memory.total --format=csv,noheader",
                5000);
            if (nvidia.Succeeded && nvidia.StdOut.Length > 0)
            {
                foreach (var rawLine in nvidia.StdOut.Split('\n'))
                {
                    var parts = rawLine.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                    {
                        continue;
                    }

                    var rawName = parts[0];
                    var name = CleanGpuModelName("Nvidia", rawName);
                    var vram = ParseLeadingInt(parts[1]);
                    var (vramType, bandwidth) = ClassifyNvidiaGpu(name, 0);
                    gpus.Add(new GpuDeviceProfile("Nvidia", name, vram, vramType, 0, bandwidth));
                }
            }
        }
        catch
        {
            // Fail-soft: no NVIDIA GPUs.
        }

        if (gpus.Count > 0)
        {
            return gpus;
        }

        // AMD: rocm-smi JSON (VRAM total; no bus width → GDDR6 estimate when > 8 GB, else unknown).
        try
        {
            var rocm = _processRunner.Run("rocm-smi", "--showmeminfo vram --json", 5000);
            if (rocm.Succeeded && rocm.StdOut.Length > 0)
            {
                var matches = Regex.Matches(rocm.StdOut, "\"vram_total\"\\s*:\\s*(\\d+)");
                foreach (Match match in matches)
                {
                    if (long.TryParse(match.Groups[1].Value, out var totalKb))
                    {
                        var vramMB = (int)Math.Max(0, totalKb / 1024);
                        var (vramType, bandwidth) = ClassifyAmdGpu("AMD GPU (rocm-smi)", vramMB);
                        gpus.Add(new GpuDeviceProfile("AMD", "AMD GPU (rocm-smi)", vramMB, vramType, 0, bandwidth));
                    }
                }
            }
        }
        catch
        {
            // Fail-soft: no AMD GPUs.
        }

        return gpus;
    }

    internal static (string VramType, double Bandwidth) ClassifyNvidiaGpu(string name, int busWidth)
    {
        var upper = name.ToUpperInvariant();
        if (upper.Contains("H100") || upper.Contains("H200") || upper.Contains("HOPPER") || upper.Contains("BLACKWELL") || upper.Contains("B200") || upper.Contains("B100") || upper.Contains("B40"))
        {
            return ("HBM3", HardwareMetrics.EstimateGpuBandwidthGBs("HBM3", busWidth));
        }

        if (upper.Contains("A100") || upper.Contains("A30") || upper.Contains("GA100"))
        {
            return ("HBM2E", HardwareMetrics.EstimateGpuBandwidthGBs("HBM2E", busWidth));
        }

        if (upper.Contains("3090") || upper.Contains("4080") || upper.Contains("4090") || upper.Contains("RTX 4") || upper.Contains("RTX 309") || upper.Contains("4070") || upper.Contains("3080") || upper.Contains("3070 TI"))
        {
            return ("GDDR6X", HardwareMetrics.EstimateGpuBandwidthGBs("GDDR6X", busWidth));
        }

        return ("GDDR6", HardwareMetrics.EstimateGpuBandwidthGBs("GDDR6", busWidth));
    }

    internal static (string VramType, double Bandwidth) ClassifyAmdGpu(string name, int vramMB)
    {
        var upper = name.ToUpperInvariant();
        if (upper.Contains("MI300") || upper.Contains("MI325"))
        {
            return ("HBM3", HardwareMetrics.EstimateGpuBandwidthGBs("HBM3", 0));
        }

        if (upper.Contains("MI250") || upper.Contains("MI200") || upper.Contains("MI100"))
        {
            return ("HBM2E", HardwareMetrics.EstimateGpuBandwidthGBs("HBM2E", 0));
        }

        var vramType = vramMB >= 8192 || upper.Contains("7900") || upper.Contains("7800") || upper.Contains("6900") || upper.Contains("6800") ? "GDDR6" : "Unknown";
        return (vramType, vramType == "GDDR6" ? HardwareMetrics.EstimateGpuBandwidthGBs("GDDR6", 0) : 0);
    }

    internal static (string VramType, double Bandwidth) ClassifyIntelGpu(string name)
    {
        var upper = name.ToUpperInvariant();
        if (upper.Contains("ARC") || upper.Contains("A770") || upper.Contains("A750") || upper.Contains("B580") || upper.Contains("B570"))
        {
            return ("GDDR6", HardwareMetrics.EstimateGpuBandwidthGBs("GDDR6", 0));
        }

        return ("Unknown", 0);
    }

    private static int ParseLeadingInt(string value)
    {
        var trimmed = value.Trim();
        var numberEnd = 0;
        while (numberEnd < trimmed.Length && char.IsDigit(trimmed[numberEnd]))
        {
            numberEnd++;
        }

        return numberEnd > 0 && int.TryParse(trimmed[..numberEnd], out var parsed) ? parsed : 0;
    }

    // ------------------------------------------------------------------
    //  macOS probes (sysctl; system_profiler optional + 60 s timeout)
    // ------------------------------------------------------------------

    private string ProbeSysctl(string key)
    {
        try
        {
            var result = _processRunner.Run("sysctl", "-n " + key, SysctlTimeoutMs);
            return result.Succeeded ? result.StdOut.Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private long ProbeMacOSRamMB()
    {
        var bytes = ProbeSysctl("hw.memsize");
        return long.TryParse(bytes, out var value) ? Math.Max(0, value / 1024 / 1024) : 0;
    }

    private IReadOnlyList<GpuDeviceProfile> ProbeMacOSGpus()
    {
        try
        {
            var result = _processRunner.Run(
                "system_profiler", "SPDisplaysDataType -json", SystemProfilerTimeoutMs);
            if (!result.Succeeded || result.StdOut.Length == 0)
            {
                return Array.Empty<GpuDeviceProfile>();
            }

            var root = System.Text.Json.JsonDocument.Parse(result.StdOut);
            var gpus = new List<GpuDeviceProfile>();
            if (root.RootElement.TryGetProperty("SPDisplaysDataType", out var displays) &&
                displays.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var display in displays.EnumerateArray())
                {
                    var name = display.TryGetProperty("_name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || name.Contains("VGA", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var vramMB = display.TryGetProperty("sram", out var s) && int.TryParse(s.GetString(), out var v) ? v : 0;
                    gpus.Add(new GpuDeviceProfile("Apple", name.Trim(), vramMB, "AppleUnified", 0, 0));
                }
            }

            return gpus;
        }
        catch
        {
            // Fail-soft: unknown GPU list.
        }

        return Array.Empty<GpuDeviceProfile>();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static long GetWindowsPhysicalMemoryMB()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MEMORYSTATUSEX();
                status.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
                if (GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0)
                {
                    return (long)(status.ullTotalPhys / 1024 / 1024);
                }
            }
        }
        catch
        {
            // Fail-soft
        }

        return 0;
    }

    private static string ProbeWindowsCpuBrand()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var brand = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString",
                    null) as string;
                if (!string.IsNullOrWhiteSpace(brand))
                {
                    return brand.Trim();
                }
            }
        }
        catch
        {
            // Fail-soft
        }

        return "";
    }

    private static string ProbeLinuxCpuBrand()
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    {
                        var colon = line.IndexOf(':');
                        if (colon > 0)
                        {
                            return line[(colon + 1)..].Trim();
                        }
                    }
                }
            }
        }
        catch
        {
            // Fail-soft
        }

        return "";
    }
}
