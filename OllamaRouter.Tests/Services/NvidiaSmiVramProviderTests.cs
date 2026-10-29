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

    [Fact]
    public async Task GetLocalGpuSnapshot_ReturnsDevicePerLine_WithSummedVram_WhenNvidiaSmiSucceeds()
    {
        var runner = new FakeProcessRunner().Succeeds(
            "nvidia-smi",
            "--query-gpu=name,memory.total --format=csv,noheader",
            "GeForce RTX 4080, 16384 MiB\nGeForce RTX 4080, 16384 MiB\n");

        var provider = new NvidiaSmiVramProvider(runner);

        var snapshot = await provider.GetLocalGpuSnapshotAsync();

        Assert.Equal("Nvidia", snapshot.Vendor);
        Assert.Equal(
            [
                "NVIDIA GeForce RTX 4080 (16384 MB)",
                "NVIDIA GeForce RTX 4080 (16384 MB)"
            ],
            snapshot.Devices);
        Assert.Equal(32768, snapshot.TotalVramMB);
    }

    [Fact]
    public async Task GetLocalGpuSnapshot_ReturnsUnknown_WhenFailedToStart()
    {
        var runner = new FakeProcessRunner().FailsToStart("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader");

        var provider = new NvidiaSmiVramProvider(runner);

        var snapshot = await provider.GetLocalGpuSnapshotAsync();

        Assert.Equal(LocalGpuSnapshot.Unknown, snapshot);
    }

    [Fact]
    public async Task GetLocalGpuSnapshot_ReturnsUnknown_WhenNonZeroExit()
    {
        var runner = new FakeProcessRunner().Fails("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader", 1, "GPU 0, 16384 MiB\n");

        var provider = new NvidiaSmiVramProvider(runner);

        var snapshot = await provider.GetLocalGpuSnapshotAsync();

        Assert.Equal(LocalGpuSnapshot.Unknown, snapshot);
    }

    [Fact]
    public async Task GetLocalGpuSnapshot_ReturnsUnknown_WhenStdoutEmptyOrUnparseable()
    {
        var empty = new FakeProcessRunner().Succeeds("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader", "");
        Assert.Equal(LocalGpuSnapshot.Unknown, await new NvidiaSmiVramProvider(empty).GetLocalGpuSnapshotAsync());

        var unparseable = new FakeProcessRunner().Succeeds("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader", "[N/A], [N/A]\n");
        Assert.Equal(LocalGpuSnapshot.Unknown, await new NvidiaSmiVramProvider(unparseable).GetLocalGpuSnapshotAsync());
    }

    [Fact]
    public async Task GetLocalGpuSnapshot_ReturnsUnknown_WhenTimedOut()
    {
        var runner = new FakeProcessRunner().TimesOut("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader", "GeForce RTX 4080, 16384");

        var provider = new NvidiaSmiVramProvider(runner);

        Assert.Equal(LocalGpuSnapshot.Unknown, await provider.GetLocalGpuSnapshotAsync());
    }

    [Fact]
    public async Task GetLocalGpuSnapshot_DoesNotDuplicateVendorPrefix_WhenStdoutAlreadyIncludesNvidia()
    {
        var runner = new FakeProcessRunner().Succeeds(
            "nvidia-smi",
            "--query-gpu=name,memory.total --format=csv,noheader",
            "NVIDIA GeForce RTX 4080 SUPER, 16376 MiB\n");

        var provider = new NvidiaSmiVramProvider(runner);

        var snapshot = await provider.GetLocalGpuSnapshotAsync();

        Assert.Equal("Nvidia", snapshot.Vendor);
        Assert.Equal(["NVIDIA GeForce RTX 4080 SUPER (16376 MB)"], snapshot.Devices);
        Assert.Equal(16376, snapshot.TotalVramMB);
    }
}
