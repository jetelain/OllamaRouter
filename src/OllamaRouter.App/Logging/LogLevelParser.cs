using Serilog.Events;

namespace OllamaRouter.App.Logging;

/// <summary>
/// Task #159 — the <c>OLLAMAROUTER_APP_LOGLEVEL</c> parser. Case-insensitive with the daemon's
/// alias set (<c>trace</c>/<c>verbose</c>, <c>information</c>/<c>info</c>, <c>warning</c>/<c>warn</c>,
/// <c>critical</c>/<c>fatal</c> — mirrors <c>ParseLogLevel</c> in the daemon's
/// <c>Program.cs</c>); null, empty, whitespace or unparsable values fall back to
/// <see cref="LogEventLevel.Information"/> (the app's default level).
/// </summary>
internal static class LogLevelParser
{
    /// <summary>Parses a raw environment value into a level (junk → <c>Information</c>).</summary>
    public static LogEventLevel Parse(string? raw) =>
        (raw?.Trim() ?? string.Empty).ToLowerInvariant() switch
        {
            "trace" or "verbose" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "information" or "info" => LogEventLevel.Information,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "critical" or "fatal" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information,
        };
}
