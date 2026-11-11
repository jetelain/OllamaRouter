using System.Net;
using OllamaRouter.App.Connection;
using OllamaRouter.App.ViewModels;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Security;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="MainWindowViewModel"/> tests (task #102, spec §7): the startup auto-start
/// (EnsureRunningAsync exactly once in local mode), the status mapping (Running PID /
/// version / uptime / bind address, Stopped, Port Conflict with Start disabled + the
/// foreign-owner hint), the Start/Stop/Restart command sequences on the fake launcher, the
/// remote mode (daemon commands disabled, 401 → key-locked detail) and the remote-connect
/// validation.
/// </summary>
public sealed class MainWindowViewModelTests : IAsyncLifetime
{
    private readonly FakeRouterApiClient _client = new();
    private readonly FakeDaemonLauncher _launcher = new();
    private readonly IDefaultKeyReader _keyReader = new FakeDefaultKeyReader();
    private readonly AppConnectionHarness _harness;

    private MainWindowViewModel? _viewModel;

    public MainWindowViewModelTests()
    {
        _harness = new AppConnectionHarness(_keyReader, () => _client, () => _launcher);
    }

    /// <summary>The poll delay gate: ticks run, the loop then parks until disposal — no
    /// test ever sleeps or spins.</summary>
    private static Task GatedDelay(TimeSpan span, CancellationToken ct) =>
        new Task(static () => { }, ct);

    [Fact]
    public async Task Startup_LocalMode_CallsEnsureRunningExactlyOnce_AndShowsTelemetry()
    {
        var viewModel = CreateViewModel();
        _launcher.Status = FakeDaemonLauncher.RunningStatus(processId: 4242, version: "9.9.9", uptimeSeconds: 95);

        await viewModel.StartAsync();

        Assert.Single(_launcher.Calls, c => c == "EnsureRunningAsync");
        Assert.Equal("Running", viewModel.StatusText);
        Assert.Contains("PID 4242", viewModel.StatusDetail);
        Assert.Contains("v9.9.9", viewModel.StatusDetail);
        Assert.Contains("1m 35s", viewModel.StatusDetail);
        Assert.Contains(OllamaRouterDaemonLauncher.DefaultBaseUrl.ToString(), viewModel.StatusDetail);
    }

    [Fact]
    public async Task StoppedDaemon_SurfacesStopped_WithoutAutoRelaunchingOnRefresh()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(); // auto-starts (EnsureRunningAsync launches it)
        await viewModel.StopDaemonCommand.ExecuteAsync(null);
        Assert.Equal("Stopped", viewModel.StatusText);

        // The manual re-probe (GetStatusAsync) NEVER launches — only (re)connect/start do.
        await viewModel.RefreshStatusCommand.ExecuteAsync(null);

        Assert.Equal("Stopped", viewModel.StatusText);
        Assert.Contains(OllamaRouterDaemonLauncher.DefaultBaseUrl.ToString(), viewModel.StatusDetail);
        Assert.Single(_launcher.Calls, c => c == "StartAsync");
    }

    [Fact]
    public async Task Startup_LocalMode_PortConflict_DisablesStart_AndShowsForeignOwnerHint()
    {
        var viewModel = CreateViewModel();
        _launcher.Status = new RouterDaemonStatus(
            RouterDaemonState.PortOccupiedByOtherService, null, "non-router payload on :11434");

        await viewModel.StartAsync();

        Assert.Equal("Port Conflict", viewModel.StatusText);
        Assert.Contains("non-router payload", viewModel.StatusDetail);
        Assert.Contains("never launches against or kills", viewModel.StatusDetail);
        Assert.Contains("11434", viewModel.StatusDetail);
        Assert.False(viewModel.StartDaemonCommand.CanExecute(null));
        // Auto-start must NOT have launched against the foreign port.
        Assert.DoesNotContain("StartAsync", _launcher.Calls);
    }

    [Fact]
    public async Task StartDaemonCommand_LaunchesTheStoppedDaemon()
    {
        var viewModel = CreateViewModel();
        _launcher.Status = FakeDaemonLauncher.RunningStatus();

        await viewModel.StartAsync();
        _launcher.Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null);

        await viewModel.StartDaemonCommand.ExecuteAsync(null);

        Assert.Contains("StartAsync", _launcher.Calls);
        Assert.Equal("Running", viewModel.StatusText);
    }

    [Fact]
    public async Task StopDaemonCommand_StopsTheRunningDaemon()
    {
        var viewModel = CreateViewModel();
        _launcher.Status = FakeDaemonLauncher.RunningStatus();

        await viewModel.StartAsync();

        await viewModel.StopDaemonCommand.ExecuteAsync(null);

        Assert.Contains("StopAsync", _launcher.Calls);
        Assert.Equal("Stopped", viewModel.StatusText);
    }

    [Fact]
    public async Task RestartDaemonCommand_StopsThenStartsSequentially()
    {
        var viewModel = CreateViewModel();
        _launcher.Status = FakeDaemonLauncher.RunningStatus();

        await viewModel.StartAsync();

        await viewModel.RestartDaemonCommand.ExecuteAsync(null);

        var calls = _launcher.Calls.Where(c => c is "StopAsync" or "StartAsync").ToList();
        Assert.Equal(new[] { "StopAsync", "StartAsync" }, calls);
        Assert.Equal("Running", viewModel.StatusText);
    }

    [Fact]
    public async Task RemoteMode_DisablesDaemonCommands_AndProbesTheClient()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync();

        await _harness.Service.SwitchConnectionAsync(new RouterConnectionProfile(
            RouterConnectionMode.Remote, new Uri("http://192.168.1.50:11434/"), "key"));
        _client.InfoResponse = FakeDaemonLauncher.RunningStatus(77, "7.7.7", 60).Info!;
        await viewModel.RefreshStatusCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsLocalMode);
        Assert.False(viewModel.StartDaemonCommand.CanExecute(null));
        Assert.False(viewModel.StopDaemonCommand.CanExecute(null));
        Assert.False(viewModel.RestartDaemonCommand.CanExecute(null));
        Assert.Equal("Remote", viewModel.StatusText);
        Assert.Contains("PID 77", viewModel.StatusDetail);
        Assert.Contains("http://192.168.1.50:11434/", viewModel.StatusDetail);
    }

    [Fact]
    public async Task RemoteMode_401_SurfacesTheKeyLockedDetail()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync();
        await _harness.Service.SwitchConnectionAsync(new RouterConnectionProfile(
            RouterConnectionMode.Remote, new Uri("http://192.168.1.50:11434/"), "bad-key"));

        _client.InfoException = new OllamaRouterApiException(
            HttpStatusCode.Unauthorized, "authorization required"); // sticky: the background +
        // the explicit refresh both observe the 401 deterministically
        await viewModel.RefreshStatusCommand.ExecuteAsync(null);

        Assert.Equal("Remote", viewModel.StatusText);
        Assert.Contains("key-locked", viewModel.StatusDetail);
    }

    [Fact]
    public async Task ConnectRemoteCommand_ValidatesUrlAndKey_BeforeSwitching()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync();

        viewModel.RemoteUrl = "not-a-url";
        viewModel.RemoteApiKey = "key";
        await viewModel.ConnectRemoteCommand.ExecuteAsync(null);
        Assert.Equal(RouterConnectionMode.Local, _harness.Service.Profile.Mode);

        viewModel.RemoteUrl = "http://192.168.1.50:11434";
        viewModel.RemoteApiKey = "  ";
        await viewModel.ConnectRemoteCommand.ExecuteAsync(null);
        Assert.Equal(RouterConnectionMode.Local, _harness.Service.Profile.Mode);

        viewModel.RemoteUrl = "http://192.168.1.50:11434";
        viewModel.RemoteApiKey = "key";
        _client.InfoResponse = FakeDaemonLauncher.RunningStatus().Info!;
        await viewModel.ConnectRemoteCommand.ExecuteAsync(null);

        Assert.Equal(RouterConnectionMode.Remote, _harness.Service.Profile.Mode);
        Assert.Equal("Remote", viewModel.StatusText);
    }

    private MainWindowViewModel CreateViewModel()
    {
        // The harness factories install THIS fake client / launcher as the service's active
        // ones (the app's GetClient accessor resolves to them on every call).
        var connection = _harness.Service;
        Assert.Same(_client, connection.Client);
        Assert.Same(_launcher, connection.DaemonLauncher);
        _viewModel = new MainWindowViewModel(
            connection,
            new MonitorViewModel(() => _client, GatedDelay),
            new FleetTargetsViewModel(() => _client, GatedDelay),
            new CartographyViewModel(() => _client, GatedDelay),
            new SettingsViewModel(() => _client, GatedDelay));
        return _viewModel;
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_viewModel is not null)
        {
            await _viewModel.DisposeAsync();
        }

        _harness.Dispose();
    }
}
