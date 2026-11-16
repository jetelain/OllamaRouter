using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.Client;

namespace OllamaRouter.App.ViewModels.InstallModelWizard;

/// <summary>
/// The root state machine of the modal "Install Model" wizard (task #125) — the 3-step
/// Model / Targets / Review &amp; Install flow following the setup-wizard root pattern
/// (step index + Back/Next/Finish commands with per-step <c>CanExecute</c> gates + the
/// <c>IWizardStep</c> lifecycle fan-out + <c>IAsyncDisposable</c>) WITHOUT the setup
/// wizard's app exit-code contract (a cancellable modal dialog: Cancel / Back / Next /
/// Install / Done; closing during a run is intercepted by the window, which cancels the
/// install via the review step's <see cref="InstallReviewStepViewModel.DisposeAsync"/>
/// before the VM is disposed).
/// </summary>
public sealed partial class InstallModelWizardViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IWizardStep[] _steps;

    public InstallModelWizardViewModel(Func<IOllamaRouterApiClient> clientAccessor)
    {
        if (clientAccessor is null)
        {
            throw new ArgumentNullException(nameof(clientAccessor));
        }

        var modelStep = new InstallModelStepViewModel(clientAccessor);
        var targetsStep = new InstallTargetsStepViewModel(
            clientAccessor,
            () => modelStep.ModelRef,
            () => modelStep.Assisted);
        var reviewStep = new InstallReviewStepViewModel(
            clientAccessor,
            () => targetsStep,
            () => modelStep.ModelRef);

        _steps =
        [
            modelStep,
            targetsStep,
            reviewStep
        ];
        ModelStep = modelStep;
        TargetsStep = targetsStep;
        ReviewStep = reviewStep;

        _selectedStep = 0;
        BackCommand = new RelayCommand(() =>
        {
            if (SelectedStep > 0 && !ReviewStep.IsRunning)
            {
                SelectedStep--;
            }
        });
        NextCommand = new RelayCommand(() =>
        {
            if (ReviewStep.IsRunning)
            {
                return;
            }

            if (SelectedStep == 0 && ModelStep.IsValid)
            {
                SelectedStep = 1;
            }
            else if (SelectedStep == 1 && TargetsStep.IsValid)
            {
                SelectedStep = 2;
            }
        });
        CancelCommand = new RelayCommand(() => RequestClose(false, null));
        DoneCommand = new RelayCommand(
            () =>
            {
                if (ReviewStep.RunCompleted && !ReviewStep.IsRunning)
                {
                    RequestClose(true, ReviewStep.RunResultText);
                }
            },
            () => ReviewStep.RunCompleted && !ReviewStep.IsRunning);
    }

    /// <summary>The step 0 — Model view model (mode + the model reference).</summary>
    public InstallModelStepViewModel ModelStep { get; }

    /// <summary>The step 1 — Targets view model (rows + selection + the install plan).</summary>
    public InstallTargetsStepViewModel TargetsStep { get; }

    /// <summary>The step 2 — Review &amp; Install view model (run rows + the install loop).</summary>
    public InstallReviewStepViewModel ReviewStep { get; }

    /// <summary>The 0-based step index (0 = Model, 1 = Targets, 2 = Review &amp; Install).</summary>
    [ObservableProperty]
    private int _selectedStep;

    public IRelayCommand BackCommand { get; }

    public IRelayCommand NextCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand DoneCommand { get; }

    /// <summary>The window's CloseRequested event args (step 2 / cancel).</summary>
    public bool CloseRequested { get; private set; }

    /// <summary>The overall run result text when closing after a completed run (else null).</summary>
    public string? CloseRunResult { get; private set; }

    private IWizardStep CurrentStep => _steps[SelectedStep];

    partial void OnSelectedStepChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(Step0Active));
        OnPropertyChanged(nameof(Step1Active));
        OnPropertyChanged(nameof(Step2Active));
        _ = RunStepEnteredAsync(_steps[value]);
    }

    public bool Step0Active => SelectedStep == 0;

    public bool Step1Active => SelectedStep == 1;

    public bool Step2Active => SelectedStep == 2;

    /// <summary>Invoked by the window's Done / Cancel buttons (step 2) or the Cancel button.</summary>
    public event Action<bool, string?>? CloseRequestedSignal;

    private void RequestClose(bool completed, string? result)
    {
        CloseRequested = completed;
        CloseRunResult = result;
        CloseRequestedSignal?.Invoke(completed, result);
    }

    /// <summary>
    /// The step lifecycle's OnStepEnteredAsync fan-out — the setup-wizard pattern: awaited
    /// so a fetch-on-entry step (targets) has its data BEFORE the gate is queried.
    /// </summary>
    private async Task RunStepEnteredAsync(IWizardStep step)
    {
        try
        {
            await step.OnStepEnteredAsync().ConfigureAwait(true);
        }
        catch
        {
            // Step VMs surface fetch errors themselves (hint text); nothing to do.
        }
    }

    /// <summary>
    /// Cancellation of the window's closing WHILE an install is running (the window confirms
    /// with the user first): cancels the in-flight install so the close does not orphan it —
    /// then the window closes.
    /// </summary>
    public void CancelRunningInstall()
    {
        // The close was ALREADY confirmed: the window's re-entrant OnClosing (Close() while
        // the install task has not unwound yet — IsRunning still true) must close WITHOUT
        // re-confirming.
        CloseConfirmed = true;
        ReviewStep.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>True once the user confirmed the "cancel the in-progress install and close?"
    /// dialog (the window's OnClosing skips the confirm for the re-entrant close).</summary>
    internal bool CloseConfirmed { get; private set; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var step in _steps)
        {
            await step.DisposeAsync().ConfigureAwait(false);
        }
    }
}
