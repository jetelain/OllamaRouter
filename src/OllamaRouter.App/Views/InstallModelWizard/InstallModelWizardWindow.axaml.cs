using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OllamaRouter.App.Logging;
using OllamaRouter.App.ViewModels.InstallModelWizard;
using OllamaRouter.App.Views.Dialogs;

namespace OllamaRouter.App.Views.InstallModelWizard;

/// <summary>
/// The modal "Install Model" wizard window (task #125): 3 steps (Model / Targets /
/// Review &amp; Install), cancellable (Cancel / Back / Next / Install / Done). Closing
/// WHILE an install is running confirms first ("Cancel the in-progress install and
/// close?"); on confirmation the in-flight install is cancelled (no orphaned pull) and
/// the window closes. <see cref="Window.OnClosed"/> disposes the view model (mirrors
/// <c>SetupWizardWindow</c>). Task #159: the async handlers run through <see cref="SafeRun"/>
/// (log-only — no window-level message surface); the <c>base.OnClosing</c>/<c>base.OnClosed</c>
/// ordering is preserved exactly inside the lambda.
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is not InstallModelWizardViewModel viewModel
            || !viewModel.ReviewStep.IsRunning
            || viewModel.CloseConfirmed)
        {
            base.OnClosing(e);
            return;
        }

        // The original async-void handler set e.Cancel SYNCHRONOUSLY before the first await —
        // keep that exactly (SafeRun starts the body synchronously up to the first await, but
        // a synchronous pre-await fault would otherwise leave the cancel unset).
        e.Cancel = true;
        SafeRun.Run(async () =>
        {
            var confirmed = await Confirm.ShowAsync(
                this,
                "An install is in progress. Cancel the in-progress install and close?",
                "Install in progress");
            if (confirmed)
            {
                viewModel.CancelRunningInstall();
                Close();
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is InstallModelWizardViewModel viewModel)
        {
            // base.OnClosed(e) stays AFTER the awaited disposal (today's exact ordering).
            SafeRun.Run(async () =>
            {
                await viewModel.DisposeAsync();
                base.OnClosed(e);
            });
        }
        else
        {
            base.OnClosed(e);
        }
    }
}
