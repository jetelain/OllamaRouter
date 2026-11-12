namespace OllamaRouter.App.Bootstrap;

/// <summary>
/// Parses the desktop app's command-line arguments for wizard mode (task #103, spec §3.3).
/// <c>--wizard</c> (case-insensitive exact match) selects the Setup Wizard; every other
/// argument is deliberately IGNORED (the same convention as task #102's arg handling — the
/// app has no other command-line surface).
/// </summary>
internal static class WizardArgs
{
    /// <summary>The explicit wizard-mode marker token (the CLI's <c>setup</c> command always
    /// passes this).</summary>
    internal const string WizardToken = "--wizard";

    /// <summary>
    /// Whether the wizard should open: <c>true</c> when <paramref name="args"/> contains the
    /// <c>--wizard</c> marker (case-insensitive exact match, order-independent).
    /// </summary>
    public static bool IsWizard(IReadOnlyList<string>? args) =>
        args is not null && args.Any(a => string.Equals(a, WizardToken, StringComparison.OrdinalIgnoreCase));
}
