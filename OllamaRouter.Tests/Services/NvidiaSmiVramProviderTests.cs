using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class NvidiaSmiVramProviderTests
{
    [Fact]
    public void GetFreeVramMB_ReturnsFixtureValue_OnCannedStdout()
    {
        var runner = new FakeProcessRunner().Succeeds("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits", "12345\n");

        var provider = new NvidiaSmiVramProvider(runner);

        Assert.Equal(12345, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_TrimWhitespace_IgnoresExtraCsvRows()
    {
        var runner = new FakeProcessRunner().Succeeds("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits", "  4096  \n8192\n");

        var provider = new NvidiaSmiVramProvider(runner);

        Assert.Equal(4096, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenFailedToStart()
    {
        var runner = new FakeProcessRunner().FailsToStart("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits");

        var provider = new NvidiaSmiVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenNonZeroExit()
    {
        var runner = new FakeProcessRunner().Fails("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits", 1, "12345\n");

        var provider = new NvidiaSmiVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenStdoutUnparseable()
    {
        var runner = new FakeProcessRunner().Succeeds("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits", "[N/A]\n");

        var provider = new NvidiaSmiVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenStdoutEmpty()
    {
        var runner = new FakeProcessRunner().Succeeds("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits", "");

        var provider = new NvidiaSmiVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }
}
