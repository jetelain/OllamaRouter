using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.Logging;
using OllamaRouter.App.ViewModels.Wizard;

namespace OllamaRouter.App.Views.Wizard;

/// <summary>
/// Step 2 — Fleet Targets (task #103; reworked by task #151). The DataContext is the
/// <c>WizardTargetsStepViewModel</c> (set by the wizard window). The "Add Ollama Node" /
/// "Add Provider" buttons open the SAME dedicated dialogs the main app's Fleet tab uses
/// (task #149) via <see cref="WizardDialogLauncher"/> — modal ownership needs the Window top
/// level (exactly the <c>FleetTargetsView</c> pattern), so the dialogs are opened view-side
/// and the resulting request is handed to the view model's <c>AddTargetCommand</c>.
/// Task #159: the handlers run through <see cref="SafeRun"/> and surface faults in the
/// step's public <c>Message</c> area instead of dying.
/// </summary>
public partial class WizardTargetsStepView : UserControl
{
    public WizardTargetsStepView()
    {
        InitializeComponent();
    }

    private void OnAddOllamaNodeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WizardTargetsStepViewModel viewModel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        SafeRun.Run(
            async () =>
            {
                var request = await WizardDialogLauncher.ShowAddOllamaNodeAsync(owner);
                if (request is not null)
                {
                    viewModel.AddTargetCommand.Execute(request);
                }
            },
            surfaceError: message => viewModel.Message = message);
    }

    private void OnAddProviderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WizardTargetsStepViewModel viewModel
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
            surfaceError: message => viewModel.Message = message);
    }
}
