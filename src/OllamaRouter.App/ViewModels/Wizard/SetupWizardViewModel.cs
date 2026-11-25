using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.Connection;

namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// The root view model of the 5-step Setup Wizard (task #103, spec §3.5; task #151 adds Step 3
/// "Cloud &amp; External Providers"): the step index, Back / Next navigation (busy-gated across
/// all steps, with Step 1 → Step 2 daemon readiness check), global daemon status badge &amp;
/// inline recovery banner, the Finish (exit 0) / Abandon (exit 1) exit signal, and the
/// disposal of all five step view models.
/// <para>
/// Step 0's <c>OnStepEnteredAsync</c> fires exactly ONCE at construction — the wizard is the
/// main window, so construction is the window-open moment (task #147: Step 1's Ollama/port
/// detection and router auto-start run immediately). All later step entries and leaves are
/// driven by <see cref="GoToAsync"/> per navigation.
/// </para>
/// <para>
/// The exit-code contract (D2): <see cref="Finish"/> → exit <c>0</c>; <see cref="Abandon"/>
/// (window closed without Finish) → exit <c>1</c>. The window code-behind wires
/// <see cref="ExitSignal"/> to <c>IClassicDesktopStyleApplicationLifetime.TryShutdown(code)</c>.
/// </para>
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAppConnectionService _connection;
    private readonly IWizardStep[] _steps;
    private string? _bannerError;
    private int _routerProbeInFlight;
    private bool _disposed;

    public SetupWizardViewModel(
        IAppConnectionService connection,
        WizardEnvironmentStepViewModel environmentStep,
        WizardTargetsStepViewModel targetsStep,
        WizardCloudStepViewModel cloudStep,
        WizardBenchmarkStepViewModel benchmarkStep,
        WizardRecommendationsStepViewModel recommendationsStep,
        Action<int>? exitSignal = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        EnvironmentStep = environmentStep ?? throw new ArgumentNullException(nameof(environmentStep));
        TargetsStep = targetsStep ?? throw new ArgumentNullException(nameof(targetsStep));
        CloudStep = cloudStep ?? throw new ArgumentNullException(nameof(cloudStep));
        BenchmarkStep = benchmarkStep ?? throw new ArgumentNullException(nameof(benchmarkStep));
        RecommendationsStep = recommendationsStep ?? throw new ArgumentNullException(nameof(recommendationsStep));
        _steps = [EnvironmentStep, TargetsStep, CloudStep, BenchmarkStep, RecommendationsStep];
        ExitSignal = exitSignal;

        BackCommand = new AsyncRelayCommand(() => GoToAsync(SelectedStep - 1), () => CanGoBack);
        NextCommand = new AsyncRelayCommand(() => GoToAsync(SelectedStep + 1), () => CanGoNext);
        FinishCommand = new RelayCommand(Finish, () => CanFinish);

        EnvironmentStep.PropertyChanged += OnStepPropertyChanged;
        TargetsStep.PropertyChanged += OnStepPropertyChanged;
        CloudStep.PropertyChanged += OnStepPropertyChanged;
        BenchmarkStep.PropertyChanged += OnStepPropertyChanged;
        RecommendationsStep.PropertyChanged += OnStepPropertyChanged;

        // Task #147: the wizard is the main window, so construction == window open. Fire step 0's
        // entry ONCE here — GoToAsync alone never reaches it (its newStep == SelectedStep guard
        // makes an explicit GoToAsync(0) while on step 0 a no-op), which left Step 1 showing its
        // false "Ollama not found" defaults until a manual Re-check.
        InitialStepEntry = EnterInitialStepAsync();
    }

    private async Task EnterInitialStepAsync()
    {
        try
        {
            await _steps[0].OnStepEnteredAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Detection failure must not crash app start-up: the step keeps its default state and
            // the Re-check command remains the recovery path. Same tolerance as
            // InstallModelWizardViewModel.RunStepEnteredAsync.
        }
    }

    /// <summary>Step 1 — Local Ollama for the Router.</summary>
    public WizardEnvironmentStepViewModel EnvironmentStep { get; }

    /// <summary>Step 2 — Fleet Targets.</summary>
    public WizardTargetsStepViewModel TargetsStep { get; }

    /// <summary>Step 3 — Cloud &amp; External Providers (task #151).</summary>
    public WizardCloudStepViewModel CloudStep { get; }

    /// <summary>Step 4 — Micro-Benchmark.</summary>
    public WizardBenchmarkStepViewModel BenchmarkStep { get; }

    /// <summary>Step 5 — Role Recommendations.</summary>
    public WizardRecommendationsStepViewModel RecommendationsStep { get; }

    /// <summary>
    /// The one-shot initial (step 0) entry — task #147. Awaits the Step 1 detection that runs as
    /// soon as the wizard opens (fires exactly once at construction). Exposed so tests can
    /// deterministically await the initial detection.
    /// </summary>
    internal Task InitialStepEntry { get; }

    /// <summary>The active step index (0–4).</summary>
    [ObservableProperty]
    private int _selectedStep;

    /// <summary>Whether a step transition or banner recovery action is in flight.</summary>
    [ObservableProperty]
    private bool _isTransitioning;

    /// <summary>Whether the wizard completed via Finish (guards the Abandon exit signal).</summary>
    public bool Finished { get; private set; }

    /// <summary>The exit-code sink: <c>0</c> = finished, <c>1</c> = abandoned (wired by the
    /// window to <c>TryShutdown</c>).</summary>
    internal Action<int>? ExitSignal { get; set; }

    public IAsyncRelayCommand BackCommand { get; }

    public IAsyncRelayCommand NextCommand { get; }

    public IRelayCommand FinishCommand { get; }

    /// <summary>Whether any step or transition is currently busy.</summary>
    public bool IsAnyStepBusy =>
        IsTransitioning
        || EnvironmentStep.IsBusy
        || TargetsStep.IsBusy
        || CloudStep.IsBusy
        || BenchmarkStep.IsBusy
        || RecommendationsStep.IsBusy;

    /// <summary>Whether Back is available (not on step 0 and not busy).</summary>
    public bool CanGoBack => SelectedStep > 0 && !IsAnyStepBusy;

    /// <summary>Whether Next is available (not on the last step, 4, and not busy).</summary>
    public bool CanGoNext => SelectedStep < 4 && !IsAnyStepBusy;

    /// <summary>Whether Finish is available (only on the last step, 4, and not busy).</summary>
    public bool CanFinish => SelectedStep == 4 && !IsAnyStepBusy;

    /// <summary>Status pill text displayed in the wizard header.</summary>
    public string DaemonBadgeText => _connection.DaemonLauncher is null
        ? "Remote Mode"
        : EnvironmentStep.Port11434Status switch
        {
            Port11434Status.RouterRunning => "● Router Running (:11434)",
            Port11434Status.ForeignService => "⚠ Port :11434 Conflict",
            Port11434Status.Free => "● Router Stopped",
            _ => "● Checking Router…",
        };

    /// <summary>Whether the local daemon is currently running on :11434.</summary>
    public bool IsDaemonRunning => EnvironmentStep.Port11434Status == Port11434Status.RouterRunning;

    /// <summary>Whether port :11434 is occupied by a non-router service (raw Ollama).</summary>
    public bool IsDaemonPortConflict => EnvironmentStep.Port11434Status == Port11434Status.ForeignService;

    /// <summary>Whether to display the global daemon recovery banner on Steps 2–5.</summary>
    public bool ShowDaemonWarningBanner =>
        SelectedStep > 0
        && _connection.DaemonLauncher is not null
        && EnvironmentStep.Port11434Status != Port11434Status.RouterRunning;

    /// <summary>Whether the inline "Start Router" button should appear inside the recovery banner.</summary>
    public bool ShowInlineStartRouterButton =>
        ShowDaemonWarningBanner && EnvironmentStep.Port11434Status == Port11434Status.Free;

    /// <summary>Actionable message shown in the global daemon recovery banner.</summary>
    public string DaemonBannerMessage => EnvironmentStep.Port11434Status == Port11434Status.ForeignService
        ? "Port :11434 is occupied by raw Ollama. Return to Step 1 and click 'Change port' so OllamaRouter can start."
        : !string.IsNullOrWhiteSpace(_bannerError)
            ? _bannerError
            : "The OllamaRouter daemon is not running on :11434. Start it now to continue setup.";

    /// <summary>Starts the local router daemon directly from the Steps 2–5 recovery banner,
    /// then refreshes the active step so its data loads immediately.</summary>
    [RelayCommand(CanExecute = nameof(CanRunBannerAction))]
    private async Task StartRouterFromBannerAsync()
    {
        if (IsAnyStepBusy)
        {
            return;
        }

        IsTransitioning = true;
        _bannerError = null;
        NotifyDaemonBannerProperties();
        try
        {
            var running = await EnvironmentStep.EnsureRouterAutoStartedAsync(CancellationToken.None);
            if (running && SelectedStep > 0)
            {
                await _steps[SelectedStep].OnStepLeftAsync();
                await _steps[SelectedStep].OnStepEnteredAsync(CancellationToken.None);
            }
            else if (!running)
            {
                _bannerError = EnvironmentStep.MessageIsError && EnvironmentStep.HasMessage
                    ? EnvironmentStep.Message
                    : "Could not start the router on :11434.";
                NotifyDaemonBannerProperties();
            }
        }
        finally
        {
            IsTransitioning = false;
        }
    }

    /// <summary>Navigates back to Step 1 from the global daemon recovery banner.</summary>
    [RelayCommand(CanExecute = nameof(CanRunBannerAction))]
    private Task GoToStep1Async() => GoToAsync(0);

    private bool CanRunBannerAction() => !IsAnyStepBusy;

    /// <summary>Completes the wizard: marks it finished and signals exit code <c>0</c>.</summary>
    public void Finish()
    {
        if (Finished)
        {
            return;
        }

        Finished = true;
        ExitSignal?.Invoke(0);
    }

    /// <summary>
    /// Abandons the wizard (window closed without Finish): signals exit code <c>1</c>. A no-op
    /// once <see cref="Finish"/> has already fired.
    /// </summary>
    public void Abandon()
    {
        if (Finished)
        {
            return;
        }

        ExitSignal?.Invoke(1);
    }

    /// <summary>
    /// Navigates to <paramref name="newStep"/>: validates local daemon readiness when advancing
    /// past Step 1, steps the index, and drives the step lifecycle (the outgoing step's
    /// <c>OnStepLeft</c>, then the incoming step's <c>OnStepEntered</c>).
    /// </summary>
    public async Task GoToAsync(int newStep)
    {
        if (newStep < 0 || newStep > 4 || newStep == SelectedStep || IsAnyStepBusy)
        {
            return;
        }

        IsTransitioning = true;
        try
        {
            if (SelectedStep == 0 && newStep > 0 && _connection.DaemonLauncher is not null)
            {
                if (EnvironmentStep.Port11434Status == Port11434Status.Unknown)
                {
                    await EnvironmentStep.RefreshRouterStatusAsync(CancellationToken.None);
                }

                if (EnvironmentStep.Port11434Status == Port11434Status.Free)
                {
                    await EnvironmentStep.EnsureRouterAutoStartedAsync(CancellationToken.None);
                }

                if (EnvironmentStep.Port11434Status == Port11434Status.ForeignService)
                {
                    EnvironmentStep.MessageIsError = true;
                    EnvironmentStep.Message = "Free port :11434 (click 'Change port') so OllamaRouter can start before continuing.";
                    return;
                }

                if (EnvironmentStep.Port11434Status != Port11434Status.RouterRunning)
                {
                    if (!EnvironmentStep.MessageIsError)
                    {
                        EnvironmentStep.MessageIsError = true;
                        EnvironmentStep.Message = "Could not verify router status on :11434. Try Re-check before continuing.";
                    }
                    return;
                }
            }
            else if (SelectedStep > 0 && newStep > 0 && _connection.DaemonLauncher is not null)
            {
                await EnvironmentStep.RefreshRouterStatusAsync(CancellationToken.None);
            }

            var oldStep = SelectedStep;
            SelectedStep = newStep;
            await _steps[oldStep].OnStepLeftAsync();
            await _steps[newStep].OnStepEnteredAsync(CancellationToken.None);
        }
        finally
        {
            IsTransitioning = false;
        }
    }

    partial void OnSelectedStepChanged(int value)
    {
        if (value == 0)
        {
            _bannerError = null;
        }

        NotifyDaemonBannerProperties();
        NotifyNavigationStateChanged();
    }

    partial void OnIsTransitioningChanged(bool value) => NotifyNavigationStateChanged();

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WizardEnvironmentStepViewModel.IsBusy))
        {
            NotifyNavigationStateChanged();
        }

        if (ReferenceEquals(sender, EnvironmentStep)
            && e.PropertyName == nameof(WizardEnvironmentStepViewModel.Port11434Status))
        {
            if (EnvironmentStep.Port11434Status == Port11434Status.RouterRunning)
            {
                _bannerError = null;
            }

            NotifyDaemonBannerProperties();
        }

        if (!_disposed && SelectedStep > 0 && _connection.DaemonLauncher is not null)
        {
            var shouldProbe =
                (ReferenceEquals(sender, TargetsStep) && e.PropertyName == nameof(WizardTargetsStepViewModel.MessageIsError)
                 && (TargetsStep.MessageIsError || EnvironmentStep.Port11434Status != Port11434Status.RouterRunning))
                || (ReferenceEquals(sender, CloudStep) && e.PropertyName == nameof(WizardCloudStepViewModel.HasError)
                    && (CloudStep.HasError || EnvironmentStep.Port11434Status != Port11434Status.RouterRunning))
                || (ReferenceEquals(sender, BenchmarkStep) && e.PropertyName == nameof(WizardBenchmarkStepViewModel.HasError)
                    && (BenchmarkStep.HasError || EnvironmentStep.Port11434Status != Port11434Status.RouterRunning))
                || (ReferenceEquals(sender, RecommendationsStep) && e.PropertyName == nameof(WizardRecommendationsStepViewModel.MessageIsError)
                    && (RecommendationsStep.MessageIsError || EnvironmentStep.Port11434Status != Port11434Status.RouterRunning));

            if (shouldProbe)
            {
                TriggerRouterStatusRefresh();
            }
        }
    }

    private void TriggerRouterStatusRefresh()
    {
        if (Interlocked.CompareExchange(ref _routerProbeInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = RefreshRouterStatusInBackgroundAsync();
    }

    private async Task RefreshRouterStatusInBackgroundAsync()
    {
        try
        {
            await EnvironmentStep.RefreshRouterStatusAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _routerProbeInFlight, 0);
        }
    }

    private void NotifyDaemonBannerProperties()
    {
        WizardUiThread.InvokeOrPost(() =>
        {
            OnPropertyChanged(nameof(DaemonBadgeText));
            OnPropertyChanged(nameof(IsDaemonRunning));
            OnPropertyChanged(nameof(IsDaemonPortConflict));
            OnPropertyChanged(nameof(ShowDaemonWarningBanner));
            OnPropertyChanged(nameof(ShowInlineStartRouterButton));
            OnPropertyChanged(nameof(DaemonBannerMessage));
        });
    }

    private void NotifyNavigationStateChanged()
    {
        WizardUiThread.InvokeOrPost(() =>
        {
            OnPropertyChanged(nameof(IsAnyStepBusy));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanFinish));
            BackCommand.NotifyCanExecuteChanged();
            NextCommand.NotifyCanExecuteChanged();
            FinishCommand.NotifyCanExecuteChanged();
            StartRouterFromBannerCommand.NotifyCanExecuteChanged();
            GoToStep1Command.NotifyCanExecuteChanged();
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EnvironmentStep.PropertyChanged -= OnStepPropertyChanged;
        TargetsStep.PropertyChanged -= OnStepPropertyChanged;
        CloudStep.PropertyChanged -= OnStepPropertyChanged;
        BenchmarkStep.PropertyChanged -= OnStepPropertyChanged;
        RecommendationsStep.PropertyChanged -= OnStepPropertyChanged;

        foreach (var step in _steps)
        {
            await step.DisposeAsync().ConfigureAwait(false);
        }
    }
}
