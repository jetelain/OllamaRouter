using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels;

namespace OllamaRouter.App.Views.Dialogs;

/// <summary>
/// The dedicated "Add External Provider" dialog (task #149): the from-scratch add form for
/// OpenAI-compatible endpoints (Name / Base URL / masked API key / provider label + the
/// OpenRouter / Groq / OpenAI / Custom preset chips — no type dropdown, no SSH fields, no
/// Max Parallel). Save returns <c>true</c> (the caller builds and submits the request);
/// Cancel returns <c>false</c>.
/// </summary>
public partial class AddExternalProviderDialog : Window
{
    /// <summary>Creates the dialog (XAML compiler hook; the parameterized constructor is used
    /// by the view code).</summary>
    public AddExternalProviderDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Creates the dialog for the given form.
    /// </summary>
    public AddExternalProviderDialog(ExternalProviderAddFormViewModel form)
    {
        InitializeComponent();
        DataContext = form ?? throw new ArgumentNullException(nameof(form));
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) =>
        Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) =>
        Close(false);
}
