using System;
using Avalonia.Controls;
using OllamaRouter.App.ViewModels.Wizard;

namespace OllamaRouter.App.Views.Wizard;

/// <summary>
/// The 4-step Setup Wizard window (task #103, spec §3.6). The DataContext is the
/// <see cref="SetupWizardViewModel"/> (set by the composition root).
/// <para>
/// The exit-code contract (D2): closing the window WITHOUT Finish cancels the close, signals
/// the view model to abandon (exit code <c>1</c> via <c>TryShutdown</c>), and <c>TryShutdown</c>
/// re-fires <see cref="WindowClosing"/> — the one-shot <see cref="_abandonInFlight"/> guard
/// allows that second pass so the window actually closes (and the process exits <c>1</c> rather
/// than looping forever). Finish (exit <c>0</c>) takes the normal, uncancelled close path.
/// </para>
/// </summary>
public partial class SetupWizardWindow : Window
{
    private bool _abandonInFlight;

    public SetupWizardWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is SetupWizardViewModel viewModel
            && !viewModel.Finished
            && !_abandonInFlight)
        {
            // Abandoned: cancel this close, signal exit 1, and let the re-fired close proceed.
            _abandonInFlight = true;
            e.Cancel = true;
            viewModel.Abandon();
            return;
        }

        base.OnClosing(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (DataContext is SetupWizardViewModel viewModel)
        {
            await viewModel.DisposeAsync();
        }

        base.OnClosed(e);
    }
}
