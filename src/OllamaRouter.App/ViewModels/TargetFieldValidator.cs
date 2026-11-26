namespace OllamaRouter.App.ViewModels;

/// <summary>
/// Shared target-field validation (task #149): the Ollama-node add form, the
/// external-provider add form and the shared add/edit form all enforce the SAME name /
/// URL / maxParallel / ssh-credential rules, so the rules (and their messages) live here
/// once. <strong>The error message strings are a tested contract</strong> —
/// <c>FleetTargetsViewModelTests</c> asserts them verbatim — keep them byte-identical.
/// </summary>
internal static class TargetFieldValidator
{
    /// <summary>
    /// Validates the target name: non-blank, ≤ 64 characters (trimmed), no
    /// <c>/</c>, <c>\</c> or <c>:</c>, and not the reserved "Cloud" pseudo-target name
    /// (case-insensitive).
    /// </summary>
    public static bool ValidateName(string name, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "The target name must not be empty.";
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length > 64)
        {
            error = "The target name must be 64 characters or fewer.";
            return false;
        }

        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains(':'))
        {
            error = "The target name must not contain '/', '\\' or ':'.";
            return false;
        }

        if (string.Equals(trimmed, "Cloud", System.StringComparison.OrdinalIgnoreCase))
        {
            error = "\"Cloud\" is reserved for the cloud overflow pseudo-target.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates a target base URL: must be an absolute http(s) URI.
    /// </summary>
    public static bool ValidateUrl(string url, out string? error)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "The target URL must be an absolute http(s) URI.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates the maxParallel budget: must parse to an int &gt;= 1.
    /// </summary>
    public static bool ValidateMaxParallel(string value, out string? error)
    {
        if (!int.TryParse(value, out var maxParallel) || maxParallel < 1)
        {
            error = "maxParallel must be an integer of 1 or greater.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates the node power-draw override in watts (optional): blank is valid (no
    /// change / unset); a present value must be a non-negative number (invariant culture).
    /// (Task #171: the rule moved here from the retiring shared
    /// <c>TargetEditFormViewModel</c> — the Ollama-node edit form is now the only form with
    /// the field, and this stays the single home of the rule.)
    /// </summary>
    public static bool ValidatePowerWatts(string value, out string? error)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var watts) || watts < 0))
        {
            error = "powerWatts must be a non-negative number (or blank).";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates the daemon-side hardware-probe ssh user (optional): blank is valid; a
    /// non-blank value must match the POSIX-ish charset the server enforces
    /// (^[A-Za-z0-9._-]{1,64}$ — safe to interpolate into {user}@{host}).
    /// </summary>
    public static bool ValidateSshUser(string value, out string? error)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9._-]{1,64}$"))
        {
            error = "sshUser must be 1–64 characters of letters, digits, '.', '_' or '-' (or blank).";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates the daemon-side hardware-probe ssh port (optional): blank is valid
    /// (the default port 22); a present value must be 1–65535.
    /// </summary>
    public static bool ValidateSshPort(string value, out string? error)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            (!int.TryParse(value, out var sshPort) || sshPort < 1 || sshPort > 65535))
        {
            error = "sshPort must be a port between 1 and 65535 (or blank).";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// The ssh port wire value: blank → null (unset / no change), else the parsed port
    /// (validated by <see cref="ValidateSshPort"/> before this is called).
    /// </summary>
    public static int? ParseSshPort(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Trims the URL and strips trailing <c>/</c> (the server's stored form).
    /// </summary>
    public static string NormalizeUrl(string url) =>
        url.Trim().TrimEnd('/');
}
