using System;
using System.Globalization;
using OllamaRouter.App.Logging;
using Serilog.Events;
using Xunit;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Task #159 — unit tests for the app-logging subsystem: the env-var level parser
/// (valid/invalid/case), the session-header format, and the file-logging pipeline
/// (file creation in a TEMP dir only — AC8: never the real user profile).
/// The file-logging tests use the static-free <c>AppLogging.CreateAndWriteHeader</c> seam
/// (they NEVER assign the process-wide <c>Serilog.Log.Logger</c>): a test that assigned the
/// static would let other, concurrently-running test classes' view models capture that
/// file-sink logger via their <c>_logger = logger ?? Serilog.Log.Logger</c> default and
/// cross-pollute the temp file (observed flake). The one-line static assignment inside
/// <c>Bootstrap</c> itself is integration-verified (maintainer's Windows re-test, risk #7),
/// and <c>Bootstrap</c>'s fail-open boundary is unit-tested directly (safe: an uncreatable
/// directory throws BEFORE the static is ever assigned).
/// </summary>
public sealed class AppLoggingTests
{
    // ------------------------------------------------------------------
    //  LogLevelParser
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("trace", LogEventLevel.Verbose)]
    [InlineData("verbose", LogEventLevel.Verbose)]
    [InlineData("debug", LogEventLevel.Debug)]
    [InlineData("information", LogEventLevel.Information)]
    [InlineData("info", LogEventLevel.Information)]
    [InlineData("warning", LogEventLevel.Warning)]
    [InlineData("warn", LogEventLevel.Warning)]
    [InlineData("error", LogEventLevel.Error)]
    [InlineData("critical", LogEventLevel.Fatal)]
    [InlineData("fatal", LogEventLevel.Fatal)]
    public void LogLevelParser_CanonicalNamesAndAliases(string raw, LogEventLevel expected)
    {
        Assert.Equal(expected, LogLevelParser.Parse(raw));
    }

    [Theory]
    [InlineData("DeBuG", LogEventLevel.Debug)]
    [InlineData("INFO", LogEventLevel.Information)]
    [InlineData("Warning", LogEventLevel.Warning)]
    [InlineData("  debug  ", LogEventLevel.Debug)]
    [InlineData("\tFATAL\t", LogEventLevel.Fatal)]
    public void LogLevelParser_CaseInsensitiveAndPadded(string raw, LogEventLevel expected)
    {
        Assert.Equal(expected, LogLevelParser.Parse(raw));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("999")]
    [InlineData(null)]
    public void LogLevelParser_JunkFallsBackToInformation(string? raw)
    {
        Assert.Equal(LogEventLevel.Information, LogLevelParser.Parse(raw));
    }

    // ------------------------------------------------------------------
    //  SecretsMasker (the AC7 choke point)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null, "(none)")]
    [InlineData("", "(none)")]
    [InlineData("   ", "(none)")]
    [InlineData("sk-super-secret-123", "[REDACTED]")]
    [InlineData("a", "[REDACTED]")]
    public void SecretsMasker_MasksEveryNonEmptyValue(string? secret, string expected)
    {
        Assert.Equal(expected, SecretsMasker.Mask(secret));
    }

    // ------------------------------------------------------------------
    //  Constants / helpers
    // ------------------------------------------------------------------

    [Fact]
    public void Constants_MatchTheSpec()
    {
        Assert.Equal("ollamarouter-app-.log", AppLogging.LogFileBaseName);
        Assert.Equal(31, AppLogging.RetainedFileCountLimit);
        Assert.Equal("OLLAMAROUTER_APP_LOGLEVEL", AppLogging.LogLevelEnvironmentVariable);
    }

    [Fact]
    public void BuildSessionHeader_ExactPinnedFormat()
    {
        var header = AppLogging.BuildSessionHeader(
            "1.2.3.4", "Windows 10.0.19045 (Win32NT)", LogEventLevel.Debug, "C:\\logs\\ollamarouter-app-20261121.log");
        Assert.Equal(
            "OllamaRouter app v1.2.3.4 | Windows 10.0.19045 (Win32NT) | log level: Debug | log file: C:\\logs\\ollamarouter-app-20261121.log",
            header);
    }

    [Fact]
    public void AppVersion_NeverNull()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppLogging.AppVersion()));
    }

    // ------------------------------------------------------------------
    //  File logging (hermetic: temp dir only — NEVER the user profile)
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs the full file-logging pipeline (directory creation + daily-rolling file sink +
    /// session header) against a temp dir, then disposes the returned logger so the header
    /// is durable on disk — and returns the dated file path. Deliberately static-free (see
    /// the class docs): no other test class can capture this test's file sink.
    /// </summary>
    private static string CreateLogAndFlush(string tempDir, LogEventLevel level)
    {
        var (logger, datedPath) = AppLogging.CreateAndWriteHeader(
            level, tempDir, AppLogging.AppVersion(), AppLogging.OperatingSystemDescription());
        ((IDisposable)logger).Dispose(); // flushes the session header and closes the sink
        return datedPath;
    }

    /// <summary>
    /// The expected dated filename, with a 2-attempt midnight-rollover recompute (the only
    /// theoretical flake window is 00:00:00 exactly).
    /// </summary>
    private static string ExpectedDatedPath(string directory)
    {
        // Serilog daily rolling inserts the date BEFORE the ".log" extension.
        var expected = Path.Combine(directory, "ollamarouter-app-" +
            DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
        if (!File.Exists(expected))
        {
            // Rollover raced: recompute once more.
            expected = Path.Combine(directory, "ollamarouter-app-" +
                DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
        }

        return expected;
    }

    [Fact]
    public void FileLogging_CreatesTheDatedLogFile_WithSessionHeader()
    {
        var tempDir = Directory.CreateTempSubdirectory("ollamarouter-applog-").FullName;
        try
        {
            var file = CreateLogAndFlush(tempDir, LogEventLevel.Debug);
            Assert.True(File.Exists(file), $"expected log file {file}");

            // Static-free ⇒ the file carries EXACTLY one line: this test's own session header.
            var lines = File.ReadAllLines(file);
            var header = Assert.Single(lines);
            // The daemon's output template: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] …"
            // The session header is always logged at Information → the {Level:u3} prefix is INF.
            Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [^\]]* INF\] ", header);
            Assert.Contains("OllamaRouter app v", header);
            Assert.Contains("log level: Debug", header);
            Assert.Contains(Path.GetFileName(file), header); // the dated path in the header
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void FileLogging_InformationLevelHeader_ShowsInformation()
    {
        // The env→parse step (junk → Information) is covered by
        // LogLevelParser_JunkFallsBackToInformation; this pins the header for the
        // Information level the default (and junk-fallback) parsing produces.
        var tempDir = Directory.CreateTempSubdirectory("ollamarouter-applog-").FullName;
        try
        {
            var file = CreateLogAndFlush(tempDir, LogEventLevel.Information);
            var header = Assert.Single(File.ReadAllLines(file));
            Assert.Contains("log level: Information", header, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void FileLogging_WithPreSeededOldFiles_RetentionHandlingNeverThrows()
    {
        // AC3: daily rolling with 31-day retention. The 31-day arithmetic is Serilog's
        // (not re-implemented) — this proves pre-existing dated files in the directory
        // never throw into the bootstrap path.
        var tempDir = Directory.CreateTempSubdirectory("ollamarouter-applog-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "ollamarouter-app-20261110.log"), "old-1\n");
            File.WriteAllText(Path.Combine(tempDir, "ollamarouter-app-20261111.log"), "old-2\n");

            var file = CreateLogAndFlush(tempDir, LogEventLevel.Information);

            Assert.True(File.Exists(ExpectedDatedPath(tempDir)), $"expected {ExpectedDatedPath(tempDir)}; got {file}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void Bootstrap_FailOpen_ABadDirectoryNeverThrows()
    {
        // AC3 "never throws into the crash path": a log-directory path that is actually a
        // FILE makes Directory.CreateDirectory throw — Bootstrap must swallow it. Safe to
        // call Bootstrap directly here: the throw happens BEFORE the static is assigned.
        var blockerFile = Path.Combine(
            Directory.CreateTempSubdirectory("ollamarouter-applog-").FullName, "blocker");
        File.WriteAllText(blockerFile, "I am a file, not a directory");
        try
        {
            AppLogging.Bootstrap(
                logLevelEnvironmentValue: "debug",
                logDirectoryOverride: () => blockerFile);
            // reaching here without a throw is the assertion
        }
        finally
        {
            try { File.Delete(blockerFile); Directory.Delete(Path.GetDirectoryName(blockerFile)!); }
            catch { /* best effort */ }
        }
    }
}
