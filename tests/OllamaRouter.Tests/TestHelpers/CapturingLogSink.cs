using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace OllamaRouter.Tests.TestHelpers;

/// <summary>
/// Task #159 — the in-memory Serilog sink for the app-logging unit tests.
/// <c>Serilog.Sinks.InMemory</c> is NOT in the offline NuGet cache, so this is the
/// ~30-line home-grown <see cref="ILogEventSink"/> the approved plan calls for
/// (<c>WriteTo.Sink(ILogEventSink)</c> is core Serilog API — no extra package). Thread-safe
/// (a lock: the wizard poll-gate tests interleave thread-pool ticks with UI-thread
/// refreshes).
/// </summary>
internal sealed class CapturingLogSink : ILogEventSink
{
    private readonly object _gate = new();
    private readonly List<LogEvent> _events = [];

    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            _events.Add(logEvent);
        }
    }

    /// <summary>Every captured event, in order.</summary>
    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToList();
            }
        }
    }

    /// <summary>The events at <paramref name="level"/> or ANY MORE VERBOSE level
    /// (numerically <= — e.g. <c>EventsAt(Warning)</c> returns Debug/Information/Warning
    /// events, but NOT Error/Fatal).</summary>
    public IReadOnlyList<LogEvent> EventsAt(LogEventLevel level)
    {
        lock (_gate)
        {
            return _events.Where(e => e.Level <= level).ToList();
        }
    }

    /// <summary>True when any captured event's RENDERED message (or the exception
    /// rendering) contains <paramref name="text"/>.</summary>
    public bool RendersContaining(string text)
    {
        lock (_gate)
        {
            return _events.Any(e => Rendered(e).Contains(text, StringComparison.Ordinal));
        }
    }

    /// <summary>The first captured event whose rendered message (or the exception
    /// rendering) contains <paramref name="text"/> (null when none).</summary>
    public LogEvent? FirstRendered(string text)
    {
        lock (_gate)
        {
            return _events.FirstOrDefault(e => Rendered(e).Contains(text, StringComparison.Ordinal));
        }
    }

    private static readonly MessageTemplateTextFormatter SinkMessageFormatter =
        new("{Message:lj}");

    private static string Rendered(LogEvent e)
    {
        // Render EXACTLY like the file sink's {Message:lj} output template token
        // (literal, unquoted — string scalars come out without JSON quotes).
        using var writer = new StringWriter();
        SinkMessageFormatter.Format(e, writer);
        var rendered = new StringBuilder(writer.ToString());
        if (e.Exception is { } ex)
        {
            rendered.Append(Environment.NewLine).Append(ex);
        }

        return rendered.ToString();
    }
}

/// <summary>
/// Task #159 — a ready-to-inject <see cref="ILogger"/> over a
/// <see cref="CapturingLogSink"/> (the VM/seam tests construct their view models with
/// <see cref="Logger"/>; the static <c>Serilog.Log</c> is NEVER touched except by the
/// explicit static-fallback SafeRun test).
/// </summary>
internal sealed class CapturingLogger
{
    public CapturingLogger()
    {
        Sink = new CapturingLogSink();
        // MinimumLevel.Verbose: the capturing logger must NEVER filter what it captures
        // (the Step 1 detection diagnostics are Debug-level; level FILTERING is covered by
        // the AppLoggingTests against the real file sink).
        Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(Sink).CreateLogger();
    }

    /// <summary>The capturing sink (assertion surface).</summary>
    public CapturingLogSink Sink { get; }

    /// <summary>The injectable logger (VM constructor seams).</summary>
    public ILogger Logger { get; }
}
