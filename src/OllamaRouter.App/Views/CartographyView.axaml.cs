using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.App.Views;

/// <summary>
/// The Model Cartography &amp; Preload tab view (task #102). The per-model Preload button
/// opens the <see cref="PreloadDialog"/> (preview-then-preload, owned by the dialog view
/// model).
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
}
