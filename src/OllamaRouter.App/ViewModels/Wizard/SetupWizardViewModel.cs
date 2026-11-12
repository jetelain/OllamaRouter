using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.Connection;

namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// The root view model of the 4-step Setup Wizard (task #103, spec §3.5): the step index,
/// Back / Next navigation (steps are freely navigable/skippable — validation is per-step and
/// Finish is the only hard gate), the Finish (exit 0) / Abandon (exit 1) exit signal, and the
/// disposal of all four step view models.
/// <para>
/// The exit-code contract (D2): <see cref="Finish"/> → exit <c>0</c>; <see cref="Abandon"/>
/// (window closed without Finish) → exit <c>1</c>. The window code-behind wires
/// <see cref="ExitSignal"/> to <c>IClassicDesktopStyleApplicationLifetime.TryShutdown(code)</c>.
/// </para>
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IWizardStep[] _steps;
    private bool _disposed;

    public SetupWizardViewModel(
        IAppConnectionService connection,
        WizardEnvironmentStepViewModel environmentStep,
        WizardTargetsStepViewModel targetsStep,
        WizardBenchmarkStepViewModel benchmarkStep,
        WizardRecommendationsStepViewModel recommendationsStep,
        Action<int>? exitSignal = null)
    {
        _ = connection ?? throw new ArgumentNullException(nameof(connection));
        EnvironmentStep = environmentStep ?? throw new ArgumentNullException(nameof(environmentStep));
        TargetsStep = targetsStep ?? throw new ArgumentNullException(nameof(targetsStep));
        BenchmarkStep = benchmarkStep ?? throw new ArgumentNullException(nameof(benchmarkStep));
        RecommendationsStep = recommendationsStep ?? throw new ArgumentNullException(nameof(recommendationsStep));
        _steps = [EnvironmentStep, TargetsStep, BenchmarkStep, RecommendationsStep];
        ExitSignal = exitSignal;

        BackCommand = new AsyncRelayCommand(async () => await GoToAsync(SelectedStep - 1), () => SelectedStep > 0);
        NextCommand = new AsyncRelayCommand(async () => await GoToAsync(SelectedStep + 1), () => SelectedStep < 3);
        FinishCommand = new AsyncRelayCommand(async () => Finish(), () => SelectedStep == 3);
    }

    /// <summary>Step 1 — Local Ollama for the Router.</summary>
    public WizardEnvironmentStepViewModel EnvironmentStep { get; }

    /// <summary>Step 2 — Fleet Targets.</summary>
    public WizardTargetsStepViewModel TargetsStep { get; }

    /// <summary>Step 3 — Micro-Benchmark.</summary>
    public WizardBenchmarkStepViewModel BenchmarkStep { get; }

    /// <summary>Step 4 — Role Recommendations.</summary>
    public WizardRecommendationsStepViewModel RecommendationsStep { get; }

    /// <summary>The active step index (0–3).</summary>
    [ObservableProperty]
    private int _selectedStep;

    /// <summary>Whether the wizard completed via Finish (guards the Abandon exit signal).</summary>
    public bool Finished { get; private set; }

    /// <summary>The exit-code sink: <c>0</c> = finished, <c>1</c> = abandoned (wired by the
    /// window to <c>TryShutdown</c>).</summary>
    internal Action<int>? ExitSignal { get; set; }

    public IAsyncRelayCommand BackCommand { get; }

    public IAsyncRelayCommand NextCommand { get; }

    public IAsyncRelayCommand FinishCommand { get; }

    /// <summary>Whether Back is available (not on step 0).</summary>
    public bool CanGoBack => SelectedStep > 0;

    /// <summary>Whether Next is available (not on the last step, 3).</summary>
    public bool CanGoNext => SelectedStep < 3;

    /// <summary>Whether Finish is available (only on the last step, 3).</summary>
    public bool CanFinish => SelectedStep == 3;

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
    /// Navigates to <paramref name="newStep"/>: steps the index and drives the step lifecycle
    /// (the outgoing step's <c>OnStepLeft</c>, then the incoming step's <c>OnStepEntered</c>).
    /// </summary>
    public async Task GoToAsync(int newStep)
    {
        if (newStep < 0 || newStep > 3 || newStep == SelectedStep)
        {
            return;
        }

        var oldStep = SelectedStep;
        SelectedStep = newStep;
        await _steps[oldStep].OnStepLeftAsync().ConfigureAwait(false);
        await _steps[newStep].OnStepEnteredAsync(CancellationToken.None).ConfigureAwait(false);
    }

    partial void OnSelectedStepChanged(int value)
    {
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        FinishCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var step in _steps)
        {
            await step.DisposeAsync().ConfigureAwait(false);
        }
    }
}
