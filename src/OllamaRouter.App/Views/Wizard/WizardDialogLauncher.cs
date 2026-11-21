using Avalonia.Controls;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views.Dialogs;
using OllamaRouter.Endpoints.RouterApi.Targets;

namespace OllamaRouter.App.Views.Wizard;

/// <summary>
/// The Setup Wizard's shared add-dialog launcher (task #151): opens the SAME dedicated
/// dialogs the main app's Fleet tab uses (task #149 — <see cref="AddOllamaNodeDialog"/> /
/// <see cref="AddExternalProviderDialog"/> with the bare <c>new</c> form construction of
/// <c>FleetTargetsViewModel.CreateOllamaNodeAddForm()</c> /
/// <c>CreateExternalProviderAddForm()</c>) modally over the wizard window, and returns the
/// validated <see cref="AddTargetRequest"/> (null on cancel).
/// <para>
/// The single-pass validation flow mirrors <c>FleetTargetsView</c> byte-for-byte: a valid
/// Save returns the request; an invalid Save re-opens the dialog with the error banner
/// (<c>form.ReportError(error); dialog.Show();</c>) and the follow-up save closes it WITHOUT
/// submitting (the established #149 behavior — do NOT "improve" it here).
/// </para>
/// </summary>
internal static class WizardDialogLauncher
{
    /// <summary>The "Add Ollama Node" dialog (Ollama-protocol targets).</summary>
    public static async Task<AddTargetRequest?> ShowAddOllamaNodeAsync(Window owner)
    {
        var form = new OllamaNodeAddFormViewModel();
        var dialog = new AddOllamaNodeDialog(form);
        if (await dialog.ShowDialog<bool>(owner) is not true)
        {
            return null; // Cancel.
        }

        var (request, error) = form.BuildAddRequest();
        if (error is not null)
        {
            form.ReportError(error);
            dialog.Show(); // keep the dialog open on the validation error (the #149 flow)
            return null;
        }

        return request;
    }

    /// <summary>The "Add External Provider" dialog (OpenAI-compatible endpoints).</summary>
    public static async Task<AddTargetRequest?> ShowExternalProviderAddAsync(Window owner)
    {
        var form = new ExternalProviderAddFormViewModel();
        var dialog = new AddExternalProviderDialog(form);
        if (await dialog.ShowDialog<bool>(owner) is not true)
        {
            return null; // Cancel.
        }

        var (request, error) = form.BuildAddRequest();
        if (error is not null)
        {
            form.ReportError(error);
            dialog.Show(); // keep the dialog open on the validation error (the #149 flow)
            return null;
        }

        return request;
    }
}
