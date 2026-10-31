using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class TargetLocationClassifierTests
{
    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://LOCALHOST:11434")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://[::1]:11434")]
    [InlineData("http://0.0.0.0:11434")]
    [InlineData("http://[::]:11434")]
    public void ClassifyUrl_LoopbackHosts_AreLocal(string url)
    {
        Assert.Equal(TargetLocation.Local, TargetLocationClassifier.ClassifyUrl(url));
    }

    [Theory]
    [InlineData("http://10.0.0.5:11434")]
    [InlineData("http://172.16.0.1:11434")]
    [InlineData("http://172.31.255.255:11434")]
    [InlineData("http://192.168.1.25:11434")]
    [InlineData("http://[fd12:3456::1]:11434")]   // IPv6 ULA fc00::/7
    [InlineData("http://[fc00::abcd]:11434")]     // IPv6 ULA fc00::/7
    [InlineData("http://[fe80::1]:11434")]        // link-local fe80::/10
    [InlineData("http://[febf:0:0:0::1]:11434")]  // link-local fe80::/10 (top of range)
    [InlineData("http://[fdff:ffff::1]:11434")]   // IPv6 ULA fc00::/7 (top of range)
    [InlineData("http://home.local:11434")]
    [InlineData("http://aiserver:11434")]         // single-label hostname
    [InlineData("http://aiserver.tailff3b9f.ts.net:11434")]
    public void ClassifyUrl_LanHosts_AreRemote(string url)
    {
        Assert.Equal(TargetLocation.Remote, TargetLocationClassifier.ClassifyUrl(url));
    }

    [Theory]
    [InlineData("http://100.64.0.1:11434")]       // CGNAT
    [InlineData("http://11.0.0.1:11434")]
    [InlineData("http://172.32.0.1:11434")]
    [InlineData("http://172.15.0.1:11434")]
    [InlineData("http://192.169.0.1:11434")]
    [InlineData("http://8.8.8.8:11434")]
    [InlineData("http://[2001:db8::1]:11434")]    // public IPv6
    [InlineData("http://[fec0::1]:11434")]        // above link-local fe80::/10 range
    [InlineData("http://example.com:11434")]
    [InlineData("http://aiserver.internal:11434")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("http://")]
    public void ClassifyUrl_PublicOrInvalidHosts_AreCloud(string? url)
    {
        Assert.Equal(TargetLocation.Cloud, TargetLocationClassifier.ClassifyUrl(url));
    }

    [Fact]
    public void Classify_OverrideWinsOverUrl()
    {
        var options = new OllamaTargetOptions
        {
            Url = "http://example.com:11434",
            Location = TargetLocation.Remote
        };

        Assert.Equal(TargetLocation.Remote, TargetLocationClassifier.Classify(options));
    }

    [Fact]
    public void Classify_OverrideToCloudWinsOverLoopbackUrl()
    {
        var options = new OllamaTargetOptions
        {
            Url = "http://127.0.0.1:11434",
            Location = TargetLocation.Cloud
        };

        Assert.Equal(TargetLocation.Cloud, TargetLocationClassifier.Classify(options));
    }

    [Fact]
    public void Classify_NullOverride_FallsBackToUrl()
    {
        var options = new OllamaTargetOptions { Url = "http://127.0.0.1:11434" };

        Assert.Equal(TargetLocation.Local, TargetLocationClassifier.Classify(options));
    }

    [Fact]
    public void Classify_NullOptions_IsCloud()
    {
        Assert.Equal(TargetLocation.Cloud, TargetLocationClassifier.Classify(null));
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("http://0.0.0.0:11434", true)]
    [InlineData("http://[::]:11434", true)]
    [InlineData("http://192.168.1.5:11434", false)]
    [InlineData("http://example.com:11434", false)]
    [InlineData(null, false)]
    [InlineData("not a url", false)]
    public void IsLoopbackUrl_ReturnsExpectedValue(string? url, bool expected)
    {
        Assert.Equal(expected, TargetLocationClassifier.IsLoopbackUrl(url));
    }

    [Theory]
    [InlineData(TargetLocation.Local, "local")]
    [InlineData(TargetLocation.Remote, "remote")]
    [InlineData(TargetLocation.Cloud, "cloud")]
    public void ToWireValue_ReturnsLowercaseWireValue(TargetLocation location, string expected)
    {
        Assert.Equal(expected, TargetLocationClassifier.ToWireValue(location));
    }
}
