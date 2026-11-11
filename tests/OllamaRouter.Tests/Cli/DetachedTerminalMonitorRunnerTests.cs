using Moq;
using OllamaRouter.Cli.Rendering;
using OllamaRouter.Client;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.Fleet;

namespace OllamaRouter.Tests.Cli;

/// <summary>
/// Task #100: unit tests of the detached, HTTP-polling live TUI runner — fake
/// <c>IOllamaRouterApiClient</c>, scripted <c>NextKeyAsync</c>/<c>FrameWriter</c>/
/// <c>TerminalSize</c> seams, ~50 ms poll interval; every test is bounded (&lt; 2 s) and never
/// blocks on a real console key read.
/// </summary>
public class DetachedTerminalMonitorRunnerTests
{
    private const string MonitorApiUrl = "http://127.0.0.1:11434/monitor/api";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private static readonly ConsoleKeyInfo QuitKey = new('q', ConsoleKey.Q, false, false, false);

    private sealed class KeyScript
    {
        private readonly Queue<ConsoleKeyInfo> _keys;

        public KeyScript(params ConsoleKeyInfo[] keys) => _keys = new Queue<ConsoleKeyInfo>(keys);

        public ConsoleKeyInfo DequeueOrQuit() =>
            _keys.Count > 0 ? _keys.Dequeue() : QuitKey;
    }

    private static (DetachedTerminalMonitorRunner Runner, List<string> Frames, Mock<IOllamaRouterApiClient> Client)
        CreateRunner(
            Mock<IOllamaRouterApiClient>? client = null,
            bool enableAnsi = false,
            KeyScript? keys = null)
    {
        client ??= new Mock<IOllamaRouterApiClient>();
        client.Setup(c => c.GetMonitorStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(LifecycleCommandsTests.SimpleMonitorState());
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterFleetResponse(Array.Empty<RouterTargetItem>()));

        var frames = new List<string>();
        var runner = new DetachedTerminalMonitorRunner(client.Object, MonitorApiUrl, PollInterval, enableAnsi);
        runner.FrameWriter = frames.Add;
        runner.TerminalSize = () => (80, 24);
        var script = keys ?? new KeyScript();
        runner.NextKeyAsync = _ => Task.FromResult<ConsoleKeyInfo?>(script.DequeueOrQuit());
        return (runner, frames, client);
    }

    [Fact]
    public async Task QuitKey_Exits0_RestoresConsole_AndNeverShutsDownServer()
    {
        var (runner, frames, client) = CreateRunner(enableAnsi: true, keys: new KeyScript());

        var exitCode = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(frames.Count > 0, "at least one frame must be painted before the quit");
        var all = string.Join("\n", frames);
        // Console restore: cursor shown, styling reset, alternate screen buffer left.
        Assert.Contains("\x1b[?25h\x1b[0m\x1b[?1049l", all, StringComparison.Ordinal);
        // Alternate screen entered on startup.
        Assert.Contains("\x1b[?1049h", all, StringComparison.Ordinal);
        client.Verify(c => c.ShutdownAsync(It.IsAny<CancellationToken>()), Times.Never,
            "the TUI must NEVER shut the daemon down");
        client.Verify(c => c.ShutdownAsync(), Times.Never);
    }

    [Fact]
    public async Task CtrlC_Exits0()
    {
        // Ctrl+C (TreatControlCAsInput) arrives as Escape + the Control modifier bit (== 2).
        var ctrlC = new ConsoleKeyInfo('\x03', ConsoleKey.Escape, false, false, control: true);
        var (runner, _, _) = CreateRunner(keys: new KeyScript(ctrlC));

        var exitCode = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DigitKey_TogglesNthFleetRow()
    {
        var client = new Mock<IOllamaRouterApiClient>();
        client.Setup(c => c.GetMonitorStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonitorStateResponse(
                CloudEnabled: false,
                Fleet: new[]
                {
                    new MonitorFleetTargetItem("First", true, "Online", 0, "-", "local", "http://127.0.0.1:11434", null, null),
                    new MonitorFleetTargetItem("Second", true, "Online", 0, "-", "remote", "http://192.168.1.2:11434", null, null),
                    new MonitorFleetTargetItem("Third", false, "Offline", 0, "-", "cloud", null, null, null),
                },
                Statistics: new MonitorStatisticsStatus(
                    new Dictionary<string, OllamaRouter.Services.Monitoring.ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase)
                        { ["total"] = new OllamaRouter.Services.Monitoring.ActivityStatisticsTotals(0, 0, 0) },
                    new Dictionary<string, OllamaRouter.Services.Monitoring.ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase)
                        { ["total"] = new OllamaRouter.Services.Monitoring.ActivityStatisticsTotals(0, 0, 0) }),
                InProgress: Array.Empty<MonitorInProgressItem>(),
                Requests: Array.Empty<MonitorRecentRequestItem>(),
                Cost: null,
                Overhead: null));
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterFleetResponse(Array.Empty<RouterTargetItem>()));
        Dictionary<string, bool>? applied = null;
        client.Setup(c => c.UpdateTargetAvailabilityAsync(It.IsAny<IReadOnlyDictionary<string, bool>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyDictionary<string, bool>, CancellationToken>((d, _) => applied = d.ToDictionary(kv => kv.Key, kv => kv.Value))
            .ReturnsAsync((IReadOnlyDictionary<string, bool> d, CancellationToken ct) => d);

        var frames = new List<string>();
        var runner = new DetachedTerminalMonitorRunner(client.Object, MonitorApiUrl, PollInterval, enableAnsi: false);
        runner.FrameWriter = frames.Add;
        runner.TerminalSize = () => (80, 24);
        // The '2' key toggles the 2nd RENDERED fleet row ("Second", currently enabled → false).
        var script = new KeyScript(new ConsoleKeyInfo('2', ConsoleKey.D2, false, false, false));
        runner.NextKeyAsync = _ => Task.FromResult<ConsoleKeyInfo?>(script.DequeueOrQuit());

        var exitCode = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(applied);
        Assert.Single(applied!);
        Assert.False(applied!["Second"]);
    }

    [Fact]
    public async Task RKey_TriggersReclaimVram()
    {
        var reclaimCount = 0;
        var client = new Mock<IOllamaRouterApiClient>();
        client.Setup(c => c.GetMonitorStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(LifecycleCommandsTests.SimpleMonitorState());
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterFleetResponse(Array.Empty<RouterTargetItem>()));
        client.Setup(c => c.ReclaimVramAsync(It.IsAny<OllamaRouter.Endpoints.RouterApi.Vram.RouterVramReclaimRequest?>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref reclaimCount))
            .ReturnsAsync(new OllamaRouter.Endpoints.RouterApi.Vram.RouterVramReclaimResponse(
                true, 0, Array.Empty<OllamaRouter.Endpoints.RouterApi.Vram.RouterVramReclaimTargetResult>()));

        var frames = new List<string>();
        var runner = new DetachedTerminalMonitorRunner(client.Object, MonitorApiUrl, PollInterval, enableAnsi: false);
        runner.FrameWriter = frames.Add;
        runner.TerminalSize = () => (80, 24);
        var reclaimKey = new ConsoleKeyInfo('r', ConsoleKey.R, false, false, false);
        var script = new KeyScript(reclaimKey);
        runner.NextKeyAsync = _ => Task.FromResult<ConsoleKeyInfo?>(script.DequeueOrQuit());

        var exitCode = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, reclaimCount); // r triggers exactly one POST /api/router/vram/reclaim
        // The transient action banner is painted on the next repaint (the loop survived 'r').
        Assert.Contains("VRAM reclaim", string.Join("\n", frames));
    }

    [Fact]
    public async Task PollsRepeatedly_AtConfiguredInterval()
    {
        var polls = 0;
        var client = new Mock<IOllamaRouterApiClient>();
        async Task<MonitorStateResponse> PollingState(CancellationToken ct)
        {
            Interlocked.Increment(ref polls);
            await Task.Delay(20, ct);
            return LifecycleCommandsTests.SimpleMonitorState();
        }

        client.Setup(c => c.GetMonitorStateAsync(It.IsAny<CancellationToken>()))
            .Returns(PollingState);
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterFleetResponse(Array.Empty<RouterTargetItem>()));

        var runner = new DetachedTerminalMonitorRunner(client.Object, MonitorApiUrl, PollInterval, enableAnsi: false);
        var frames = new List<string>();
        runner.FrameWriter = frames.Add;
        runner.TerminalSize = () => (80, 24);
        // Ignore keys ('x') until at least 3 polls happened, then quit.
        var ignore = new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false);
        runner.NextKeyAsync = _ => Task.FromResult<ConsoleKeyInfo?>(Volatile.Read(ref polls) >= 3 ? QuitKey : ignore);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exitCode = await runner.RunAsync(cts.Token);

        Assert.Equal(0, exitCode);
        Assert.True(polls >= 3, $"expected >= 3 polls before quit, got {polls}");
    }

    [Fact]
    public async Task PollFailure_RendersBannerAndKeepsLooping()
    {
        var calls = 0;
        var client = new Mock<IOllamaRouterApiClient>();
        async Task<MonitorStateResponse> GoodThenFailingThenGoodState(CancellationToken ct)
        {
            // First poll is good (a full frame is painted); the SECOND poll fails, so the
            // "connection lost" banner must be painted OVER that last good frame; the loop
            // then recovers.
            if (Interlocked.Increment(ref calls) == 2)
            {
                throw new HttpRequestException("connection refused");
            }

            await Task.Delay(20, ct);
            return LifecycleCommandsTests.SimpleMonitorState();
        }

        client.Setup(c => c.GetMonitorStateAsync(It.IsAny<CancellationToken>()))
            .Returns(GoodThenFailingThenGoodState);
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterFleetResponse(Array.Empty<RouterTargetItem>()));

        var runner = new DetachedTerminalMonitorRunner(client.Object, MonitorApiUrl, PollInterval, enableAnsi: false);
        var frames = new List<string>();
        var bannerOverGoodFramePainted = 0;
        runner.FrameWriter = frame =>
        {
            frames.Add(frame);
            if (frame.Contains("Ollama Router - Activity Monitor", StringComparison.Ordinal) &&
                frame.Contains("connection lost", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref bannerOverGoodFramePainted, 1);
            }
        };
        runner.TerminalSize = () => (80, 24);
        var ignore = new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false);
        // Quit only once the banner has been painted OVER the last good frame (the loop
        // survived the failure and the previous frame stayed visible underneath).
        runner.NextKeyAsync = _ => Task.FromResult<ConsoleKeyInfo?>(
            Volatile.Read(ref bannerOverGoodFramePainted) == 1 ? QuitKey : ignore);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exitCode = await runner.RunAsync(cts.Token);

        Assert.Equal(0, exitCode);
        var allFrames = string.Join("\n", frames);
        Assert.Contains("connection lost", allFrames);
        Assert.Contains("Ollama Router - Activity Monitor", allFrames);
        // The banner frame must overlay the LAST GOOD frame, not clear it to a bare banner.
        Assert.Contains(frames, f =>
            f.Contains("connection lost", StringComparison.Ordinal) &&
            f.Contains("Ollama Router - Activity Monitor", StringComparison.Ordinal));
    }
}
