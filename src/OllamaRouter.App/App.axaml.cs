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
    /// Selects the main window: wizard mode when the parsed launch options request it (the
    /// <c>--wizard</c> flag or an <c>ollamarouter://wizard</c>/<c>ollamarouter://setup</c> URI)
    /// OR this is a first run (no configured install in any root), else the dashboard on the
    /// launch options' initial tab (default: Monitor).
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var launch = Program.LaunchOptions;
            var wizard = launch.Wizard || FirstRunDetector.NeedsSetupWizard();
            desktop.MainWindow = wizard
                ? CompositionRoot.CreateWizardWindow(desktop)
                : CompositionRoot.CreateMainWindow(launch.InitialTabIndex ?? 0);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
