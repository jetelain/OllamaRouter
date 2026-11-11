using OllamaRouter.Client;
using OllamaRouter.Endpoints;

namespace OllamaRouter.Cli.Rendering;

/// <summary>
/// The detached, HTTP-polling live Terminal UI of <c>ollamarouter monitor</c> (task #100).
/// <para>
/// Port of the loop removed from the server-side <c>TerminalMonitoringHostedService</c>, now a
/// pure CLIENT: it polls <c>IOllamaRouterApiClient.GetMonitorStateAsync()</c> +
/// <c>GetFleetAsync()</c> at a configurable interval (default 2 s), renders each snapshot with
/// <see cref="TerminalMonitorRenderer"/> (flicker-free: unchanged frames are skipped) and
/// serves interactive hotkeys WITHOUT stopping the server:
/// </para>
/// <list type="bullet">
/// <item><c>q</c> / <c>Ctrl+C</c> — exit the TUI cleanly (exit 0), leaving the daemon running;</item>
/// <item><c>r</c> — trigger <c>POST /api/router/vram/reclaim</c> (result shown as a transient banner);</item>
/// <item><c>1</c>–<c>9</c> — toggle the Nth RENDERED fleet row (Cloud-last order) via
/// <c>POST /targets</c> (<c>UpdateTargetAvailabilityAsync</c>).</item>
/// </list>
/// <para>
/// A poll failure renders a transient "connection lost — retrying" banner over the last good
/// frame and NEVER crashes the loop. The runner never calls <c>ShutdownAsync</c> /
/// <c>StopAsync</c> — the background daemon keeps serving after the TUI exits.
/// </para>
/// </summary>
public sealed class DetachedTerminalMonitorRunner
{
    /// <summary>The default steady-state poll interval (2 s).</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2.0);

    private readonly IOllamaRouterApiClient _client;
    private readonly string _monitorApiUrl;
    private readonly TimeSpan _pollInterval;
    private readonly bool _enableAnsi;

    private MonitorStateResponse? _lastState;
    private string _lastRenderedFrame = string.Empty;
    private (int Width, int Height) _lastRenderedSize = (80, 24);

    /// <summary>
    /// Test seam (reachable via <c>InternalsVisibleTo OllamaRouter.Tests</c>): replaces the
    /// blocking key read. Default: <c>Console.ReadKey(intercept: true)</c> (the Ctrl+C combo,
    /// enabled via <c>Console.TreatControlCAsInput</c>, arrives as <c>Escape</c> + Control).
    /// </summary>
    internal Func<CancellationToken, Task<ConsoleKeyInfo?>>? NextKeyAsync { get; set; }

    /// <summary>
    /// Test seam: replaces the frame output (home + frame + clear-below). Default: the ANSI
    /// home/clear-below console write of the removed hosted service.
    /// </summary>
    internal Action<string>? FrameWriter { get; set; }

    /// <summary>
    /// Test seam: replaces the terminal size probe. Default: <c>Console.WindowWidth/Height</c>
    /// with an (80, 24) fallback for redirected hosts.
    /// </summary>
    internal Func<(int Width, int Height)>? TerminalSize { get; set; }

    /// <summary>
    /// Creates the runner. <paramref name="monitorApiUrl"/> is the <c>/monitor/api</c> link
    /// shown in the footer (e.g. <c>http://127.0.0.1:11434/monitor/api</c>);
    /// <paramref name="pollInterval"/> defaults to 2 s (non-positive values fall back);
    /// <paramref name="enableAnsi"/> defaults to NO_COLOR unset AND unredirected output.
    /// </summary>
    public DetachedTerminalMonitorRunner(
        IOllamaRouterApiClient client,
        string monitorApiUrl,
        TimeSpan? pollInterval = null,
        bool? enableAnsi = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _monitorApiUrl = monitorApiUrl ?? throw new ArgumentNullException(nameof(monitorApiUrl));
        _pollInterval = pollInterval is { } interval && interval > TimeSpan.Zero
            ? interval
            : DefaultPollInterval;
        _enableAnsi = enableAnsi ?? (Environment.GetEnvironmentVariable("NO_COLOR") is null
            && !Console.IsOutputRedirected);
    }

    /// <summary>
    /// Runs the TUI loop until <c>q</c>/<c>Ctrl+C</c> (or a caller cancellation) — returns
    /// 0 for a clean quit. The console is ALWAYS restored in a <c>finally</c> (cursor shown,
    /// styling reset, alternate screen buffer left) and the daemon is NEVER shut down.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var keyTask = Task.Run(() => ReadNextKeyAsync(ct));
        try
        {
            if (_enableAnsi)
            {
                ConsoleHelper.TryEnableVirtualTerminalProcessing();
                WriteFrame("\x1b[?1049h\x1b[?25l\x1b[2J\x1b[H");
                try
                {
                    Console.TreatControlCAsInput = true;
                }
                catch
                {
                    // Fails safely on redirected hosts (Ctrl+C then takes the default path).
                }
            }

            // First render is IMMEDIATE (no leading interval delay) so at least one frame is
            // always painted; the steady-state polls respect the configured cadence.
            var (state, pollFailed) = await PollAsync(ct).ConfigureAwait(false);
            Render(state, banner: pollFailed ? "connection lost — retrying" : null);

            var pollTask = Task.Run(() => PollWithIntervalAsync(ct));
            while (!ct.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(pollTask, keyTask).ConfigureAwait(false);
                if (ReferenceEquals(completed, keyTask))
                {
                    if (await HandleKeyAsync(keyTask).ConfigureAwait(false))
                    {
                        return 0;
                    }

                    keyTask = Task.Run(() => ReadNextKeyAsync(ct));
                    // Repaint immediately so an action banner (reclaim/toggle) is visible right away
                    // (no-op repaint when nothing changed — flicker-free).
                    Render(_lastState, banner: null);
                    continue; // the (possibly still pending) poll is picked up on the next lap
                }

                (state, pollFailed) = await AwaitPollSafelyAsync(pollTask).ConfigureAwait(false);

                // A key that landed during the poll is handled BEFORE the repaint (snappy keys).
                if (keyTask.IsCompleted)
                {
                    if (await HandleKeyAsync(keyTask).ConfigureAwait(false))
                    {
                        return 0;
                    }

                    keyTask = Task.Run(() => ReadNextKeyAsync(ct));
                }

                Render(state, banner: pollFailed ? "connection lost — retrying" : null);
                pollTask = Task.Run(() => PollWithIntervalAsync(ct));
            }

            return 0;
        }
        finally
        {
            if (_enableAnsi)
            {
                try
                {
                    Console.TreatControlCAsInput = false;
                }
                catch
                {
                    // Best effort (redirected hosts).
                }

                // Show cursor, reset styling, and leave the alternate screen buffer.
                WriteFrame("\x1b[?25h\x1b[0m\x1b[?1049l");
            }
        }
    }

    // ------------------------------------------------------------------
    //  Polling
    // ------------------------------------------------------------------

    /// <summary>Immediate poll (used for the first frame before the cadence starts).</summary>
    private Task<(MonitorStateResponse? State, bool Failed)> PollAsync(CancellationToken ct) =>
        FetchAsync(ct);

    /// <summary>Poll with the configured leading interval delay (steady-state cadence).</summary>
    private async Task<(MonitorStateResponse? State, bool Failed)> PollWithIntervalAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (null, true); // a cancelled token surfaces as a soft failure (the loop exits on ct)
        }

        return await FetchAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One snapshot: <c>GetMonitorStateAsync()</c> + <c>GetFleetAsync()</c> in parallel. Any
    /// exception (dead daemon, timeout, cancellation) is a SOFT failure — the loop renders a
    /// banner over the last good frame and keeps going (never crashes).
    /// </summary>
    private async Task<(MonitorStateResponse? State, bool Failed)> FetchAsync(CancellationToken ct)
    {
        try
        {
            var stateTask = _client.GetMonitorStateAsync(ct);
            var fleetTask = _client.GetFleetAsync(ct);
            await Task.WhenAll(stateTask, fleetTask).ConfigureAwait(false);
            return (await stateTask.ConfigureAwait(false), false);
        }
        catch
        {
            // OperationCanceledException included: transient/cancellation is a soft failure
            // (the loop re-checks ct and exits cleanly in the cancelled case).
            return (null, true);
        }
    }

    private static async Task<(MonitorStateResponse? State, bool Failed)> AwaitPollSafelyAsync(
        Task<(MonitorStateResponse? State, bool Failed)> pollTask)
    {
        try
        {
            return await pollTask.ConfigureAwait(false);
        }
        catch
        {
            return (null, true);
        }
    }

    // ------------------------------------------------------------------
    //  Keys
    // ------------------------------------------------------------------

    /// <summary>Reads the next key through the seam (default: blocking <c>Console.ReadKey</c>).</summary>
    private async Task<ConsoleKeyInfo?> ReadNextKeyAsync(CancellationToken ct)
    {
        var reader = NextKeyAsync
            ?? (token => Task.FromResult<ConsoleKeyInfo?>(Console.ReadKey(intercept: true)));
        return await reader(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles one already-completed key read. Returns <c>true</c> when the TUI must exit
    /// (clean quit, exit 0 — the daemon keeps running). Actions report their outcome as a
    /// transient banner (via <see cref="_lastActionBanner"/>, shown by the NEXT repaint); a
    /// failed action NEVER crashes the loop.
    /// </summary>
    private async Task<bool> HandleKeyAsync(Task<ConsoleKeyInfo?> keyTask)
    {
        ConsoleKeyInfo? key;
        try
        {
            key = await keyTask.ConfigureAwait(false);
        }
        catch
        {
            return true; // cancelled / unreadable key stream (console closed) → clean exit
        }

        if (key is not { } info)
        {
            return false;
        }

        // Ctrl+C arrives as Escape + Control when TreatControlCAsInput is on.
        // (.NET 10: the System.Console.ConsoleKeyModifiers enum lives in a namespace SHADOWED
        // by the System.Console class, so the Control bit (== 2) is compared by value.)
        if (info.Key == ConsoleKey.Escape && ((int)info.Modifiers & 2) == 2)
        {
            return true; // same clean-exit path as 'q' — the daemon keeps running
        }

        switch (info.KeyChar)
        {
            case 'q':
            case 'Q':
                return true;

            case 'r':
            case 'R':
                try
                {
                    await _client.ReclaimVramAsync(null).ConfigureAwait(false);
                    _lastActionBanner = "VRAM reclaim requested (done)";
                }
                catch (OperationCanceledException)
                {
                    return true;
                }
                catch
                {
                    _lastActionBanner = "VRAM reclaim failed (see daemon logs)";
                }

                return false;

            case var c when c is >= '1' and <= '9':
                if (_lastState?.Fleet is { Count: > 0 } fleet)
                {
                    var index = c - '1';
                    if (index < fleet.Count)
                    {
                        var item = fleet[index];
                        var enabled = !item.Enabled;
                        try
                        {
                            await _client.UpdateTargetAvailabilityAsync(
                                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [item.Name] = enabled })
                                .ConfigureAwait(false);
                            _lastActionBanner = $"'{item.Name}' toggled {(enabled ? "enabled" : "disabled")}";
                        }
                        catch (OperationCanceledException)
                        {
                            return true;
                        }
                        catch
                        {
                            _lastActionBanner = $"'{item.Name}' toggle failed (see daemon logs)";
                        }
                    }
                }

                return false;

            default:
                return false; // any other key is ignored
        }
    }

    private string? _lastActionBanner;

    // ------------------------------------------------------------------
    //  Rendering
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders the frame for <paramref name="state"/>. A poll-failure <paramref name="banner"/>
    /// is painted over the LAST GOOD frame (or alone when no snapshot exists yet); the
    /// transient <see cref="_lastActionBanner"/> is shown on the next repaint and cleared
    /// after it. Flicker-free: unchanged frames + dimensions are not re-painted.
    /// </summary>
    private void Render(MonitorStateResponse? state, string? banner)
    {
        if (state is not null)
        {
            _lastState = state;
        }

        var effectiveBanner = state is null ? (banner ?? _lastActionBanner ?? "connection lost — retrying")
            : (banner ?? _lastActionBanner);
        if (effectiveBanner is not null && state is not null)
        {
            _lastActionBanner = null; // an action banner survives exactly one repaint
        }

        var (width, height) = ResolveTerminalSize();
        // A poll failure keeps the LAST GOOD frame visible under the banner (spec contract);
        // only a never-succeeded first poll renders the banner alone.
        var frameState = state ?? _lastState;
        var baseFrame = frameState is null
            ? string.Empty
            : TerminalMonitorRenderer.Render(frameState, _monitorApiUrl, width, height, _enableAnsi);
        var frame = effectiveBanner is null
            ? baseFrame
            : $"\x1b[H\x1b[1m\x1b[93m⚠ {effectiveBanner}\x1b[0m\x1b[K\r\n{baseFrame}";

        // Flicker-free: skip repainting when the rendered frame and dimensions did not change.
        if (string.Equals(frame, _lastRenderedFrame, StringComparison.Ordinal)
            && width == _lastRenderedSize.Width && height == _lastRenderedSize.Height)
        {
            return;
        }

        _lastRenderedFrame = frame;
        _lastRenderedSize = (width, height);
        WriteFrame(frame);
    }

    private (int Width, int Height) ResolveTerminalSize()
    {
        var probe = TerminalSize;
        if (probe is not null)
        {
            return probe();
        }

        try
        {
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            return (width > 0 ? width : 80, height > 0 ? height : 24);
        }
        catch
        {
            return (80, 24); // redirected hosts
        }
    }

    /// <summary>Writes a frame through the seam (default: home + frame + clear-below).</summary>
    private void WriteFrame(string frame)
    {
        var writer = FrameWriter;
        if (writer is not null)
        {
            writer(frame);
            return;
        }

        if (_enableAnsi)
        {
            try
            {
                Console.SetCursorPosition(0, 0);
            }
            catch
            {
                // Ignore if unsupported.
            }

            Console.Write("\x1b[H" + frame + "\x1b[J");
        }
        else
        {
            Console.WriteLine(frame);
        }
    }
}
