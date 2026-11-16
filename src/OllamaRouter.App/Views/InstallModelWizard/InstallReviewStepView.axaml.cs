using Avalonia.Controls;

namespace OllamaRouter.App.Views.InstallModelWizard;

/// <summary>
/// Step 2 — Review &amp; Install view of the Install Model wizard (task #125): the per-target
/// summary + live run rows (status text, byte-based progress bar, error) and the overall
/// run result. The Install / Cancel-install / Done actions live in the window's bottom bar.
/// </summary>
public partial class InstallReviewStepView : UserControl
{
    public InstallReviewStepView()
    {
        InitializeComponent();
    }
}
