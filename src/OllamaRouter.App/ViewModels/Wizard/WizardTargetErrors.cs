using System.Net;
using OllamaRouter.Client;

namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// The shared add-target error mapping of the Setup Wizard (task #151 — the single source of
/// truth for Step 2's "Add Ollama Node" / "Add Provider" and Step 3's "Add Provider"): the
/// same readable messages the pre-task #151 Step 2 remote form used (409 → already exists,
/// 422 → unreachable with the Step 1 retry hint, 400 → the server text, transport →
/// connection failed) so the wizard steps can never drift from each other.
/// </summary>
internal static class WizardTargetErrors
{
    /// <summary>Maps an add-target failure to the wizard's readable message text.</summary>
    public static string MapAddTarget(Exception ex) => ex switch
    {
        OllamaRouterApiException api => MapApiException(api),
        System.Net.Http.HttpRequestException http => $"Connection failed: {http.Message}",
        _ => ex.Message,
    };

    private static string MapApiException(OllamaRouterApiException ex) => ex.Status switch
    {
        HttpStatusCode.Conflict => "Target already exists.",
        HttpStatusCode.UnprocessableEntity => "unreachable — fix Step 1 and retry",
        HttpStatusCode.BadRequest => ex.Error ?? "Invalid target.",
        _ => $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}",
    };
}
