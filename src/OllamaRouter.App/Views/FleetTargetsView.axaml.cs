using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.Logging;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.App.Views;

/// <summary>
/// The Fleet &amp; Cloud Targets tab view (task #102; task #149 entry points): the two
/// DEDICATED per-type add dialogs (<see cref="AddOllamaNodeDialog"/> / <see cref="AddExternalProviderDialog"/>)
/// behind the "Add Ollama Node" / "Add External Provider" toolbar buttons; the shared
/// <see cref="TargetEditDialog"/> handles EDIT only; delete confirms first.
/// Task #159: every handler runs through <see cref="SafeRun"/> with the tab's
/// <c>LastError</c>/<c>IsErrorVisible</c> message surface.
/// </summary>
public partial class FleetTargetsView : UserControl
{
    /// <summary>Creates the view (the DataContext is the <see cref="FleetTargetsViewModel"/>).</summary>
    public FleetTargetsView()
    {
        InitializeComponent();
    }

    private FleetTargetsViewModel? ViewModel => DataContext as FleetTargetsViewModel;

    private void OnAddOllamaNodeClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var form = viewModel.CreateOllamaNodeAddForm();
        var dialog = new AddOllamaNodeDialog(form);
        SafeRun.Run(
            async () =>
            {
                if (await dialog.ShowDialog<bool>(owner) is true)
                {
                    var (request, error) = form.BuildAddRequest();
                    if (error is null)
                    {
                        viewModel.AddTargetCommand.Execute(request);
                    }
                    else
                    {
                        form.ReportError(error);
                        dialog.Show(); // keep the dialog open on the validation error
                    }
                }
            },
            surfaceError: message =>
            {
                viewModel.LastError = message;
                viewModel.IsErrorVisible = true;
            });
    }

    private void OnAddExternalProviderClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var form = viewModel.CreateExternalProviderAddForm();
        var dialog = new AddExternalProviderDialog(form);
        SafeRun.Run(
            async () =>
            {
                if (await dialog.ShowDialog<bool>(owner) is true)
                {
                    var (request, error) = form.BuildAddRequest();
                    if (error is null)
                    {
                        viewModel.AddTargetCommand.Execute(request);
                    }
                    else
                    {
                        form.ReportError(error);
                        dialog.Show(); // keep the dialog open on the validation error
                    }
                }
            },
            surfaceError: message =>
            {
                viewModel.LastError = message;
                viewModel.IsErrorVisible = true;
            });
    }

    private void OnEditTargetClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || TopLevel.GetTopLevel(this) is not Window owner ||
            e.Source is not Control { DataContext: FleetTargetRow row })
        {
            return;
        }

        var form = viewModel.CreateEditForm(row);
        form.ExistingApiKeyConfigured = row.ApiKeyConfigured;
        var dialog = new TargetEditDialog(form);
        SafeRun.Run(
            async () =>
            {
                if (await dialog.ShowDialog<bool>(owner) is true)
                {
                    var (request, error) = form.BuildUpdateRequest();
                    if (error is null)
                    {
                        viewModel.EditTargetCommand.Execute((row.Name, request));
                    }
                    else
                    {
                        form.ReportError(error);
                        dialog.Show();
                    }
                }
            },
            surfaceError: message =>
            {
                viewModel.LastError = message;
                viewModel.IsErrorVisible = true;
            });
    }

    private void OnDeleteTargetClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || e.Source is not Control { DataContext: FleetTargetRow row } ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        SafeRun.Run(
            async () =>
            {
                var confirmed = await Confirm.ShowAsync(
                    owner,
                    $"Delete the fleet target \"{row.Name}\"? This removes it from the router configuration.",
                    "Delete Target");
                if (confirmed)
                {
                    viewModel.DeleteTargetCommand.Execute(row.Name);
                }
            },
            surfaceError: message =>
            {
                viewModel.LastError = message;
                viewModel.IsErrorVisible = true;
            });
    }

    private void OnTogglePersistedClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || e.Source is not Control { DataContext: FleetTargetRow row })
        {
            return;
        }

        viewModel.TogglePersistedEnabledCommand.Execute(row);
    }

    private void OnUpdateHardwareClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || e.Source is not Control { DataContext: FleetTargetRow row })
        {
            return;
        }

        viewModel.UpdateHardwareCommand.Execute(row);
    }
}
