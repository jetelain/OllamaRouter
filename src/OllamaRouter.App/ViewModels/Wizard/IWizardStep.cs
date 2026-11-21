namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// The lifecycle contract of one Setup Wizard step (task #103, spec §3.5; 5 steps since task
/// #151): the root <see cref="SetupWizardViewModel"/> drives <see cref="OnStepEnteredAsync"/> /
/// <see cref="OnStepLeftAsync"/> as the user navigates (Step 2 (Fleet) and Step 4 (Benchmark)
/// start/stop their pollers here; Step 3 (Cloud) refreshes on entry; Steps 1 and 5 just
/// refresh / are no-ops) and disposes every step on close.
/// </summary>
internal interface IWizardStep : IAsyncDisposable
{
    /// <summary>Called when the step becomes the active step.</summary>
    Task OnStepEnteredAsync(CancellationToken ct = default);

    /// <summary>Called when the step stops being the active step.</summary>
    Task OnStepLeftAsync();
}
