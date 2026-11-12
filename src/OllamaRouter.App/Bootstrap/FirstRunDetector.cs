namespace OllamaRouter.App.Bootstrap;

/// <summary>
/// The first-run / fresh-install heuristic that auto-opens the Setup Wizard (task #103, spec
/// §3.3). The daemon runs with its executable directory as content root and the app + server
/// ship side by side, so "no <c>config/config.json</c> in any config root" == fresh install
/// (nothing has been configured yet). The roots are probed in the daemon-launcher order:
/// <c>$OLLAMAROUTER_HOME</c> → <see cref="AppContext.BaseDirectory"/> → platform default
/// install dir.
/// </summary>
internal static class FirstRunDetector
{
    /// <summary>The environment variable that overrides the config root (probe #1).</summary>
    internal const string HomeEnvironmentVariable = "OLLAMAROUTER_HOME";

    /// <summary>The user-config file, relative to each config root.</summary>
    internal static readonly string ConfigFileRelativePath = Path.Combine("config", "config.json");

    /// <summary>
    /// Whether the Setup Wizard should open automatically: <c>true</c> when
    /// <c>config/config.json</c> is absent from EVERY probed root (fresh install), <c>false</c>
    /// as soon as it exists in ANY of them (already configured).
    /// </summary>
    public static bool NeedsSetupWizard() => Probe(DefaultRoots());

    /// <summary>
    /// Probes an explicit set of config roots (test seam: deterministic temp dirs — no env
    /// mutation, no real install-dir leakage). Returns <c>true</c> (needs wizard) only when
    /// <c>config/config.json</c> is found in NONE of the roots.
    /// </summary>
    internal static bool Probe(IEnumerable<string>? roots)
    {
        if (roots is null)
        {
            return true;
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            try
            {
                if (File.Exists(Path.Combine(root, ConfigFileRelativePath)))
                {
                    return false; // a configured install exists — not a first run
                }
            }
            catch (Exception)
            {
                // An unreadable root is treated as "no config here" (the next root still counts).
            }
        }

        return true; // no config.json in any root — fresh install
    }

    private static IEnumerable<string> DefaultRoots()
    {
        var home = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return home;
        }

        yield return AppContext.BaseDirectory;
        yield return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OllamaRouter")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollamarouter");
    }
}
