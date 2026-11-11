using OllamaRouter.App.ViewModels;

namespace OllamaRouter.App.Views.Dialogs;

/// <summary>
/// The Preload Model dialog (task #102): the optional target pin / keepAliveSeconds /
/// contextIntentTokens inputs, the side-effect-free PREVIEW (candidate table) and the
/// actual PRELOAD (applied result panel).
/// </summary>
public partial class PreloadDialog : Avalonia.Controls.Window
{
    /// <summary>Creates the dialog (XAML compiler hook; the parameterized constructor is used
    /// by the view code).</summary>
    public PreloadDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Creates the dialog for the given dialog view model.
    /// </summary>
    public PreloadDialog(PreloadDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
