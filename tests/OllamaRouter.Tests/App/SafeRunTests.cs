using OllamaRouter.App.Logging;
using OllamaRouter.Tests.TestHelpers;
using Serilog;
using Serilog.Events;
using Xunit;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Task #159 — unit tests for the <see cref="SafeRun"/> async-void hardening net
/// (AC4): logged + swallowed (never rethrown), the message surface receives the
/// exception's one-line message, the injected-logger seam works, the static fallback
/// works, and a throwing surface callback is swallowed AFTER the log entry is written.
/// The static-fallback test is the ONLY test in the suite that assigns the process-wide
/// <c>Serilog.Log.Logger</c> (the AppLogging file-logging tests are static-free), and the
/// five tests in this class run sequentially — the bounded-retry loop in that test is a
/// further safety net against any future static writer interleaving in the set→read window.
/// </summary>
public sealed class SafeRunTests
{
    /// <summary>Waits (cooperatively, no real sleep) for an async-void body to complete.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what, int maxBeats = 200_000)
    {
        for (var i = 0; i < maxBeats; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Yield();
        }

        Assert.True(condition(), $"condition not met after {maxBeats} cooperative yields: {what}");
    }

    [Fact]
    public async Task Run_HappyPath_NoEventsCallbackNotCalled()
    {
        var capturing = new CapturingLogger();
        var completed = false;
        var surfaced = false;

        SafeRun.Run(
            async () =>
            {
                await Task.Yield();
                completed = true;
            },
            logger: capturing.Logger,
            surfaceError: _ => surfaced = true);

        await WaitUntilAsync(() => completed, "the happy-path body to complete");
        Assert.Empty(capturing.Sink.Events);
        Assert.False(surfaced);
    }

    [Fact]
    public async Task Run_FaultingWork_LogsErrorWithSameExceptionInstance_SurfacesMessage_DoesNotThrow()
    {
        var capturing = new CapturingLogger();
        var boom = new InvalidOperationException("boom-message");
        string? surfaced = null;

        // The Run call site itself must never throw (that was the async-void crash).
        SafeRun.Run(
            () => Task.FromException(boom),
            logger: capturing.Logger,
            surfaceError: message => surfaced = message);

        await WaitUntilAsync(() => capturing.Sink.Events.Count == 1, "the Error event to be written");

        var onlyEvent = capturing.Sink.Events.First();
        Assert.Equal(LogEventLevel.Error, onlyEvent.Level);
        Assert.Same(boom, onlyEvent.Exception);
        Assert.Equal("boom-message", surfaced);
    }

    [Fact]
    public async Task Run_NullLogger_FallsBackToStaticLogger()
    {
        var capturing = new CapturingLogger();
        var previous = Log.Logger;
        try
        {
            // The fallback reads the process-wide static Log.Logger synchronously inside
            // Run, and the event is then written synchronously (an already-faulted task
            // does not hop threads). The ONLY flake source is a concurrent AppLogging
            // bootstrap clobbering the static in the set→read window; re-asserting the
            // static before each attempt makes this deterministic (bounded retries).
            for (var attempt = 0; attempt < 25 && capturing.Sink.Events.Count == 0; attempt++)
            {
                Log.Logger = capturing.Logger;
                SafeRun.Run(() => Task.FromException(new InvalidOperationException("static-fallback")));
                await Task.Yield();
            }

            var onlyEvent = capturing.Sink.Events
                .First(e => e.Exception is InvalidOperationException);
            Assert.Equal(LogEventLevel.Error, onlyEvent.Level);
            Assert.IsType<InvalidOperationException>(onlyEvent.Exception);
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [Fact]
    public async Task Run_ThrowingSurfaceCallback_SwallowedAfterTheLogEntryIsWritten()
    {
        var capturing = new CapturingLogger();
        var surfaceCalls = 0;

        SafeRun.Run(
            () => Task.FromException(new InvalidOperationException("boom")),
            logger: capturing.Logger,
            surfaceError: _ =>
            {
                surfaceCalls++;
                throw new InvalidOperationException("the surface itself blew up");
            });

        await WaitUntilAsync(
            () => capturing.Sink.Events.Count == 1 && surfaceCalls == 1,
            "the log entry AND the surface call (the surface throw must not mask the log)");
        var onlyEvent = capturing.Sink.Events.First();
        Assert.Equal(LogEventLevel.Error, onlyEvent.Level);
        Assert.Equal("boom", onlyEvent.Exception!.Message);
    }

    [Fact]
    public void Run_NullWork_Throws()
    {
        var capturing = new CapturingLogger();
        Assert.Throws<ArgumentNullException>(() => SafeRun.Run(null!, capturing.Logger));
    }
}
