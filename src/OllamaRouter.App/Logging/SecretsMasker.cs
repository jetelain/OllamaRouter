namespace OllamaRouter.App.Logging;

/// <summary>
/// Task #159 — the privacy choke point (AC7): the ONLY path a secret-shaped value
/// (a <c>RouterConnectionProfile.ApiKey</c>) may take toward a log statement. Every log
/// site that would otherwise carry a key value must call <see cref="Mask"/> first — no
/// raw key literal may reach any <c>Log</c> call (auditable by grep).
/// </summary>
internal static class SecretsMasker
{
    /// <summary>The constant replacement for any non-empty secret value.</summary>
    public const string Masked = "[REDACTED]";

    /// <summary>The constant replacement for an absent (null/empty) secret value.</summary>
    public const string Absent = "(none)";

    /// <summary>Masks a secret: null/empty → <c>"(none)"</c>; any other value → <c>"[REDACTED]"</c>.</summary>
    public static string Mask(string? secret) =>
        string.IsNullOrWhiteSpace(secret) ? Absent : Masked;
}
