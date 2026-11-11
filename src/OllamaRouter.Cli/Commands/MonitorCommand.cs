using System.Globalization;
using OllamaRouter.Cli.Rendering;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// <c>ollamarouter monitor</c> (alias <c>top</c>, and the default interactive no-argument
/// action): calls <c>EnsureRunningAsync()</c> (the ONLY start path — a foreign port owner is
/// reported and exits 1 BEFORE the TUI launches, a failed auto-start exits 1) and then runs
/// the detached HTTP-polling <see cref="DetachedTerminalMonitorRunner"/>. The runner's exit
/// code (0 = clean quit via <c>q</c>/<c>Ctrl+C</c>, daemon left running) is the command's.
/// </summary>
public sealed class MonitorCommand : IRouterCommand
{
    public string Name => "monitor";
    public IReadOnlyList<string> Aliases => ["top"];
    public string Description => "Launch the live activity monitor TUI (auto-starts the daemon).";

    /// <summary>
    /// Test seam: replaces the runner construction (tests script the runner's key/frames).
    /// Default: a real runner over the context's client with the requested poll interval.
    /// </summary>
    internal Func<IOllamaRouterApiClient, string, TimeSpan, DetachedTerminalMonitorRunner>? RunnerFactory { get; set; }

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        var pollInterval = TimeSpan.FromSeconds(2.0);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--interval" or "-i")
            {
                if (i + 1 >= args.Count || !TryParseSeconds(args[++i], out var seconds))
                {
                    UsageError(context);
                    return 2;
                }

                pollInterval = TimeSpan.FromSeconds(seconds);
            }
            else if (arg.StartsWith("--interval=", StringComparison.Ordinal) &&
                     TryParseSeconds(arg["--interval=".Length..], out var attached))
            {
                pollInterval = TimeSpan.FromSeconds(attached);
            }
            else
            {
                UsageError(context);
                return 2;
            }
        }

        RouterDaemonStatus status;
        try
        {
            status = await context.Launcher.EnsureRunningAsync(ct);
        }
        catch (RouterDaemonException ex)
        {
            context.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        if (status.State != RouterDaemonState.Running)
        {
            // PortOccupiedByOtherService — the TUI must never start against a foreign owner.
            context.Error.WriteLine($"error: {status.Detail ?? "the daemon is not running"}");
            return 1;
        }

        var factory = RunnerFactory ?? ((client, url, interval) =>
            new DetachedTerminalMonitorRunner(client, url, interval));
        var runner = factory(context.Client, context.MonitorApiUrl, pollInterval);
        return await runner.RunAsync(ct);
    }

    private static void UsageError(CliContext context) =>
        context.Error.WriteLine("usage: ollamarouter monitor [--interval <seconds>]");

    private static bool TryParseSeconds(string text, out double seconds) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds > 0;
}
