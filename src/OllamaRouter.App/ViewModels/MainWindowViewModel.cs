using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.Connection;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Endpoints.RouterApi.Info;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// The main window view model (task #102, spec §3): the connection bar (daemon status /
/// PID / version / uptime / bind address, Start/Stop/Restart in local mode only, remote
/// connect with a session-only key), the tab ownership and the polling orchestration.
/// <para>
/// Startup (local): <c>EnsureRunningAsync</c> (never throws on a foreign port — returns
/// <c>PortOccupiedByOtherService</c>) then the status probe; the client authenticates
/// seamlessly through the <c>DefaultKeyReader</c> (NO API-key prompt). Remote: no daemon
/// management; the probe surfaces 401 as a key-locked hint.
/// </para>
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAppConnectionService _connection;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Creates the main view model over the composition-root connection service and the four
    /// tab view models.
    /// </summary>
    public MainWindowViewModel(
        IAppConnectionService connection,
        MonitorViewModel monitor,
        FleetTargetsViewModel fleet,
        CartographyViewModel cartography,
        SettingsViewModel settings)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        MonitorTab = monitor ?? throw new ArgumentNullException(nameof(monitor));
        FleetTab = fleet ?? throw new ArgumentNullException(nameof(fleet));
        CartographyTab = cartography ?? throw new ArgumentNullException(nameof(cartography));
        SettingsTab = settings ?? throw new ArgumentNullException(nameof(settings));
        _connection.ConnectionChanged += OnConnectionChanged;
    }

    /// <summary>The Monitor &amp; Savings tab.</summary>
    public MonitorViewModel MonitorTab { get; }

    /// <summary>The Fleet &amp; Cloud Targets tab.</summary>
    public FleetTargetsViewModel FleetTab { get; }

    /// <summary>The Model Cartography &amp; Preload tab.</summary>
    public CartographyViewModel CartographyTab { get; }

    /// <summary>The Settings &amp; API Keys tab.</summary>
    public SettingsViewModel SettingsTab { get; }

    /// <summary>The connection bar status text
    /// ("Running" / "Stopped" / "Port Conflict" / "Remote" / "Disconnected").</summary>
    [ObservableProperty]
    private string _statusText = "Connecting\u2026";

    /// <summary>The connection bar detail (PID, version, uptime, bind address, or the
    /// port-conflict / key-locked hint).</summary>
    [ObservableProperty]
    private string _statusDetail = string.Empty;

    /// <summary>Whether the port is foreign-owned (Start disabled — the launcher never
    /// launches against or kills a foreign port owner).</summary>
    [ObservableProperty]
    private bool _isPortConflict;

    /// <summary>Whether a daemon/connect command is in flight (busy guard).</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Whether the active profile is Local (daemon management enabled).</summary>
    public bool IsLocalMode => _connection.Profile.Mode == RouterConnectionMode.Local;

    /// <summary>The connection base URL (bind address) of the active profile.</summary>
    public string BaseUrl => _connection.Profile.BaseUrl.ToString();

    /// <summary>The remote-connect form: the router URL (absolute http(s)).</summary>
    [ObservableProperty]
    private string _remoteUrl = string.Empty;

    /// <summary>The remote-connect form: the API key (held in MEMORY FOR THE SESSION ONLY —
    /// never persisted to disk).</summary>
    [ObservableProperty]
    private string _remoteApiKey = string.Empty;

    /// <summary>The selected tab index.</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    // -- Commands --------------------------------------------------------------

    /// <summary>Re-probes the daemon (local) or the router (remote) status.</summary>
    [RelayCommand(CanExecute = nameof(CanRunCommand))]
    private Task RefreshStatusAsync(CancellationToken ct = default)
    {
        return RunGuardedAsync(RefreshStatusCoreAsync, ct);
    }

    /// <summary>
    /// Starts the daemon (local only; idempotent). Foreign-owned port → Port Conflict state
    /// (the launcher refuses); missing executable / start timeout → detail text.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartDaemon))]
    private async Task StartDaemonAsync(CancellationToken ct = default)
    {
        await RunGuardedAsync(async token =>
        {
            try
            {
                var status = await _connection.DaemonLauncher!.StartAsync(token).ConfigureAwait(false);
                ApplyDaemonStatus(status);
            }
            catch (RouterDaemonPortOccupiedException ex)
            {
                ApplyForeignPortStatus(ex.Status);
            }
            catch (RouterDaemonExecutableNotFoundException ex)
            {
                ApplyStoppedStatus(ex.Message);
            }
            catch (RouterDaemonStartTimeoutException ex)
            {
                ApplyStoppedStatus(ex.Message);
            }
            catch (Exception ex)
            {
                ApplyStoppedStatus(ex.Message);
            }
        }, ct);
    }

    /// <summary>Stops the daemon gracefully (local only).</summary>
    [RelayCommand(CanExecute = nameof(CanManageDaemon))]
    private async Task StopDaemonAsync(CancellationToken ct = default)
    {
        await RunGuardedAsync(async token =>
        {
            try
            {
                var status = await _connection.DaemonLauncher!.StopAsync(token).ConfigureAwait(false);
                ApplyDaemonStatus(status);
            }
            catch (RouterDaemonException ex)
            {
                ApplyStoppedStatus(ex.Message);
            }
            catch (Exception ex)
            {
                ApplyStoppedStatus(ex.Message);
            }
        }, ct);
    }

    /// <summary>Restarts the daemon (local only): sequential <c>StopAsync</c> →
    /// <c>StartAsync</c>, busy-guarded.</summary>
    [RelayCommand(CanExecute = nameof(CanManageDaemon))]
    private async Task RestartDaemonAsync(CancellationToken ct = default)
    {
        await RunGuardedAsync(async token =>
        {
            try
            {
                var launcher = _connection.DaemonLauncher!;
                await launcher.StopAsync(token).ConfigureAwait(false);
                var status = await launcher.StartAsync(token).ConfigureAwait(false);
                ApplyDaemonStatus(status);
            }
            catch (RouterDaemonPortOccupiedException ex)
            {
                ApplyForeignPortStatus(ex.Status);
            }
            catch (Exception ex)
            {
                ApplyStoppedStatus(ex.Message);
            }
        }, ct);
    }

    /// <summary>Switches back to the local connection (daemon URL + key-reader auth) and
    /// auto-starts the daemon when stopped.</summary>
    [RelayCommand(CanExecute = nameof(CanRunCommand))]
    private async Task ConnectLocalAsync(CancellationToken ct = default)
    {
        await RunGuardedAsync(async token =>
        {
            await _connection
                .SwitchConnectionAsync(AppConnectionService.LocalProfile(), token).ConfigureAwait(false);
            IsLocalModeChanged();
            await LocalInitializeAsync(token).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// Switches to a remote LAN router (URL + API key, validated; the key is kept in memory
    /// for the session only). Daemon management is disabled in remote mode.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunCommand))]
    private async Task ConnectRemoteAsync(CancellationToken ct = default)
    {
        if (!Uri.TryCreate(RemoteUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusDetail = "The remote URL must be an absolute http(s) URI.";
            return;
        }

        if (string.IsNullOrWhiteSpace(RemoteApiKey))
        {
            StatusDetail = "An API key is required for remote connections.";
            return;
        }

        await RunGuardedAsync(async token =>
        {
            await _connection
                .SwitchConnectionAsync(
                    new RouterConnectionProfile(RouterConnectionMode.Remote, uri, RemoteApiKey), token)
                .ConfigureAwait(false);
            IsLocalModeChanged();
            await ProbeRemoteAsync(token).ConfigureAwait(false);
        }, ct);
    }

    // -- Lifecycle -------------------------------------------------------------

    /// <summary>
    /// Window-open lifecycle (local): <c>EnsureRunningAsync</c> EXACTLY ONCE (auto-start; a
    /// foreign port is returned, never thrown) then the status application. Then the tabs
    /// start polling and the Settings tab loads on demand.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        StatusText = "Connecting\u2026";
        StatusDetail = "Checking local router daemon\u2026";
        RaiseCommandAvailability();
        try
        {
            if (IsLocalMode)
            {
                await LocalInitializeAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await ProbeRemoteAsync(ct).ConfigureAwait(false);
            }

            var monitorTask = MonitorTab.StartPollingAsync(ct);
            var fleetTask = FleetTab.StartPollingAsync(ct);
            var cartographyTask = CartographyTab.StartPollingAsync(ct);
            var settingsTask = SettingsTab.RefreshAsync(ct);
            await Task.WhenAll(monitorTask, fleetTask, cartographyTask, settingsTask).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            RaiseCommandAvailability();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.ConnectionChanged -= OnConnectionChanged;
        _commandGate.Dispose();
        await MonitorTab.DisposeAsync().ConfigureAwait(false);
        await FleetTab.DisposeAsync().ConfigureAwait(false);
        await CartographyTab.DisposeAsync().ConfigureAwait(false);
        await SettingsTab.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // -- Internals ---------------------------------------------------------------

    /// <summary>
    /// The manual refresh: a pure PROBE (local: <c>GetStatusAsync</c> — a stopped daemon stays
    /// stopped; only startup / ConnectLocal auto-start via <c>EnsureRunningAsync</c>).
    /// </summary>
    private async Task RefreshStatusCoreAsync(CancellationToken ct)
    {
        if (IsLocalMode)
        {
            try
            {
                var launcher = _connection.DaemonLauncher
                    ?? throw new InvalidOperationException("No daemon launcher for the local profile.");
                var status = await launcher.GetStatusAsync(ct).ConfigureAwait(false);
                ApplyDaemonStatus(status);
            }
            catch (RouterDaemonException ex)
            {
                ApplyStoppedStatus(ex.Message);
            }
            catch (Exception ex)
            {
                StatusText = "Disconnected";
                StatusDetail = ex.Message;
                RaiseCommandAvailability();
            }
        }
        else
        {
            await ProbeRemoteAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>The local startup path: <c>EnsureRunningAsync</c> (auto-start; never throws
    /// on a foreign port) then status application.</summary>
    private async Task LocalInitializeAsync(CancellationToken ct)
    {
        try
        {
            var launcher = _connection.DaemonLauncher
                ?? throw new InvalidOperationException("No daemon launcher for the local profile.");
            var status = await launcher.EnsureRunningAsync(ct).ConfigureAwait(false);
            ApplyDaemonStatus(status);
        }
        catch (RouterDaemonException ex)
        {
            ApplyStoppedStatus(ex.Message);
        }
        catch (Exception ex)
        {
            StatusText = "Disconnected";
            StatusDetail = ex.Message;
            RaiseCommandAvailability();
        }
    }

    /// <summary>The remote probe: 200 → version/PID/uptime; 401 → key-locked hint; anything
    /// else → Disconnected.</summary>
    private async Task ProbeRemoteAsync(CancellationToken ct)
    {
        try
        {
            var info = await _connection.Client.GetInfoAsync(ct).ConfigureAwait(false);
            IsPortConflict = false;
            StatusText = "Remote";
            StatusDetail = BuildRemoteDetail(info);
            RaiseCommandAvailability();
        }
        catch (OllamaRouterApiException ex) when (
            ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            IsPortConflict = false;
            StatusText = "Remote";
            StatusDetail =
                "The router is key-locked and rejected the configured key — enter a valid API " +
                $"key (connect → remote) at {BaseUrl}.";
            RaiseCommandAvailability();
        }
        catch (Exception ex)
        {
            IsPortConflict = false;
            StatusText = "Disconnected";
            StatusDetail = ex.Message;
            RaiseCommandAvailability();
        }
    }

    private void ApplyDaemonStatus(RouterDaemonStatus status)
    {
        switch (status.State)
        {
            case RouterDaemonState.Running:
                IsPortConflict = false;
                StatusText = "Running";
                StatusDetail = BuildLocalDetail(status.Info);
                break;
            case RouterDaemonState.Stopped:
                IsPortConflict = false;
                StatusText = "Stopped";
                StatusDetail = string.IsNullOrWhiteSpace(status.Detail)
                    ? $"The daemon is not running at {BaseUrl}."
                    : $"The daemon is not running at {BaseUrl} ({status.Detail}).";
                break;
            default: // PortOccupiedByOtherService
                ApplyForeignPortStatus(status);
                break;
        }

        RaiseCommandAvailability();
    }

    private void ApplyForeignPortStatus(RouterDaemonStatus status)
    {
        IsPortConflict = true;
        StatusText = "Port Conflict";
        var detail = string.IsNullOrWhiteSpace(status.Detail) ? "port occupied" : status.Detail;
        StatusDetail =
            $"{BaseUrl}: {detail} — a NON-router service owns this port. The launcher never " +
            "launches against or kills a foreign port owner; move the raw 'ollama' off " +
            ":11434 (or connect to the router via its actual URL → remote).";
        RaiseCommandAvailability();
    }

    private void ApplyStoppedStatus(string message)
    {
        IsPortConflict = false;
        StatusText = "Stopped";
        StatusDetail = message;
        RaiseCommandAvailability();
    }

    private string BuildLocalDetail(RouterInfoResponse? info)
    {
        var parts = new List<string>();
        if (info is not null)
        {
            if (info.ProcessId > 0)
            {
                parts.Add($"PID {info.ProcessId}");
            }

            if (!string.IsNullOrWhiteSpace(info.Version))
            {
                parts.Add($"v{info.Version}");
            }

            if (info.UptimeSeconds > 0)
            {
                parts.Add($"uptime {FormatUptime(info.UptimeSeconds)}");
            }
        }

        parts.Add($"bind {BaseUrl}");
        return string.Join(" · ", parts);
    }

    private string BuildRemoteDetail(RouterInfoResponse info)
    {
        var parts = new List<string>();
        if (info.ProcessId > 0)
        {
            parts.Add($"PID {info.ProcessId}");
        }

        if (!string.IsNullOrWhiteSpace(info.Version))
        {
            parts.Add($"v{info.Version}");
        }

        if (info.UptimeSeconds > 0)
        {
            parts.Add($"uptime {FormatUptime(info.UptimeSeconds)}");
        }

        parts.Add($"remote {BaseUrl}");
        return string.Join(" · ", parts);
    }

    private string FormatUptime(double seconds)
    {
        var timeSpan = TimeSpan.FromSeconds(seconds);
        if (timeSpan.TotalHours >= 1)
        {
            return $"{(int)timeSpan.TotalHours}h {(int)timeSpan.TotalMinutes % 60}m";
        }

        if (timeSpan.TotalMinutes >= 1)
        {
            return $"{(int)timeSpan.TotalMinutes}m {timeSpan.Seconds}s";
        }

        return $"{timeSpan.Seconds}s";
    }

    private bool CanRunCommand() => !IsBusy;

    private bool CanManageDaemon() => IsLocalMode && !IsBusy;

    private bool CanStartDaemon() => IsLocalMode && !IsPortConflict && !IsBusy;

    private void IsLocalModeChanged() => RaiseCommandAvailability();

    private void RaiseCommandAvailability()
    {
        void Notify()
        {
            RefreshStatusCommand.NotifyCanExecuteChanged();
            StartDaemonCommand.NotifyCanExecuteChanged();
            StopDaemonCommand.NotifyCanExecuteChanged();
            RestartDaemonCommand.NotifyCanExecuteChanged();
            ConnectLocalCommand.NotifyCanExecuteChanged();
            ConnectRemoteCommand.NotifyCanExecuteChanged();
        }

        if (Avalonia.Application.Current is null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Notify();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Notify);
        }
    }

    /// <summary>
    /// Single-flight command execution: overlapping command invocations QUEUE (never overlap),
    /// so an explicit refresh after a connection switch is always deterministic.
    /// </summary>
    private async Task RunGuardedAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _commandGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // The view model was disposed while the command was queued.
        }

        if (_disposed)
        {
            return; // The view model was disposed while the command was queued.
        }

        IsBusy = true;
        RaiseCommandAvailability();
        try
        {
            await action(ct).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            RaiseCommandAvailability();
            _commandGate.Release();
        }
    }

    private void OnConnectionChanged()
    {
        if (_disposed)
        {
            return;
        }

        IsLocalModeChanged();
        StatusText = "Connecting\u2026";
        StatusDetail = string.Empty;
        RaiseCommandAvailability();
        _ = RefreshStatusAsync(CancellationToken.None);
    }
}
