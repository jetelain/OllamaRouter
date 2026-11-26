using System.Globalization;

namespace OllamaRouter.Services.Benchmarking;

/// <summary>
/// The canonical live-benchmark progress text (task #174 — ONE source of truth shared by the
/// server, which builds <see cref="BenchmarkTargetProgress.Message"/>, and the App, which
/// formats the install-wizard byte progress): 1024-based human byte formatting
/// (consistent with <c>FleetTargetDisplay.FormatMemoryMB</c>) + the exact, pinned phase
/// message strings the <c>MicroBenchmarkRunner</c> writes into the progress record. Pure
/// string logic — AOT-safe, unit-testable without any service graph.
/// </summary>
public static class BenchmarkProgressText
{
    private const long MiB = 1024L * 1024L;
    private const long GiB = 1024L * MiB;

    /// <summary>
    /// Formats a byte count for humans (1024-based, invariant culture): &lt; 1024 MiB →
    /// <c>"{mb:0.#} MB"</c>; otherwise <c>"{gb:0.#} GB"</c> (e.g. 536,870,912 →
    /// <c>512 MB</c>, 1,925,560,320 → <c>1.8 GB</c>, 4,613,734,400 → <c>4.3 GB</c>).
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < GiB)
        {
            return (bytes / (double)MiB).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        return (bytes / (double)GiB).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
    }

    /// <summary>
    /// The ready-to-render download sentence: <c>total &gt; 0 &amp;&amp; completed != null</c> →
    /// <c>"Downloading {model} ({pct}% · {completed} / {total})"</c> with
    /// <c>pct = min(100, floor(completed * 100.0 / total))</c>; else a non-blank
    /// <paramref name="detail"/> (the raw daemon status text) → <c>"Downloading {model}
    /// ({detail})"</c>; else <c>"Downloading {model}…"</c>.
    /// </summary>
    public static string DownloadMessage(string model, string? detail, long? completed, long? total)
    {
        if (total is { } t and > 0 && completed is { } c)
        {
            var pct = Math.Min(100L, (long)Math.Floor(c * 100.0 / t));
            return $"Downloading {model} ({pct}% · {FormatBytes(c)} / {FormatBytes(t)})";
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            return $"Downloading {model} ({detail})";
        }

        return $"Downloading {model}…";
    }

    /// <summary>The pinned message of the <c>queued</c> phase.</summary>
    public static string QueuedMessage() => "Queued";

    /// <summary>The pinned message of the <c>sizing</c> phase.</summary>
    public static string SizingMessage(string model) => $"Sizing {model}…";

    /// <summary>The pinned message of the <c>checking-model</c> phase.</summary>
    public static string CheckingMessage(string model) => $"Checking {model}…";

    /// <summary>The pinned message of the <c>inspecting-model</c> phase.</summary>
    public static string InspectingMessage(string model) => $"Inspecting {model}…";

    /// <summary>The pinned message of the <c>unloading-model</c> phase (the evicted model).</summary>
    public static string UnloadingMessage(string model) => $"Unloading {model}…";

    /// <summary>
    /// The pinned message of the <c>benchmarking</c> phase: the model, the rung context and
    /// the 1-based model/rung indices (e.g. <c>"Benchmarking qwen3:8b @ 32768 ctx (model
    /// 2/5, rung 1/3)"</c>).
    /// </summary>
    public static string BenchmarkingMessage(
        string model, int numCtx, int candidateIndex, int candidateCount, int rungIndex, int rungCount)
        => $"Benchmarking {model} @ {numCtx} ctx (model {candidateIndex + 1}/{candidateCount}, rung {rungIndex + 1}/{rungCount})";

    /// <summary>The pinned message of the <c>persisting</c> phase.</summary>
    public static string PersistingMessage() => "Persisting results…";
}
