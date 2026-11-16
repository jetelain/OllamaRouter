using Avalonia.Controls;

namespace OllamaRouter.App.Views.InstallModelWizard;

/// <summary>
/// Step 1 — Targets view of the Install Model wizard (task #125): the checkable target
/// rows (liveness, hardware, present badge, assisted-mode per-target context + suggestion)
/// and the manual-mode shared context / alias switch / preview.
/// </summary>
public partial class InstallTargetsStepView : UserControl
{
    public InstallTargetsStepView()
    {
        InitializeComponent();
    }
}
