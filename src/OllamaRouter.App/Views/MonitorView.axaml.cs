using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.App.Views;

/// <summary>
/// The Monitor &amp; Savings tab view (task #102). The "Reclaim Local VRAM (Gaming Mode)"
/// action confirms through a dialog before invoking the view-model command (the VM never
/// modal-blocks).
/// </summary>
public partial class MonitorView : UserControl
{
    /// <summary>Creates the view (the DataContext is the <see cref="MonitorViewModel"/>).</summary>
    public MonitorView()
    {
        InitializeComponent();
    }

    private async void OnReclaimLocalVramClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MonitorViewModel viewModel ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

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
    }
}
