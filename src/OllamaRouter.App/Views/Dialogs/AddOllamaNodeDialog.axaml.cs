using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels;

namespace OllamaRouter.App.Views.Dialogs;

/// <summary>
/// The dedicated "Add Ollama Node" dialog (task #149): the from-scratch add form for
/// Ollama-protocol targets (Name / URL / Max Parallel / optional ssh credential / test-
/// connection checkbox — no type dropdown, no API key, no provider, no models, no power).
/// Save returns <c>true</c> (the caller builds and submits the request); Cancel returns
/// <c>false</c>.
/// </summary>
public partial class AddOllamaNodeDialog : Window
{
    /// <summary>Creates the dialog (XAML compiler hook; the parameterized constructor is used
    /// by the view code).</summary>
    public AddOllamaNodeDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Creates the dialog for the given form.
    /// </summary>
    public AddOllamaNodeDialog(OllamaNodeAddFormViewModel form)
    {
        InitializeComponent();
        DataContext = form ?? throw new ArgumentNullException(nameof(form));
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) =>
        Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) =>
        Close(false);
}
