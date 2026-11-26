using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels;

namespace OllamaRouter.App.Views.Dialogs;

/// <summary>
/// The dedicated "Edit Ollama Node" dialog (task #171): the edit form for Ollama-protocol
/// targets, split out of the retired monolithic shared target-edit dialog (its
/// <see cref="OllamaNodeEditFormViewModel"/> handles the logic). Save returns
/// <c>true</c> (the caller builds and submits the request); Cancel returns <c>false</c>.
/// The dialog stays open on a failed Save (the single-pass
/// <c>ReportError</c> + <c>Show()</c> contract — the follow-up Save closes without
/// submitting).
/// </summary>
public partial class EditOllamaNodeDialog : Window
{
    /// <summary>Creates the dialog (XAML compiler hook; the parameterized constructor is
    /// used by the view code).</summary>
    public EditOllamaNodeDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Creates the dialog for the given edit form (pre-filled from the row).
    /// </summary>
    public EditOllamaNodeDialog(OllamaNodeEditFormViewModel form)
    {
        InitializeComponent();
        DataContext = form ?? throw new ArgumentNullException(nameof(form));
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) =>
        Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) =>
        Close(false);
}
