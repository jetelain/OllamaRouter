using System.Globalization;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// <c>ollamarouter logs [--follow|-f] [--lines|-n &lt;count&gt;]</c> (default 50 lines): resolves
/// the NEWEST <c>logs/ollamarouter-*.log</c> file (Serilog day-rolling) across the search
/// roots <c>$OLLAMAROUTER_HOME/logs</c> → <c>&lt;AppContext.BaseDirectory&gt;/logs</c> (CLI
/// sibling) → the platform install dir (<c>%LOCALAPPDATA%\OllamaRouter\logs</c> /
/// <c>~/.ollamarouter/logs</c>) — newest by <c>LastWriteTimeUtc</c> (file name as tie-break).
/// Prints the last N lines; <c>--follow</c> polls the file every 500 ms (no OS inotify/
/// ReadDirectoryWatch dependency → AOT + cross-platform) and streams appended lines until
/// Ctrl+C (clean exit 0). No file found → the searched roots on stderr, exit 1.
/// </summary>
public sealed class LogsCommand : IRouterCommand
{
    /// <summary>The <c>--follow</c> poll interval (500 ms; AOT/cross-platform safe).</summary>
    private static readonly TimeSpan FollowPollInterval = TimeSpan.FromMilliseconds(500);

    public string Name => "logs";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Tail the latest logs/ollamarouter-*.log file (--follow, --lines <count>).";

    /// <summary>
    /// Test seam: replaces the default search-root list (tests point it at a temp directory).
    /// Default: the documented production roots (computed per invocation).
    /// </summary>
    internal Func<IReadOnlyList<string>>? SearchRoots { get; set; }

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        var follow = false;
        var lines = 50;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--follow" or "-f")
            {
                follow = true;
            }
            else if (arg is "--lines" or "-n")
            {
                if (i + 1 >= args.Count ||
                    !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
                    count <= 0)
                {
                    UsageError(context);
                    return 2;
                }

                lines = count;
            }
            else
            {
                UsageError(context);
                return 2;
            }
        }

        var (filePath, searchedRoots) = ResolveLatestLogFile();
        if (filePath is null)
        {
            context.Error.WriteLine("error: no ollamarouter log file found. Searched:");
            foreach (var root in searchedRoots)
            {
                context.Error.WriteLine("  " + root);
            }

            return 1;
        }

        try
        {
            if (!follow)
            {
                PrintLastLines(context.Output, filePath, lines);
                return 0;
            }

            return await FollowWithCleanCtrlCQuitAsync(context, filePath, lines, ct);
        }
        catch (IOException ex)
        {
            context.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Runs the follow loop with a CLEAN <c>Ctrl+C</c> exit (spec contract: "stream appended
    /// lines until Ctrl+C (clean exit 0)"): on an interactive console the <c>Ctrl+C</c> is
    /// intercepted via <c>Console.CancelKeyPress</c> (<c>e.Cancel = true</c>) and surfaced as
    /// a cancelled linked token → exit 0. Hosts where the handler cannot be attached (or a
    /// caller-supplied cancellation) take the same clean-exit-0 path; the follow loop itself
    /// never blocks on a read (500 ms poll).
    /// </summary>
    private static async Task<int> FollowWithCleanCtrlCQuitAsync(
        CliContext context, string path, int initialLines, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler? cancelHandler = null;
        try
        {
            cancelHandler = (_, e) =>
            {
                e.Cancel = true; // do NOT let the OS default kill the process
                linked.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
        }
        catch
        {
            cancelHandler = null; // redirected/non-interactive host → OS default Ctrl+C
        }

        try
        {
            return await FollowAsync(context, path, initialLines, linked.Token);
        }
        catch (OperationCanceledException)
        {
            return 0; // Ctrl+C (or caller cancellation) → clean exit
        }
        finally
        {
            if (cancelHandler is not null)
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
    }

    /// <summary>Prints the last <paramref name="count"/> lines of the file (default 50).</summary>
    private static void PrintLastLines(System.IO.TextWriter output, string path, int count)
    {
        var all = File.ReadAllLines(path);
        foreach (var line in all[^Math.Min(count, all.Length)..])
        {
            output.WriteLine(line);
        }
    }

    /// <summary>
    /// Follows the file: prints the last N lines once, then polls every 500 ms and streams
    /// appended COMPLETE lines until the token is cancelled (Ctrl+C → clean exit 0).
    /// </summary>
    private static async Task<int> FollowAsync(
        CliContext context, string path, int initialLines, CancellationToken ct)
    {
        PrintLastLines(context.Output, path, initialLines);

        var position = new FileInfo(path).Length;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(FollowPollInterval, ct);

            long end;
            try
            {
                end = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                continue; // the file may be mid-rotation; retry on the next lap
            }

            if (end < position)
            {
                continue; // truncated/rotated — do not re-print
            }

            if (end == position)
            {
                continue;
            }

            using var stream = new System.IO.FileStream(
                path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            stream.Seek(position, System.IO.SeekOrigin.Begin);
            var buffer = new System.IO.StreamReader(stream).ReadToEnd();

            var lines = buffer.Split('\n');
            // The trailing element is empty (buffer ends on a newline) or a PARTIAL line (the
            // writer has not finished it yet) — never print it; advance only to the partial
            // line's start so it is re-read once it completes. The advance is the exact UTF-8
            // BYTE count of the printed complete lines (each + its '\n'): FileInfo.Length and
            // FileStream.Seek are byte offsets, so char counts would drift on multibyte text.
            var complete = lines.Length - 1;
            for (var i = 0; i < complete; i++)
            {
                context.Output.WriteLine(lines[i].TrimEnd('\r'));
            }

            var advance = 0;
            for (var i = 0; i < complete; i++)
            {
                advance += System.Text.Encoding.UTF8.GetByteCount(lines[i]) + 1;
            }

            position += advance;
        }

        return 0;
    }

    /// <summary>
    /// Resolves the newest <c>ollamarouter-*.log</c> across the search roots (newest
    /// <c>LastWriteTimeUtc</c>, file name as tie-break). Returns null when nothing matches.
    /// </summary>
    private (string? FilePath, IReadOnlyList<string> Roots) ResolveLatestLogFile()
    {
        var roots = SearchRoots is { } custom ? custom() : DefaultSearchRoots();
        string? bestPath = null;
        var bestTime = DateTime.MinValue;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "ollamarouter-*.log"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc > bestTime ||
                    (info.LastWriteTimeUtc == bestTime && bestPath is not null &&
                     string.CompareOrdinal(info.Name, Path.GetFileName(bestPath)) > 0))
                {
                    bestTime = info.LastWriteTimeUtc;
                    bestPath = file;
                }
            }
        }

        return (bestPath, roots);
    }

    /// <summary>The documented production search roots, in precedence order.</summary>
    private static List<string> DefaultSearchRoots()
    {
        var roots = new List<string>();

        var home = Environment.GetEnvironmentVariable("OLLAMAROUTER_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            roots.Add(Path.Combine(home, "logs"));
        }

        // The CLI sibling directory (the daemon writes logs next to the installed executable).
        roots.Add(Path.Combine(AppContext.BaseDirectory, "logs"));

        roots.Add(OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OllamaRouter", "logs")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ollamarouter", "logs"));

        return roots;
    }

    private static void UsageError(CliContext context) =>
        context.Error.WriteLine("usage: ollamarouter logs [--follow|-f] [--lines|-n <count>]");
}
