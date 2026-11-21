using System.Net;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;

namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// The shared add-target error mapping of the Setup Wizard (task #151 — the single source of
/// truth for Step 2's "Add Ollama Node" / "Add Provider" and Step 3's "Add Provider"): the
/// same readable messages the pre-task #151 Step 2 remote form used (409 → already exists,
/// 422 → unreachable with the Step 1 retry hint, 400 → the server text, transport → the
/// shared daemon-down message, task #158) so the wizard steps can never drift from each
/// other.
/// </summary>
internal static class WizardTargetErrors
{
    /// <summary>
    /// The single daemon-down message of the Setup Wizard (task #158 — the single source of
    /// truth shared by Step 2's fleet poll, the Local auto-add and every add-target transport
    /// failure): the actionable "start the router in Step 1" text with the daemon endpoint
    /// derived from the connection-profile base URL (default = the canonical
    /// <see cref="OllamaRouterDaemonLauncher.DefaultBaseUrl"/>). NEVER a raw exception dump —
    /// the port appears only inside this one sentence, phrased as the daemon's port.
    /// </summary>
    public static string DaemonDownMessage(Uri? daemonBaseUrl = null) =>
        $"The OllamaRouter daemon is not reachable on {EndpointText(daemonBaseUrl ?? OllamaRouterDaemonLauncher.DefaultBaseUrl)} — go back to Step 1 and click 'Start router'.";

    /// <summary>
    /// Maps an add-target failure to the wizard's readable message text. A transport failure
    /// (the daemon is DOWN — the same root cause as Step 2's poll/auto-add, task #158) maps to
    /// the shared <see cref="DaemonDownMessage"/>; the API arms and the fallback are the
    /// readable messages the pre-task #158 wizard steps used.
    /// </summary>
    public static string MapAddTarget(Exception ex, Uri? daemonBaseUrl = null) => ex switch
    {
        OllamaRouterApiException api => MapApiException(api),
        System.Net.Http.HttpRequestException => DaemonDownMessage(daemonBaseUrl),
        _ => ex.Message,
    };

    private static string MapApiException(OllamaRouterApiException ex) => ex.Status switch
    {
        HttpStatusCode.Conflict => "Target already exists.",
        HttpStatusCode.UnprocessableEntity => "unreachable — fix Step 1 and retry",
        HttpStatusCode.BadRequest => ex.Error ?? "Invalid target.",
        _ => $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}",
    };

    /// <summary>The daemon endpoint rendering (host alone when the base URL is on the scheme's
    /// default port, else <c>host:port</c> — never a raw <see cref="Uri.ToString"/>, so no
    /// scheme/trailing slash).</summary>
    private static string EndpointText(Uri baseUrl)
    {
        // Reconstruct with ONLY scheme + host so <c>.Port</c> reports the scheme's DEFAULT
        // port (a port-less Uri carries the scheme default); when the given base URL is on that
        // port the host renders alone, otherwise <c>host:port</c>.
        int defaultPort = new UriBuilder(baseUrl.Scheme, baseUrl.Host).Uri.Port;
        return baseUrl.Port == defaultPort ? baseUrl.Host : $"{baseUrl.Host}:{baseUrl.Port}";
    }
}
