using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace OllamaRouter.App;

/// <summary>
/// The Fluent-themed application shell (task #102). The Inter font is registered
/// automatically by the <c>Avalonia.Fonts.Inter</c> package. The window is built through
/// the composition root when the framework is initialized.
/// </summary>
public partial class App : Application
{
    /// <summary>Unknown command-line arguments are deliberately ignored in task #102 (the
    /// <c>--wizard</c> branch arrives with task #103).</summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CompositionRoot.CreateMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
