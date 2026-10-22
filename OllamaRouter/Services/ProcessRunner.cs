using System.Diagnostics;

namespace OllamaRouter.Services;

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
}

/// <inheritdoc cref="IProcessRunner" />
internal sealed class ProcessRunner : IProcessRunner
{
    public ProcessResult Run(string fileName, string arguments, int timeoutMs)
    {
        Process process;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
        }
        catch (Exception ex)
        {
            return ProcessResult.FailedToStart(ex.Message);
        }

        string stdOut = string.Empty;
        string stdErr = string.Empty;
        bool timedOut = false;
        int exitCode = -1;
        try
        {
            if (!process.WaitForExit(timeoutMs))
            {
                // Expected-kill path (e.g. `intel_gpu_top -s 1` one-shot): kill, then still
                // drain whatever output was produced.
                KillQuietly(process);
                timedOut = true;
            }
            else if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }

            stdOut = process.StandardOutput.ReadToEnd();
            stdErr = process.StandardError.ReadToEnd();
        }
        catch
        {
            KillQuietly(process);
            timedOut = true;
        }
        finally
        {
            process.Dispose();
        }

        return new ProcessResult(exitCode, stdOut, stdErr, timedOut);
    }

    private static void KillQuietly(Process process)
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
