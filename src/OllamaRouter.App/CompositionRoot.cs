using OllamaRouter.App.Connection;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views;
using OllamaRouter.Security;

namespace OllamaRouter.App;

/// <summary>
/// The single composition (wiring) point of the desktop application (task #102, spec §2):
/// <list type="number">
/// <item>
/// <c>IDefaultKeyReader keyReader = DefaultKeyReader.CreateDefault();</c>
/// </item>
/// <item>
/// <c>IAppConnectionService connection = new AppConnectionService(keyReader);</c> (default
/// profile = Local)
/// </item>
/// <item>
/// The four tab view models, constructed with <c>connection.GetClient</c> (a
/// <c>Func&lt;IOllamaRouterApiClient&gt;</c> accessor so a reconnection swaps the client without
/// rebuilding the tab tree)
/// </item>
/// <item>
/// The main view model + the <see cref="MainWindow"/>
/// </item>
/// </list>
/// Extension point for task #103 (Setup Wizard): <c>App</c> argument handling is deliberately
/// minimal in task #102 (unknown args ignored); the <c>--wizard</c> branch lands in #103.
/// </summary>
internal static class CompositionRoot
{
    /// <summary>Builds the connection service with the production <c>DefaultKeyReader</c>
    /// (the default profile is Local).</summary>
    public static IAppConnectionService CreateConnectionService() =>
        new AppConnectionService(DefaultKeyReader.CreateDefault());

    /// <summary>Builds the main window with its full view-model tree.</summary>
    public static MainWindow CreateMainWindow()
    {
        var connection = CreateConnectionService();
        var monitor = new MonitorViewModel(connection.GetClient);
        var fleet = new FleetTargetsViewModel(connection.GetClient);
        var cartography = new CartographyViewModel(connection.GetClient);
        var settings = new SettingsViewModel(connection.GetClient);
        var mainViewModel = new MainWindowViewModel(connection, monitor, fleet, cartography, settings);
        return new MainWindow { DataContext = mainViewModel };
    }
}
