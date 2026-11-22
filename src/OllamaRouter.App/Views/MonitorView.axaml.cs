using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.Logging;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.App.Views;

/// <summary>
/// The Monitor &amp; Savings tab view (task #102). The "Reclaim Local VRAM (Gaming Mode)"
/// action confirms through a dialog before invoking the view-model command (the VM never
/// modal-blocks). Task #159: the handler runs through <see cref="SafeRun"/> with the
/// tab's <c>LastError</c>/<c>IsErrorVisible</c> message surface.
/// </summary>
public partial class MonitorView : UserControl
{
    /// <summary>Creates the view (the DataContext is the <see cref="MonitorViewModel"/>).</summary>
    public MonitorView()
    {
        InitializeComponent();
    }

    private void OnReclaimLocalVramClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MonitorViewModel viewModel ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        SafeRun.Run(
            async () =>
            {
                var confirmed = await Confirm.ShowAsync(
                    owner,
                    "Unload ALL models from every LOCAL target and administratively DISABLE them? " +
                    "The GPU VRAM is returned to the OS (gaming mode). Re-enable the targets and " +
                    "preload models again when you are done.",
                    "Reclaim Local VRAM (Gaming Mode)");
                if (confirmed)
                {
                    viewModel.ReclaimLocalVramCommand.Execute(null);
                }
            },
            surfaceError: message =>
            {
                viewModel.LastError = message;
                viewModel.IsErrorVisible = true;
            });
    }
}
