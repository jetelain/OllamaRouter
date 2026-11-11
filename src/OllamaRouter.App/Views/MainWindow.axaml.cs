using Avalonia.Controls;
using OllamaRouter.App.ViewModels;

namespace OllamaRouter.App.Views;

/// <summary>
/// The main window (task #102): connection bar + the four dashboard tabs. The window
/// lifecycle drives the view models: open → daemon auto-start + status probe + polling
/// start; close → polling stop + tab disposal.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Creates the window (the DataContext is set by the composition root).</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override async void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.StartAsync();
        }
    }

    protected override async void OnClosed(System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.DisposeAsync();
        }

        base.OnClosed(e);
    }
}
