using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.ViewModels.InstallModelWizard;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.App.Views.InstallModelWizard;

/// <summary>
/// The modal "Install Model" wizard window (task #125): 3 steps (Model / Targets /
/// Review &amp; Install), cancellable (Cancel / Back / Next / Install / Done). Closing
/// WHILE an install is running confirms first ("Cancel the in-progress install and
/// close?"); on confirmation the in-flight install is cancelled (no orphaned pull) and
/// the window closes. <see cref="Window.OnClosed"/> disposes the view model (mirrors
/// <c>SetupWizardWindow</c>).
/// </summary>
public partial class InstallModelWizardWindow : Window
{
    public InstallModelWizardWindow()
    {
        InitializeComponent();
    }

    /// <summary>Creates the window with its view model (the composition root — the
    /// CartographyView code-behind — builds the VM via the factory and shows the dialog).</summary>
    public InstallModelWizardWindow(InstallModelWizardViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequestedSignal += OnCloseRequested;
    }

    private void OnCloseRequested(bool completed, string? result) => Close();

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is InstallModelWizardViewModel viewModel
            && viewModel.ReviewStep.IsRunning
            && !viewModel.CloseConfirmed)
        {
            e.Cancel = true;
            var confirmed = await Confirm.ShowAsync(
                this,
                "An install is in progress. Cancel the in-progress install and close?",
                "Install in progress");
            if (confirmed)
            {
                viewModel.CancelRunningInstall();
                Close();
            }

            return;
        }

        base.OnClosing(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (DataContext is InstallModelWizardViewModel viewModel)
        {
            await viewModel.DisposeAsync();
        }

        base.OnClosed(e);
    }
}
