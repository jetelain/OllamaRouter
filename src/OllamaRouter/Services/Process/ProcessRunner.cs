using System.Diagnostics;
using System.Text;

namespace OllamaRouter.Services.Process;

/// <summary>
/// Result of a (possibly failed or timed-out) external process invocation.
/// </summary>
/// <param name="ExitCode">Process exit code (<c>-1</c> when the process failed to start or was killed).</param>
/// <param name="StdOut">Standard output captured from the process (possibly partial on timeout).</param>
/// <param name="StdErr">Standard error captured from the process.</param>
/// <param name="TimedOut"><c>true</c> when the process had to be killed after the timeout.</param>
internal readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    /// <summary>
    /// Creates a result representing a failed process launch (e.g. binary not found on PATH).
    /// </summary>
    public static ProcessResult FailedToStart(string error) => new(-1, "", error, false);

    /// <summary>
    /// <c>true</c> when the process started, ran to completion, and exited with code 0.
    /// </summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Runs an external process synchronously, capturing its output and never throwing on launch failures.
/// </summary>
internal interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> and waits up to <paramref name="timeoutMs"/>
    /// for it to exit. On timeout the process is killed and partial output is returned with <see cref="ProcessResult.TimedOut"/> set.
    /// </summary>
    ProcessResult Run(string fileName, string arguments, int timeoutMs);

    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> and an optional
    /// <paramref name="standardInput"/> payload. Stdout and stderr are drained concurrently while
    /// the process runs, and <paramref name="standardInput"/> (when non-null) is written
    /// concurrently before closing stdin. <c>null</c> stdin behaves like
    /// <see cref="Run(string, string, int)"/>.
    /// </summary>
    ProcessResult Run(string fileName, string arguments, int timeoutMs, string? standardInput);
}

/// <inheritdoc cref="IProcessRunner" />
internal sealed class ProcessRunner : IProcessRunner
{
    private const int ReadBufferChars = 4096;
    private const int DrainAfterExitTimeoutMs = 2_000;

    /// <inheritdoc />
    public ProcessResult Run(string fileName, string arguments, int timeoutMs) =>
        Run(fileName, arguments, timeoutMs, standardInput: null);

    /// <inheritdoc />
    public ProcessResult Run(string fileName, string arguments, int timeoutMs, string? standardInput)
    {
        System.Diagnostics.Process? process = null;
        try
        {
            process = new System.Diagnostics.Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
        }
        catch (Exception ex)
        {
            process?.Dispose();
            return ProcessResult.FailedToStart(ex.Message);
        }

        var stdOutBuilder = new StringBuilder();
        var stdErrBuilder = new StringBuilder();
        var stdOutGate = new object();
        var stdErrGate = new object();
        Task? stdOutTask = null;
        Task? stdErrTask = null;
        Task? stdInTask = null;

        bool timedOut = false;
        int exitCode = -1;
        try
        {
            var stdOutReader = process.StandardOutput;
            var stdErrReader = process.StandardError;
            var stdInWriter = process.StandardInput;

            // Drain stdout and stderr concurrently while the child runs so verbose output
            // cannot fill the OS pipe buffer and deadlock WaitForExit.
            stdOutTask = StartBackgroundTask(() => DrainStream(stdOutReader, stdOutBuilder, stdOutGate));
            stdErrTask = StartBackgroundTask(() => DrainStream(stdErrReader, stdErrBuilder, stdErrGate));

            if (standardInput is not null)
            {
                stdInTask = StartBackgroundTask(() => WriteStandardInput(stdInWriter, standardInput));
            }
            else
            {
                CloseQuietly(stdInWriter);
            }

            if (!process.WaitForExit(timeoutMs))
            {
                // Expected-kill path (e.g. `intel_gpu_top -s 1` one-shot): kill, then let
                // the background readers drain any buffered output before returning.
                KillQuietly(process);
                timedOut = true;
            }
            else if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
        }
        catch
        {
            KillQuietly(process);
            timedOut = true;
        }
        finally
        {
            WaitQuietly(stdOutTask, stdErrTask, stdInTask, DrainAfterExitTimeoutMs);
            process.Dispose();
        }

        string stdOut;
        lock (stdOutGate)
        {
            stdOut = stdOutBuilder.ToString();
        }

        string stdErr;
        lock (stdErrGate)
        {
            stdErr = stdErrBuilder.ToString();
        }

        return new ProcessResult(exitCode, stdOut, stdErr, timedOut);
    }

    private static Task StartBackgroundTask(Func<Task> action) =>
        Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    private static async Task DrainStream(StreamReader reader, StringBuilder destination, object gate)
    {
        var buffer = new char[ReadBufferChars];
        try
        {
            int charsRead;
            while ((charsRead = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                lock (gate)
                {
                    destination.Append(buffer, 0, charsRead);
                }
            }
        }
        catch
        {
            // Best effort: the process may have been killed or its pipe broken mid-read.
        }
        finally
        {
            CloseQuietly(reader);
        }
    }

    private static async Task WriteStandardInput(StreamWriter writer, string standardInput)
    {
        try
        {
            await writer.WriteAsync(standardInput);
            await writer.FlushAsync();
        }
        catch
        {
            // The process may have exited or closed its stdin early; output capture still runs.
        }
        finally
        {
            CloseQuietly(writer);
        }
    }

    private static void CloseQuietly(IDisposable stream)
    {
        try
        {
            stream.Dispose();
        }
        catch
        {
            // Best effort: the stream or pipe may already be closed.
        }
    }

    private static void WaitQuietly(Task? stdOutTask, Task? stdErrTask, Task? stdInTask, int timeoutMs)
    {
        var tasks = new List<Task>(3);
        if (stdOutTask is not null)
        {
            tasks.Add(stdOutTask);
        }

        if (stdErrTask is not null)
        {
            tasks.Add(stdErrTask);
        }

        if (stdInTask is not null)
        {
            tasks.Add(stdInTask);
        }

        if (tasks.Count == 0)
        {
            return;
        }

        try
        {
            Task.WaitAll(tasks.ToArray(), timeoutMs);
        }
        catch
        {
            // Best effort: drain workers catch their own I/O failures.
        }
    }

    private static void KillQuietly(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort: the process may already be gone.
        }
    }
}
