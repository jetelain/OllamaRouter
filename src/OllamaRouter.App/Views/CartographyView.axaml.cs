using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.App.Views;

/// <summary>
/// The Model Cartography &amp; Preload tab view (task #102). The per-model Preload button
/// opens the <see cref="PreloadDialog"/> (preview-then-preload, owned by the dialog view
/// model); the per-model Uninstall button (hidden when the model is present on no target —
/// task #126) opens the <see cref="UninstallDialog"/> (destructive, Confirm-gated) and
/// refreshes the matrix immediately afterwards.
/// </summary>
public partial class CartographyView : UserControl
{
    /// <summary>Creates the view (the DataContext is the <see cref="CartographyViewModel"/>).</summary>
    public CartographyView()
    {
        InitializeComponent();
    }

    private void OnPreloadClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CartographyViewModel viewModel ||
            e.Source is not Control { DataContext: CartographyModelRowVm row } ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new PreloadDialog(viewModel.CreatePreloadDialog(row.Name));
        dialog.ShowDialog(owner);
    }

    private async void OnUninstallClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CartographyViewModel viewModel ||
            e.Source is not Control { DataContext: CartographyModelRowVm row } ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new UninstallDialog(viewModel.CreateUninstallDialog(row));
        await dialog.ShowDialog(owner);

        // The matrix reflects the deletion immediately (deleted cells read "not present"; the
        // 5 s poller keeps it consistent regardless — the daemon's /api/tags + /api/ps no
        // longer carry the model, so the resident-process table drops it too).
        await viewModel.RefreshAsync();
    }
}
