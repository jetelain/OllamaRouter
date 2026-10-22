using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class WindowsVramProviderTests
{
    private const string WmicArgs = "path win32_videocard get AdapterRAM /format:list";
    private const string PowerShellArgs = "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_VideoController).AdapterRAM\"";

    [Fact]
    public void GetFreeVramMB_SumsAdapterRamBytesAcrossCards_FromWmicFixture()
    {
        // Two 4 GB cards: 4294967296 bytes each → 8192 MB total.
        const string output = "AdapterRAM=4294967296\n" +
            "\n" +
            "AdapterRAM=4294967296\n" +
            "\n";
        var runner = new FakeProcessRunner().Succeeds("wmic", WmicArgs, output);

        var provider = new WindowsVramProvider(runner);

        Assert.Equal(8192, provider.GetFreeVramMB());
        Assert.Equal(0, runner.InvocationCount("powershell", PowerShellArgs));
    }

    [Fact]
    public void GetFreeVramMB_FallsBackToPowerShell_WhenWmicFailedToStart()
    {
        const string output = "8589934592\n"; // 8 GB
        var runner = new FakeProcessRunner()
            .FailsToStart("wmic", WmicArgs)
            .Succeeds("powershell", PowerShellArgs, output);

        var provider = new WindowsVramProvider(runner);

        Assert.Equal(8192, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_CachesFirstSuccess_DoesNotInvokeRunnerAgain()
    {
        const string output = "AdapterRAM=4294967296\n";
        var runner = new FakeProcessRunner().Succeeds("wmic", WmicArgs, output);

        var provider = new WindowsVramProvider(runner);

        Assert.Equal(4096, provider.GetFreeVramMB());
        Assert.Equal(4096, provider.GetFreeVramMB());

        Assert.Equal(1, runner.InvocationCount("wmic", WmicArgs));
        Assert.Equal(0, runner.InvocationCount("powershell", PowerShellArgs));
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenBothCommandsFail_AndDoesNotCacheFailure()
    {
        var runner = new FakeProcessRunner()
            .FailsToStart("wmic", WmicArgs)
            .Fails("powershell", PowerShellArgs, 1);

        var provider = new WindowsVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());

        // Failure is not cached: the next call retries both commands.
        Assert.Equal(0, provider.GetFreeVramMB());

        Assert.Equal(2, runner.InvocationCount("wmic", WmicArgs));
        Assert.Equal(2, runner.InvocationCount("powershell", PowerShellArgs));
    }
}
