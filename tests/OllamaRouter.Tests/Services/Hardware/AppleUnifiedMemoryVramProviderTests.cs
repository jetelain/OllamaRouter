using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Tests.Services.Hardware;

/// <summary>
/// Unit tests for <see cref="AppleUnifiedMemoryVramProvider"/>: <c>vm_stat</c> parsing, the
/// default GPU-budget heuristic, the free-VRAM computation, and the end-to-end probe flow
/// through a <see cref="FakeProcessRunner"/> (no real process launches, no network).
/// </summary>
public class AppleUnifiedMemoryVramProviderTests
{
    // 16 KB pages: wired = 128000 * 16384 B = exactly 2000 MB;
    // available = (64000 + 128000 + 6400) * 16384 B = exactly 3100 MB.
    private const string VmStatFixture = """
        Mach Virtual Memory Statistics: (page size of 16384 bytes)
        Pages free:                                    64000.
        Pages active:                                 455078.
        Pages inactive:                               128000.
        Pages speculative:                              6400.
        Pages throttled:                                   0.
        Pages wired down:                             128000.
        Pages purgeable:                               74010.
        "Translation faults":                       20847363.
        Pages copy-on-write:                          337937.
        """;

    [Fact]
    public void TryParseVmStat_ValidOutput_ParsesPageSizeAndCounts()
    {
        var parsed = AppleUnifiedMemoryVramProvider.TryParseVmStat(VmStatFixture, out var stats);

        Assert.True(parsed);
        Assert.Equal((16384, 64000, 128000, 6400, 128000),
            (stats.PageSize, stats.FreePages, stats.InactivePages, stats.SpeculativePages, stats.WiredPages));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Pages free: 100.")]
    [InlineData("Mach Virtual Memory Statistics: (page size of 16384 bytes)\nPages active: 455078.")]
    public void TryParseVmStat_InvalidOutput_ReturnsFalse(string output)
    {
        var parsed = AppleUnifiedMemoryVramProvider.TryParseVmStat(output, out var stats);

        Assert.False(parsed);
        Assert.Equal(default, stats);
    }

    [Theory]
    [InlineData(24576, 16384)]   // 24 GB: 2/3 of RAM
    [InlineData(36864, 24576)]   // exactly 36 GB: 2/3 (threshold is strict >)
    [InlineData(65536, 49152)]   // 64 GB: 3/4 of RAM
    public void GetDefaultGpuLimitMB_UsesTwoThirdsUpTo36GbAndThreeQuartersAbove(long totalMB, long expected)
    {
        Assert.Equal(expected, AppleUnifiedMemoryVramProvider.GetDefaultGpuLimitMB(totalMB));
    }

    [Fact]
    public void ComputeFreeVramMB_AvailableMemoryIsTheLimit_ReturnsAvailableMemory()
    {
        // Default 2/3 budget on 24 GB = 16384 MB; 16384 - 2000 wired = 14384 > 3100 available.
        Assert.Equal(3100, AppleUnifiedMemoryVramProvider.ComputeFreeVramMB(24576, 0, VmStatFixture));
    }

    [Fact]
    public void ComputeFreeVramMB_GpuBudgetIsTheLimit_ReturnsRemainingGpuBudget()
    {
        // Explicit 4000 MB limit; 4000 - 2000 wired = 2000 < 3100 available.
        Assert.Equal(2000, AppleUnifiedMemoryVramProvider.ComputeFreeVramMB(24576, 4000, VmStatFixture));
    }

    [Fact]
    public void ComputeFreeVramMB_WiredAboveLimit_ReturnsZero()
    {
        // Explicit 1000 MB limit; 1000 - 2000 wired < 0, clamped to 0.
        Assert.Equal(0, AppleUnifiedMemoryVramProvider.ComputeFreeVramMB(24576, 1000, VmStatFixture));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(24576, 0)]
    public void ComputeFreeVramMB_InvalidInput_ReturnsZero(long totalMB, long wiredLimitMB)
    {
        Assert.Equal(0, AppleUnifiedMemoryVramProvider.ComputeFreeVramMB(totalMB, wiredLimitMB, totalMB <= 0 ? VmStatFixture : "garbage"));
    }

    [Fact]
    public void GetFreeVramMB_FakeRunner_ComputesFromUnifiedMemory()
    {
        var runner = new FakeProcessRunner()
            .Succeeds("sysctl", "-n hw.memsize", "25769803776")   // 24 GB = 24576 MB
            .FailsToStart("sysctl", "-n iogpu.wired_limit_mb")   // key unset on macOS => default 2/3 heuristic
            .Succeeds("vm_stat", "", VmStatFixture);
        var provider = new AppleUnifiedMemoryVramProvider(runner);

        Assert.Equal(3100, provider.GetFreeVramMB());

        var invocations = runner.GetInvocations();
        Assert.Equal(
            new (string, string, int, string?)[]
            {
                ("sysctl", "-n hw.memsize", 5000, null),
                ("sysctl", "-n iogpu.wired_limit_mb", 5000, null),
                ("vm_stat", "", 5000, null)
            },
            invocations);
    }

    [Fact]
    public void GetFreeVramMB_ExplicitWiredLimit_IsTheBudgetCeiling()
    {
        var runner = new FakeProcessRunner()
            .Succeeds("sysctl", "-n hw.memsize", "25769803776")
            .Succeeds("sysctl", "-n iogpu.wired_limit_mb", "4000")
            .Succeeds("vm_stat", "", VmStatFixture);
        var provider = new AppleUnifiedMemoryVramProvider(runner);

        Assert.Equal(2000, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_SysctlMemsizeFails_ReturnsZero()
    {
        var runner = new FakeProcessRunner()
            .Fails("sysctl", "-n hw.memsize", 1);
        var provider = new AppleUnifiedMemoryVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
        Assert.Equal(1, runner.InvocationCount("sysctl", "-n hw.memsize"));
    }

    [Fact]
    public void GetFreeVramMB_VmStatFails_ReturnsZero()
    {
        var runner = new FakeProcessRunner()
            .Succeeds("sysctl", "-n hw.memsize", "25769803776")
            .FailsToStart("sysctl", "-n iogpu.wired_limit_mb")
            .FailsToStart("vm_stat", "");
        var provider = new AppleUnifiedMemoryVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }
}
