using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using OllamaRouter.App.Bootstrap;

namespace OllamaRouter.App;

/// <summary>
/// The Fluent-themed application shell (task #102; task #103 adds the wizard mode). The Inter
/// font is registered automatically by the <c>Avalonia.Fonts.Inter</c> package. The window is
/// built through the composition root: the 4-step Setup Wizard when <c>--wizard</c> is passed
/// OR when no <c>config/config.json</c> exists in any config root (first run), else the dashboard.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Selects the main window: wizard mode when <c>--wizard</c> is present or this is a first
    /// run (no configured install in any root), else the dashboard.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var wizard = WizardArgs.IsWizard(desktop.Args) || FirstRunDetector.NeedsSetupWizard();
            desktop.MainWindow = wizard
                ? CompositionRoot.CreateWizardWindow(desktop)
                : CompositionRoot.CreateMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
