using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.Logging;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;
using OllamaRouter.Endpoints.RouterApi.ApiKeys;

namespace OllamaRouter.App.Views;

/// <summary>
/// The Settings &amp; API Keys tab view (task #102). The destructive actions (delete key,
/// rotate default key) confirm through a dialog before invoking the view-model commands.
/// Task #159: every handler is a plain <c>void</c> that runs through <see cref="SafeRun"/> —
/// the Settings tab has a <c>RouterTabViewModel</c> <c>LastError</c>/<c>IsErrorVisible</c>
/// message surface, so faults additionally surface there instead of dying.
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>Creates the view (the DataContext is the <see cref="SettingsViewModel"/>).</summary>
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnDeleteKeyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel ||
            e.Source is not Control { DataContext: ApiKeyInfoResponse key } ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        SafeRun.Run(
            async () =>
            {
                var confirmed = await Confirm.ShowAsync(
                    owner,
                    $"Delete the API key \"{key.Name}\"? Clients using it lose access immediately.",
                    "Delete API Key");
                if (confirmed)
                {
                    viewModel.DeleteKeyCommand.Execute(key.Name);
                }
            },
            surfaceError: message =>
            {
                viewModel.LastError = message;
                viewModel.IsErrorVisible = true;
            });
    }

    private void OnRotateDefaultKeyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        SafeRun.Run(
            async () =>
            {
                var confirmed = await Confirm.ShowAsync(
                    owner,
                    "Rotate the default key? The PREVIOUS default key stops working IMMEDIATELY (no " +
                    "grace period). Local clients keep working (the default key file is re-read per " +
                    "request). Copy the new key before closing this dialog — it is shown exactly once.",
                    "Rotate Default Key");
                if (confirmed)
                {
                    viewModel.RotateDefaultKeyCommand.Execute(null);
                }
            },
            surfaceError: message =>
            {
                viewModel.LastError = message;
                viewModel.IsErrorVisible = true;
            });
    }
}
