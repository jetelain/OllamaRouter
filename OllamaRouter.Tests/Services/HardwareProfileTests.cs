using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Options;
using OllamaRouter.Services;
using OllamaRouter.Services.Hardware;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OllamaRouter.Tests.Services;

public class HardwareProfileTests
{
    // ------------------------------------------------------------------
    //  HardwareMetrics: derived-metric invariants
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(SystemRamType.Ddr3, 2, 12.8)]
    [InlineData(SystemRamType.Ddr4, 2, 25.6)]
    [InlineData(SystemRamType.Ddr5, 2, 38.4)]
    [InlineData(SystemRamType.LpDdr5X, 4, 341.2)]
    [InlineData(SystemRamType.LpDdr5X, 0, 341.2)]
    [InlineData(SystemRamType.Unknown, 0, 0)]
    public void EstimateRamBandwidthGBs_FollowsConservativeTables(SystemRamType ramType, int channels, double expected)
    {
        Assert.Equal(expected, HardwareMetrics.EstimateRamBandwidthGBs(ramType, channels), precision: 2);
    }

    [Fact]
    public void EstimateRamBandwidthGBs_FourChannelServer_DoublesTheBaseline()
    {
        Assert.Equal(51.2, HardwareMetrics.EstimateRamBandwidthGBs(SystemRamType.Ddr4, 4), precision: 2);
    }

    [Theory]
    [InlineData("Apple M3 Ultra", 819)]
    [InlineData("Apple M2 Ultra", 400)]
    [InlineData("Apple M1 Max", 307)]
    [InlineData("Apple M2 Max", 307)]
    [InlineData("Apple M2 Pro", 307)]
    [InlineData("Apple M4 Pro", 546)]
    [InlineData("Apple M3", 546)]
    [InlineData("Apple M1", 153)]
    [InlineData("Apple M2", 153)]
    [InlineData("Unknown SoC", 100)]
    [InlineData("", 100)]
    public void EstimateAppleUnifiedBandwidthGBs_MapsSocClassTable(string cpuBrand, double expected)
    {
        Assert.Equal(expected, HardwareMetrics.EstimateAppleUnifiedBandwidthGBs(cpuBrand), precision: 2);
    }

    [Theory]
    [InlineData("GDDR6", 512, 512)]
    [InlineData("GDDR6", 0, 256)]
    [InlineData("GDDR6X", 384, 936)]
    [InlineData("HBM2E", 512, 1555)]
    [InlineData("HBM3", 512, 1555)]
    [InlineData("Unknown", 512, 0)]
    [InlineData(null, 512, 0)]
    public void EstimateGpuBandwidthGBs_NeverFabricatesUnknownTiers(string? vramType, int busWidth, double expected)
    {
        Assert.Equal(expected, HardwareMetrics.EstimateGpuBandwidthGBs(vramType, busWidth), precision: 2);
    }

    [Fact]
    public void AppleSiliconProfile_IsUnified_UsesSystemRam_AndBandwidthExceedsFloor()
    {
        // Apple Silicon: arm64 + Apple SoC + 36 GB RAM, no discrete GPU.
        var isUnified = HardwareMetrics.DetectUnifiedMemory(
            "macos", "arm64", "Apple M2", SystemRamType.AppleUnified, Array.Empty<GpuDeviceProfile>());

        var totalFast = HardwareMetrics.ComputeTotalFastMemoryMB(isUnified, 36864, Array.Empty<GpuDeviceProfile>());
        var bandwidth = HardwareMetrics.ComputeEffectiveBandwidthGBs(
            isUnified, SystemRamType.AppleUnified, "Apple M2", 0, Array.Empty<GpuDeviceProfile>());

        Assert.True(isUnified);
        Assert.Equal(36864, totalFast);
        Assert.True(bandwidth > 100, $"expected > 100 GB/s, got {bandwidth}");
    }

    [Fact]
    public void DiscreteGpuProfile_IsNotUnified_UsesVramAsFastMemory()
    {
        // RTX node: 16 GB VRAM GPU + 128 GB DDR5 system RAM.
        var gpus = new[]
        {
            new GpuDeviceProfile("Nvidia", "GeForce RTX 4080", 16384, "GDDR6X", 384, 936)
        };

        var isUnified = HardwareMetrics.DetectUnifiedMemory("linux", "x64", "Intel", SystemRamType.Ddr5, gpus);
        var totalFast = HardwareMetrics.ComputeTotalFastMemoryMB(isUnified, 131072, gpus);
        var bandwidth = HardwareMetrics.ComputeEffectiveBandwidthGBs(isUnified, SystemRamType.Ddr5, "Intel", 2, gpus);

        Assert.False(isUnified);
        Assert.Equal(16384, totalFast);
        Assert.Equal(936, bandwidth, precision: 2);
    }

    [Fact]
    public void CpuOnlyProfile_FallsBackToSystemRam_WithoutExceptions()
    {
        var isUnified = HardwareMetrics.DetectUnifiedMemory("linux", "x64", "Intel", SystemRamType.Unknown, Array.Empty<GpuDeviceProfile>());
        var totalFast = HardwareMetrics.ComputeTotalFastMemoryMB(isUnified, 8192, Array.Empty<GpuDeviceProfile>());
        var bandwidth = HardwareMetrics.ComputeEffectiveBandwidthGBs(isUnified, SystemRamType.Unknown, "Intel", 0, Array.Empty<GpuDeviceProfile>());

        Assert.False(isUnified);
        Assert.Equal(8192, totalFast);
        Assert.Equal(0, bandwidth, precision: 2);
    }

    [Theory]
    [InlineData(true, 65536L, 0, 65536L)]               // unified → RAM
    [InlineData(false, 131072L, 2, 24576L)]             // discrete → sum of 8192 + 16384 VRAM
    [InlineData(false, 16384L, 1, 16384L)]              // GPU reports 0 VRAM → RAM fallback
    [InlineData(false, 8192L, 0, 8192L)]                // CPU-only → RAM
    public void ComputeTotalFastMemoryMB_TableDriven(bool isUnified, long ramMB, int gpuCount, long expected)
    {
        var gpus = new List<GpuDeviceProfile>();
        for (var i = 0; i < gpuCount; i++)
        {
            gpus.Add(i == 0
                ? new GpuDeviceProfile("Nvidia", "A", 8192, "GDDR6X", 384, 936)
                : new GpuDeviceProfile("Nvidia", "B", 16384, "GDDR6X", 384, 936));
        }

        // The gpuCount == 1 row models a GPU with a 0-VRAM probe result.
        if (gpuCount == 1)
        {
            gpus[0] = gpus[0] with { VramMB = 0 };
        }

        Assert.Equal(expected, HardwareMetrics.ComputeTotalFastMemoryMB(isUnified, ramMB, gpus));
    }

    [Theory]
    [InlineData(SystemRamType.Ddr5, 4, 76.8)]  // 4-channel server multiplier
    [InlineData(SystemRamType.Ddr3, 4, 25.6)]
    [InlineData(SystemRamType.Ddr4, 1, 25.6)]  // single channel: no multiplier
    public void EstimateRamBandwidthGBs_MultiChannelVariants(SystemRamType ramType, int channels, double expected)
    {
        Assert.Equal(expected, HardwareMetrics.EstimateRamBandwidthGBs(ramType, channels), precision: 2);
    }

    [Fact]
    public void ComputeEffectiveBandwidthGBs_PrefersExplicitGpuBandwidthOverTable()
    {
        // A per-device bandwidth measured by the probe (950) wins over the tier table (936).
        var gpus = new[] { new GpuDeviceProfile("Nvidia", "GeForce RTX 4080", 16384, "GDDR6X", 384, 950) };
        Assert.Equal(950, HardwareMetrics.ComputeEffectiveBandwidthGBs(false, SystemRamType.Ddr5, "Intel", 2, gpus), precision: 2);
    }

    [Fact]
    public void ComputeEffectiveBandwidthGBs_UnifiedApu_UsesRamTierTable()
    {
        // AMD Strix Halo: unified, LPDDR5X, 4-channel default ≈ 341.2 GB/s.
        var bandwidth = HardwareMetrics.ComputeEffectiveBandwidthGBs(
            isUnifiedMemory: true, SystemRamType.LpDdr5X, "AMD Ryzen AI 9 HX 370", 4, Array.Empty<GpuDeviceProfile>());
        Assert.Equal(341.2, bandwidth, precision: 2);
    }

    [Fact]
    public void AmdLpDdr5XNoGpu_IsDetectedAsUnified()
    {
        var isUnified = HardwareMetrics.DetectUnifiedMemory(
            "linux", "x64", "AMD Ryzen AI 9 HX 370", SystemRamType.LpDdr5X, Array.Empty<GpuDeviceProfile>());
        Assert.True(isUnified);
    }

    [Fact]
    public void AmdLpDdr5XWithDiscreteGpu_IsNotUnified()
    {
        var gpus = new[] { new GpuDeviceProfile("AMD", "Radeon RX 7900 XTX", 24576, "GDDR6", 384, 960) };
        var isUnified = HardwareMetrics.DetectUnifiedMemory(
            "linux", "x64", "AMD Ryzen 9 7950X", SystemRamType.LpDdr5X, gpus);
        Assert.False(isUnified);
    }

    [Fact]
    public void AmdLpDdr5XWithApuIntegratedGpu_IsDetectedAsUnified()
    {
        var gpus = new[] { new GpuDeviceProfile("AMD", "Strix Halo [Radeon Graphics / Radeon 8060S Graphics]", 0, "Unknown", 0, 0) };
        var isUnified = HardwareMetrics.DetectUnifiedMemory(
            "linux", "x64", "AMD RYZEN AI MAX+ 395 w/ Radeon 8060S", SystemRamType.LpDdr5X, gpus);
        var totalFast = HardwareMetrics.ComputeTotalFastMemoryMB(isUnified, 98304, gpus);
        var bandwidth = HardwareMetrics.ComputeEffectiveBandwidthGBs(
            isUnified, SystemRamType.LpDdr5X, "AMD RYZEN AI MAX+ 395 w/ Radeon 8060S", 1, gpus);

        Assert.True(isUnified);
        Assert.Equal(98304, totalFast);
        Assert.Equal(341.2, bandwidth, precision: 1);
    }

    [Fact]
    public void UnknownHardware_NeverProducesFabricatedBandwidth()
    {
        var bandwidth = HardwareMetrics.ComputeEffectiveBandwidthGBs(
            isUnifiedMemory: false, SystemRamType.Unknown, "", 0,
            new[] { new GpuDeviceProfile("Unknown", "Mystery GPU", 4096, "Unknown", 0, 0) });
        Assert.Equal(0, bandwidth, precision: 2);
    }

    // ------------------------------------------------------------------
    //  LocalHardwareProfileProvider: single-flight cache + fail-soft
    // ------------------------------------------------------------------

    private static LocalHardwareProfileProvider CreateProvider(
        FakeProcessRunner processRunner,
        NodeHardwareProfile? testOverride = null,
        IGpuVramProvider? gpuVramProvider = null,
        Func<string, string?>? memInfoReader = null)
        => new(
            processRunner,
            gpuVramProvider ?? new Mock<IGpuVramProvider>().Object,
            NullLogger<LocalHardwareProfileProvider>.Instance,
            testOverride: testOverride is null ? null : () => testOverride,
            memInfoReader);

    [Fact]
    public async Task GetLocalProfile_CachesResult_ForProcessLifetime()
    {
        var probeCount = 0;
        var provider = new LocalHardwareProfileProvider(
            new FakeProcessRunner(),
            new Mock<IGpuVramProvider>().Object,
            NullLogger<LocalHardwareProfileProvider>.Instance,
            testOverride: () =>
            {
                probeCount++;
                return new NodeHardwareProfile(
                    "cached-node", "linux", "x64", false, 16384, SystemRamType.Ddr5,
                    Array.Empty<GpuDeviceProfile>(), 16384, 38.4);
            });

        var first = await provider.GetLocalProfileAsync();
        var second = await provider.GetLocalProfileAsync();
        var third = await provider.GetLocalProfileAsync();

        Assert.Equal("cached-node", first.NodeName);
        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task GetLocalProfile_FailSoft_ReturnsProfileWhenAllProbesFail()
    {
        // No test override: the provider runs the real OS probe against a FakeProcessRunner
        // that fails every invocation, plus an explicit /proc/meminfo failure. Whatever the
        // CI host is, the profile must ALWAYS be produced (fail-soft contract).
        var processRunner = new FakeProcessRunner();
        var provider = new LocalHardwareProfileProvider(
            processRunner,
            new Mock<IGpuVramProvider>().Object,
            NullLogger<LocalHardwareProfileProvider>.Instance,
            testOverride: null,
            memInfoReader: _ => null);

        var profile = await provider.GetLocalProfileAsync();

        Assert.NotNull(profile);
        Assert.False(string.IsNullOrWhiteSpace(profile.NodeName));
        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos", profile.Os);
        Assert.False(profile.Gpus is null);
    }

    [Fact]
    public async Task GetLocalProfile_LiveOnCurrentMachine_ProbesSensibleValues()
    {
        var runner = new ProcessRunner();
        var gpu = new NvidiaSmiVramProvider(runner);
        var provider = new LocalHardwareProfileProvider(runner, gpu, NullLogger<LocalHardwareProfileProvider>.Instance);

        var profile = await provider.GetLocalProfileAsync();

        Assert.NotNull(profile);
        Assert.False(string.IsNullOrWhiteSpace(profile.NodeName));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(profile.SystemRamMB > 0, $"Expected RAM > 0, got {profile.SystemRamMB}");
            Assert.NotEqual(SystemRamType.Unknown, profile.SystemRamType);
            if (profile.Gpus.Count > 0)
            {
                Assert.NotEqual("Unknown", profile.Gpus[0].VramType);
                Assert.False(profile.Gpus[0].ModelName.StartsWith("NVIDIA NVIDIA"));
            }
        }
    }

    [Fact]
    public void GetLocalProfile_ThrowsWhenConstructorDepsMissing()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalHardwareProfileProvider(
            (IProcessRunner?)null!,
            new Mock<IGpuVramProvider>().Object,
            NullLogger<LocalHardwareProfileProvider>.Instance));
    }

    [Fact]
    public async Task GetLocalProfile_InjectableMemInfoReader_IsUsedOnLinux()
    {
        var fakeMeminfo = "MemTotal:        32768000 kB\nMemFree:        10000000 kB\n";
        var provider = CreateProvider(new FakeProcessRunner(), memInfoReader: _ => fakeMeminfo);
        var profile = await provider.GetLocalProfileAsync();

        if (OperatingSystem.IsLinux())
        {
            // 32768000 kB / 1024 = 32000 MB.
            Assert.Equal(32000, profile.SystemRamMB);
        }
        // On non-Linux CI hosts the /proc/meminfo reader is never consulted.
    }

    // ------------------------------------------------------------------
    //  WMI / dmidecode parsers (pure)
    // ------------------------------------------------------------------

    [Fact]
    public void ParseWin32PhysicalMemory_SumsCapacity_MapsType_AndCountsBanks()
    {
        const string wmicOutput = """
            Caption=
            Capacity=8589934592
            DeviceLocator=DIMM A1
            MemoryType=26

            Caption=
            Capacity=8589934592
            DeviceLocator=DIMM A2
            MemoryType=26

            Caption=
            Capacity=8589934592
            DeviceLocator=DIMM B1
            MemoryType=26

            Caption=
            Capacity=8589934592
            DeviceLocator=DIMM B2
            MemoryType=26

            """;

        var (ramMB, type, channels) = LocalHardwareProfileProvider.ParseWin32PhysicalMemory(wmicOutput);

        Assert.Equal(32768, ramMB);
        Assert.Equal(SystemRamType.Ddr4, type);
        Assert.Equal(2, channels); // DIMM A + DIMM B → 2 channels
    }

    [Fact]
    public void ParseWmiMemoryType_MapsDocumentedCodes()
    {
        Assert.Equal(SystemRamType.Ddr3, LocalHardwareProfileProvider.ParseWmiMemoryType("20"));
        Assert.Equal(SystemRamType.Ddr4, LocalHardwareProfileProvider.ParseWmiMemoryType("26"));
        Assert.Equal(SystemRamType.Ddr5, LocalHardwareProfileProvider.ParseWmiMemoryType("24"));
        Assert.Equal(SystemRamType.LpDdr5X, LocalHardwareProfileProvider.ParseWmiMemoryType("27"));
        Assert.Equal(SystemRamType.Unknown, LocalHardwareProfileProvider.ParseWmiMemoryType("99"));
        Assert.Equal(SystemRamType.Unknown, LocalHardwareProfileProvider.ParseWmiMemoryType(""));
    }

    [Theory]
    [InlineData("19", SystemRamType.Ddr3)]   // DDR3 (alternative MS-MQX code)
    [InlineData("22", SystemRamType.Unknown)] // LPDDR3 is not in the enum → Unknown
    [InlineData("25", SystemRamType.LpDdr5X)] // LPDDR5
    [InlineData("0", SystemRamType.Unknown)]
    [InlineData("24x", SystemRamType.Unknown)]
    public void ParseWmiMemoryType_AdditionalCodes_MapFailSoft(string code, SystemRamType expected)
    {
        Assert.Equal(expected, LocalHardwareProfileProvider.ParseWmiMemoryType(code));
    }

    [Fact]
    public void ParseCimInstanceMemoryOutput_SumsCapacityAndMapsType()
    {
        // Get-CimInstance Win32_PhysicalMemory fallback: "MemoryType,Capacity" per module.
        const string psOutput = "26,8589934592\r\n26,8589934592\r\n24,17179869184\r\n";
        var (ramMB, type) = LocalHardwareProfileProvider.ParseCimInstanceMemoryOutput(psOutput);

        Assert.Equal(32768, ramMB); // 2 x 8 GiB + 16 GiB
        Assert.Equal(SystemRamType.Ddr5, type); // highest-severity code wins (24 > 26? no: 26=Ddr4, 24=Ddr5 -> Ddr5 enum value 3 > 2)
    }

    [Fact]
    public void ParseCimInstanceMemoryOutput_AccumulatesBaseline_AndToleratesBareTypeLines()
    {
        // A baseline from an earlier probe accumulates; bare "MemoryType" lines (no
        // capacity) still update the type.
        var (ramMB, type) = LocalHardwareProfileProvider.ParseCimInstanceMemoryOutput(
            "26\n26,1073741824\n", ramMBBaseline: 1024);

        Assert.Equal(2048, ramMB); // 1024 baseline + 1 GiB
        Assert.Equal(SystemRamType.Ddr4, type);
    }

    [Theory]
    [InlineData("24", SystemRamType.Ddr3)]
    [InlineData("26", SystemRamType.Ddr4)]
    [InlineData("34", SystemRamType.Ddr5)]
    [InlineData("35", SystemRamType.LpDdr5X)]
    [InlineData("0", SystemRamType.Unknown)]
    [InlineData("99", SystemRamType.Unknown)]
    [InlineData("", SystemRamType.Unknown)]
    public void ParseSmbiosMemoryType_MapsDocumentedCodes(string code, SystemRamType expected)
    {
        Assert.Equal(expected, LocalHardwareProfileProvider.ParseSmbiosMemoryType(code));
    }

    [Fact]
    public void ParseWin32PhysicalMemory_FormatList_ParsesCapacity_SmbiosMemoryType_AndControllers()
    {
        const string psOutput = """
            Capacity         : 34359738368
            MemoryType       : 0
            SMBIOSMemoryType : 34
            Speed            : 6400
            DeviceLocator    : Controller0-DIMM1

            Capacity         : 34359738368
            MemoryType       : 0
            SMBIOSMemoryType : 34
            Speed            : 6400
            DeviceLocator    : Controller1-DIMM1
            """;

        var (ramMB, type, channels) = LocalHardwareProfileProvider.ParseWin32PhysicalMemory(psOutput);

        Assert.Equal(65536, ramMB);
        Assert.Equal(SystemRamType.Ddr5, type);
        Assert.Equal(2, channels);
    }

    [Fact]
    public void ParseWin32PhysicalMemory_SpeedFallback_WhenTypeCodesZero()
    {
        const string psOutput = """
            Capacity: 17179869184
            MemoryType: 0
            SMBIOSMemoryType: 0
            Speed: 6000
            DeviceLocator: DIMM1
            """;

        var (ramMB, type, channels) = LocalHardwareProfileProvider.ParseWin32PhysicalMemory(psOutput);

        Assert.Equal(16384, ramMB);
        Assert.Equal(SystemRamType.Ddr5, type);
    }

    [Theory]
    [InlineData("GeForce RTX 4080 SUPER", 0, "GDDR6X")]
    [InlineData("GeForce RTX 4090", 384, "GDDR6X")]
    [InlineData("GeForce RTX 3080", 320, "GDDR6X")]
    [InlineData("GeForce RTX 3070", 256, "GDDR6")]
    [InlineData("NVIDIA H100 80GB HBM3", 512, "HBM3")]
    [InlineData("NVIDIA A100-PCIE-40GB", 512, "HBM2E")]
    public void ClassifyNvidiaGpu_MapsGenerationsAndTiers(string name, int busWidth, string expectedType)
    {
        var (vramType, bandwidth) = LocalHardwareProfileProvider.ClassifyNvidiaGpu(name, busWidth);
        Assert.Equal(expectedType, vramType);
        Assert.True(bandwidth > 0);
    }

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX", 24576, "GDDR6")]
    [InlineData("AMD Instinct MI300X", 196608, "HBM3")]
    [InlineData("AMD GPU (rocm-smi)", 4096, "Unknown")]
    public void ClassifyAmdGpu_MapsGenerationsAndTiers(string name, int vramMB, string expectedType)
    {
        var (vramType, bandwidth) = LocalHardwareProfileProvider.ClassifyAmdGpu(name, vramMB);
        Assert.Equal(expectedType, vramType);
        if (expectedType != "Unknown")
        {
            Assert.True(bandwidth > 0);
        }
    }

    [Theory]
    [InlineData("Intel Arc A770 Graphics", "GDDR6")]
    [InlineData("Intel Arc B580", "GDDR6")]
    [InlineData("Intel UHD Graphics 770", "Unknown")]
    public void ClassifyIntelGpu_MapsGenerationsAndTiers(string name, string expectedType)
    {
        var (vramType, bandwidth) = LocalHardwareProfileProvider.ClassifyIntelGpu(name);
        Assert.Equal(expectedType, vramType);
        if (expectedType != "Unknown")
        {
            Assert.True(bandwidth > 0);
        }
    }

    [Fact]
    public void ParseDmidecodeMemory_ExtractsTypeAndChannelBanks()
    {
        const string dmidecode = """
            # dmidecode 3.5

            Handle 0x0100, DMI type 17, 40 bytes
            Memory Device
                Locator: DIMM_A1
                Bank Locator: P0 Channel A
                Type: DDR5
                Speed: 4800 MT/s
                Size: 16 GB

            Handle 0x0101, DMI type 17, 40 bytes
            Memory Device
                Locator: DIMM_A2
                Bank Locator: P0 Channel A
                Type: DDR5
                Speed: 4800 MT/s
                Size: 16 GB

            Handle 0x0102, DMI type 17, 40 bytes
            Memory Device
                Locator: DIMM_B1
                Bank Locator: P0 Channel B
                Type: DDR5
                Speed: 4800 MT/s
                Size: 16 GB

            Handle 0x0103, DMI type 17, 40 bytes
            Memory Device
                Locator: DIMM_B2
                Bank Locator: P0 Channel B
                Type: DDR5
                Size: No Module Installed

            """;

        var (type, channels) = LocalHardwareProfileProvider.ParseDmidecodeMemory(dmidecode);

        Assert.Equal(SystemRamType.Ddr5, type);
        Assert.Equal(2, channels); // DIMM_A + DIMM_B → 2 channel banks
    }

    [Fact]
    public void ParseDmidecodeType_MapsTechnologyNames()
    {
        Assert.Equal(SystemRamType.Ddr5, LocalHardwareProfileProvider.ParseDmidecodeType("DDR5"));
        Assert.Equal(SystemRamType.Ddr4, LocalHardwareProfileProvider.ParseDmidecodeType("ddr4"));
        Assert.Equal(SystemRamType.Ddr3, LocalHardwareProfileProvider.ParseDmidecodeType("DDR3"));
        Assert.Equal(SystemRamType.LpDdr5X, LocalHardwareProfileProvider.ParseDmidecodeType("LPDDR5"));
        Assert.Equal(SystemRamType.Unknown, LocalHardwareProfileProvider.ParseDmidecodeType("Unknown"));
    }

    // ------------------------------------------------------------------
    //  FleetHardwareService: store, persistence, peer probes
    // ------------------------------------------------------------------

    private sealed class TempStateDir : IDisposable
    {
        public string Path { get; }

        public TempStateDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hw-fleet-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static OllamaRouterOptions TwoTargetOptions() => new()
    {
        Targets =
        [
            new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" },
            new OllamaTargetOptions { Name = "Peer", Url = "http://192.168.1.50:11435" }
        ]
    };

    private static FleetHardwareService CreateService(
        string stateDir,
        NodeHardwareProfile? localProfile = null)
    {
        var options = TwoTargetOptions();
        var localMock = new Mock<IHardwareProfileProvider>();
        localMock
            .Setup(l => l.GetLocalProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(localProfile ?? new NodeHardwareProfile(
                "local-node", "linux", "x64", false, 16384, SystemRamType.Ddr5,
                Array.Empty<GpuDeviceProfile>(), 16384, 38.4));

        var probeClient = new HttpClient(new ThrowingHandler()) { Timeout = TimeSpan.FromSeconds(3) };
        return new FleetHardwareService(
            MsOptions.Create(options),
            localMock.Object,
            probeClient,
            NullLogger<FleetHardwareService>.Instance,
            Path.Combine(stateDir, "targets-hardware.json"));
    }

    internal sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("peer unreachable (test)"));
    }

    [Fact]
    public async Task GetFleetHardware_LocalTargetIsServedFromProvider_PeerUnreachableIsFailSoft()
    {
        using var state = new TempStateDir();
        var service = CreateService(state.Path);

        var nodes = await service.GetFleetHardwareAsync();

        // Every effective target (Cloud excluded), configuration order.
        Assert.Equal(2, nodes.Count);

        var local = nodes[0];
        Assert.Equal("Local", local.Target);
        Assert.Equal("local", local.Source);
        Assert.True(local.Reachable);
        Assert.NotNull(local.Profile);

        var peer = nodes[1];
        Assert.Equal("Peer", peer.Target);
        Assert.False(peer.Reachable);
        Assert.Null(peer.Profile);
        Assert.Equal("none", peer.Source);
    }

    [Fact]
    public async Task UpsertProfile_StoresProfile_UnknownTargetReturnsFalse()
    {
        using var state = new TempStateDir();
        var service = CreateService(state.Path);
        var profile = new NodeHardwareProfile(
            "pushed-node", "linux", "x64", true, 98304, SystemRamType.LpDdr5X,
            Array.Empty<GpuDeviceProfile>(), 98304, 341.3);

        Assert.True(await service.UpsertProfileAsync("peer", profile));
        Assert.True(await service.UpsertProfileAsync("PEER", profile)); // case-insensitive
        Assert.False(await service.UpsertProfileAsync("NoSuchTarget", profile));

        Assert.Same(profile, service.GetProfile("peer"));
        Assert.Same(profile, service.GetProfile("PEER"));
        Assert.Null(service.GetProfile("NoSuchTarget"));
        Assert.Null(service.GetProfile(""));
    }

    [Fact]
    public async Task UpsertProfile_PersistsToStateFile_AndSurvivesRestart_WithoutPeerContact()
    {
        using var state = new TempStateDir();
        var stateFile = Path.Combine(state.Path, "targets-hardware.json");
        var profile = new NodeHardwareProfile(
            "pushed-node", "macos", "arm64", true, 36864, SystemRamType.AppleUnified,
            Array.Empty<GpuDeviceProfile>(), 36864, 153);

        var service1 = CreateService(state.Path);
        Assert.True(await service1.UpsertProfileAsync("Peer", profile));

        // The state file exists, contains the target key, and round-trips.
        Assert.True(File.Exists(stateFile));
        var raw = await File.ReadAllTextAsync(stateFile);
        Assert.Contains("Peer", raw);
        Assert.Contains("pushed-node", raw);

        // Restart with the same state directory: the persisted profile is served
        // WITHOUT any peer contact (the handler throws on any attempt).
        var service2 = CreateService(state.Path);
        var reloaded = service2.GetProfile("peer");
        Assert.NotNull(reloaded);
        Assert.Equal("pushed-node", reloaded.NodeName);
        Assert.Equal(SystemRamType.AppleUnified, reloaded.SystemRamType);

        // Corrupt file → log + start empty (failsafe).
        await File.WriteAllTextAsync(stateFile, "{ not valid json !!");
        var service3 = CreateService(state.Path);
        Assert.Null(service3.GetProfile("peer"));
    }

    [Fact]
    public async Task ManualEntriesAreAuthoritative_PeerRefreshNeverOverwritesThem()
    {
        using var state = new TempStateDir();
        var options = TwoTargetOptions();
        var stateFile = Path.Combine(state.Path, "targets-hardware.json");

        // Handler that SERVES a peer profile (200 + valid JSON).
        var servedProfile = new NodeHardwareProfile(
            "peer-reported", "linux", "x64", false, 32768, SystemRamType.Ddr4,
            Array.Empty<GpuDeviceProfile>(), 16384, 25.6);
        var handler = new CannedHandler(servedProfile);

        var localMock = new Mock<IHardwareProfileProvider>();
        localMock
            .Setup(l => l.GetLocalProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NodeHardwareProfile("local", "linux", "x64", false, 8192, SystemRamType.Ddr4,
                Array.Empty<GpuDeviceProfile>(), 8192, 25.6));

        var service = new FleetHardwareService(
            MsOptions.Create(options),
            localMock.Object,
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) },
            NullLogger<FleetHardwareService>.Instance,
            stateFile);

        var manualProfile = new NodeHardwareProfile(
            "manual-wins", "linux", "x64", false, 65536, SystemRamType.Ddr5,
            Array.Empty<GpuDeviceProfile>(), 32768, 38.4);
        Assert.True(await service.UpsertProfileAsync("Peer", manualProfile));

        var nodes = await service.GetFleetHardwareAsync();
        var peerNode = nodes.First(n => n.Target == "Peer");

        // The peer daemon answered, but the MANUAL profile stays authoritative.
        Assert.Equal("manual", peerNode.Source);
        Assert.Equal("manual-wins", peerNode.Profile!.NodeName);
        Assert.True(peerNode.Reachable);
        Assert.Same(manualProfile, service.GetProfile("Peer"));
    }

    [Fact]
    public async Task PeerProfileWithoutLocalEntry_IsStoredAsPeerAndPersisted()
    {
        using var state = new TempStateDir();
        var options = TwoTargetOptions();
        var stateFile = Path.Combine(state.Path, "targets-hardware.json");

        var servedProfile = new NodeHardwareProfile(
            "peer-reported", "linux", "x64", false, 32768, SystemRamType.Ddr4,
            Array.Empty<GpuDeviceProfile>(), 32768, 25.6);
        var handler = new CannedHandler(servedProfile);
        var localMock = new Mock<IHardwareProfileProvider>();
        localMock
            .Setup(l => l.GetLocalProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NodeHardwareProfile("local", "linux", "x64", false, 8192, SystemRamType.Ddr4,
                Array.Empty<GpuDeviceProfile>(), 8192, 25.6));

        var service = new FleetHardwareService(
            MsOptions.Create(options),
            localMock.Object,
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) },
            NullLogger<FleetHardwareService>.Instance,
            stateFile);

        var nodes = await service.GetFleetHardwareAsync();
        var peerNode = nodes.First(n => n.Target == "Peer");

        Assert.Equal("peer", peerNode.Source);
        Assert.True(peerNode.Reachable);
        Assert.Equal("peer-reported", peerNode.Profile!.NodeName);
        var storedPeer = service.GetProfile("Peer");
        Assert.NotNull(storedPeer);
        Assert.Equal("peer-reported", storedPeer.NodeName);
        Assert.Equal(SystemRamType.Ddr4, storedPeer.SystemRamType);
        Assert.True(File.Exists(stateFile));

        // Second read within the 5-min TTL: no extra probe is issued.
        var callsBefore = handler.RequestCount;
        var nodes2 = await service.GetFleetHardwareAsync();
        Assert.Equal(callsBefore, handler.RequestCount);
        Assert.Equal("peer-reported", nodes2.First(n => n.Target == "Peer").Profile!.NodeName);
    }

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly NodeHardwareProfile? _profile;
        private readonly System.Net.HttpStatusCode _statusCode;

        public CannedHandler(NodeHardwareProfile? profile, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
        {
            _profile = profile;
            _statusCode = statusCode;
        }

        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (_statusCode != System.Net.HttpStatusCode.OK || _profile is null)
            {
                return new HttpResponseMessage(_statusCode);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(
                _profile, OllamaRouter.Serialization.OllamaRouterJsonSerializerContext.Default.NodeHardwareProfile);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task UpsertProfile_NullOrEmptyArguments_ReturnFalse_WithoutPersisting()
    {
        using var state = new TempStateDir();
        var service = CreateService(state.Path);
        var profile = new NodeHardwareProfile("x", "linux", "x64", false, 1024, SystemRamType.Ddr4,
            Array.Empty<GpuDeviceProfile>(), 1024, 25.6);

        Assert.False(await service.UpsertProfileAsync("", profile));
        Assert.False(await service.UpsertProfileAsync("   ", profile));
        Assert.False(await service.UpsertProfileAsync("Peer", null!));

        Assert.Null(service.GetProfile("Peer"));
        Assert.False(File.Exists(Path.Combine(state.Path, "targets-hardware.json")));
    }

    [Fact]
    public async Task GetFleetHardware_ExpiredPeerProfile_UnreachablePeer_KeepsStoredProfileMarkedOffline()
    {
        using var state = new TempStateDir();
        var options = TwoTargetOptions();
        var stateFile = Path.Combine(state.Path, "targets-hardware.json");
        var storedProfile = new NodeHardwareProfile(
            "stored-peer", "linux", "x64", false, 32768, SystemRamType.Ddr4,
            Array.Empty<GpuDeviceProfile>(), 32768, 25.6);

        var localMock = new Mock<IHardwareProfileProvider>();
        localMock
            .Setup(l => l.GetLocalProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NodeHardwareProfile("local", "linux", "x64", false, 8192, SystemRamType.Ddr4,
                Array.Empty<GpuDeviceProfile>(), 8192, 25.6));

        var service = new FleetHardwareService(
            MsOptions.Create(options),
            localMock.Object,
            new HttpClient(new ThrowingHandler()) { Timeout = TimeSpan.FromSeconds(3) },
            NullLogger<FleetHardwareService>.Instance,
            stateFile);

        Assert.True(await service.UpsertProfileAsync("Peer", storedProfile));

        // First read: stored manual profile served immediately; the background peer refresh
        // fails (throwing handler) → the stored profile is kept but reachability is false.
        var nodes = await service.GetFleetHardwareAsync();
        var peerNode = nodes.First(n => n.Target == "Peer");

        Assert.Equal("stored-peer", peerNode.Profile!.NodeName);
        Assert.False(peerNode.Reachable);

        // Regression: the NEXT read within the 5-min TTL (no new probe issued) must keep
        // the last-observed-unreachable flag — a stored profile alone does not imply
        // the peer is alive.
        var nodesAgain = await service.GetFleetHardwareAsync();
        var peerAgain = nodesAgain.First(n => n.Target == "Peer");
        Assert.Equal("stored-peer", peerAgain.Profile!.NodeName);
        Assert.False(peerAgain.Reachable);
    }

    [Fact]
    public async Task PeerProbeNon200_ResponseTreatedAsUnreachable_WithoutStoring()
    {
        using var state = new TempStateDir();
        var options = TwoTargetOptions();
        var stateFile = Path.Combine(state.Path, "targets-hardware.json");
        var handler = new CannedHandler(null, System.Net.HttpStatusCode.InternalServerError);

        var localMock = new Mock<IHardwareProfileProvider>();
        localMock
            .Setup(l => l.GetLocalProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NodeHardwareProfile("local", "linux", "x64", false, 8192, SystemRamType.Ddr4,
                Array.Empty<GpuDeviceProfile>(), 8192, 25.6));

        var service = new FleetHardwareService(
            MsOptions.Create(options),
            localMock.Object,
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) },
            NullLogger<FleetHardwareService>.Instance,
            stateFile);

        var nodes = await service.GetFleetHardwareAsync();
        var peerNode = nodes.First(n => n.Target == "Peer");

        Assert.Equal("none", peerNode.Source);
        Assert.Null(peerNode.Profile);
        Assert.False(peerNode.Reachable);
        Assert.Null(service.GetProfile("Peer"));
    }

    [Fact]
    public async Task PeerProbeMalformedJson_TreatedAsUnreachable_WithoutThrowing()
    {
        using var state = new TempStateDir();
        var options = TwoTargetOptions();
        var stateFile = Path.Combine(state.Path, "targets-hardware.json");

        var localMock = new Mock<IHardwareProfileProvider>();
        localMock
            .Setup(l => l.GetLocalProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NodeHardwareProfile("local", "linux", "x64", false, 8192, SystemRamType.Ddr4,
                Array.Empty<GpuDeviceProfile>(), 8192, 25.6));

        var badHandler = new BadJsonHandler();
        var service = new FleetHardwareService(
            MsOptions.Create(options),
            localMock.Object,
            new HttpClient(badHandler) { Timeout = TimeSpan.FromSeconds(3) },
            NullLogger<FleetHardwareService>.Instance,
            stateFile);

        var nodes = await service.GetFleetHardwareAsync();
        var peerNode = nodes.First(n => n.Target == "Peer");

        Assert.Equal("none", peerNode.Source);
        Assert.Null(peerNode.Profile);
    }

    private sealed class BadJsonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{ not valid json !!", System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void GetProfile_CaseInsensitive_AndUnknownReturnsNull()
    {
        using var state = new TempStateDir();
        var service = CreateService(state.Path);
        var profile = new NodeHardwareProfile("x", "linux", "x64", false, 1024, SystemRamType.Ddr4,
            Array.Empty<GpuDeviceProfile>(), 1024, 25.6);

        Assert.Null(service.GetProfile("PEER")); // not stored yet
        service.UpsertProfileAsync("Peer", profile).GetAwaiter().GetResult();

        Assert.Same(profile, service.GetProfile("peer"));
        Assert.Same(profile, service.GetProfile("pEeR"));
        Assert.Null(service.GetProfile("  "));
        Assert.Null(service.GetProfile("NoSuchTarget"));
    }

    [Fact]
    public async Task LocalTargetManualEntry_IsPreservedByFleetRead()
    {
        using var state = new TempStateDir();
        var service = CreateService(state.Path);
        var manual = new NodeHardwareProfile(
            "manual-local", "linux", "x64", false, 65536, SystemRamType.Ddr5,
            Array.Empty<GpuDeviceProfile>(), 65536, 38.4);
        Assert.True(await service.UpsertProfileAsync("Local", manual));

        var nodes = await service.GetFleetHardwareAsync();
        var localNode = nodes.First(n => n.Target == "Local");

        // The local provider's profile is served for the row, but the manual store entry
        // must survive (GetProfile keeps the manual value, never overwritten by local).
        Assert.Same(manual, service.GetProfile("Local"));
        Assert.NotNull(localNode.Profile);
    }

    [Theory]
    [InlineData("http://192.168.1.20:11435", null, "http://192.168.1.20:11434")]
    [InlineData("http://192.168.1.20:11435/", null, "http://192.168.1.20:11434")]
    [InlineData("https://laptop.local:11435", null, "https://laptop.local:11434")]
    [InlineData("http://192.168.1.20:11435", "http://custom-router:9090", "http://custom-router:9090")]
    [InlineData("http://192.168.1.20:11435", "http://custom-router:9090/", "http://custom-router:9090")]
    [InlineData("http://aiserver:11434", null, null)]
    [InlineData("http://custom:12345", null, null)]
    [InlineData("", null, null)]
    public void ResolveRouterUrl_HonorsRouterUrlAndPort11435Fallback(string targetUrl, string? routerUrl, string? expected)
    {
        var target = new OllamaTargetOptions
        {
            Name = "TestTarget",
            Url = targetUrl,
            RouterUrl = routerUrl
        };

        var resolved = target.ResolveRouterUrl();
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public async Task GetFleetHardware_Non11435WithoutRouterUrl_DoesNotProbe()
    {
        using var state = new TempStateDir();
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" },
                new OllamaTargetOptions { Name = "DaemonOnly", Url = "http://aiserver:11434" }
            ]
        };

        var handler = new CannedHandler(new NodeHardwareProfile(
            "unreachable-server", "linux", "x64", false, 32768, SystemRamType.Ddr4,
            Array.Empty<GpuDeviceProfile>(), 32768, 25.6));

        var localMock = new Mock<IHardwareProfileProvider>();
        localMock
            .Setup(l => l.GetLocalProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NodeHardwareProfile("local", "linux", "x64", false, 8192, SystemRamType.Ddr4,
                Array.Empty<GpuDeviceProfile>(), 8192, 25.6));

        var service = new FleetHardwareService(
            MsOptions.Create(options),
            localMock.Object,
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) },
            NullLogger<FleetHardwareService>.Instance,
            Path.Combine(state.Path, "targets-hardware.json"));

        var nodes = await service.GetFleetHardwareAsync();
        var serverNode = nodes.First(n => n.Target == "DaemonOnly");

        // The handler was never queried because DaemonOnly has no RouterUrl and port != 11435
        Assert.Equal(0, handler.RequestCount);
        Assert.False(serverNode.Reachable);
        Assert.Null(serverNode.Profile);
        Assert.Equal("none", serverNode.Source);
    }

    [Fact]
    public async Task GetFleetHardware_ExplicitRouterUrl_QueriesSpecifiedEndpoint()
    {
        using var state = new TempStateDir();
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435" },
                new OllamaTargetOptions
                {
                    Name = "ServerWithRouter",
                    Url = "http://aiserver:11434",
                    RouterUrl = "http://aiserver:9999"
                }
            ]
        };

        var servedProfile = new NodeHardwareProfile(
            "server-node", "linux", "x64", false, 65536, SystemRamType.Ddr5,
            Array.Empty<GpuDeviceProfile>(), 65536, 38.4);
        var handler = new CannedHandler(servedProfile);

        var localMock = new Mock<IHardwareProfileProvider>();
        localMock
            .Setup(l => l.GetLocalProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NodeHardwareProfile("local", "linux", "x64", false, 8192, SystemRamType.Ddr4,
                Array.Empty<GpuDeviceProfile>(), 8192, 25.6));

        var service = new FleetHardwareService(
            MsOptions.Create(options),
            localMock.Object,
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) },
            NullLogger<FleetHardwareService>.Instance,
            Path.Combine(state.Path, "targets-hardware.json"));

        var nodes = await service.GetFleetHardwareAsync();
        var serverNode = nodes.First(n => n.Target == "ServerWithRouter");

        // The handler was queried exactly once via RouterUrl
        Assert.Equal(1, handler.RequestCount);
        Assert.True(serverNode.Reachable);
        Assert.Equal("peer", serverNode.Source);
        Assert.Equal("server-node", serverNode.Profile!.NodeName);
    }
}
