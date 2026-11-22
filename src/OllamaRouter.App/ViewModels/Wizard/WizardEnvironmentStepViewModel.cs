using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.Connection;
using OllamaRouter.App.Logging;
using OllamaRouter.Client.Process;
using Serilog;

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
/// The classification of the effective Ollama port surfaced by Step 1's "Configuration for
/// router" section (task #150). <see cref="Ok"/> = serving on the router-expected
/// <c>:11435</c>; <see cref="WrongPort"/> = serving on a DIFFERENT port (the one-click
/// "Change port" case, task #160).
/// </summary>
public enum OllamaPortStatus
{
    Unknown,
    NotRunning,
    Ok,
    WrongPort,
    NotDetected,
}

/// <summary>
/// Step 1 — Local Ollama for the Router (task #103, spec §3.5; reworked by task #150;
/// unified row grid by task #160): state-driven detection with one-click actions. Section 1
/// "Ollama": Install (the official ollama.com script — all OSes), Start (Win/macOS), Update
/// (stop → script → restart — all OSes) and a green running check; which of these are visible
/// is driven by the detection state. Section 2 "Configuration for router" is one aligned row
/// grid (label left + ✓-state/action button right): the state-driven LAN-exposure row
/// (detected <see cref="OllamaLanState"/> — "Expose on the LAN" / "Restrict to localhost",
/// hidden on Linux and when Ollama is not installed), the effective-Ollama-port check
/// (✓ when Ok, one-click "Change port" when drifted, via the existing start-only env-write +
/// restart path) and the state-driven <c>:11434</c> router row (✓ while running, "Start
/// router" while the port is free — task #150, M2: the legacy "Configure Local Ollama for
/// Router" button is gone, subsumed by Start + Change port + Start router).
/// <see cref="LocalOllamaConfigurator"/> performs the actual work behind injectable seams;
/// every action preserves the DETECTED LAN bind (the removed LAN-mesh checkbox is gone —
/// task #160), and every failure surfaces a readable (red) message — never a crash or a
/// silent hang.
/// </summary>
public sealed partial class WizardEnvironmentStepViewModel : ObservableObject, IWizardStep
{
    private readonly LocalOllamaConfigurator _configurator;
    private readonly IAppConnectionService _connection;
    // Task #159: the resolved logger (the seam defaults to the static Serilog.Log.Logger).
    private readonly Serilog.ILogger _logger;
    private bool _disposed;

    internal WizardEnvironmentStepViewModel(
        LocalOllamaConfigurator configurator,
        IAppConnectionService connection,
        Func<CancellationToken, Task>? delay = null,
        Serilog.ILogger? logger = null)
    {
        _configurator = configurator ?? throw new ArgumentNullException(nameof(configurator));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? Serilog.Log.Logger;
        // Forward the (test) port-wait gate to the configurator so every action path uses it.
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

    /// <summary>The severity of <see cref="Message"/> (true → rendered red): set from each
    /// action's Success flag; informational text (e.g. the refresh hints) stays non-error.</summary>
    [ObservableProperty]
    private bool _messageIsError;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The detected LAN-exposure state of the local Ollama (task #160 — set from
    /// the port check's <c>LanState</c> in <see cref="RefreshAsync"/>): drives the LAN row's
    /// label, its toggle button and the bind every action preserves.</summary>
    [ObservableProperty]
    private OllamaLanState _ollamaLanState = OllamaLanState.Unknown;

    [ObservableProperty]
    private OllamaPortStatus _ollamaPortStatus = OllamaPortStatus.Unknown;

    [ObservableProperty]
    private int? _ollamaDetectedPort;

    /// <summary>True on Linux (neither Windows nor macOS) → Start and Change port are
    /// disabled (the existing configurator paths gate on <c>SupportsOneClick</c>), the LAN
    /// row is hidden (task #160); Install and Update stay one-click (task #150, M1) and the
    /// manual-instructions block is shown.</summary>
    public bool IsLinux => !_configurator.SupportsOneClick;

    /// <summary>One-line detection summary (raises <see cref="PropertyChanged"/> on its inputs below).</summary>
    public string DetectionText => OllamaInstalled
        ? $"Ollama {(OllamaRunning ? "is running" : "is installed but not running")}"
          + (string.IsNullOrWhiteSpace(OllamaBinaryPath) ? string.Empty : $" ({OllamaBinaryPath})")
        : "Ollama not found on this machine";

    // -- Section 1 "Ollama": state → visible actions (task #150 contract matrix) ----------

    /// <summary>Not installed → the one-click Install (official script, all OSes).</summary>
    public bool ShowInstallButton => !OllamaInstalled;

    /// <summary>Installed + stopped → the one-click Start (Win/macOS only).</summary>
    public bool ShowStartButton => OllamaInstalled && !OllamaRunning && !IsLinux;

    /// <summary>Installed → the one-click Update (stop → script → restart, all OSes).</summary>
    public bool ShowUpdateButton => OllamaInstalled;

    /// <summary>Installed + running → the green check.</summary>
    public bool ShowRunningCheck => OllamaRunning;

    // -- Section 2 "Configuration for router": unified row grid (task #160) ---------------

    /// <summary>Row 1 (LAN exposure) visibility (task #160): shown when Ollama is installed
    /// AND this is not Linux (Linux keeps the manual block; the LAN state has no GUI-readable
    /// source there).</summary>
    public bool ShowLanRow => OllamaInstalled && !IsLinux;

    /// <summary>The toggle button inside the LAN row (task #160): shown for a DECIDED state
    /// only — the Unknown state shows the neutral label with NO button.</summary>
    public bool ShowLanToggle => ShowLanRow && OllamaLanState is OllamaLanState.Loopback or OllamaLanState.Lan;

    /// <summary>The LAN row label (task #160 — byte-pinned per state; raises with the state).</summary>
    public string LanRowLabel => OllamaLanState switch
    {
        OllamaLanState.Loopback => "Ollama is configured to listen only on loopback. To use it " +
                                   "from another computer on the network, allow listening on the LAN.",
        OllamaLanState.Lan => "Ollama is configured to listen on LAN, and can be used by other " +
                              "computers on the network.",
        _ => "Ollama's LAN configuration could not be determined — try Re-check.",
    };

    /// <summary>The LAN row toggle button text (task #160): expose while loopback, restrict
    /// while LAN.</summary>
    public string LanToggleText => OllamaLanState == OllamaLanState.Lan
        ? "Restrict to localhost"
        : "Expose on the LAN";

    /// <summary>The human label for the effective Ollama port (raises with the port state
    /// and the detected port; task #160 pins the Ok / WrongPort texts — the green ✓ lives in
    /// the row's RIGHT cell, not in the text).</summary>
    public string OllamaPortLabel => OllamaPortStatus switch
    {
        OllamaPortStatus.Ok => $"Ollama is listening on {LocalOllamaConfigurator.OllamaPort}",
        OllamaPortStatus.WrongPort =>
            $"Ollama is listening on {OllamaDetectedPort}, but this port should be freed for the router.",
        OllamaPortStatus.NotRunning => "Ollama is not running — start it in the section above",
        OllamaPortStatus.NotDetected => "Ollama is running but its port could not be detected — try Re-check",
        _ => "Checking the Ollama port…",
    };

    public bool ShowPortOk => OllamaPortStatus == OllamaPortStatus.Ok;

    public bool ShowPortWrong => OllamaPortStatus == OllamaPortStatus.WrongPort;

    public bool ShowPortNotRunning => OllamaPortStatus == OllamaPortStatus.NotRunning;

    public bool ShowPortNotDetected => OllamaPortStatus == OllamaPortStatus.NotDetected;

    public bool ShowPortUnknown => OllamaPortStatus == OllamaPortStatus.Unknown;

    /// <summary>Wrong port → the one-click "Change port" (task #160 rename of the legacy
    /// fix action; Win/macOS: the existing start-only env-write + restart path, preserving
    /// the detected LAN bind).</summary>
    public bool ShowChangePortButton => OllamaPortStatus == OllamaPortStatus.WrongPort && !IsLinux;

    /// <summary>Router running → the green ✓ in the router row's RIGHT cell (task #160 moved
    /// it out of the label text).</summary>
    public bool ShowRouterRunningCheck => Port11434Status == Port11434Status.RouterRunning;

    /// <summary>Router port free + a launcher available (local mode) → "Start router"
    /// (task #150, M2 — Free = "not running"; ForeignService/RouterRunning show no button).</summary>
    public bool ShowStartRouterButton => Port11434Status == Port11434Status.Free && _connection.DaemonLauncher is not null;

    // -- Commands (all busy-gated: while IsBusy, NO action runs concurrently) -------------

    /// <summary>
    /// One-click Install (task #150): runs the official ollama.com script for the platform
    /// (elevated on Windows) with a "several minutes" status message, then re-detects — the
    /// new state is reflected without a manual Re-check.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallOllama))]
    private async Task InstallOllama()
    {
        IsBusy = true;
        try
        {
            _logger.Information("Wizard step 1: install started");
            Message = "Installing Ollama via the official script — this can take several minutes…";
            MessageIsError = false;
            var result = await _configurator.InstallOllamaAsync(CancellationToken.None).ConfigureAwait(false);
            LogStepOutcome("install", result);
            Message = result.Message;
            MessageIsError = !result.Success;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One-click Update (task #150): stop Ollama → official script → restart (all OSes),
    /// preserving the DETECTED LAN bind (task #160 — the restart no longer silently resets a
    /// LAN-configured Ollama to loopback). On a script failure the message reports that
    /// Ollama is left stopped and how to recover.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateOllama))]
    private async Task UpdateOllama()
    {
        IsBusy = true;
        try
        {
            _logger.Information("Wizard step 1: update started");
            Message = "Updating Ollama: stop → official script → restart…";
            MessageIsError = false;
            var result = await _configurator.UpdateOllamaAsync(OllamaLanState == OllamaLanState.Lan, CancellationToken.None).ConfigureAwait(false);
            LogStepOutcome("update", result);
            Message = result.Message;
            MessageIsError = !result.Success;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One-click Start Ollama (env + start + port-wait; no router start) — for the "installed
    /// but not running" case (Win/macOS). Preserves the DETECTED LAN bind (task #160:
    /// <see cref="OllamaLanState"/> = Lan → 0.0.0.0, otherwise → loopback).
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
            _logger.Information("Wizard step 1: start started");
            var result = await _configurator.StartOllamaOnlyAsync(OllamaLanState == OllamaLanState.Lan, CancellationToken.None).ConfigureAwait(false);
            LogStepOutcome("start", result);
            Message = result.Message;
            MessageIsError = !result.Success;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One-click Change port (task #160 rename of the legacy fix-port action, Win/macOS):
    /// reuses the existing start-only path — writes OLLAMA_HOST to the port-corrected host
    /// preserving the DETECTED LAN bind (task #160: <see cref="OllamaLanState"/> = Lan →
    /// <c>0.0.0.0:11435</c>, otherwise → <c>127.0.0.1:11435</c>) and restarts Ollama with
    /// the explicit env + port wait.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangePort))]
    private async Task ChangePort()
    {
        IsBusy = true;
        try
        {
            _logger.Information("Wizard step 1: change port started");
            Message = "Changing the Ollama port (set OLLAMA_HOST and restart Ollama)…";
            MessageIsError = false;
            var result = await _configurator.StartOllamaOnlyAsync(OllamaLanState == OllamaLanState.Lan, CancellationToken.None).ConfigureAwait(false);
            LogStepOutcome("change port", result);
            Message = result.Message;
            MessageIsError = !result.Success;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One-click LAN toggle (task #160 — replaces the old LAN-mesh checkbox): from the
    /// DECIDED loopback state, "Expose on the LAN" writes OLLAMA_HOST =
    /// <c>0.0.0.0:11435</c>; from the LAN state, "Restrict to localhost" writes
    /// <c>127.0.0.1:11435</c>. Both reuse the start-only path (stop → start with explicit
    /// env + port wait) so the toggle WORKS WHILE Ollama IS STOPPED (it starts it — decision
    /// 3); on completion the state is re-detected and the row flips label/button. The
    /// Unknown state shows no button (CanExecute is false there).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleLan))]
    private async Task ToggleLan()
    {
        var target = OllamaLanState != OllamaLanState.Lan;
        IsBusy = true;
        try
        {
            _logger.Information("Wizard step 1: lan toggle started ({Target})", target ? "LAN" : "loopback");
            Message = target
                ? $"Exposing Ollama on the LAN (set OLLAMA_HOST to 0.0.0.0:{LocalOllamaConfigurator.OllamaPort} and restart Ollama)…"
                : $"Restricting Ollama to localhost (set OLLAMA_HOST to 127.0.0.1:{LocalOllamaConfigurator.OllamaPort} and restart Ollama)…";
            MessageIsError = false;
            var result = await _configurator.StartOllamaOnlyAsync(target, CancellationToken.None).ConfigureAwait(false);
            LogStepOutcome("lan toggle", result);
            Message = result.Message;
            MessageIsError = !result.Success;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One-click Start router (task #150, M2): reuses the existing
    /// <see cref="IAppConnectionService.DaemonLauncher"/> — success/failure is a message,
    /// NEVER a throw (the Step 2/4 daemon-down tolerance precedent).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartRouter))]
    private async Task StartRouter()
    {
        IsBusy = true;
        try
        {
            _logger.Information("Wizard step 1: router start requested");
            Message = "Starting the router on :11434…";
            MessageIsError = false;
            try
            {
                var status = await _connection.DaemonLauncher!.StartAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.Information("Wizard step 1: router start → {State}", status.State.ToString());
                Message = status.State == RouterDaemonState.Running
                    ? "Router is running on :11434 ✓"
                    : "Router is starting on :11434";
                MessageIsError = false;
            }
            catch (Exception ex)
            {
                // Typed launcher failures (port occupied / executable not found / start
                // timeout) and anything else: a readable message, never a crash.
                _logger.Warning(ex, "Wizard step 1: router start failed: {Message}", ex.Message);
                Message = $"Could not start the router: {ex.Message}";
                MessageIsError = true;
            }

            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The on-demand re-check command (bound to the Step 1 Re-check button; always
    /// available, busy-gated like every other action — task #150).</summary>
    [RelayCommand(CanExecute = nameof(CanRecheck))]
    private Task Recheck() => RefreshAsync(CancellationToken.None);

    /// <summary>
    /// Re-checks the local Ollama detection, its EFFECTIVE port + LAN state (task #150;
    /// task #160) and the <c>:11434</c> port state. One binary detection per refresh: the
    /// port/LAN check reuses the detection pass (the task #147 "the initial entry runs
    /// exactly once" invariant — the LAN state is derived from socket probes and the
    /// persisted env INSIDE that same pass, never from a second binary probe).
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var detection = await _configurator.DetectAsync(ct).ConfigureAwait(false);
        OllamaInstalled = detection.Installed;
        OllamaBinaryPath = detection.BinaryPath;
        OllamaRunning = detection.ProcessRunning;

        var portCheck = await _configurator.DetectPortAsync(detection, ct).ConfigureAwait(false);
        OllamaPortStatus = !portCheck.OllamaRunning
            ? OllamaPortStatus.NotRunning
            : portCheck.DetectedPort is null
                ? OllamaPortStatus.NotDetected
                : portCheck.DetectedPort == LocalOllamaConfigurator.OllamaPort
                    ? OllamaPortStatus.Ok
                    : OllamaPortStatus.WrongPort;
        OllamaDetectedPort = portCheck.DetectedPort;
        OllamaLanState = portCheck.LanState; // task #160: the detected LAN-exposure state

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

        // Task #159: the detection results are Debug-level diagnostics (not per-action spam).
        _logger.Debug(
            "Wizard step 1 detection: installed={Installed} running={Running} effectivePort={EffectivePort} portStatus={PortStatus} routerPort={RouterPort}",
            OllamaInstalled, OllamaRunning, OllamaDetectedPort, OllamaPortStatus.ToString(), Port11434Status.ToString());
    }

    /// <summary>
    /// Task #159: logs a Step 1 one-click action outcome — Information on success, Warning on
    /// failure (the <see cref="LocalOllamaConfigResult"/> carries the readable message, never a
    /// secret: script paths and OLLAMA_HOST values are safe to log).
    /// </summary>
    private void LogStepOutcome(string action, LocalOllamaConfigResult result)
    {
        LogStepOutcomeCore(action, result.Success, result.Message);
    }

    private void LogStepOutcome(string action, OllamaScriptResult result)
    {
        LogStepOutcomeCore(action, result.Success, result.Message);
    }

    private void LogStepOutcomeCore(string action, bool success, string message)
    {
        if (success)
        {
            _logger.Information("Wizard step 1: {Action} completed: {Message}", action, message);
        }
        else
        {
            _logger.Warning("Wizard step 1: {Action} failed: {Message}", action, message);
        }
    }

    /// <summary>Step lifecycle: refresh the detection + port state on entry.</summary>
    public Task OnStepEnteredAsync(CancellationToken ct = default) => RefreshAsync(ct);

    /// <summary>Step lifecycle: nothing to stop (no poller).</summary>
    public Task OnStepLeftAsync() => Task.CompletedTask;

    private bool CanInstallOllama() => ShowInstallButton && !IsBusy;

    private bool CanUpdateOllama() => ShowUpdateButton && !IsBusy;

    private bool CanStartOllama() => _configurator.SupportsOneClick && OllamaInstalled && !OllamaRunning && !IsBusy;

    private bool CanChangePort() => ShowChangePortButton && !IsBusy;

    private bool CanToggleLan() => ShowLanToggle && !IsBusy;

    private bool CanStartRouter() => ShowStartRouterButton && !IsBusy;

    private bool CanRecheck() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        // Busy disables ALL Step 1 actions concurrently (task #150, AC6).
        InstallOllamaCommand.NotifyCanExecuteChanged();
        StartOllamaCommand.NotifyCanExecuteChanged();
        UpdateOllamaCommand.NotifyCanExecuteChanged();
        ChangePortCommand.NotifyCanExecuteChanged();
        ToggleLanCommand.NotifyCanExecuteChanged();
        StartRouterCommand.NotifyCanExecuteChanged();
        RecheckCommand.NotifyCanExecuteChanged();
    }

    partial void OnOllamaInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(DetectionText));
        OnPropertyChanged(nameof(ShowInstallButton));
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(ShowUpdateButton));
        OnPropertyChanged(nameof(ShowLanRow));
        OnPropertyChanged(nameof(ShowLanToggle));
        InstallOllamaCommand.NotifyCanExecuteChanged();
        StartOllamaCommand.NotifyCanExecuteChanged();
        UpdateOllamaCommand.NotifyCanExecuteChanged();
        ToggleLanCommand.NotifyCanExecuteChanged();
    }

    partial void OnOllamaRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(DetectionText));
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(ShowRunningCheck));
        StartOllamaCommand.NotifyCanExecuteChanged();
    }

    partial void OnOllamaBinaryPathChanged(string? value) => OnPropertyChanged(nameof(DetectionText));

    partial void OnOllamaLanStateChanged(OllamaLanState value)
    {
        OnPropertyChanged(nameof(ShowLanToggle));
        OnPropertyChanged(nameof(LanRowLabel));
        OnPropertyChanged(nameof(LanToggleText));
        ToggleLanCommand.NotifyCanExecuteChanged();
    }

    partial void OnOllamaPortStatusChanged(OllamaPortStatus value)
    {
        OnPropertyChanged(nameof(OllamaPortLabel));
        OnPropertyChanged(nameof(ShowPortOk));
        OnPropertyChanged(nameof(ShowPortWrong));
        OnPropertyChanged(nameof(ShowPortNotRunning));
        OnPropertyChanged(nameof(ShowPortNotDetected));
        OnPropertyChanged(nameof(ShowPortUnknown));
        OnPropertyChanged(nameof(ShowChangePortButton));
        ChangePortCommand.NotifyCanExecuteChanged();
    }

    partial void OnOllamaDetectedPortChanged(int? value) => OnPropertyChanged(nameof(OllamaPortLabel));

    partial void OnPort11434StatusChanged(Port11434Status value)
    {
        OnPropertyChanged(nameof(ShowStartRouterButton));
        OnPropertyChanged(nameof(ShowRouterRunningCheck));
        StartRouterCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Human summary for a port state (bound to the status label; task #160: the
    /// running check lives in the row's right cell, not in the text — and the ForeignService
    /// wording no longer says "below", the port row with "Change port" is now ABOVE).
    /// </summary>
    internal static string Describe(Port11434Status status) => status switch
    {
        Port11434Status.RouterRunning => "Router is running on :11434",
        Port11434Status.ForeignService => "Raw Ollama occupies :11434 — free it for the router with \"Change port\" above.",
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
