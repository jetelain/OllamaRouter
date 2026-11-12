using Avalonia.Controls;

namespace OllamaRouter.App.Views.Wizard;

/// <summary>
/// Step 1 — Local Ollama for the Router (task #103). The DataContext is the
/// <c>WizardEnvironmentStepViewModel</c> (set by the wizard window).
/// </summary>
public partial class WizardEnvironmentStepView : UserControl
{
    public WizardEnvironmentStepView()
    {
        InitializeComponent();
    }
}
