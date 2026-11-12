using System.Diagnostics;
using OllamaRouter.App.ViewModels;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="DashboardPoller"/> primitive tests (task #102, spec §4/step 4): the first tick
/// runs immediately, a GATED delay parks the loop (no sleep, no unbounded spin), tick
/// exceptions do not kill the loop (bounded tick count, then parked), and disposal cancels +
/// awaits the loop (no orphan work, no hang).
/// </summary>
public sealed class DashboardPollerTests
{
    /// <summary>Parks the loop until cancellation (completes as cancelled when the token
    /// fires).</summary>
    private static Task GatedDelay(TimeSpan span, CancellationToken ct) => new Task(static () => { }, ct);

    /// <summary>
    /// A bounded, AWAITING wait loop (no sleep): yields to the scheduler until
    /// <paramref name="condition"/> holds. The wait is bounded by BOTH the beat budget AND
    /// the wall-clock <paramref name="maxSeconds"/> deadline (it extends until whichever
    /// runs out LAST) — a beat budget alone can be exhausted before a co-operative task gets
    /// scheduled under heavy xUnit parallel-suite load. Negative assertions pass a tight beat
    /// budget with <paramref name="maxSeconds"/> = 0 to stay fast and deterministic.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int maxBeats = 10000, int maxSeconds = 10)
    {
        var stopwatch = Stopwatch.StartNew();
        var beats = 0;
        while (!condition())
        {
            var beatsExhausted = beats >= maxBeats;
            var deadlineExceeded = maxSeconds <= 0 || stopwatch.Elapsed.TotalSeconds >= maxSeconds;
            if (beatsExhausted && deadlineExceeded)
            {
                break;
            }

            beats++;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task Start_RunsTheFirstTickImmediately_ThenParks()
    {
        var ticks = 0;
        var poller = new DashboardPoller(
            _ => { Interlocked.Increment(ref ticks); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(500),
            GatedDelay);

        await poller.StartAsync();
        await WaitUntilAsync(() => ticks >= 1);
        Assert.True(ticks >= 1, "the first tick must run immediately");

        var afterFirst = ticks;
        await WaitUntilAsync(() => ticks >= 2, maxBeats: 500, maxSeconds: 0);
        Assert.Equal(afterFirst, ticks); // the gated delay parked the loop — no second tick
        await poller.DisposeAsync();
    }

    [Fact]
    public async Task TickExceptions_DoNotKillTheLoop()
    {
        var ticks = 0;
        var delays = 0;
        // Zero delay for the first 50 ticks (bounded work), then park — no sleep, no spin.
        var poller = new DashboardPoller(
            _ =>
            {
                Interlocked.Increment(ref ticks);
                throw new InvalidOperationException("tick boom");
            },
            TimeSpan.FromMilliseconds(1),
            (_, ct) => Interlocked.Increment(ref delays) < 50
                ? Task.CompletedTask
                : GatedDelay(TimeSpan.FromMilliseconds(1), ct));

        await poller.StartAsync();
        await WaitUntilAsync(() => ticks >= 3);

        Assert.True(ticks >= 3, $"the loop must survive tick exceptions (ticks={ticks})");
        await poller.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_CancelsAndAwaitsTheLoop_NoOrphanWork()
    {
        var started = 0;
        var finished = 0;
        var poller = new DashboardPoller(
            async ct =>
            {
                Interlocked.Increment(ref started);
                try
                {
                    await Task.Delay(25, ct); // a ct-aware tick that outlives the disposal decision
                }
                catch (OperationCanceledException)
                {
                    // A cancelled tick still terminates (no orphan work either way).
                }

                Interlocked.Increment(ref finished);
            },
            TimeSpan.FromMilliseconds(10),
            GatedDelay);

        await poller.StartAsync();
        await WaitUntilAsync(() => started >= 1);

        var dispose = poller.DisposeAsync();
        var completed = await Task.WhenAny(dispose.AsTask(), Task.Delay(1000));
        Assert.Same(dispose.AsTask(), completed); // disposal must not hang

        await dispose;
        Assert.Equal(started, finished); // the in-flight tick was awaited, no orphan work
    }

    [Fact]
    public async Task Start_Twice_DoesNotDoubleTick()
    {
        var ticks = 0;
        var poller = new DashboardPoller(
            _ => { Interlocked.Increment(ref ticks); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(500),
            GatedDelay);

        await poller.StartAsync();
        await poller.StartAsync();
        await WaitUntilAsync(() => ticks >= 1);
        await WaitUntilAsync(() => ticks >= 2, maxBeats: 500, maxSeconds: 0);

        Assert.Equal(1, ticks); // exactly one loop ran — the second start was a no-op
        await poller.DisposeAsync();
    }
}
