using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OllamaRouter.App.Views.Dialogs;

public sealed record ConnectInstanceResult(bool IsLocal, string? RemoteUrl, string? RemoteApiKey);

public partial class ConnectInstanceDialog : Window
{
    public ConnectInstanceDialog()
    {
        InitializeComponent();
    }

    public ConnectInstanceDialog(string? currentRemoteUrl, string? currentApiKey, bool isLocal)
    {
        InitializeComponent();
        if (isLocal)
        {
            LocalRadio.IsChecked = true;
        }
        else
        {
            RemoteRadio.IsChecked = true;
        }

        if (!string.IsNullOrWhiteSpace(currentRemoteUrl))
        {
            RemoteUrlBox.Text = currentRemoteUrl;
        }
        if (!string.IsNullOrWhiteSpace(currentApiKey))
        {
            RemoteKeyBox.Text = currentApiKey;
        }
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        var isLocal = LocalRadio.IsChecked == true;
        var url = RemoteUrlBox.Text?.Trim();
        var key = RemoteKeyBox.Text;
        Close(new ConnectInstanceResult(isLocal, url, key));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}

