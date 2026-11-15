using OllamaRouter.App.Bootstrap;

namespace OllamaRouter.App;

/// <summary>
/// The resolved startup launch options for the desktop app (task #107): whether the 4-step
/// Setup Wizard opens and (for dashboard mode) which tab opens first.
/// </summary>
internal sealed record AppLaunchOptions
{
    /// <summary>True ⇒ open the Setup Wizard (the <c>--wizard</c> flag or a
    /// <c>ollamarouter://wizard</c>/<c>ollamarouter://setup</c> URI).</summary>
    public required bool Wizard { get; init; }

    /// <summary>
    /// The initial dashboard tab index (0 = Monitor, 1 = Agents &amp; Sessions, 2 = Fleet,
    /// 3 = Cartography, 4 = Settings — the <c>MainWindow.axaml</c> <c>TabItem</c> order), or null
    /// ⇒ the app default (0). Surfaces the <c>?tab=</c> query of the first
    /// <c>ollamarouter://</c> URI argument; the app ignores it in wizard mode.
    /// </summary>
    public int? InitialTabIndex { get; init; }
}

/// <summary>
/// Pure, AOT-safe parser for the desktop app's startup arguments (task #107):
/// <c>--wizard</c> flags and <c>ollamarouter://</c> URIs delivered by the OS protocol handler
/// (e.g. <c>ollamarouter-app "ollamarouter://open"</c>). Deterministic contract:
/// <list type="number">
/// <item>
/// <c>--wizard</c> (detected by <see cref="WizardArgs.IsWizard"/> — the single source of truth
/// for the token) ⇒ <c>Wizard = true</c>; the flag WINS over any URI host (task #103 D1:
/// <c>ollamarouter setup</c> always launches the wizard).
/// </item>
/// <item>
/// The FIRST argument that <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> accepts as an
/// absolute <c>ollamarouter</c>-scheme URI (scheme + host compared case-insensitively —
/// <see cref="Uri"/> normalizes both to lowercase):
/// <c>ollamarouter://</c> (bare), <c>//open</c>, <c>//dashboard</c> (trailing slash irrelevant)
/// ⇒ dashboard; <c>//wizard</c>, <c>//setup</c> ⇒ wizard; any other host (e.g. <c>//nope</c>)
/// ⇒ dashboard (safe fallback). Non-URI args and other schemes (<c>http://…</c>, …) are ignored.
/// </item>
/// <item>
/// The <c>?tab=</c> query of that URI: the FIRST <c>tab=</c> pair wins (key
/// case-insensitive, value percent-decoded and compared case-insensitively):
/// <c>monitor</c>⇒0, <c>sessions</c>⇒1, <c>fleet</c>⇒2, <c>cartography</c>⇒3,
/// <c>settings</c>⇒4; unknown/missing/empty ⇒ null. Surfaced regardless of mode.
/// </item>
/// <item>
/// No args / null ⇒ dashboard, no tab.
/// </item>
/// </list>
/// No I/O, no reflection — a pure function, safe under Native AOT.
/// </summary>
internal static class ProtocolUriParser
{
    /// <summary>The custom URI scheme (lowercase — <see cref="Uri"/> normalizes schemes to
    /// lowercase).</summary>
    internal const string ProtocolScheme = "ollamarouter";

    /// <summary>
    /// Parses the startup arguments into <see cref="AppLaunchOptions"/> (see the type remarks for
    /// the full deterministic contract).
    /// </summary>
    public static AppLaunchOptions Parse(IReadOnlyList<string>? args)
    {
        var wizard = WizardArgs.IsWizard(args);
        int? initialTabIndex = null;

        if (args is not null)
        {
            foreach (var arg in args)
            {
                if (arg is null || Uri.TryCreate(arg, UriKind.Absolute, out var uri) is false)
                {
                    continue;
                }

                if (!string.Equals(uri.Scheme, ProtocolScheme, StringComparison.Ordinal))
                {
                    continue; // other schemes (http, foo://…) are ignored entirely
                }

                // Uri normalized host to lowercase; trailing slashes are irrelevant.
                var host = uri.Host;
                if (host is "wizard" or "setup")
                {
                    wizard = true;
                }

                initialTabIndex = TryGetTabIndex(uri.Query);
                break; // the first ollamarouter:// URI wins
            }
        }

        return new AppLaunchOptions { Wizard = wizard, InitialTabIndex = initialTabIndex };
    }

    /// <summary>
    /// Maps the first <c>tab=</c> query pair of <paramref name="query"/> to a tab index
    /// (case-insensitive key and value, percent-decoded), or null when absent/unknown/empty.
    /// </summary>
    private static int? TryGetTabIndex(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = pair[..equals];
            if (!string.Equals(key, "tab", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(pair[(equals + 1)..]);
            int? index = value.ToLowerInvariant() switch
            {
                "monitor" => 0,
                "sessions" => 1,
                "fleet" => 2,
                "cartography" => 3,
                "settings" => 4,
                _ => null,
            };
            return index; // the first tab= pair wins
        }

        return null;
    }
}
