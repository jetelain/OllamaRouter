using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels;

namespace OllamaRouter.App.Views.Dialogs;

/// <summary>
/// The Uninstall Model dialog (task #126): the checkbox list of the targets where the model
/// is PRESENT (all pre-checked), the destructive-action warning and the per-target results
/// (the result panel + the Close button appear once the command ran). The CONFIRMATION step
/// is view-level (deliberately not in the view model — the VM stays host-free and
/// unit-testable): the Uninstall button builds the exact-enumeration message (model + every
/// checked target) and routes it through the shared <see cref="Confirm"/> dialog; on OK the
/// <c>UninstallCommand</c> fires and the dialog stays open to display the per-target
/// results (Cancel at either step sends NO request).
/// </summary>
public partial class UninstallDialog : Window
{
    /// <summary>Creates the dialog (XAML compiler hook; the parameterized constructor is used
    /// by the view code).</summary>
    public UninstallDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Creates the dialog for the given dialog view model.
    /// </summary>
    public UninstallDialog(UninstallDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private async void OnUninstallClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not UninstallDialogViewModel viewModel)
        {
            return;
        }

        var selected = viewModel.Targets.Where(t => t.IsSelected).Select(t => t.Target).ToList();
        if (selected.Count == 0)
        {
            // The validation error is surfaced by the command (no request is sent).
            await viewModel.UninstallCommand.ExecuteAsync(null);
            return;
        }

        var targets = string.Join("\n", selected.Select(t => $"  • {t}"));
        var confirmed = await Confirm.ShowAsync(
            this,
            $"Permanently delete model \"{viewModel.ModelName}\" from:\n{targets}\n"
            + "This removes the model files from the selected nodes and cannot be undone.",
            "Uninstall Model");
        if (confirmed)
        {
            await viewModel.UninstallCommand.ExecuteAsync(null);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
