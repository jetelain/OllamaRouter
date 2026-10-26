using OllamaRouter.Services;
using Xunit;

namespace OllamaRouter.Tests.Services;

public class AppleUnifiedMemoryVramProviderTests
{
    // 16 KB pages: 64 pages = 1 MB.
    private const string VmStatOutput = """
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
        Assert.True(AppleUnifiedMemoryVramProvider.TryParseVmStat(VmStatOutput, out var stats));

        Assert.Equal(16384, stats.PageSize);
        Assert.Equal(64000, stats.FreePages);
        Assert.Equal(128000, stats.InactivePages);
        Assert.Equal(6400, stats.SpeculativePages);
        Assert.Equal(128000, stats.WiredPages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Pages free: 100.")]
    [InlineData("Mach Virtual Memory Statistics: (page size of 16384 bytes)\nPages active: 100.")]
    public void TryParseVmStat_InvalidOutput_ReturnsFalse(string output)
    {
        Assert.False(AppleUnifiedMemoryVramProvider.TryParseVmStat(output, out _));
    }

    [Theory]
    [InlineData(24576, 16384)]
    [InlineData(36864, 24576)]
    [InlineData(65536, 49152)]
    public void GetDefaultGpuLimitMB_UsesTwoThirdsUpTo36GbAndThreeQuartersAbove(long totalMB, long expectedMB)
    {
        Assert.Equal(expectedMB, AppleUnifiedMemoryVramProvider.GetDefaultGpuLimitMB(totalMB));
    }

    [Fact]
    public void ComputeFreeVramMB_AvailableMemoryIsTheLimit_ReturnsAvailableMemory()
    {
        // Available: 1000 + 2000 + 100 = 3100 MB; GPU budget: 16384 - 2000 wired = 14384 MB.
        Assert.Equal(3100, AppleUnifiedMemoryVramProvider.ComputeFreeVramMB(24576, 0, VmStatOutput));
    }

    [Fact]
    public void ComputeFreeVramMB_GpuBudgetIsTheLimit_ReturnsRemainingGpuBudget()
    {
        // Explicit iogpu.wired_limit_mb of 4000 MB minus 2000 MB already wired.
        Assert.Equal(2000, AppleUnifiedMemoryVramProvider.ComputeFreeVramMB(24576, 4000, VmStatOutput));
    }

    [Fact]
    public void ComputeFreeVramMB_WiredAboveLimit_ReturnsZero()
    {
        Assert.Equal(0, AppleUnifiedMemoryVramProvider.ComputeFreeVramMB(24576, 1000, VmStatOutput));
    }

    [Fact]
    public void ComputeFreeVramMB_InvalidInput_ReturnsZero()
    {
        Assert.Equal(0, AppleUnifiedMemoryVramProvider.ComputeFreeVramMB(0, 0, VmStatOutput));
        Assert.Equal(0, AppleUnifiedMemoryVramProvider.ComputeFreeVramMB(24576, 0, "garbage"));
    }

    [Fact]
    public void GetFreeVramMB_OnMacOS_ReturnsPositiveValue()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.True(new AppleUnifiedMemoryVramProvider().GetFreeVramMB() > 0);
    }
}
