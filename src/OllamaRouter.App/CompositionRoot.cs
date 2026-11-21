using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using OllamaRouter.App.Connection;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.App.Views;
using OllamaRouter.App.Views.Wizard;
using OllamaRouter.Security;

namespace OllamaRouter.App;

/// <summary>
/// The single composition (wiring) point of the desktop application (task #102, spec §2):
/// <list type="number">
/// <item>
/// <c>IDefaultKeyReader keyReader = OllamaRouterDefaultKeyReader.Create();</c>
/// </item>
/// <item>
/// <c>IAppConnectionService connection = new AppConnectionService(keyReader);</c> (default
/// profile = Local)
/// </item>
/// <item>
/// The five tab view models, constructed with <c>connection.GetClient</c> (a
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
    /// <summary>Builds the connection service with the production <c>OllamaRouterDefaultKeyReader</c>
    /// (the default profile is Local).</summary>
    public static IAppConnectionService CreateConnectionService() =>
        new AppConnectionService(OllamaRouterDefaultKeyReader.Create());

    /// <summary>
    /// Builds the main window with its full view-model tree, opening on
    /// <paramref name="initialTabIndex"/> (0 = Monitor, 1 = Agents &amp; Sessions, 2 = Fleet,
    /// 3 = Cartography, 4 = Settings — the <c>MainWindow.axaml</c> <c>TabItem</c> order).
    /// </summary>
    public static MainWindow CreateMainWindow(int initialTabIndex = 0)
    {
        var connection = CreateConnectionService();
        var monitor = new MonitorViewModel(connection.GetClient);
        var sessions = new AgentSessionsViewModel(connection.GetClient);
        var fleet = new FleetTargetsViewModel(connection.GetClient);
        var cartography = new CartographyViewModel(connection.GetClient);
        var settings = new SettingsViewModel(connection.GetClient);
        var mainViewModel = new MainWindowViewModel(
            connection, monitor, sessions, fleet, cartography, settings, initialTabIndex);
        return new MainWindow { DataContext = mainViewModel };
    }

    /// <summary>
    /// Builds the 4-step Setup Wizard window with its full view-model tree (task #103, spec
    /// §3.3). The local-profile connection, the real <see cref="LocalOllamaConfigurator"/>, the
    /// four step view models and the root view model (whose <c>exitSignal</c> wires to
    /// <see cref="IClassicDesktopStyleApplicationLifetime.TryShutdown"/> for the
    /// Finish=0 / abandoned=1 exit-code contract).
    /// </summary>
    public static Window CreateWizardWindow(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        var connection = CreateConnectionService();
        var configurator = new LocalOllamaConfigurator();
        var environmentStep = new WizardEnvironmentStepViewModel(configurator, connection);

        WizardTargetsStepViewModel targetsStep = null!;
        targetsStep = new WizardTargetsStepViewModel(connection.GetClient, ct => targetsStep.RefreshAsync(ct));

        // Task #151: the new Step 3 (Cloud & External Providers) — entry-refresh only (no
        // poller), so it needs no self-referential tick wiring.
        var cloudStep = new WizardCloudStepViewModel(connection.GetClient);

        WizardBenchmarkStepViewModel benchmarkStep = null!;
        benchmarkStep = new WizardBenchmarkStepViewModel(connection.GetClient, ct => benchmarkStep.RefreshAsync(ct));

        SetupWizardViewModel root = null!;
        // Task #151: the micro-benchmark is now step index 3 (Step 4).
        var recommendationsStep = new WizardRecommendationsStepViewModel(
            connection.GetClient, goToBenchmark: () => _ = root.GoToAsync(3));

        root = new SetupWizardViewModel(
            connection, environmentStep, targetsStep, cloudStep, benchmarkStep, recommendationsStep,
            exitSignal: code => lifetime.TryShutdown(code));

        return new SetupWizardWindow { DataContext = root };
    }
}
