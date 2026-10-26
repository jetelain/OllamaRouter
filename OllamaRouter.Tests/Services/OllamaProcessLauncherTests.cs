using OllamaRouter.Services;
using Xunit;

namespace OllamaRouter.Tests.Services;

public class OllamaProcessLauncherTests
{
    [Theory]
    [InlineData("http://127.0.0.1:11435", "127.0.0.1:11435")]
    [InlineData("http://127.0.0.1:11435/", "127.0.0.1:11435")]
    [InlineData("http://localhost", "localhost:80")]
    public void GetOllamaHost_ValidUrl_ReturnsHostAndPort(string localUrl, string expected)
    {
        Assert.Equal(expected, OllamaProcessLauncher.GetOllamaHost(localUrl));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    public void GetOllamaHost_InvalidUrl_ReturnsNull(string localUrl)
    {
        Assert.Null(OllamaProcessLauncher.GetOllamaHost(localUrl));
    }
}
