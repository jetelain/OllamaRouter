using OllamaRouter.App.Connection;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Security;
using Pmad.LocalApi.Security;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="IAppConnectionService"/> tests (task #102, spec §7): the default LOCAL profile
/// (daemon URL + key-reader wiring, no explicit key), the REMOTE switch (explicit key, no key
/// reader, previous client disposed, <c>ConnectionChanged</c> raised, the
/// <c>GetClient</c> accessor tracking) and validation.
/// </summary>
public sealed class AppConnectionServiceTests : IDisposable
{
    private readonly FakeRouterApiClient _client = new();
    private readonly FakeDaemonLauncher _launcher = new();
    private readonly IDefaultKeyReader _keyReader = new FakeDefaultKeyReader();
    private readonly List<AppConnectionHarness> _harnesses = [];

    [Fact]
    public void DefaultProfile_IsLocal_WithDaemonUrl_And_NoExplicitKey()
    {
        var harness = NewHarness();
        var service = harness.Service;

        Assert.Equal(RouterConnectionMode.Local, service.Profile.Mode);
        Assert.Equal(OllamaRouterDaemonLauncher.DefaultBaseUrl, service.Profile.BaseUrl);
        Assert.Null(service.Profile.ApiKey);
        Assert.Same(_keyReader, service.KeyReader);
        Assert.NotNull(service.DaemonLauncher);
        Assert.NotNull(service.Client);
        // Local client: the key reader is wired and the explicit key is NULL (no API-key
        // prompt; transparent default-key rotation per request).
        var captured = Assert.Single(harness.Clients);
        Assert.Same(_keyReader, captured.KeyReader);
        Assert.Null(captured.ApiKey);
        Assert.Equal(OllamaRouterDaemonLauncher.DefaultBaseUrl, captured.BaseUrl);
        // The local launcher is built with the daemon URL and the key reader.
        Assert.Equal(OllamaRouterDaemonLauncher.DefaultBaseUrl, _launcher.CapturedBaseUrl);
    }

    [Fact]
    public async Task GetClient_Accessor_TracksSwitches_WithoutRebuilding()
    {
        var harness = NewHarness();
        var service = harness.Service;
        var accessor = service.GetClient;

        Assert.Same(service.Client, accessor());

        await service.SwitchConnectionAsync(RemoteProfile());

        // The stable accessor keeps resolving the CURRENT client (tab trees never rebuild)…
        Assert.Same(service.Client, accessor());
        // …and the switch installed a client configured for the REMOTE profile
        // (explicit key, no key reader, the user URL).
        Assert.Equal(2, harness.Clients.Count);
        var captured = harness.Clients[^1];
        Assert.Equal("s3cret", captured.ApiKey);
        Assert.Null(captured.KeyReader);
        Assert.Equal(new Uri("http://192.168.1.50:11434/"), captured.BaseUrl);
    }

    [Fact]
    public async Task SwitchToRemote_UsesExplicitKey_DropsKeyReader_KeepsLauncherNull()
    {
        var harness = NewHarness();
        var service = harness.Service;

        var raised = 0;
        service.ConnectionChanged += () => raised++;
        await service.SwitchConnectionAsync(RemoteProfile());

        Assert.Equal(1, raised);
        Assert.Equal(RouterConnectionMode.Remote, service.Profile.Mode);
        Assert.Null(service.KeyReader);
        Assert.Null(service.DaemonLauncher);
        var captured = harness.Clients[^1];
        Assert.Equal("s3cret", captured.ApiKey);
        Assert.Null(captured.KeyReader);
        Assert.Equal(new Uri("http://192.168.1.50:11434/"), captured.BaseUrl);
    }

    [Fact]
    public async Task SwitchConnection_DisposesPreviousClient()
    {
        var harness = NewHarness();
        var service = harness.Service;

        var first = harness.Clients[^1];
        await service.SwitchConnectionAsync(RemoteProfile());

        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task SwitchToLocalProfile_DropsAnyExplicitKey()
    {
        var harness = NewHarness();
        var service = harness.Service;

        await service.SwitchConnectionAsync(new RouterConnectionProfile(
            RouterConnectionMode.Local, new Uri("http://10.0.0.9:9999/"), "should-be-dropped"));

        Assert.Equal(OllamaRouterDaemonLauncher.DefaultBaseUrl, service.Profile.BaseUrl);
        Assert.Null(service.Profile.ApiKey);
        Assert.Same(_keyReader, service.KeyReader);
    }

    [Fact]
    public async Task RemoteProfile_RequiresAbsoluteHttpUrl_AndNonEmptyKey()
    {
        var harness = NewHarness();
        var service = harness.Service;

        await Assert.ThrowsAsync<ArgumentException>(() => service.SwitchConnectionAsync(new RouterConnectionProfile(
            RouterConnectionMode.Remote, new Uri("ftp://192.168.1.50/"), "key")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SwitchConnectionAsync(new RouterConnectionProfile(
            RouterConnectionMode.Remote, new Uri("http://192.168.1.50/"), "  ")));
    }

    [Fact]
    public void Dispose_DisposesTheActiveClient()
    {
        var harness = NewHarness();
        var client = harness.Clients[^1];

        harness.Dispose();

        Assert.True(client.Disposed);
    }

    private static RouterConnectionProfile RemoteProfile() =>
        new(RouterConnectionMode.Remote, new Uri("http://192.168.1.50:11434/"), "s3cret");

    private AppConnectionHarness NewHarness()
    {
        var harness = new AppConnectionHarness(
            _keyReader,
            () => _client,
            () => _launcher);
        _harnesses.Add(harness);
        return harness;
    }

    public void Dispose()
    {
        foreach (var harness in _harnesses)
        {
            harness.Dispose();
        }
    }
}

/// <summary>A trivial <see cref="IDefaultKeyReader"/> double (returns a fixed key).</summary>
internal sealed class FakeDefaultKeyReader : IDefaultKeyReader
{
    /// <summary>The key to return (null = no key file).</summary>
    public string? Key { get; set; } = "default-key-1";

    /// <inheritdoc />
    public string KeyFilePath => "fake-default-key.dat";

    /// <inheritdoc />
    public string? ReadDefaultKey() => Key;
}
