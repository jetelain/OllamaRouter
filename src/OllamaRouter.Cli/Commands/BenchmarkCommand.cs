using System.Globalization;
using System.Net;
using System.Text.Json;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Services.Benchmarking;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// <c>ollamarouter benchmark run|status|clear</c> (task #101): triggers
/// <c>POST /api/router/benchmark</c> (202 queued; single-flight → 409 → exit 1 + a
/// <c>benchmark status</c> hint), polls <c>GET /api/router/benchmark/results</c> while
/// <c>--wait</c> (or interactive by default — the run trigger marks every queued target
/// <c>Running</c> synchronously, so "no Running rows" cannot fire early), and renders the
/// viability matrix (decode/prefill tokens/sec across the context ladder per node, with the
/// non-measured-row reasons). <c>benchmark status</c> renders the same table on demand;
/// <c>benchmark clear</c> clears the results. Cancellation (Ctrl+C token) exits 0 cleanly —
/// the server-side run continues regardless.
/// </summary>
public sealed class BenchmarkCommand : IRouterCommand
{
    public string Name => "benchmark";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Run / inspect / clear the micro-benchmark viability matrix (run|status|clear).";

    /// <summary>
    /// Test seam: replaces the interactivity probe (default: the SAME expression the dispatcher
    /// uses — interactive = stdin AND stdout not redirected).
    /// </summary>
    internal Func<bool>? IsInteractive { get; set; }

    /// <summary>Test seam: replaces the wait-loop poll interval (default: 2 s).</summary>
    internal TimeSpan? PollInterval { get; set; }

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1)
        {
            Usage(context);
            return 2;
        }

        return args[0].ToLowerInvariant() switch
        {
            "run" => await RunAsync(context, args.Skip(1), ct),
            "status" => await StatusAsync(context, args.Skip(1), ct),
            "clear" => await ClearAsync(context, args.Skip(1), ct),
            _ => Unknown(context),
        };
    }

    // ------------------------------------------------------------------
    //  run
    // ------------------------------------------------------------------

    private async Task<int> RunAsync(CliContext context, IEnumerable<string> args, CancellationToken ct)
    {
        var target = default(string?);
        var model = default(string?);
        var explicitWait = false;
        var json = false;

        for (var i = 0; i < args.Count(); i++)
        {
            var arg = args.ElementAt(i);
            if (arg is "--target" && i + 1 < args.Count())
            {
                target = args.ElementAt(++i);
            }
            else if (arg.StartsWith("--target=", StringComparison.Ordinal))
            {
                target = arg["--target=".Length..];
            }
            else if (arg is "--model" && i + 1 < args.Count())
            {
                model = args.ElementAt(++i);
            }
            else if (arg.StartsWith("--model=", StringComparison.Ordinal))
            {
                model = arg["--model=".Length..];
            }
            else if (arg is "--wait")
            {
                explicitWait = true;
            }
            else if (arg is "--json" or "-j")
            {
                json = true;
            }
            else
            {
                Usage(context);
                return 2;
            }
        }

        var gate = await DaemonGate.EnsureRunningAsync(context, ct);
        if (gate.HasValue)
        {
            return gate.Value;
        }

        RouterBenchmarkRunResponse started;
        try
        {
            started = await context.Client.StartBenchmarkAsync(
                new RouterBenchmarkRequest(Target: target, Model: model), ct);
        }
        catch (OllamaRouterApiException ex)
        {
            context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
            if (ex.Status == HttpStatusCode.Conflict)
            {
                context.Error.WriteLine("hint: a run is already in progress — track it with 'ollamarouter benchmark status'");
            }

            return 1;
        }

        var wait = explicitWait || (IsInteractive ?? DefaultInteractive)();
        if (!wait)
        {
            if (json)
            {
                context.Output.WriteLine(JsonSerializer.Serialize(
                    started, OllamaRouterClientJsonContext.Default.RouterBenchmarkRunResponse));
            }
            else
            {
                context.Output.WriteLine("benchmark queued: " + string.Join(", ", started.QueuedTargets));
                context.Output.WriteLine("use 'ollamarouter benchmark status' to track progress");
            }

            return 0;
        }

        return await WaitLoopAsync(context, json, ct);
    }

    private static bool DefaultInteractive() =>
        !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>
    /// The wait loop: polls the results every <see cref="PollInterval"/> (default 2 s); human
    /// mode prints ONE summary line per poll (every known target's current status) and, after
    /// the poll containing no <c>Running</c> row, a <c>benchmark: finished</c> line + the final
    /// viability matrix; <c>--json</c> mode prints NO progress lines — exactly ONE
    /// <see cref="RouterBenchmarkResultsResponse"/> document at the end. Transient poll failures
    /// (<see cref="OllamaRouterApiException"/>) print a one-time warning and continue;
    /// cancellation exits 0 cleanly.
    /// </summary>
    private async Task<int> WaitLoopAsync(CliContext context, bool json, CancellationToken ct)
    {
        var poll = PollInterval ?? TimeSpan.FromSeconds(2.0);
        var warnedTransient = false;

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return 0;
            }

            RouterBenchmarkResultsResponse results;
            try
            {
                results = await context.Client.GetBenchmarkResultsAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (OllamaRouterApiException)
            {
                if (!warnedTransient)
                {
                    context.Error.WriteLine("warning: benchmark poll failed; retrying");
                    warnedTransient = true;
                }

                try
                {
                    await Task.Delay(poll, ct);
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }

                continue;
            }

            var finished = results.Targets.All(t => t.Status != BenchmarkRunStatus.Running);
            if (!json)
            {
                context.Output.WriteLine("benchmark: " +
                    string.Join(" ", results.Targets.Select(t => t.Target + "=" + t.Status.ToString())));
                if (finished)
                {
                    context.Output.WriteLine("benchmark: finished");
                    context.Output.WriteLine();
                    PrintResults(context.Output, results);
                }
            }

            if (finished)
            {
                if (json)
                {
                    context.Output.WriteLine(JsonSerializer.Serialize(
                        results, OllamaRouterClientJsonContext.Default.RouterBenchmarkResultsResponse));
                }

                return 0;
            }

            try
            {
                await Task.Delay(poll, ct);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }
    }

    // ------------------------------------------------------------------
    //  status
    // ------------------------------------------------------------------

    private async Task<int> StatusAsync(CliContext context, IEnumerable<string> args, CancellationToken ct)
    {
        var json = false;
        foreach (var arg in args)
        {
            if (arg is not ("--json" or "-j"))
            {
                Usage(context);
                return 2;
            }

            json = true;
        }

        var gate = await DaemonGate.EnsureRunningAsync(context, ct);
        if (gate.HasValue)
        {
            return gate.Value;
        }

        RouterBenchmarkResultsResponse results;
        try
        {
            results = await context.Client.GetBenchmarkResultsAsync(ct);
        }
        catch (OllamaRouterApiException ex)
        {
            context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
            return 1;
        }

        if (json)
        {
            context.Output.WriteLine(JsonSerializer.Serialize(
                results, OllamaRouterClientJsonContext.Default.RouterBenchmarkResultsResponse));
            return 0;
        }

        if (results.Targets.Count == 0)
        {
            context.Output.WriteLine("no benchmark results (run: ollamarouter benchmark run)");
            return 0;
        }

        PrintResults(context.Output, results);
        return 0;
    }

    // ------------------------------------------------------------------
    //  clear
    // ------------------------------------------------------------------

    private async Task<int> ClearAsync(CliContext context, IEnumerable<string> args, CancellationToken ct)
    {
        var json = false;
        foreach (var arg in args)
        {
            if (arg is not ("--json" or "-j"))
            {
                Usage(context);
                return 2;
            }

            json = true;
        }

        var gate = await DaemonGate.EnsureRunningAsync(context, ct);
        if (gate.HasValue)
        {
            return gate.Value;
        }

        RouterBenchmarkClearedResponse cleared;
        try
        {
            cleared = await context.Client.ClearBenchmarkResultsAsync(ct);
        }
        catch (OllamaRouterApiException ex)
        {
            context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
            return 1;
        }

        if (json)
        {
            context.Output.WriteLine(JsonSerializer.Serialize(
                cleared, OllamaRouterClientJsonContext.Default.RouterBenchmarkClearedResponse));
            return 0;
        }

        context.Output.WriteLine("benchmark results cleared");
        return 0;
    }

    // ------------------------------------------------------------------
    //  helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// The viability matrix shared by <c>benchmark status</c> and the wait-loop final render:
    /// one block per target row (header with status + stamps), then one line per context row
    /// (model, role, numCtx, decode/prefill tokens/sec to one decimal, viable, and the reason
    /// for non-measured rows: below-threshold / oom / sizing-pruned / error:…).
    /// </summary>
    internal static void PrintResults(System.IO.TextWriter writer, RouterBenchmarkResultsResponse results)
    {
        foreach (var target in results.Targets)
        {
            writer.WriteLine($"{target.Target} ({target.Status}, started {FormatStamp(target.StartedAt)}, finished {FormatStamp(target.FinishedAt)})");
            foreach (var row in target.Results)
            {
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-32} {1,-8} {2,8} ctx   {3,8} dec/s   {4,8} pre/s   viable {5,-3} {6}",
                    row.Model,
                    row.Role.ToString(),
                    row.NumCtx,
                    DaemonGate.FormatTps(row.DecodeTokensPerSecond),
                    DaemonGate.FormatTps(row.PrefillTokensPerSecond),
                    row.Viable ? "yes" : "no",
                    row.Reason ?? ""));
            }
        }
    }

    private static string FormatStamp(DateTimeOffset? stamp) =>
        stamp is null ? "-" : stamp.Value.ToString("u", CultureInfo.InvariantCulture);

    private static void Usage(CliContext context) =>
        context.Error.WriteLine("usage: ollamarouter benchmark run|status|clear");

    private static int Unknown(CliContext context)
    {
        Usage(context);
        return 2;
    }
}
