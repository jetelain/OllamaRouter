using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OllamaRouter.App.Views.Dialogs;

/// <summary>
/// A minimal OK/Cancel confirmation dialog (task #102). Avalonia ships no built-in
/// <c>MessageBox</c>, so destructive actions confirm through this small modal window.
/// <see cref="Show"/> blocks until the user answers (standard dialog semantics) and returns
/// <c>true</c> for OK, <c>false</c> for Cancel.
/// </summary>
public static class Confirm
{
    /// <summary>Shows the confirmation against <paramref name="owner"/> and returns the
    /// user's choice (true = OK; a closed window is treated as Cancel).</summary>
    public static async Task<bool> ShowAsync(Window owner, string message, string title)
    {
        var dialog = new ConfirmDialog(message, title);
        return await dialog.ShowDialog<bool>(owner);
    }
}

/// <summary>The confirmation dialog window (bound to nothing; the message is a field).</summary>
public partial class ConfirmDialog : Window
{
    /// <summary>Creates the dialog (XAML compiler hook; the parameterized constructor is used
    /// by the view code).</summary>
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>Creates the dialog with its message text.</summary>
    public ConfirmDialog(string message, string title)
    {
        InitializeComponent();
        PART_Message.Text = message ?? string.Empty;
        Title = title;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
