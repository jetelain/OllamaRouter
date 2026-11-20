using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels.InstallModelWizard;

namespace OllamaRouter.App.Views.InstallModelWizard;

/// <summary>
/// Step 1 — Targets view of the Install Model wizard (task #125, unified per-target inputs
/// by task #131): the checkable target rows (liveness, hardware with a full-text ToolTip,
/// present badge, per-target quant/tag + context inputs + alias hint chip in BOTH modes,
/// assisted-mode suggestion) and the always-visible "create context alias" switch. Task
/// #144 adds the per-row re-probe affordance on unprobed rows (the daemon-side hardware
/// probe, fired through the step view model).
/// </summary>
public partial class InstallTargetsStepView : UserControl
{
    public InstallTargetsStepView()
    {
        InitializeComponent();
    }

    private void OnReProbeClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Control { DataContext: InstallTargetRowVm row } ||
            DataContext is not InstallTargetsStepViewModel viewModel)
        {
            return;
        }

        viewModel.ReProbeHardwareCommand.Execute(row);
    }
}
