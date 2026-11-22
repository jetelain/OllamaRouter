using Serilog;

namespace OllamaRouter.App.Logging;

/// <summary>
/// Task #159 — the async-void hardening net (AC4). The app's 20 async-void view
/// handlers are converted to plain <c>void</c> methods that hand their async body to
/// <see cref="Run"/>: any escaping fault is then (a) logged at <see cref="Serilog.Events.LogEventLevel.Error"/>
/// with the full stack and (b) best-effort surfaced through
/// <paramref name="surfaceError"/> (the step's message area) instead of killing the
/// process with an unobserved async-void exception.
/// <para>
/// <paramref name="logger"/> is optional: the view code-behind has no DI by XAML-
/// construction design, so production falls back to the static <c>Serilog.Log.Logger</c>
/// (always bootstrapped in <c>Program.Main</c> before any window exists); unit tests
/// inject a capturing logger. <paramref name="surfaceError"/> faults are swallowed —
/// a surfacing failure must never mask the log entry.
/// </para>
/// </summary>
internal static class SafeRun
{
    /// <summary>
    /// Fire-and-forget execution of an async-void handler body. NEVER rethrows: on any
    /// exception, logs it (Error, full stack) and invokes <paramref name="surfaceError"/>
    /// with the exception's one-line message (best-effort, itself guarded).
    /// </summary>
    public static void Run(
        Func<Task> work,
        ILogger? logger = null,
        Action<string>? surfaceError = null)
    {
        if (work is null)
        {
            throw new ArgumentNullException(nameof(work));
        }

        var effectiveLogger = logger ?? Log.Logger;
        _ = RunCoreAsync(work, effectiveLogger, surfaceError);
    }

    private static async Task RunCoreAsync(
        Func<Task> work,
        ILogger logger,
        Action<string>? surfaceError)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Uncaught exception in async-void UI handler");
            try
            {
                surfaceError?.Invoke(ex.Message);
            }
            catch
            {
                // A surfacing fault must never mask the log entry (AC4: the log line is
                // the durable record; the message surface is best-effort UX).
            }
        }
    }
}
