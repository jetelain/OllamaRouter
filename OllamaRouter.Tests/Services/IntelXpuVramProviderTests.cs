using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class IntelXpuVramProviderTests
{
    private const string XpuSmiArgs = "stat -d mem";
    private const string GpuTopArgs = "-L --no-color -s 1";

    [Fact]
    public void GetFreeVramMB_ParsesXpuSmiColumnarFixture_SingleDevice()
    {
        const string output =
            "XPU    MemUsed      MemSize      MemUsed%\n" +
            "0      1024 MB      16384 MB     6.25\n";
        var runner = new FakeProcessRunner().Succeeds("xpu-smi", XpuSmiArgs, output);

        var provider = new IntelXpuVramProvider(runner);

        Assert.Equal(16384 - 1024, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ParsesXpuSmi_SumsMultipleDeviceRows()
    {
        const string output =
            "XPU    MemUsed      MemSize      MemUsed%\n" +
            "0      1024 MB      16384 MB     6.25\n" +
            "1      2048 MB      16384 MB     12.5\n";
        var runner = new FakeProcessRunner().Succeeds("xpu-smi", XpuSmiArgs, output);

        var provider = new IntelXpuVramProvider(runner);

        Assert.Equal((16384 - 1024) + (16384 - 2048), provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_FallsBackToIntelGpuTop_WhenXpuSmiFailedToStart()
    {
        // xpu-smi absent; intel_gpu_top expected-kill path (TimedOut) with partial output.
        var runner = new FakeProcessRunner()
            .FailsToStart("xpu-smi", XpuSmiArgs)
            .TimesOut("intel_gpu_top", GpuTopArgs,
                "00:00:00     GPU0:     15%\nTotal: 447.3 MiB\nUsed: 5.319 MiB\n");

        var provider = new IntelXpuVramProvider(runner);

        // 447.3 → 447, 5.319 → 5 → 442
        Assert.Equal(447 - 5, provider.GetFreeVramMB());
        Assert.Equal(1, runner.InvocationCount("intel_gpu_top", GpuTopArgs));
    }

    [Fact]
    public void GetFreeVramMB_SkipsIntelGpuTop_WhenXpuSmiReturnsNonZero()
    {
        const string output = "XPU    MemUsed      MemSize      MemUsed%\n0      0 MB      8192 MB     0\n";
        var runner = new FakeProcessRunner()
            .Succeeds("xpu-smi", XpuSmiArgs, output)
            .TimesOut("intel_gpu_top", GpuTopArgs, "should not be used");

        var provider = new IntelXpuVramProvider(runner);

        Assert.Equal(8192, provider.GetFreeVramMB());
        Assert.Equal(0, runner.InvocationCount("intel_gpu_top", GpuTopArgs));
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenBothSourcesFail()
    {
        var runner = new FakeProcessRunner()
            .FailsToStart("xpu-smi", XpuSmiArgs)
            .FailsToStart("intel_gpu_top", GpuTopArgs);

        var provider = new IntelXpuVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenBothOutputsUnparseable()
    {
        var runner = new FakeProcessRunner()
            .Succeeds("xpu-smi", XpuSmiArgs, "no header here\n0 1 2\n")
            .TimesOut("intel_gpu_top", GpuTopArgs, "no numbers at all");

        var provider = new IntelXpuVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_AcceptsCleanExitFromIntelGpuTop()
    {
        var runner = new FakeProcessRunner()
            .FailsToStart("xpu-smi", XpuSmiArgs)
            .Succeeds("intel_gpu_top", GpuTopArgs, "Total: 1000.0 MiB\nUsed: 100.0 MiB\n");

        var provider = new IntelXpuVramProvider(runner);

        Assert.Equal(900, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ClampsNegativeFreeToZero()
    {
        var runner = new FakeProcessRunner()
            .FailsToStart("xpu-smi", XpuSmiArgs)
            .TimesOut("intel_gpu_top", GpuTopArgs, "Total: 100.0 MiB\nUsed: 300.0 MiB\n");

        var provider = new IntelXpuVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }
}
