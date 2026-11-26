using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.Logging;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;
using OllamaRouter.Endpoints.RouterApi.Targets;

namespace OllamaRouter.App.Views;

/// <summary>
/// The Fleet &amp; Cloud Targets tab view (task #102; task #149 add entry points;
/// task #171 edit entry points): the two DEDICATED per-type add dialogs
/// (<see cref="AddOllamaNodeDialog"/> / <see cref="AddExternalProviderDialog"/>)
/// behind the "Add Ollama Node" / "Add External Provider" toolbar buttons; the two
/// DEDICATED per-type edit dialogs (<see cref="EditOllamaNodeDialog"/> for every
/// non-<c>openai</c> row / <see cref="EditExternalProviderDialog"/> for
/// <c>openai</c> rows, see <see cref="UseExternalProviderEditDialog"/>) handle EDIT;
/// delete confirms first.
/// Task #159: every handler runs through <see cref="SafeRun"/> with the tab's
/// <c>LastError</c>/<c>IsErrorVisible</c> message surface.
/// </summary>
public partial class FleetTargetsView : UserControl
{
    /// <summary>
    /// The task #171 edit-dispatch rule (testable without a window): an <c>openai</c>
    /// row (case-insensitive) edits through the external-provider dialog; EVERYTHING
    /// else (<c>ollama</c>, <c>osnative</c>, unknown / blank types) edits through the
    /// Ollama-node dialog — safe for osnative because the edit forms never send
    /// <c>type</c> (decision D3), so the protocol family is preserved.
    /// </summary>
    internal static bool UseExternalProviderEditDialog(string? type) =>
        string.Equals(type, "openai", StringComparison.OrdinalIgnoreCase);

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

        // Task #171: dispatch to the DEDICATED per-type edit dialog (the single-pass flow
        // mirrors the task #149 add handlers): Save → BuildUpdateRequest → the EditTarget
        // command; validation error → the banner stays up and the dialog re-opens (the
        // follow-up Save simply closes without submitting).
        Window dialog;
        Func<(UpdateTargetRequest Request, string? Error)> buildUpdateRequest;
        Action<string?> reportError;
        if (UseExternalProviderEditDialog(row.Type))
        {
            var providerForm = viewModel.CreateExternalProviderEditForm(row);
            dialog = new EditExternalProviderDialog(providerForm);
            buildUpdateRequest = providerForm.BuildUpdateRequest;
            reportError = providerForm.ReportError;
        }
        else
        {
            var ollamaForm = viewModel.CreateOllamaNodeEditForm(row);
            dialog = new EditOllamaNodeDialog(ollamaForm);
            buildUpdateRequest = ollamaForm.BuildUpdateRequest;
            reportError = ollamaForm.ReportError;
        }

        SafeRun.Run(
            async () =>
            {
                if (await dialog.ShowDialog<bool>(owner) is true)
                {
                    var (request, error) = buildUpdateRequest();
                    if (error is null)
                    {
                        viewModel.EditTargetCommand.Execute((row.Name, request));
                    }
                    else
                    {
                        reportError(error);
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
