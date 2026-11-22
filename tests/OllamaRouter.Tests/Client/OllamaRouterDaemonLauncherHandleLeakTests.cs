using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OllamaRouter.Client.Process;
using OllamaRouter.Endpoints.RouterApi.Info;

namespace OllamaRouter.Tests.Client;

/// <summary>
/// Cross-platform REAL-PROCESS integration test for task #109 (the regression gate): a
/// caller process with FULLY REDIRECTED stdio runs <c>ollamarouter start</c>. On Windows,
/// pre-fix, <c>Process.Start</c> (with the hard-coded <c>bInheritHandles = TRUE</c>) leaked
/// the caller's stdin/stdout/stderr pipe write-ends into the long-lived daemon, so the
/// caller NEVER reached EOF on the CLI's pipes after the CLI exited. Post-fix (std-handle
/// inheritance suppressed + all three streams redirected + stdin closed), both streams reach
/// EOF within 3 s while the daemon stays <see cref="RouterDaemonState.Running"/>.
/// <para>
/// Every budget is bounded (pre-warm ≤ 30 s, CLI start ≤ 45 s, EOF ≤ 3 s, stop ≤ ~12 s) and
/// the cleanup never masks the real assertion. Known benign side effect: the first daemon
/// start lazily creates <c>&lt;test bin&gt;/config/default-key.dat</c> — that is a SECRET file
/// and is intentionally NOT deleted by the cleanup (a developer may use the build dir as home).
/// </para>
/// </summary>
[Collection("ProcessSpawning")]
public sealed class OllamaRouterDaemonLauncherHandleLeakTests
{
    private static readonly string ExeSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    [Trait("Category", "Integration")]
    [Fact]
    public async Task CliStart_WithRedirectedStdio_CallerReachesEofWithinBudget_DaemonStaysRunning()
    {
        var binDir = AppContext.BaseDirectory;
        var cliExe = Path.Combine(binDir, "ollamarouter" + ExeSuffix);
        var daemonExe = Path.Combine(binDir, "ollamarouter-server" + ExeSuffix);
        var cliPresent = File.Exists(cliExe);
        var daemonPresent = File.Exists(daemonExe);
        // The test project references the CLI + server projects, so both apphosts MUST exist
        // in the output dir; their absence is an environment-contract break, not a skip.
        Assert.True(cliPresent && daemonPresent,
            $"apphosts missing from the test output: cli exists={cliPresent}, daemon exists={daemonPresent}");

        var port = GetFreePort();
        var tempLogs = Path.Combine(Path.GetTempPath(), "ollamarouter-handle-leak-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempLogs);

        Process? cli = null;
        OllamaRouterDaemonLauncher? probe = null;
        try
        {
            await PreWarmDaemon(daemonExe, port, tempLogs); // cold-start insurance (CLI budget is a fixed 5 s)

            cli = StartCli(cliExe, port, tempLogs);
            if (!await WaitForExitOrKill(cli, TimeSpan.FromSeconds(45)))
            {
                throw new TimeoutException($"'ollamarouter start' did not exit within 45 s. " +
                    $"stdout: {await TryReadToEnd(cli.StandardOutput, TimeSpan.FromSeconds(5))}" +
                    $"stderr: {await TryReadToEnd(cli.StandardError, TimeSpan.FromSeconds(5))}");
            }

            // EOF regression gate (the crux): both streams must reach EOF within 3 s of the
            // CLI exit. Pre-fix on Windows the daemon kept the pipe write-ends open → timeout.
            var stdout = await ReadToEndWithBudget(cli.StandardOutput, TimeSpan.FromSeconds(3));
            var stderr = await ReadToEndWithBudget(cli.StandardError, TimeSpan.FromSeconds(3));

            Assert.Equal(0, cli.ExitCode);
            Assert.Contains("state:  Running", stdout, StringComparison.Ordinal);

            // The daemon is still alive and responsive (200-telemetry or 401-key-locked —
            // the launcher classifies both as Running).
            probe = new OllamaRouterDaemonLauncher(new Uri($"http://127.0.0.1:{port}/"));
            var status = await probe.GetStatusAsync();
            Assert.Equal(RouterDaemonState.Running, status.State);
        }
        finally
        {
            // 1) Graceful stop (untracked handle → telemetry pid → graceful → wait → kill).
            try
            {
                if (probe is not null)
                {
                    await probe.StopAsync();
                }
            }
            catch (Exception)
            {
                // Never mask the real assertion.
            }

            // 2) Last-resort tree kill of ANY remaining daemon from this test's bin dir.
            KillRemainingDaemons(daemonExe);

            // 3) The CLI itself, if it is still alive.
            try
            {
                if (cli is { HasExited: false })
                {
                    cli.Kill(entireProcessTree: true);
                    cli.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception)
            {
                // Best-effort.
            }
            finally
            {
                cli?.Dispose();
            }

            // 4) The temp logs dir (NOT the bin-dir config/default-key.dat secret file).
            try
            {
                if (Directory.Exists(tempLogs))
                {
                    Directory.Delete(tempLogs, recursive: true);
                }
            }
            catch (Exception)
            {
                // Best-effort temp-dir cleanup.
            }
        }
    }

    // ------------------------------------------------------------------
    //  Scenario steps
    // ------------------------------------------------------------------

    /// <summary>
    /// Starts the daemon directly with the test env and waits until it answers ANY HTTP
    /// response on <c>/api/router/info</c> (30 s budget), then kills the tree. A failure
    /// here fails the test with a clear apphost/env-contract message.
    /// </summary>
    private static async Task PreWarmDaemon(string daemonExe, int port, string tempLogs)
    {
        var startInfo = new ProcessStartInfo(daemonExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        FillEnvironment(startInfo, port, tempLogs);

        var daemon = new Process { StartInfo = startInfo };
        daemon.Start();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = $"http://127.0.0.1:{port}/api/router/info";
            var deadline = Environment.TickCount64 + 30_000;
            var sawResponse = false;
            while (Environment.TickCount64 < deadline)
            {
                try
                {
                    await http.GetAsync(url).ConfigureAwait(false);
                    sawResponse = true;
                    break;
                }
                catch (Exception)
                {
                    // Connection refused / timeout: the daemon is still booting — retry.
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }

            if (!sawResponse)
            {
                throw new TimeoutException(
                    "pre-warm: the daemon apphost never answered within 30 s — the test bin " +
                    "apphost/env contract (OllamaRouter__BindAddress / OLLAMAROUTER_HOME) is broken.");
            }
        }
        finally
        {
            try
            {
                daemon.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best-effort pre-warm cleanup.
            }

            try
            {
                daemon.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
                // Best-effort pre-warm cleanup.
            }
            finally
            {
                daemon.Dispose();
            }
        }
    }

    /// <summary>Starts <c>ollamarouter start</c> with FULLY redirected stdio (the reported scenario).</summary>
    private static Process StartCli(string cliExe, int port, string tempLogs)
    {
        var startInfo = new ProcessStartInfo(cliExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("start");
        FillEnvironment(startInfo, port, tempLogs);

        var cli = new Process { StartInfo = startInfo };
        cli.Start();
        return cli;
    }

    /// <summary>
    /// Copies the current process environment and overlays the deterministic test block:
    /// the CLI base URL + bind address on a FREE loopback port (no collision with a local
    /// Ollama on 11434), <c>OLLAMAROUTER_HOME</c> pinned to the TEST bin (a developer's real
    /// install can never leak in) and a temp log directory.
    /// </summary>
    private static void FillEnvironment(ProcessStartInfo startInfo, int port, string tempLogs)
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            startInfo.EnvironmentVariables[(string)entry.Key] = entry.Value as string;
        }

        startInfo.EnvironmentVariables["OLLAMAROUTER_URL"] = $"http://127.0.0.1:{port}/";
        startInfo.EnvironmentVariables["OLLAMAROUTER_HOME"] = AppContext.BaseDirectory;
        startInfo.EnvironmentVariables["OllamaRouter__BindAddress"] = $"http://127.0.0.1:{port}";
        startInfo.EnvironmentVariables["Logging__LogsDirectory"] = tempLogs;
    }

    // ------------------------------------------------------------------
    //  Bounded helpers
    // ------------------------------------------------------------------

    /// <summary>Waits for the exit within the budget; on timeout kills the tree and returns false.</summary>
    private static async Task<bool> WaitForExitOrKill(Process process, TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(budget);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best-effort.
            }

            return false;
        }
    }

    /// <summary>Reads the stream to EOF; a timeout throws with the budget in the message.</summary>
    private static async Task<string> ReadToEndWithBudget(StreamReader reader, TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(budget);
        try
        {
            return await reader.ReadToEndAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"the CLI stream did not reach EOF within {budget} after the CLI exited " +
                "(handle leakage: a background process still holds the pipe write-end).");
        }
    }

    /// <summary>Best-effort read (diagnostics on the timeout path); returns empty on failure.</summary>
    private static async Task<string> TryReadToEnd(StreamReader reader, TimeSpan budget)
    {
        try
        {
            return await ReadToEndWithBudget(reader, budget).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return "<unreadable>";
        }
    }

    /// <summary>
    /// Kills any remaining daemon whose main module lives in the TEST bin dir (by name
    /// <c>ollamarouter-server</c>). Never touches a developer's own install.
    /// </summary>
    private static void KillRemainingDaemons(string daemonExe)
    {
        var expectedDir = Path.GetDirectoryName(daemonExe);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(daemonExe)))
        {
            try
            {
                var modulePath = process.MainModule?.FileName;
                if (modulePath is not null && Path.GetDirectoryName(modulePath) == expectedDir)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception)
            {
                // Best-effort orphan cleanup (a vanished process throws — nothing to do).
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
