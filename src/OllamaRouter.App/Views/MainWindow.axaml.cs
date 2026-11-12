using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;

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

    private async void OnSelectInstanceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            var dialog = new ConnectInstanceDialog(viewModel.RemoteUrl, viewModel.RemoteApiKey, viewModel.IsLocalMode);
            var result = await dialog.ShowDialog<ConnectInstanceResult>(this);
            if (result != null)
            {
                if (result.IsLocal)
                {
                    viewModel.ConnectLocalCommand.Execute(null);
                }
                else if (!string.IsNullOrWhiteSpace(result.RemoteUrl))
                {
                    viewModel.RemoteUrl = result.RemoteUrl;
                    viewModel.RemoteApiKey = result.RemoteApiKey ?? string.Empty;
                    viewModel.ConnectRemoteCommand.Execute(null);
                }
            }
        }
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
