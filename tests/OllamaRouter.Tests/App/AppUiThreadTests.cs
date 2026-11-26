using Avalonia;
using OllamaRouter.App.ViewModels;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="AppUiThread"/> semantics WITHOUT a live Avalonia application (task #173 —
/// the promoted <c>WizardUiThread</c>): with no <see cref="Application.Current"/> the
/// notification runs SYNCHRONOUSLY on the calling thread, so command state stays
/// deterministic in headless/unit tests.
/// <para>
/// Deliberately in the exclusive "Avalonia" collection WITHOUT a session fixture:
/// the collection is serial (no headless session can race this class while it runs,
/// so <c>Application.Current</c> is guaranteed null), but a non-collection class would
/// run in PARALLEL with the session classes and could observe their
/// <c>Application.Current</c>.
/// </para>
/// </summary>
[Collection("Avalonia")]
public sealed class AppUiThreadTests
{
    [Fact]
    public void InvokeOrPost_WithoutApplication_RunsSynchronouslyOnTheCallingThread()
    {
        Assert.Null(Avalonia.Application.Current);

        var callingThread = Thread.CurrentThread.ManagedThreadId;
        int? executedThread = null;
        bool ran = false;

        AppUiThread.InvokeOrPost(() =>
        {
            ran = true;
            executedThread = Thread.CurrentThread.ManagedThreadId;
        });

        Assert.True(ran, "without an application the action must run synchronously");
        Assert.Equal(callingThread, executedThread);
    }
}

/// <summary>
/// <see cref="AppUiThread"/> semantics WITH a live headless application (task #173): on the
/// UI thread the action runs synchronously; from a pool thread it is POSTED (deferred until
/// the dispatcher pumps) and executes EXACTLY ONCE on the UI thread.
/// </summary>
[Collection("Avalonia")]
public sealed class AppUiThreadSessionTests : IClassFixture<PreloadDialogLayoutTests.HeadlessSessionFixture>
{
    private readonly PreloadDialogLayoutTests.HeadlessSessionFixture _fixture;

    public AppUiThreadSessionTests(PreloadDialogLayoutTests.HeadlessSessionFixture fixture)
        => _fixture = fixture;

    [Fact]
    public async Task InvokeOrPost_OnUiThread_RunsSynchronously()
    {
        await _fixture.DispatchAsync(() =>
        {
            var uiThread = Thread.CurrentThread.ManagedThreadId;
            int? executedThread = null;
            bool ran = false;

            AppUiThread.InvokeOrPost(() =>
            {
                ran = true;
                executedThread = Thread.CurrentThread.ManagedThreadId;
            });

            Assert.True(ran, "on the UI thread the action must run synchronously");
            Assert.Equal(uiThread, executedThread);
        });
    }

    [Fact]
    public async Task InvokeOrPost_FromPoolThread_RunsExactlyOnce_OnADispatcherThread_NotThePool()
    {
        int executedCount = 0;
        int executedThread = 0;

        await _fixture.DispatchAsync(async () =>
        {
            int poolThread = 0;

            // Inside the active dispatch the headless session exposes the Application, so
            // the guard takes its POST branch exactly as in production. NOTE: the posted
            // job may complete on the dispatcher's (background) loop thread WHILE we are
            // still awaiting — so the pin is thread-identity (never the raising pool
            // thread), not timing.
            var work = Task.Run(() =>
            {
                poolThread = Thread.CurrentThread.ManagedThreadId;
                AppUiThread.InvokeOrPost(() =>
                {
                    executedCount++;
                    executedThread = Thread.CurrentThread.ManagedThreadId;
                });
            });
            await work;
            Assert.True(poolThread != 0);

            _poolThread = poolThread;
        });

        // Bounded pump: the posted job is on the dispatcher queue and may land just after
        // this dispatch's job.
        var executed = false;
        for (var i = 0; i < 100 && !executed; i++)
        {
            executed = await _fixture.DispatchAsync(() => executedCount == 1);
        }

        await _fixture.DispatchAsync(() =>
        {
            // Executed EXACTLY ONCE, and off the raising pool thread: an unguarded
            // (synchronous) invocation would have run inline on the pool thread.
            Assert.True(executed, "the posted action must have executed exactly once");
            Assert.Equal(1, executedCount);
            Assert.True(executedThread != _poolThread,
                $"the action ran on the raising pool thread {_poolThread}, not a dispatcher thread");
        });
    }

    private int _poolThread;

    [Fact]
    public async Task InvokeOrPost_TwiceFromPoolThreads_RunsTwice_WithoutThrowing()
    {
        int executedCount = 0;

        await _fixture.DispatchAsync(async () =>
        {
            var tasks = new[]
            {
                Task.Run(() => AppUiThread.InvokeOrPost(() => executedCount++)),
                Task.Run(() => AppUiThread.InvokeOrPost(() => executedCount++)),
            };
            await Task.WhenAll(tasks);
        });

        var pumpException = await Record.ExceptionAsync(
            async () => await _fixture.DispatchAsync(() => { }));

        // Bounded pump: both posted jobs are on the dispatcher queue.
        var executedTwice = false;
        for (var i = 0; i < 100 && !executedTwice; i++)
        {
            executedTwice = await _fixture.DispatchAsync(() => executedCount == 2);
        }

        Assert.True(pumpException is null,
            "two posted notifications must settle without exceptions, got " + pumpException);
        Assert.True(executedTwice, "both posted actions must have executed exactly once each");
        Assert.Equal(2, executedCount);
    }
}
