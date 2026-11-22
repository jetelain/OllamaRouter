using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.Logging;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;
using OllamaRouter.App.Views.InstallModelWizard;

namespace OllamaRouter.App.Views;

/// <summary>
/// The Model Cartography &amp; Preload tab view (task #102). The header's Install Model button
/// (task #125) opens the modal <see cref="InstallModelWizardWindow"/> (manual /
/// benchmark-assisted install onto one or more fleet targets) and refreshes the matrix
/// afterwards; the per-model Preload button opens the <see cref="PreloadDialog"/>
/// (preview-then-preload, owned by the dialog view model); the per-model Uninstall button
/// (hidden when the model is present on no target — task #126) opens the
/// <see cref="UninstallDialog"/> (destructive, Confirm-gated) and refreshes the matrix
/// immediately afterwards. Task #159: the async handlers run through <see cref="SafeRun"/>
/// with the tab's <c>LastError</c>/<c>IsErrorVisible</c> message surface.
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

    private void OnInstallModelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CartographyViewModel viewModel ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var window = new InstallModelWizardWindow(viewModel.CreateInstallModelWizard());
        SafeRun.Run(
            async () =>
            {
                await window.ShowDialog(owner);

                // Immediate matrix refresh after the wizard closes (the 5 s poll + the server-side
                // catalog refresh also cover it; this makes the install visible at once).
                await viewModel.RefreshAsync();
            },
            surfaceError: message =>
            {
                viewModel.LastError = message;
                viewModel.IsErrorVisible = true;
            });
    }

    private void OnUninstallClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CartographyViewModel viewModel ||
            e.Source is not Control { DataContext: CartographyModelRowVm row } ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new UninstallDialog(viewModel.CreateUninstallDialog(row));
        SafeRun.Run(
            async () =>
            {
                await dialog.ShowDialog(owner);

                // The matrix reflects the deletion immediately (deleted cells read "not present"; the
                // 5 s poller keeps it consistent regardless — the daemon's /api/tags + /api/ps no
                // longer carry the model, so the resident-process table drops it too).
                await viewModel.RefreshAsync();
            },
            surfaceError: message =>
            {
                viewModel.LastError = message;
                viewModel.IsErrorVisible = true;
            });
    }
}
