namespace OllamaRouter.Services;

/// <summary>
/// Hosted service driving the <see cref="ITargetExecutionTracker"/> refresh loop: an
/// asynchronous (non-blocking) boot refresh plus a <see cref="PeriodicTimer"/> tick every
/// <c>RefreshTtl</c> (default 2 s). Exceptions are swallowed at Debug — the timer itself
/// must never throw, and <see cref="ITargetExecutionTracker.RefreshAsync"/> is already
/// fail-soft and coalescing, so this loop only schedules. Mirrors the pattern of
/// <see cref="TargetLivenessService"/>'s re-probe loop (fire-and-forget boot work,
/// 2 s-bounded stop).
/// </summary>
public sealed class TargetExecutionTrackerRefreshService(
    ITargetExecutionTracker tracker,
    ILogger<TargetExecutionTrackerRefreshService> logger) : IHostedService
{
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Asynchronous boot refresh (non-blocking: app.Run() is never delayed).
        _ = Task.Run(() => RefreshSafeAsync(token), CancellationToken.None);

        _loopTask = Task.Run(() => RunLoopAsync(token), CancellationToken.None);

        logger.LogInformation(
            "Execution-state tracker refresh started (period: {Period}).",
            tracker.RefreshTtl);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown raced with the timer; the loop exits via its own token.
            }
            catch (TimeoutException)
            {
                // The loop was stuck in a refresh (bounded by the per-target budget);
                // give up on waiting — the process is going down anyway.
            }
        }

        _cts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(tracker.RefreshTtl);
        while (true)
        {
            try
            {
                await timer.WaitForNextTickAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RefreshSafeAsync(token);
        }
    }

    private async Task RefreshSafeAsync(CancellationToken token)
    {
        try
        {
            if (!token.IsCancellationRequested)
            {
                await tracker.RefreshAsync(token);
            }
        }
        catch (Exception ex)
        {
            // The timer itself must never throw (RefreshAsync is fail-soft; this is defense
            // in depth in case a future change breaks that contract).
            logger.LogDebug(ex, "Execution-state refresh tick failed; continuing.");
        }
    }
}
