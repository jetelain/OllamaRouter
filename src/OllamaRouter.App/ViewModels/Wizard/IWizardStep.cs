namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// The lifecycle contract of one Setup Wizard step (task #103, spec §3.5): the root
/// <see cref="SetupWizardViewModel"/> drives <see cref="OnStepEnteredAsync"/> /
/// <see cref="OnStepLeftAsync"/> as the user navigates (Steps 2 and 3 start/stop their pollers
/// here; Steps 1 and 4 just refresh / are no-ops) and disposes every step on close.
/// </summary>
internal interface IWizardStep : IAsyncDisposable
{
    /// <summary>Called when the step becomes the active step.</summary>
    Task OnStepEnteredAsync(CancellationToken ct = default);

    /// <summary>Called when the step stops being the active step.</summary>
    Task OnStepLeftAsync();
}
