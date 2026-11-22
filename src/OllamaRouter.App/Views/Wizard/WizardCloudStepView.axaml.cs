using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.Logging;
using OllamaRouter.App.ViewModels.Wizard;

namespace OllamaRouter.App.Views.Wizard;

/// <summary>
/// Step 3 — Cloud &amp; External Providers (task #151). The DataContext is the
/// <c>WizardCloudStepViewModel</c> (set by the wizard window). The "Add Provider" button opens
/// the SAME dedicated dialog the main app's Fleet tab uses (task #149) via
/// <see cref="WizardDialogLauncher"/> — modal ownership needs the Window top level (exactly the
/// <c>FleetTargetsView</c> pattern), so the dialogs are opened view-side and the resulting
/// request is handed to the view model's <c>AddTargetCommand</c>.
/// Task #159: the handlers run through <see cref="SafeRun"/> and surface faults in the
/// step's public <c>Error</c> area instead of dying.
/// </summary>
public partial class WizardCloudStepView : UserControl
{
    public WizardCloudStepView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The ComboBox selection-change surface (task #151): the Avalonia 12 ComboBox has no
    /// Command property, so this event drives the VM's <c>SelectCloudModelCommand</c> (the
    /// persist-on-selection with revert-on-failure; a VM-driven re-seed of the selection is a
    /// no-op — the VM's remote-state guard suppresses it).
    /// </summary>
    private void OnCloudModelSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is WizardCloudStepViewModel viewModel)
        {
            SafeRun.Run(
                () => viewModel.SelectCloudModelCommand.ExecuteAsync(viewModel.SelectedCloudModel),
                surfaceError: message => viewModel.Error = message);
        }
    }

    private void OnAddProviderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WizardCloudStepViewModel viewModel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        SafeRun.Run(
            async () =>
            {
                var request = await WizardDialogLauncher.ShowExternalProviderAddAsync(owner);
                if (request is not null)
                {
                    viewModel.AddTargetCommand.Execute(request);
                }
            },
            surfaceError: message => viewModel.Error = message);
    }
}
