using System.Reflection;
using System.Runtime.InteropServices;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.File;

namespace OllamaRouter.App.Logging;

/// <summary>
/// Task #159 — the app-wide Serilog file-logging bootstrap. One daily-rolling file
/// (<c>%LOCALAPPDATA%\OllamaRouter\logs\ollamarouter-app-YYYYMMDD.log</c>, 31-day
/// retention, the daemon's exact output template) captures EVERYTHING — Information and
/// up by default (env override <c>OLLAMAROUTER_APP_LOGLEVEL</c>) including crashes.
/// <para>
/// Fail-open by design (AC3): <see cref="Bootstrap"/> NEVER throws — logging must not be
/// able to prevent app start (the crash hooks in <c>Program.Main</c> attach regardless).
/// <c>Serilog.Sinks.File</c> writes synchronously per event, so a <c>Log.Fatal</c> is
/// durable when it returns; there is deliberately NO <c>Log.CloseAndFlush</c> anywhere
/// (it would dispose the static logger and silence later events).
/// </para>
/// </summary>
internal static class AppLogging
{
    /// <summary>The log file base name (Serilog daily rolling appends <c>-yyyyMMdd</c>).</summary>
    public const string LogFileBaseName = "ollamarouter-app-.log";

    /// <summary>The 31-day retention window (AC3 — same as the daemon).</summary>
    public const int RetainedFileCountLimit = 31;

    /// <summary>The minimum-level environment variable (case-insensitive level names).</summary>
    public const string LogLevelEnvironmentVariable = "OLLAMAROUTER_APP_LOGLEVEL";

    /// <summary>
    /// The daemon's exact output template (copied verbatim from
    /// <c>src/OllamaRouter/Program.cs</c>) — one convention across daemon + app logs.
    /// </summary>
    public const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Resolves the log directory: <c>%LOCALAPPDATA%\OllamaRouter\logs</c> on Windows,
    /// <c>~/.local/share/OllamaRouter/logs</c> on Linux/macOS (the
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> convention, cross-
    /// platform). NOT created here — <see cref="Bootstrap"/> creates it on demand.
    /// </summary>
    public static string GetLogDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OllamaRouter", "logs");

    /// <summary>The session-header app version (no csproj <c>&lt;Version&gt;</c> exists, so
    /// the assembly version defaults to 1.0.0.0; a missing version degrades to 0.0.0).</summary>
    public static string AppVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// The pinned AC1 session-header format (asserted verbatim by the unit tests):
    /// <c>"OllamaRouter app v{version} | {os} | log level: {level} | log file: {path}"</c>.
    /// </summary>
    public static string BuildSessionHeader(string version, string os, LogEventLevel level, string logFilePath) =>
        $"OllamaRouter app v{version} | {os} | log level: {level} | log file: {logFilePath}";

    /// <summary>The operating-system description of the session header.</summary>
    public static string OperatingSystemDescription() =>
        $"{RuntimeInformation.OSDescription} ({Environment.OSVersion.Platform})";

    /// <summary>
    /// Builds the daily-rolling file-sink logger for <paramref name="directory"/> (created
    /// on demand) and writes the session header (Information) to it, returning the
    /// disposable logger and the dated path the header carries. Does NOT touch the
    /// process-wide static <c>Serilog.Log.Logger</c> — so the unit tests can exercise file
    /// creation + the header in FULL isolation (a test that assigns the static would let
    /// other, concurrently-running test classes' view models capture that logger via their
    /// <c>_logger = logger ?? Serilog.Log.Logger</c> default and cross-pollute the temp
    /// file). <paramref name="directory"/> being uncreatable throws (the caller —
    /// <see cref="Bootstrap"/> — is the fail-open boundary).
    /// </summary>
    internal static (Serilog.ILogger Logger, string DatedPath) CreateAndWriteHeader(
        LogEventLevel level, string directory, string version, string os)
    {
        Directory.CreateDirectory(directory);
        var basePath = Path.Combine(directory, LogFileBaseName);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: basePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCountLimit,
                shared: true,
                outputTemplate: OutputTemplate)
            .CreateLogger();

        var datedPath = DatedLogFilePath(directory);
        logger.Information(BuildSessionHeader(version, os, level, datedPath));
        return (logger, datedPath);
    }

    /// <summary>
    /// The dated path Serilog's daily rolling names the live file: the base name already
    /// ends in "-" (the date is appended directly before the extension: ollamarouter-app-.log
    /// → ollamarouter-app-yyyyMMdd.log).
    /// </summary>
    internal static string DatedLogFilePath(string directory)
    {
        var basePath = Path.Combine(directory, LogFileBaseName);
        var extension = Path.GetExtension(basePath);
        return basePath.Substring(0, basePath.Length - extension.Length) +
            DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + extension;
    }

    /// <summary>
    /// Boots the static <c>Serilog.Log.Logger</c> onto the daily-rolling file sink and writes
    /// the session header (Information). Injected seams keep the unit tests hermetic (AC8):
    /// <paramref name="logLevelEnvironmentValue"/> replaces the environment read, and
    /// <paramref name="logDirectoryOverride"/> replaces <see cref="GetLogDirectory"/> (tests
    /// point it at a temp dir — NEVER the real user profile). The whole body is fail-open:
    /// any bootstrap fault is swallowed (the crash hooks still attach in <c>Program.Main</c>).
    /// </summary>
    public static void Bootstrap(
        string? logLevelEnvironmentValue = null,
        Func<string>? logDirectoryOverride = null)
    {
        try
        {
            var level = LogLevelParser.Parse(
                logLevelEnvironmentValue
                    ?? Environment.GetEnvironmentVariable(LogLevelEnvironmentVariable));
            var directory = logDirectoryOverride?.Invoke() ?? GetLogDirectory();
            var (logger, _) = CreateAndWriteHeader(level, directory, AppVersion(), OperatingSystemDescription());
            Log.Logger = logger;
        }
        catch
        {
            // Fail-open (AC3): a logging fault (read-only directory, a path component that is
            // a file, …) must never prevent app start. Serilog.Log falls back to its no-op
            // logger, and the crash hooks in Program.Main attach unconditionally.
        }
    }
}
