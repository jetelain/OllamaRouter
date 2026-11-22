using System.Reflection;
using System.Text;
using OllamaRouter.Services.Process;
using OllamaRouter.Tests.Client;
using OllamaRouter.Tests.Services.Hardware;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Services.Process;

/// <summary>
/// Integration tests for the real <see cref="ProcessRunner"/> verifying concurrent stdout/stderr/stdin
/// pipe draining on payloads larger than OS anonymous pipe buffers (4 KB on Windows, 64 KB on Linux/macOS),
/// timeout cleanup with partial output preservation, non-zero exit codes, and launch failures.
/// </summary>
/// <remarks>
/// This class spawns real OS processes, so it participates in the serial "ProcessSpawning"
/// collection (task #162) to avoid CPU contention with the rest of the parallel suite.
/// </remarks>
[Collection("ProcessSpawning")]
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

    /// <summary>
    /// Deterministic drain-window regression gate (task #162). The historical 2 s post-exit
    /// drain cap flaked when, at child exit, a large remainder still needed draining under
    /// full-suite load. The only way to make post-exit draining take deterministically more
    /// than 2 s is to have a <b>backgrounded descendant hold the stdout pipe open while the
    /// direct child has already exited</b>: the drain then legitimately blocks in
    /// <c>ReadAsync</c> (no data, no EOF) until the descendant flushes its tail and exits.
    /// The shell therefore writes the payload head, backgrounds <c>sleep 4; cat tail</c>, and
    /// exits immediately — so the post-exit drain takes ≈ 4 s. With the old 2 s cap the
    /// snapshot at the <c>WaitAll</c> timeout contains at most the head and
    /// <c>Assert.Equal(payload, ...)</c> fails deterministically; with the 30 s safety window
    /// the drain completes when the tail is flushed (≥ 26 s margin).
    /// </summary>
    [Fact]
    public void Run_ChildExitsButDescendantHoldsPipe_DrainsFullPayloadAfterExitWithinSafetyWindow()
    {
        var payload = BuildAsciiPayload("holdpipe-line", 256 * 1024);
        var splitAt = payload.IndexOf('\n', 120 * 1024);
        var head = payload[..(splitAt + 1)];
        var tail = payload[(splitAt + 1)..];
        var headFile = CreateTempFile(head);
        var tailFile = CreateTempFile(tail);
        try
        {
            var (fileName, arguments) = BuildShellCommand(
                windowsCommand: $"type \"{headFile}\" & start /b cmd /c \"ping -n 5 127.0.0.1 >nul & type \"{tailFile}\"\"",
                unixCommand: $"cat '{headFile}'; (sleep 4; cat '{tailFile}') &");

            var runner = new ProcessRunner();
            var result = runner.Run(fileName, arguments, timeoutMs: OperatingSystem.IsWindows() ? 15_000 : 10_000);

            Assert.True(result.Succeeded, $"Expected success, timedOut={result.TimedOut}, exit={result.ExitCode}, err={result.StdErr}");
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(payload, result.StdOut);
            Assert.Equal(string.Empty, result.StdErr);
        }
        finally
        {
            DeleteQuietly(headFile);
            DeleteQuietly(tailFile);
        }
    }

    /// <summary>
    /// Layout guard (task #162): the "ProcessSpawning" collection must be defined with
    /// parallelization disabled, and the real-process-spawning test classes must participate.
    /// The collection name is read from the attribute's constructor argument
    /// (<see cref="CustomAttributeData.ConstructorArguments"/>) so the guard does not depend
    /// on a version-specific property name on xunit's collection attributes.
    /// </summary>
    [Fact]
    public void ProcessSpawning_CollectionIsDefinedAndProcessRunnerTestsParticipates()
    {
        AssertParticipatesInProcessSpawningCollection(typeof(ProcessRunnerTests));
        AssertParticipatesInProcessSpawningCollection(typeof(OllamaRouterDaemonLauncherHandleLeakTests));
        AssertParticipatesInProcessSpawningCollection(typeof(HardwareProfileTests));

        var definitionData = typeof(ProcessSpawningCollection).GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionDefinitionAttribute));
        Assert.NotNull(definitionData);
        Assert.Equal("ProcessSpawning", (string?)definitionData!.ConstructorArguments[0].Value);

        var definition = typeof(ProcessSpawningCollection).GetCustomAttribute<CollectionDefinitionAttribute>();
        Assert.NotNull(definition);
        Assert.True(definition.DisableParallelization);
    }

    private static void AssertParticipatesInProcessSpawningCollection(Type testClass)
    {
        var collectionData = testClass.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute));
        Assert.NotNull(collectionData);
        Assert.Equal("ProcessSpawning", (string?)collectionData!.ConstructorArguments[0].Value);
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
