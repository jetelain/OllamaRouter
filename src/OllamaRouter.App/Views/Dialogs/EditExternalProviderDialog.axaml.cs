using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels;

namespace OllamaRouter.App.Views.Dialogs;

/// <summary>
/// The dedicated "Edit External Provider" dialog (task #171): the edit form for
/// OpenAI-compatible endpoints, split out of the retired monolithic shared target-edit
/// dialog (its <see cref="ExternalProviderEditFormViewModel"/> handles the
/// logic). Save returns <c>true</c> (the caller builds and submits the request); Cancel
/// returns <c>false</c>. The dialog stays open on a failed Save (the single-pass
/// <c>ReportError</c> + <c>Show()</c> contract — the follow-up Save closes without
/// submitting).
/// </summary>
public partial class EditExternalProviderDialog : Window
{
    /// <summary>Creates the dialog (XAML compiler hook; the parameterized constructor is
    /// used by the view code).</summary>
    public EditExternalProviderDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Creates the dialog for the given edit form (pre-filled from the row).
    /// </summary>
    public EditExternalProviderDialog(ExternalProviderEditFormViewModel form)
    {
        InitializeComponent();
        DataContext = form ?? throw new ArgumentNullException(nameof(form));
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) =>
        Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) =>
        Close(false);

    /// <summary>Removes the roster row the "Remove" button sits on (the
    /// <c>Tag="{Binding}"</c> row-item pattern of the retired shared dialog).</summary>
    private void OnRemoveModelRowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExternalProviderEditFormViewModel form &&
            e.Source is Control { DataContext: ModelRosterEntry entry })
        {
            form.RemoveModelRowCommand.Execute(entry);
        }
    }
}
