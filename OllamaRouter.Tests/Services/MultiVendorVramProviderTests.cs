using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class MultiVendorVramProviderTests
{
    [Fact]
    public void GetFreeVramMB_ReturnsFirstNonZero_DoesNotProbeLaterProviders()
    {
        var amd = new FakeVramProvider(2048);
        var intel = new FakeVramProvider(4096);
        var provider = new MultiVendorVramProvider(new IGpuVramProvider[]
        {
            new FakeVramProvider(1024), amd, intel
        });

        Assert.Equal(1024, provider.GetFreeVramMB());
        Assert.Equal(0, amd.Invocations);
        Assert.Equal(0, intel.Invocations);
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenAllProvidersReturnZero()
    {
        var provider = new MultiVendorVramProvider(new IGpuVramProvider[]
        {
            new FakeVramProvider(0), new FakeVramProvider(0), new FakeVramProvider(0)
        });

        Assert.Equal(0, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ReturnsSecondProviderValue_WhenFirstIsZero()
    {
        var intel = new FakeVramProvider(4096);
        var provider = new MultiVendorVramProvider(new IGpuVramProvider[]
        {
            new FakeVramProvider(0), new FakeVramProvider(2048), intel
        });

        Assert.Equal(2048, provider.GetFreeVramMB());
        Assert.Equal(0, intel.Invocations);
    }

    [Fact]
    public void GetFreeVramMB_ReturnsThirdProviderValue_WhenFirstTwoAreZero()
    {
        var provider = new MultiVendorVramProvider(new IGpuVramProvider[]
        {
            new FakeVramProvider(0), new FakeVramProvider(0), new FakeVramProvider(4096)
        });

        Assert.Equal(4096, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_RespectsOrderFromConstructorList()
    {
        var ascending = new MultiVendorVramProvider(new IGpuVramProvider[]
        {
            new FakeVramProvider(1024), new FakeVramProvider(2048), new FakeVramProvider(4096)
        });
        var descending = new MultiVendorVramProvider(new IGpuVramProvider[]
        {
            new FakeVramProvider(4096), new FakeVramProvider(2048), new FakeVramProvider(1024)
        });

        Assert.Equal(1024, ascending.GetFreeVramMB());
        Assert.Equal(4096, descending.GetFreeVramMB());
    }

    private sealed class FakeVramProvider : IGpuVramProvider
    {
        public FakeVramProvider(int freeMB) => FreeMB = freeMB;

        public int FreeMB { get; }
        public int Invocations { get; private set; }

        public int GetFreeVramMB()
        {
            Invocations++;
            return FreeMB;
        }
    }
}
