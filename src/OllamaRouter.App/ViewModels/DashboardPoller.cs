namespace OllamaRouter.App.ViewModels;

/// <summary>
/// Test-safe, resource-safe polling loop (task #102, spec §4). Runs <c>Tick</c> on the thread
/// pool at a fixed interval; exceptions are swallowed (each tick surfaces its own error state)
/// and a cancelled loop exits immediately.
/// <para>
/// The inter-tick <see cref="Delay"/> is injectable (default <see cref="Task.Delay"/>) so tests
/// can gate the loop with a manual gate — NO test ever sleeps or spins.
/// </para>
/// </summary>
internal sealed class DashboardPoller : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _tick;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    // Task #151: NOT readonly — a fresh source per start (see StartAsync): StopAsync cancels
    // the current source, so reusing it would let the next loop observe the stale
    // cancellation and exit without ever ticking (stale data on step re-entry).
    private CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// Creates the poller. <paramref name="tick"/> must be idempotent and catch-all (the
    /// owning view model's <c>RefreshAsync</c>); <paramref name="interval"/> must be positive.
    /// </summary>
    public DashboardPoller(
        Func<CancellationToken, Task> tick,
        TimeSpan interval,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval must be positive.");
        }

        _interval = interval;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    /// <summary>
    /// Starts the loop (a no-op when already running). The first tick runs immediately.
    /// A stop/start cycle reuses this poller (the wizard steps, the router tab): each start
    /// gets a FRESH cancellation source, because <see cref="StopAsync"/> cancels the current
    /// one — reusing a cancelled source would make the new loop exit before its first tick
    /// (task #151: stale fleet/benchmark data on step re-entry).
    /// </summary>
    public Task StartAsync()
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(RunLoopAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the loop: cancels the source and awaits the loop task (no orphan work, no hang —
    /// the delay is cancellation-aware).
    /// </summary>
    public async Task StopAsync()
    {
        var loop = _loop;
        if (loop is null)
        {
            return;
        }

        _loop = null;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await loop;
        }
        catch (Exception)
        {
            // Observation boundary (task #165): a loop task that faults during teardown — e.g.
            // a cancellation abort surfacing as a socket-layer exception from the delay — is
            // OBSERVED here and must never propagate into disposers or the fire-and-forget
            // Task.Run(() => poller.StopAsync()) self-stop (unobserved → the app's FTL hook).
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }

    private async Task RunLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _tick(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // The tick surfaces its own error state; the loop keeps polling.
            }

            try
            {
                await _delay(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
