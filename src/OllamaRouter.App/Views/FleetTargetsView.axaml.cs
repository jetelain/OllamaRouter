using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.App.Views;

/// <summary>
/// The Fleet &amp; Cloud Targets tab view (task #102). The shared
/// <see cref="TargetEditDialog"/> handles add/edit (mode flag); delete confirms first.
/// </summary>
public partial class FleetTargetsView : UserControl
{
    /// <summary>Creates the view (the DataContext is the <see cref="FleetTargetsViewModel"/>).</summary>
    public FleetTargetsView()
    {
        InitializeComponent();
    }

    private FleetTargetsViewModel? ViewModel => DataContext as FleetTargetsViewModel;

    private async void OnAddTargetClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var form = viewModel.CreateAddForm();
        var dialog = new TargetEditDialog(form);
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
    }

    private async void OnEditTargetClick(object? sender, RoutedEventArgs e)
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
    }

    private async void OnDeleteTargetClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || e.Source is not Control { DataContext: FleetTargetRow row } ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var confirmed = await Confirm.ShowAsync(
            owner,
            $"Delete the fleet target \"{row.Name}\"? This removes it from the router configuration.",
            "Delete Target");
        if (confirmed)
        {
            viewModel.DeleteTargetCommand.Execute(row.Name);
        }
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
}
