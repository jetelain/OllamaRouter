using System.IO;
using System.Text.Json;
using Moq;
using OllamaRouter.Cli;
using OllamaRouter.Cli.Commands;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Info;

namespace OllamaRouter.Tests.Cli;

/// <summary>
/// Task #100: unit tests of the CLI lifecycle command execution (start/stop/restart/status/
/// logs) and the hand-rolled dispatcher — fake <c>IOllamaRouterDaemonLauncher</c> +
/// <c>IOllamaRouterApiClient</c> (Moq), <c>CliContext</c> with <see cref="StringWriter"/>
/// output; no real processes, no real console.
/// </summary>
public class LifecycleCommandsTests
{
    private static readonly Uri TestBaseUrl = new("http://127.0.0.1:11434/");

    private static CliContext BuildContext(
        Mock<IOllamaRouterApiClient> client,
        Mock<IOllamaRouterDaemonLauncher> launcher,
        out StringWriter output,
        out StringWriter error)
    {
        output = new StringWriter();
        error = new StringWriter();
        return CliContext.Create(
            baseUrl: TestBaseUrl,
            client: client.Object,
            launcher: launcher.Object,
            output: output,
            error: error);
    }

    private static RouterDaemonStatus RunningStatus(int pid = 4242, double uptime = 3723.0) =>
        new(
            RouterDaemonState.Running,
            new RouterInfoResponse("9.9.9-test", true,
                new RouterFleetSummary(2, 1, true), new RouterFeatures(true, true, true, true),
                pid, DateTimeOffset.UtcNow.AddSeconds(-uptime), uptime),
            null);

    private static RouterFleetResponse TwoTargetFleet() =>
        new(new[]
        {
            new RouterTargetItem("Local", "http://127.0.0.1:11434", true, "Online", 5, "local",
                new RouterTargetHardware("nvidia", new[] { "GPU 0" }, 16000, 4, 1.0),
                new RouterTargetExecution(2, "llama3:8b", "4k")),
            new RouterTargetItem("Remote", "http://192.168.1.2:11434", false, "Offline", null, "remote",
                new RouterTargetHardware("amd", new[] { "GPU 0" }, 32000, 8, null),
                new RouterTargetExecution(0, null, "-")),
        });

    // ------------------------------------------------------------------
    //  start / stop / restart
    // ------------------------------------------------------------------

    [Fact]
    public async Task Start_AlreadyRunning_PrintsPidVersionBindAndExits0()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningStatus());

        var context = BuildContext(client, launcher, out var output, out _);

        var exitCode = await new StartCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exitCode);
        launcher.Verify(l => l.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        var text = output.ToString();
        Assert.Contains("Running", text);
        Assert.Contains("9.9.9-test", text);
        Assert.Contains("4242", text);
        Assert.Contains("http://127.0.0.1:11434", text);
    }

    [Fact]
    public async Task Start_Stopped_ReportsRunningWithPidAfterLaunch()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningStatus(pid: 777));

        var context = BuildContext(client, launcher, out var output, out _);

        var exitCode = await new StartCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("777", output.ToString());
    }

    [Fact]
    public async Task Start_DaemonException_Exit1()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.StartAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RouterDaemonStartTimeoutException(TimeSpan.FromSeconds(5), "/x/ollamarouter-server"));

        var context = BuildContext(client, launcher, out _, out var error);

        var exitCode = await new StartCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("did not report a running identity probe", error.ToString());
    }

    [Fact]
    public async Task Stop_Stopped_IsNoOpExit0()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.StopAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterDaemonStatus(RouterDaemonState.Stopped, null, "connection refused"));

        var context = BuildContext(client, launcher, out var output, out _);

        var exitCode = await new StopCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exitCode);
        launcher.Verify(l => l.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("Stopped", output.ToString());
    }

    [Fact]
    public async Task Stop_Running_CallsStopAsync()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.StopAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterDaemonStatus(RouterDaemonState.Stopped, null, null));

        var context = BuildContext(client, launcher, out var output, out _);

        var exitCode = await new StopCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exitCode);
        launcher.Verify(l => l.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("Stopped", output.ToString());
    }

    [Fact]
    public async Task Stop_PortOccupied_Exit1WithMessage()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.StopAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RouterDaemonPortOccupiedException(new RouterDaemonStatus(
                RouterDaemonState.PortOccupiedByOtherService, null, "HTTP 404 — a non-router service occupies the port")));

        var context = BuildContext(client, launcher, out _, out var error);

        var exitCode = await new StopCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("occupied by a non-router service", error.ToString());
    }

    [Fact]
    public async Task Restart_CallsStopThenStartInOrder()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        var order = new List<string>();
        launcher.Setup(l => l.StopAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("stop"))
            .ReturnsAsync(new RouterDaemonStatus(RouterDaemonState.Stopped, null, null));
        launcher.Setup(l => l.StartAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("start"))
            .ReturnsAsync(RunningStatus());

        var context = BuildContext(client, launcher, out var output, out _);

        var exitCode = await new RestartCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { "stop", "start" }, order);
        var text = output.ToString();
    }

    // ------------------------------------------------------------------
    //  status
    // ------------------------------------------------------------------

    [Fact]
    public async Task Status_Running_HumanOutputHasStateUptimePidAndTargetRows()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoTargetFleet());
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningStatus(pid: 4242, uptime: 3723.0)); // 1h 2m 3s

        var context = BuildContext(client, launcher, out var output, out _);

        var exitCode = await new StatusCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Running", text);
        Assert.Contains("4242", text);
        Assert.Contains("1h 2m 3s", text);
        Assert.Contains("Local", text);
        Assert.Contains("Remote", text);
        Assert.Contains("Online", text);
        Assert.Contains("Offline", text);
    }

    [Fact]
    public async Task Status_Stopped_Exit0()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterDaemonStatus(RouterDaemonState.Stopped, null, "connection refused"));

        var context = BuildContext(client, launcher, out var output, out _);

        var exitCode = await new StatusCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Stopped", output.ToString());
        client.Verify(c => c.GetFleetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Status_PortOccupied_Exit1()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null,
                "HTTP 404 — a non-router service occupies the port"));

        var context = BuildContext(client, launcher, out _, out var error);

        var exitCode = await new StatusCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("non-router service", error.ToString());
    }

    [Fact]
    public async Task Status_Json_ParsesAndCarriesStateVersionPidAndFleet()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoTargetFleet());
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningStatus(pid: 4242, uptime: 60));

        var context = BuildContext(client, launcher, out var output, out _);

        var exitCode = await new StatusCommand().ExecuteAsync(context, ["--json"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.Equal("Running", root.GetProperty("state").GetString());
        Assert.Equal("9.9.9-test", root.GetProperty("info").GetProperty("version").GetString());
        Assert.Equal(4242, root.GetProperty("info").GetProperty("processId").GetInt32());
        var targets = root.GetProperty("fleet").GetProperty("targets");
        Assert.Equal(2, targets.GetArrayLength());
        Assert.Equal("Local", targets[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Status_UnknownArg_Exit2()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        var context = BuildContext(client, launcher, out _, out var error);

        var exitCode = await new StatusCommand().ExecuteAsync(context, ["--wat"], CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("usage: ollamarouter status", error.ToString());
    }

    // ------------------------------------------------------------------
    //  logs
    // ------------------------------------------------------------------

    [Fact]
    public async Task Logs_PrintsOnlyRequestedLastNLines_UsesNewestFile()
    {
        await using var temp = new TempDirectory();
        var older = Path.Combine(temp.Path, "ollamarouter-2026-11-09.log");
        var newer = Path.Combine(temp.Path, "ollamarouter-2026-11-10.log");
        await File.WriteAllTextAsync(older, string.Join("\n", "old1", "old2", "old3"));
        await File.WriteAllTextAsync(newer, string.Join("\n", "new1", "new2", "new3", "new4", "new5"));
        File.SetLastWriteTimeUtc(older, DateTimeOffset.UtcNow.AddHours(-24).UtcDateTime);
        File.SetLastWriteTimeUtc(newer, DateTimeOffset.UtcNow.UtcDateTime);

        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        var context = BuildContext(client, launcher, out var output, out _);
        var command = new LogsCommand { SearchRoots = () => [temp.Path] };

        var exitCode = await command.ExecuteAsync(context, ["--lines", "2"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().ReplaceLineEndings("\n").Split("\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["new4", "new5"], lines);
    }

    [Fact]
    public async Task Logs_DefaultLineCount_IsFifty()
    {
        await using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "ollamarouter-2026-11-10.log");
        var all = Enumerable.Range(1, 60).Select(i => $"line{i}").ToArray();
        await File.WriteAllTextAsync(file, string.Join("\n", all));

        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        var context = BuildContext(client, launcher, out var output, out _);
        var command = new LogsCommand { SearchRoots = () => [temp.Path] };

        var exitCode = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().ReplaceLineEndings("\n").Split("\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(50, lines.Length);
        Assert.Equal("line11", lines[0]);
        Assert.Equal("line60", lines[^1]);
    }

    [Fact]
    public async Task Logs_NoLogFile_Exit1_ListsSearchedRoots()
    {
        await using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "logs"));

        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        var context = BuildContext(client, launcher, out _, out var error);
        var command = new LogsCommand { SearchRoots = () => [Path.Combine(temp.Path, "logs")] };

        var exitCode = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(1, exitCode);
        var text = error.ToString();
        Assert.Contains("no ollamarouter log file found", text);
        Assert.Contains(Path.Combine(temp.Path, "logs"), text);
    }

    [Fact]
    public async Task Logs_InvalidLineCount_Exit2()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        var context = BuildContext(client, launcher, out _, out var error);

        var exitCode = await new LogsCommand().ExecuteAsync(context, ["-n", "abc"], CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("usage: ollamarouter logs", error.ToString());
    }

    [Fact]
    public async Task Logs_Follow_PrintsInitialLines_IsInterruptible_AndExits0()
    {
        await using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "ollamarouter-2026-11-10.log");
        await File.WriteAllTextAsync(file, "first\nsecond\nthird");

        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        var context = BuildContext(client, launcher, out var output, out _);
        var command = new LogsCommand { SearchRoots = () => [temp.Path] };

        // The follow loop must be interruptible (no blocking reads, no infinite loop): a
        // cancellation inside the first 500 ms poll lap surfaces as a CLEAN exit 0
        // (the production Ctrl+C path is intercepted the same way by the command).
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var exitCode = await command.ExecuteAsync(context, ["--follow"], cts.Token);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["first", "second", "third"], lines);
    }

    [Fact]
    public async Task Logs_Follow_PartialLineIsNotSplitAcrossPolls()
    {
        await using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "ollamarouter-2026-11-10.log");
        await File.WriteAllTextAsync(file, "complete\n");

        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        var context = BuildContext(client, launcher, out var output, out _);
        var command = new LogsCommand { SearchRoots = () => [temp.Path] };

        // The writer appends a PARTIAL line ("par") before the first 500 ms poll lap and
        // completes it ("tial\n") before the second. Follow must print "partial" as ONE
        // complete line: the unterminated tail is re-read once it completes, never printed
        // early and re-emitted as a fragment (regression: the follow loop used to print the
        // partial line and advance the file offset past it, splitting the line).
        var writer = Task.Run(async () =>
        {
            await Task.Delay(150);
            await File.AppendAllTextAsync(file, "par");
            await Task.Delay(600);
            await File.AppendAllTextAsync(file, "tial\n");
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1600));
        var exitCode = await command.ExecuteAsync(context, ["--follow"], cts.Token);
        await writer;

        Assert.Equal(0, exitCode);
        var lines = output.ToString().ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["complete", "partial"], lines);
    }

    // ------------------------------------------------------------------
    //  dispatcher
    // // ------------------------------------------------------------------

    [Fact]
    public async Task UnknownCommand_Exit2WithUsage()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        var context = BuildContext(client, launcher, out _, out var error);

        var exitCode = await CliDispatcher.DispatchAsync(
            ["frobnicate"], context, () => false, CancellationToken.None);

        Assert.Equal(2, exitCode);
        var text = error.ToString();
        Assert.Contains("unknown command", text);
        Assert.Contains("ollamarouter", text);
    }

    [Fact]
    public async Task NoArgsNonInteractive_Exit2WithUsage()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        var context = BuildContext(client, launcher, out _, out var error);

        var exitCode = await CliDispatcher.DispatchAsync(
            [], context, () => false, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage:", error.ToString());
    }

    [Fact]
    public async Task Help_Exit0_ListsAllCommands()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        var context = BuildContext(client, launcher, out var output, out _);

        var exitCode = await CliDispatcher.DispatchAsync(
            ["help"], context, () => false, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        foreach (var command in new[] { "start", "stop", "restart", "status", "monitor", "logs", "help", "--version" })
        {
            Assert.Contains(command, text);
        }

        Assert.Contains("Exit codes:", text);
    }

    [Fact]
    public async Task Version_PrintsInformationalVersion()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        var context = BuildContext(client, launcher, out var output, out _);

        var exitCode = await CliDispatcher.DispatchAsync(
            ["--version"], context, () => false, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var line = output.ToString().Trim();
        Assert.StartsWith("ollamarouter ", line, StringComparison.Ordinal);
        // Strip build metadata (e.g. "1.0.0+<gitsha>") before parsing.
        var versionText = line["ollamarouter ".Length..].Split('+')[0];
        Assert.True(
            System.Version.TryParse(versionText, out _),
            $"expected a version number after the prefix: '{line}'");
    }

    [Fact]
    public async Task MonitorAlias_Top_DispatchesToMonitor()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.EnsureRunningAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterDaemonStatus(RouterDaemonState.Stopped, null, null));

        var context = BuildContext(client, launcher, out _, out var error);

        var exitCode = await CliDispatcher.DispatchAsync(
            ["top"], context, () => false, CancellationToken.None);

        // Stopped (not foreign-owned) → 'top' must route to the monitor command (its ONLY start
        // path is EnsureRunningAsync) which then exits 1 for the stopped daemon BEFORE the TUI.
        launcher.Verify(l => l.EnsureRunningAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, exitCode);
        Assert.Contains("the daemon is not running", error.ToString());
    }

    // ------------------------------------------------------------------
    //  monitor (TUI lifecycle)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Monitor_AutoStartsDaemonThenRunsTui()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        client.Setup(c => c.GetMonitorStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SimpleMonitorState());
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterFleetResponse(Array.Empty<RouterTargetItem>()));

        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.EnsureRunningAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningStatus());

        var context = BuildContext(client, launcher, out _, out _);

        var frames = new List<string>();
        var command = new MonitorCommand
        {
            RunnerFactory = (c, url, interval) =>
            {
                Assert.Equal(TestBaseUrl.ToString().TrimEnd('/') + "/monitor/api", url);
                var runner = new OllamaRouter.Cli.Rendering.DetachedTerminalMonitorRunner(
                    c, url, TimeSpan.FromMilliseconds(50), enableAnsi: false);
                runner.FrameWriter = frames.Add;
                runner.TerminalSize = () => (80, 24);
                runner.NextKeyAsync = _ => Task.FromResult<ConsoleKeyInfo?>(
                    new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false));
                return runner;
            },
        };

        var exitCode = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exitCode);
        launcher.Verify(l => l.EnsureRunningAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotEmpty(frames);
        client.Verify(c => c.ShutdownAsync(It.IsAny<CancellationToken>()), Times.Never,
            "the TUI must NEVER shut the daemon down");
    }

    [Fact]
    public async Task Monitor_PortOccupiedByOtherService_Exit1_RunnerNotStarted()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.EnsureRunningAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null,
                "HTTP 404 — a non-router service occupies the port"));

        var context = BuildContext(client, launcher, out _, out var error);

        var runnerStarted = false;
        var command = new MonitorCommand
        {
            RunnerFactory = (_, _, _) =>
            {
                runnerStarted = true;
                throw new InvalidOperationException("the runner must not start against a foreign port");
            },
        };

        var exitCode = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.False(runnerStarted);
        Assert.Contains("non-router service", error.ToString());
    }

    // ------------------------------------------------------------------
    //  helpers
    // ------------------------------------------------------------------

    /// <summary>A minimal monitor state (one local target, no activity) for the TUI tests.</summary>
    internal static MonitorStateResponse SimpleMonitorState() =>
        new(
            CloudEnabled: false,
            Fleet: new[]
            {
                new MonitorFleetTargetItem("Local", true, "Online", 0, "-", "local",
                    "http://127.0.0.1:11434", null, "16gb"),
            },
            Statistics: new MonitorStatisticsStatus(
                new Dictionary<string, OllamaRouter.Services.Monitoring.ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Local"] = new OllamaRouter.Services.Monitoring.ActivityStatisticsTotals(0, 0, 0),
                    ["total"] = new OllamaRouter.Services.Monitoring.ActivityStatisticsTotals(0, 0, 0),
                },
                new Dictionary<string, OllamaRouter.Services.Monitoring.ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Local"] = new OllamaRouter.Services.Monitoring.ActivityStatisticsTotals(0, 0, 0),
                    ["total"] = new OllamaRouter.Services.Monitoring.ActivityStatisticsTotals(0, 0, 0),
                }),
            InProgress: Array.Empty<MonitorInProgressItem>(),
            Requests: Array.Empty<MonitorRecentRequestItem>(),
            Cost: null,
            Overhead: null);

    /// <summary>A self-deleting temp directory for the log-file tests.</summary>
    private sealed class TempDirectory : IAsyncDisposable
    {
        public TempDirectory() => Path = Directory.CreateTempSubdirectory("ollamarouter-cli-").FullName;

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }

            return ValueTask.CompletedTask;
        }
    }
}
