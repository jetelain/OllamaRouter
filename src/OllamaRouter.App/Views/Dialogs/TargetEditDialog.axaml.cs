using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels;

namespace OllamaRouter.App.Views.Dialogs;

/// <summary>
/// The shared add/edit target dialog (task #102): one window for both modes (the mode flag
/// lives on the <see cref="TargetEditFormViewModel"/>). Save returns
/// <c>true</c> (the caller builds and submits the request); Cancel returns <c>false</c>.
/// </summary>
public partial class TargetEditDialog : Window
{
    /// <summary>Creates the dialog (XAML compiler hook; the parameterized constructor is used
    /// by the view code).</summary>
    public TargetEditDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Creates the dialog for the given form (add or edit mode).
    /// </summary>
    public TargetEditDialog(TargetEditFormViewModel form)
    {
        InitializeComponent();
        DataContext = form ?? throw new ArgumentNullException(nameof(form));
        Title = form.IsEdit ? "Edit Target" : "Add Target";
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) =>
        Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) =>
        Close(false);

    private void OnAddModelRowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TargetEditFormViewModel form)
        {
            form.AddModelRowCommand.Execute(null);
        }
    }

    private void OnRemoveModelRowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TargetEditFormViewModel form &&
            e.Source is Control { DataContext: ModelRosterEntry entry })
        {
            form.RemoveModelRowCommand.Execute(entry);
        }
    }
}
