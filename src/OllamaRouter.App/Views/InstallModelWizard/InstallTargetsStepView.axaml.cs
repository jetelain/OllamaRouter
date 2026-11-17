using Avalonia.Controls;

namespace OllamaRouter.App.Views.InstallModelWizard;

/// <summary>
/// Step 1 — Targets view of the Install Model wizard (task #125, unified per-target inputs
/// by task #131): the checkable target rows (liveness, hardware with a full-text ToolTip,
/// present badge, per-target quant/tag + context inputs + alias hint chip in BOTH modes,
/// assisted-mode suggestion) and the always-visible "create context alias" switch.
/// </summary>
public partial class InstallTargetsStepView : UserControl
{
    public InstallTargetsStepView()
    {
        InitializeComponent();
    }
}
