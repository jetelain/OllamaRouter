namespace OllamaRouter.App.ViewModels;

/// <summary>
/// Marshals UI-affine command/binding notifications (such as <c>NotifyCanExecuteChanged</c>,
/// which Avalonia's <c>Button.CanExecuteChanged</c> handles on the calling thread) onto
/// <see cref="Avalonia.Threading.Dispatcher.UIThread"/> when an Avalonia application is active.
/// In headless/unit tests without an <see cref="Avalonia.Application.Current"/>, runs
/// synchronously so command state remains deterministic.
/// <para>
/// Shared by the Setup Wizard and the main window/tab view models (task #173). INVARIANT:
/// the five tab view models (<c>MonitorViewModel</c>, <c>AgentSessionsViewModel</c>,
/// <c>FleetTargetsViewModel</c>, <c>CartographyViewModel</c>, <c>SettingsViewModel</c>) have
/// NO <c>CanExecute</c>-gated commands (all plain <c>[RelayCommand]</c>) — nothing to guard
/// there today. Any FUTURE <c>CanExecute</c>-gated command in a tab view model MUST notify
/// through <see cref="InvokeOrPost"/> (an unguarded <c>NotifyCanExecuteChanged</c> raised on
/// a pool thread throws Avalonia's cross-thread <c>InvalidOperationException</c> inside the
/// button's <c>CanExecuteChanged</c> handler).
/// </para>
/// </summary>
internal static class AppUiThread
{
    public static void InvokeOrPost(Action action)
    {
        if (Avalonia.Application.Current is null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
    }
}
