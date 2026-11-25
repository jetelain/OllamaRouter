namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// Marshals UI-affine command/binding notifications (such as <c>NotifyCanExecuteChanged</c>,
/// which Avalonia's <c>Button.CanExecuteChanged</c> handles on the calling thread) onto
/// <see cref="Avalonia.Threading.Dispatcher.UIThread"/> when an Avalonia application is active.
/// In headless/unit tests without an <see cref="Avalonia.Application.Current"/>, runs
/// synchronously so command state remains deterministic.
/// </summary>
internal static class WizardUiThread
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
