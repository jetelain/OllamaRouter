using System.Globalization;
using System.Text.Json;
using OllamaRouter.Cli.Serialization;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Endpoints.RouterApi.Fleet;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// <c>ollamarouter status [--json]</c>: queries
/// <see cref="IOllamaRouterDaemonLauncher.GetStatusAsync"/> + <c>GetFleetAsync()</c> (when
/// running) and prints the daemon status, uptime, PID, bind address and a per-target summary
/// (name, liveness, enabled, in-flight; the fleet rows already arrive Cloud-last) — or the raw
/// <see cref="CliStatusPayload"/> JSON document via the source-generated <see cref="CliJsonContext"/>
/// when <c>--json</c> is passed. A stopped daemon is exit 0; a foreign port owner is exit 1.
/// </summary>
public sealed class StatusCommand : IRouterCommand
{
    public string Name => "status";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Show daemon status and the per-target fleet summary (--json for raw JSON).";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        var json = false;
        foreach (var arg in args)
        {
            if (arg is "--json" or "-j")
            {
                json = true;
            }
            else
            {
                context.Error.WriteLine("usage: ollamarouter status [--json]");
                return 2;
            }
        }

        var status = await context.Launcher.GetStatusAsync(ct);

        if (status.State == RouterDaemonState.PortOccupiedByOtherService)
        {
            context.Error.WriteLine($"error: {status.Detail ?? "a non-router service occupies the daemon port"}");
            return 1;
        }

        RouterFleetResponse? fleet = null;
        if (status.State == RouterDaemonState.Running)
        {
            try
            {
                fleet = await context.Client.GetFleetAsync(ct);
            }
            catch (OllamaRouterApiException)
            {
                // Fleet rows are a best-effort supplement; the status block still prints.
            }
        }

        if (json)
        {
            var payload = new CliStatusPayload(status.State.ToString(), status.Detail, status.Info, fleet);
            context.Output.WriteLine(JsonSerializer.Serialize(payload, CliJsonContext.Default.CliStatusPayload));
            return 0;
        }

        DaemonStatusOutput.Print(context.Output, status, context.BaseUrl);

        if (fleet is not null && fleet.Targets.Count > 0)
        {
            context.Output.WriteLine();
            context.Output.WriteLine("   Target      Liveness    Enabled In-flight");
            context.Output.WriteLine("   ─────────── ────────── ─────── ─────────");
            foreach (var target in fleet.Targets)
            {
                context.Output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "   {0,-11} {1,-11} {2,-7} {3,9}",
                    target.Name,
                    target.Liveness,
                    target.Enabled ? "yes" : "no",
                    target.Execution.InFlightRequests));
            }
        }

        return 0;
    }
}
