using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.Connection;
using OllamaRouter.Client.Process;

namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// The classification of the router port <c>:11434</c> surfaced by Step 1 (task #103, spec
/// §3.5). <see cref="ForeignService"/> is the raw-Ollama conflict case (Step 1's whole reason
/// to exist).
/// </summary>
public enum Port11434Status
{
    Unknown,
    RouterRunning,
    ForeignService,
    Free,
}

/// <summary>
/// Step 1 — Local Ollama for the Router (task #103, spec §3.5): detects a local Ollama, shows
/// the <c>:11434</c> port state (router-running / foreign / free), and offers the one-click
/// "Configure Local Ollama for Router" action (Windows + macOS; Linux gets manual
/// instructions). <see cref="LocalOllamaConfigurator"/> performs the actual work behind
/// injectable seams.
/// </summary>
public sealed partial class WizardEnvironmentStepViewModel : ObservableObject, IWizardStep
{
    private readonly LocalOllamaConfigurator _configurator;
    private readonly IAppConnectionService _connection;
    private bool _disposed;

    internal WizardEnvironmentStepViewModel(
        LocalOllamaConfigurator configurator,
        IAppConnectionService connection,
        Func<CancellationToken, Task>? delay = null)
    {
        _configurator = configurator ?? throw new ArgumentNullException(nameof(configurator));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        // Forward the (test) port-wait gate to the configurator so both the configure and the
        // start-only paths use it.
        if (delay is not null)
        {
            _configurator.Delay = delay;
        }
    }

    [ObservableProperty]
    private bool _ollamaInstalled;

    [ObservableProperty]
    private string? _ollamaBinaryPath;

    [ObservableProperty]
    private bool _ollamaRunning;

    [ObservableProperty]
    private Port11434Status _port11434Status = Port11434Status.Unknown;

    [ObservableProperty]
    private string _routerStatusLabel = "Unknown";

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>LAN-mesh option: expose local Ollama on <c>0.0.0.0:11435</c> (default off =
    /// loopback <c>127.0.0.1:11435</c>).</summary>
    [ObservableProperty]
    private bool _lanMeshEnabled;

    /// <summary>True on Linux (neither Windows nor macOS) → the one-click action is disabled and
    /// the manual-instructions block is shown.</summary>
    public bool IsLinux => !_configurator.SupportsOneClick;

    /// <summary>One-line detection summary (raises <see cref="PropertyChanged"/> on its inputs below).</summary>
    public string DetectionText => OllamaInstalled
        ? $"Ollama {(OllamaRunning ? "is running" : "is installed but not running")}"
          + (string.IsNullOrWhiteSpace(OllamaBinaryPath) ? string.Empty : $" ({OllamaBinaryPath})")
        : "Ollama not found on this machine";

    /// <summary>The on-demand re-check command (bound to the Step 1 Re-check button).</summary>
    [RelayCommand]
    private Task Recheck() => RefreshAsync(CancellationToken.None);

    /// <summary>Re-checks the local Ollama detection and the <c>:11434</c> port state.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)    {
        var detection = await _configurator.DetectAsync(ct).ConfigureAwait(false);
        OllamaInstalled = detection.Installed;
        OllamaBinaryPath = detection.BinaryPath;
        OllamaRunning = detection.ProcessRunning;

        var launcher = _connection.DaemonLauncher;
        if (launcher is null)
        {
            Port11434Status = Port11434Status.Unknown;
            RouterStatusLabel = "Router launcher unavailable (not in local mode).";
            return;
        }

        var status = await launcher.GetStatusAsync(ct).ConfigureAwait(false);
        Port11434Status = status.State switch
        {
            RouterDaemonState.Running => Port11434Status.RouterRunning,
            RouterDaemonState.PortOccupiedByOtherService => Port11434Status.ForeignService,
            _ => Port11434Status.Free,
        };
        RouterStatusLabel = Describe(Port11434Status);
    }

    /// <summary>Step lifecycle: refresh the detection + port state on entry.</summary>
    public Task OnStepEnteredAsync(CancellationToken ct = default) => RefreshAsync(ct);

    /// <summary>Step lifecycle: nothing to stop (no poller).</summary>
    public Task OnStepLeftAsync() => Task.CompletedTask;

    /// <summary>
    /// The one-click "Configure Local Ollama for Router": sets the OLLAMA_* environment,
    /// restarts Ollama on <c>:11435</c> and starts the router on <c>:11434</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private async Task ConfigureForRouter()
    {
        if (!_configurator.SupportsOneClick)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _configurator.ConfigureForRouterAsync(LanMeshEnabled, _connection, CancellationToken.None)
                .ConfigureAwait(false);
            Message = result.Message;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Starts only Ollama on <c>:11435</c> (env + start + port-wait; no router start) — for the
    /// "installed but not running" case.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartOllama))]
    private async Task StartOllama()
    {
        if (!_configurator.SupportsOneClick)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _configurator.StartOllamaOnlyAsync(LanMeshEnabled, CancellationToken.None).ConfigureAwait(false);
            Message = result.Message;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConfigure() => _configurator.SupportsOneClick && !IsBusy;

    private bool CanStartOllama() => _configurator.SupportsOneClick && OllamaInstalled && !OllamaRunning && !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        ConfigureForRouterCommand.NotifyCanExecuteChanged();
        StartOllamaCommand.NotifyCanExecuteChanged();
    }

    partial void OnOllamaInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(DetectionText));
        StartOllamaCommand.NotifyCanExecuteChanged();
    }

    partial void OnOllamaRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(DetectionText));
        StartOllamaCommand.NotifyCanExecuteChanged();
    }

    partial void OnOllamaBinaryPathChanged(string? value) => OnPropertyChanged(nameof(DetectionText));

    /// <summary>Human summary for a port state (bound to the status label).</summary>
    internal static string Describe(Port11434Status status) => status switch
    {
        Port11434Status.RouterRunning => "Router is running on :11434 ✓",
        Port11434Status.ForeignService => "Raw Ollama occupies :11434 — configure it for the router below",
        Port11434Status.Free => ":11434 is free",
        _ => "Unknown",
    };

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
