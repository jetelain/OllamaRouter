using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class RocmSmiVramProviderTests
{
    private const string Args = "--showmeminfo vram --json";

    [Fact]
    public void GetFreeVramMB_ParsesVramFreeFromJsonFixture()
    {
        // 11790300 KB → 11514 MB (11790300 / 1024)
        var json = "{\"card0\":{\"vram_total\":\"23580599\",\"vram_used\":\"11790299\",\"vram_free\":\"11790300\"}}";
        var runner = new FakeProcessRunner().Succeeds("rocm-smi", Args, json);

        var provider = new RocmSmiVramProvider(runner);

        Assert.Equal(11790300 / 1024, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_UsesTotalMinusUsed_WhenVramFreeAbsent()
    {
        // (23580599 - 11790299) = 11790300 KB → 11514 MB
        var json = "{\"card0\":{\"vram_total\":\"23580599\",\"vram_used\":\"11790299\"}}";
        var runner = new FakeProcessRunner().Succeeds("rocm-smi", Args, json);

        var provider = new RocmSmiVramProvider(runner);

        Assert.Equal((23580599 - 11790299) / 1024, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_SumsFreeAcrossCards()
    {
        var json = "{\"card0\":{\"vram_free\":\"1048576\"},\"card1\":{\"vram_free\":\"2097152\"}}";
        var runner = new FakeProcessRunner().Succeeds("rocm-smi", Args, json);

        var provider = new RocmSmiVramProvider(runner);

        Assert.Equal((1048576 + 2097152) / 1024, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenFailedToStart()
    {
        var runner = new FakeProcessRunner().FailsToStart("rocm-smi", Args);

        var provider = new RocmSmiVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenNonZeroExit()
    {
        var runner = new FakeProcessRunner().Fails("rocm-smi", Args, 1);

        var provider = new RocmSmiVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenMalformedJson()
    {
        var runner = new FakeProcessRunner().Succeeds("rocm-smi", Args, "not json at all {");

        var provider = new RocmSmiVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenEmptyJsonObject()
    {
        var runner = new FakeProcessRunner().Succeeds("rocm-smi", Args, "{}");

        var provider = new RocmSmiVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_ReturnsZero_WhenComputedValueNegative_Clamped()
    {
        // used > total → clamped to 0 per card
        var json = "{\"card0\":{\"vram_total\":\"100\",\"vram_used\":\"200\"}}";
        var runner = new FakeProcessRunner().Succeeds("rocm-smi", Args, json);

        var provider = new RocmSmiVramProvider(runner);

        Assert.Equal(0, provider.GetFreeVramMB());
    }

    [Fact]
    public void GetFreeVramMB_HandlesNumericJsonValues()
    {
        // values may come as JSON numbers rather than strings
        var json = "{\"card0\":{\"vram_free\":1048576}}";
        var runner = new FakeProcessRunner().Succeeds("rocm-smi", Args, json);

        var provider = new RocmSmiVramProvider(runner);

        Assert.Equal(1024, provider.GetFreeVramMB());
    }
}
