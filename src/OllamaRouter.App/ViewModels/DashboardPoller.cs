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
    private readonly CancellationTokenSource _cts = new();
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

    /// <summary>Starts the loop (a no-op when already running). The first tick runs immediately.</summary>
    public Task StartAsync()
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

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
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
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
