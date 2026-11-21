using System.Text;
using OllamaRouter.Services.Process;

namespace OllamaRouter.Tests.Services.Process;

/// <summary>
/// Integration tests for the real <see cref="ProcessRunner"/> verifying concurrent stdout/stderr/stdin
/// pipe draining on payloads larger than OS anonymous pipe buffers (4 KB on Windows, 64 KB on Linux/macOS),
/// timeout cleanup with partial output preservation, non-zero exit codes, and launch failures.
/// </summary>
public class ProcessRunnerTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void Run_VerboseStdOutExceedingPipeBuffer_CapturesFullOutputWithoutTimeout()
    {
        var payload = BuildAsciiPayload("stdout-line", 256 * 1024);
        var tempFile = CreateTempFile(payload);
        try
        {
            var (fileName, arguments) = BuildShellCommand(
                windowsCommand: $"type \"{tempFile}\"",
                unixCommand: $"cat '{tempFile}'");

            var runner = new ProcessRunner();
            var result = runner.Run(fileName, arguments, timeoutMs: 10_000);

            Assert.True(result.Succeeded, $"Expected success, timedOut={result.TimedOut}, exit={result.ExitCode}, err={result.StdErr}");
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(payload, result.StdOut);
            Assert.Equal(string.Empty, result.StdErr);
        }
        finally
        {
            DeleteQuietly(tempFile);
        }
    }

    [Fact]
    public void Run_VerboseStdErrExceedingPipeBuffer_CapturesFullErrorWithoutTimeout()
    {
        var payload = BuildAsciiPayload("stderr-line", 256 * 1024);
        var tempFile = CreateTempFile(payload);
        try
        {
            var (fileName, arguments) = BuildShellCommand(
                windowsCommand: $"type \"{tempFile}\" 1>&2",
                unixCommand: $"cat '{tempFile}' >&2");

            var runner = new ProcessRunner();
            var result = runner.Run(fileName, arguments, timeoutMs: 10_000);

            Assert.True(result.Succeeded, $"Expected success, timedOut={result.TimedOut}, exit={result.ExitCode}, err={result.StdErr}");
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StdOut);
            Assert.Equal(payload, result.StdErr);
        }
        finally
        {
            DeleteQuietly(tempFile);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Run_VerboseStdOutAndStdErrBothLarge_DoesNotDeadlockAndCapturesBoth(bool stdErrFirst)
    {
        var expectedOut = BuildAsciiPayload("out-chunk", 160 * 1024);
        var expectedErr = BuildAsciiPayload("err-chunk", 160 * 1024);
        var outFile = CreateTempFile(expectedOut);
        var errFile = CreateTempFile(expectedErr);
        try
        {
            var (fileName, arguments) = stdErrFirst
                ? BuildShellCommand(
                    windowsCommand: $"type \"{errFile}\" 1>&2 && type \"{outFile}\"",
                    unixCommand: $"cat '{errFile}' >&2 && cat '{outFile}'")
                : BuildShellCommand(
                    windowsCommand: $"type \"{outFile}\" && type \"{errFile}\" 1>&2",
                    unixCommand: $"cat '{outFile}' && cat '{errFile}' >&2");

            var runner = new ProcessRunner();
            var result = runner.Run(fileName, arguments, timeoutMs: 10_000);

            Assert.True(result.Succeeded, $"Expected success, timedOut={result.TimedOut}, exit={result.ExitCode}");
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expectedOut, result.StdOut);
            Assert.Equal(expectedErr, result.StdErr);
        }
        finally
        {
            DeleteQuietly(outFile);
            DeleteQuietly(errFile);
        }
    }

    [Fact]
    public void Run_VerboseOutputThenTimeout_KillsProcessAndPreservesPartialOutput()
    {
        var payload = BuildAsciiPayload("partial-line", 160 * 1024);
        var tempFile = CreateTempFile(payload);
        try
        {
            var (fileName, arguments) = BuildShellCommand(
                windowsCommand: $"type \"{tempFile}\" && ping -n 20 127.0.0.1 >nul",
                unixCommand: $"cat '{tempFile}' && sleep 20");

            var runner = new ProcessRunner();
            var result = runner.Run(fileName, arguments, timeoutMs: 1_500);

            Assert.True(result.TimedOut);
            Assert.False(result.Succeeded);
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal(payload, result.StdOut);
        }
        finally
        {
            DeleteQuietly(tempFile);
        }
    }

    [Fact]
    public void Run_LargeStandardInputEchoedToStdOut_RoundTripsWithoutDeadlock()
    {
        var payload = BuildAsciiPayload("stdin-line", 160 * 1024);
        var (fileName, arguments, timeoutMs) = OperatingSystem.IsWindows()
            ? ("powershell", "-NoProfile -NonInteractive -Command \"[Console]::Out.Write([Console]::In.ReadToEnd())\"", 15_000)
            : ("/bin/sh", "-c \"cat\"", 10_000);

        var runner = new ProcessRunner();
        var result = runner.Run(fileName, arguments, timeoutMs, payload);

        Assert.True(result.Succeeded, $"Expected success, timedOut={result.TimedOut}, exit={result.ExitCode}, err={result.StdErr}");
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(payload, result.StdOut);
    }

    [Fact]
    public void Run_NonZeroExitCode_ReturnsExitCodeAndCapturedStreams()
    {
        var (fileName, arguments) = BuildShellCommand(
            windowsCommand: "echo stdout-marker& echo stderr-marker 1>&2& exit /b 42",
            unixCommand: "printf 'stdout-marker\\n'; printf 'stderr-marker\\n' >&2; exit 42");

        var runner = new ProcessRunner();
        var result = runner.Run(fileName, arguments, timeoutMs: 10_000);

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal(42, result.ExitCode);
        Assert.Contains("stdout-marker", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("stderr-marker", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_MissingExecutable_ReturnsFailedToStart()
    {
        var runner = new ProcessRunner();
        var result = runner.Run($"nonexistent-ollamarouter-binary-{Guid.NewGuid():N}", string.Empty, timeoutMs: 5_000);

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StdErr));
    }

    private static (string FileName, string Arguments) BuildShellCommand(string windowsCommand, string unixCommand)
    {
        if (OperatingSystem.IsWindows())
        {
            var comSpec = Environment.GetEnvironmentVariable("ComSpec");
            var fileName = string.IsNullOrWhiteSpace(comSpec) ? "cmd.exe" : comSpec;
            return (fileName, $"/d /c \"{windowsCommand}\"");
        }

        return ("/bin/sh", $"-c \"{unixCommand}\"");
    }

    private static string BuildAsciiPayload(string prefix, int targetBytes)
    {
        var sb = new StringBuilder(targetBytes + 128);
        int index = 0;
        while (sb.Length < targetBytes)
        {
            sb.Append(prefix)
              .Append('-')
              .Append(index.ToString("D6"))
              .Append(": 0123456789abcdef0123456789abcdef0123456789abcdef\n");
            index++;
        }

        return sb.ToString();
    }

    private static string CreateTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ollamarouter-procrunner-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content, Utf8NoBom);
        return path;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup of temporary test files.
        }
    }
}
