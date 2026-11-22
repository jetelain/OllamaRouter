using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.Logging;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.App.Views;

/// <summary>
/// The main window (task #102): connection bar + the four dashboard tabs. The window
/// lifecycle drives the view models: open → daemon auto-start + status probe + polling
/// start; close → polling stop + tab disposal. Task #159: every handler runs through
/// <see cref="SafeRun"/> (log-only — <c>MainWindowViewModel</c> has no message surface).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Creates the window (the DataContext is set by the composition root).</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnSelectInstanceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            var dialog = new ConnectInstanceDialog(viewModel.RemoteUrl, viewModel.RemoteApiKey, viewModel.IsLocalMode);
            SafeRun.Run(async () =>
            {
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
            });
        }
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainWindowViewModel viewModel)
        {
            SafeRun.Run(() => viewModel.StartAsync());
        }
    }

    protected override void OnClosed(System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            // Task #159: base.OnClosed(e) stays AFTER the awaited disposal (today's exact
            // ordering — SafeRun never reorders the body).
            SafeRun.Run(async () =>
            {
                await viewModel.DisposeAsync();
                base.OnClosed(e);
            });
        }
        else
        {
            base.OnClosed(e);
        }
    }
}
