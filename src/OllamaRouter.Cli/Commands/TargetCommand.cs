using System.Globalization;
using System.Text.Json;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Targets;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// <c>ollamarouter target list|add|remove|enable|disable|reset-streak</c> (task #101): manages
/// the persistent fleet targets (Setup API <c>POST</c>/<c>DELETE /api/router/targets</c>), the
/// runtime availability (<c>POST /targets</c>, with the target name validated case-insensitively
/// against <c>GET /api/router/fleet</c> FIRST — the endpoint silently ignores unknown names) and
/// the failure-streak resets (per-target + <c>--all</c>; a bare <c>reset-streak</c> is a usage
/// error, exit 2). <c>target add</c> exposes exactly what the wire API supports (type /
/// provider / api-key / power-watts) — the effective maxVramMB / speedFactor shown by
/// <c>target list</c> are config.json-only (no wire API to persist them). The API key value is
/// NEVER echoed in any output.
/// </summary>
public sealed class TargetCommand : IRouterCommand
{
    public string Name => "target";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Manage fleet targets: list|add|remove|enable|disable|reset-streak.";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1)
        {
            Usage(context);
            return 2;
        }

        // Sub-verbs are matched case-insensitively (same convention as the dispatcher).
        return args[0].ToLowerInvariant() switch
        {
            "list" => await ListAsync(context, args.Skip(1), ct),
            "add" => await AddAsync(context, args.Skip(1), ct),
            "remove" => await RemoveAsync(context, args.Skip(1), ct),
            "enable" => await SetAvailabilityAsync(context, args.Skip(1), disable: false, ct),
            "disable" => await SetAvailabilityAsync(context, args.Skip(1), disable: true, ct),
            "reset-streak" => await ResetStreakAsync(context, args.Skip(1), ct),
            _ => Unknown(context),
        };
    }

    // ------------------------------------------------------------------
    //  list
    // ------------------------------------------------------------------

    private async Task<int> ListAsync(CliContext context, IEnumerable<string> args, CancellationToken ct)
    {
        var json = false;
        foreach (var arg in args)
        {
            if (arg is not ("--json" or "-j"))
            {
                context.Error.WriteLine("usage: ollamarouter target list [--json]");
                return 2;
            }

            json = true;
        }

        var gate = await DaemonGate.EnsureRunningAsync(context, ct);
        if (gate.HasValue)
        {
            return gate.Value;
        }

        RouterConfigResponse config;
        try
        {
            config = await context.Client.GetConfigAsync(ct);
        }
        catch (OllamaRouterApiException ex)
        {
            context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
            return 1;
        }

        // The runtime columns are a best-effort supplement: a fleet failure still exits 0
        // with the persistent data (mirrors 'status').
        RouterFleetResponse? fleet = null;
        try
        {
            fleet = await context.Client.GetFleetAsync(ct);
        }
        catch (OllamaRouterApiException)
        {
            // Omit the runtime columns.
        }

        if (json)
        {
            // The persistent data only — deterministic.
            context.Output.WriteLine(JsonSerializer.Serialize(
                config, OllamaRouterClientJsonContext.Default.RouterConfigResponse));
            return 0;
        }

        var writer = context.Output;
        writer.WriteLine($"Targets: {config.Targets.Count} configured");
        writer.WriteLine();

        if (fleet is null)
        {
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-12} {1,-32} {2,-9} {3,-6} {4,-7}", "Name", "Url", "Type", "Enabled", "Power"));
        }
        else
        {
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-12} {1,-32} {2,-9} {3,-6} {4,-14} {5,-8} {6,-6} {7,-7}",
                "Name", "Url", "Type", "Enabled", "Liveness", "MaxVRAM", "Speed", "Power"));
        }

        foreach (var target in config.Targets)
        {
            var runtime = fleet?.Targets
                .FirstOrDefault(t => string.Equals(t.Name, target.Name, StringComparison.OrdinalIgnoreCase));
            var power = target.PowerWatts is { } watts
                ? watts.ToString(CultureInfo.InvariantCulture)
                : "-";

            if (runtime is null)
            {
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-12} {1,-32} {2,-9} {3,-6} {4,-7}",
                    target.Name, target.Url, target.Type, target.Enabled ? "yes" : "no", power));
            }
            else
            {
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-12} {1,-32} {2,-9} {3,-6} {4,-14} {5,-8} {6,-6} {7,-7}",
                    target.Name,
                    target.Url,
                    target.Type,
                    target.Enabled ? "yes" : "no",
                    FleetDisplay.Liveness(runtime.Liveness, runtime.LatencyMs),
                    runtime.Hardware.MaxVramMB?.ToString(CultureInfo.InvariantCulture) ?? "-",
                    runtime.Hardware.SpeedFactor?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-",
                    power));
            }

            var roster = target.Models;
            if (roster is { Count: > 0 })
            {
                writer.WriteLine("     models: " +
                    string.Join(", ", roster.Select(m => m.ContextLength is { } len
                        ? m.Name + " (ctx " + len.ToString(CultureInfo.InvariantCulture) + ")"
                        : m.Name)));
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------
    //  add
    // ------------------------------------------------------------------

    private async Task<int> AddAsync(CliContext context, IEnumerable<string> args, CancellationToken ct)
    {
        var name = default(string?);
        var url = default(string?);
        var type = default(string?);
        var provider = default(string?);
        var apiKey = default(string?);
        var powerWatts = default(double?);
        var json = false;

        for (var i = 0; i < args.Count(); i++)
        {
            var arg = args.ElementAt(i);
            if (arg is "--type" && TakeNext(args, ref i, out var typeValue))
            {
                type = typeValue;
            }
            else if (arg.StartsWith("--type=", StringComparison.Ordinal))
            {
                type = arg["--type=".Length..];
            }
            else if (arg is "--provider" && TakeNext(args, ref i, out var providerValue))
            {
                provider = providerValue;
            }
            else if (arg.StartsWith("--provider=", StringComparison.Ordinal))
            {
                provider = arg["--provider=".Length..];
            }
            else if (arg is "--api-key" && TakeNext(args, ref i, out var value))
            {
                apiKey = value;
            }
            else if (arg.StartsWith("--api-key=", StringComparison.Ordinal))
            {
                apiKey = arg["--api-key=".Length..];
            }
            else if (arg is "--power-watts" && i + 1 < args.Count() &&
                     double.TryParse(args.ElementAt(i + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var next))
            {
                powerWatts = next;
                i++;
            }
            else if (arg.StartsWith("--power-watts=", StringComparison.Ordinal) &&
                     double.TryParse(arg["--power-watts=".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var attached))
            {
                powerWatts = attached;
            }
            else if (arg is "--json" or "-j")
            {
                json = true;
            }
            else if (arg.Length > 0 && arg[0] == '-')
            {
                Usage(context);
                return 2;
            }
            else
            {
                if (name is not null && url is not null)
                {
                    Usage(context);
                    return 2;
                }

                if (name is null)
                {
                    name = arg;
                }
                else
                {
                    url = arg;
                }
            }
        }

        if (name is null || url is null)
        {
            Usage(context);
            return 2;
        }

        var gate = await DaemonGate.EnsureRunningAsync(context, ct);
        if (gate.HasValue)
        {
            return gate.Value;
        }

        AddTargetResponse response;
        try
        {
            response = await context.Client.AddTargetAsync(new AddTargetRequest(
                Name: name, Url: url, Type: type, Provider: provider, ApiKey: apiKey, PowerWatts: powerWatts), ct);
        }
        catch (OllamaRouterApiException ex)
        {
            context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
            return 1;
        }

        if (json)
        {
            context.Output.WriteLine(JsonSerializer.Serialize(
                response, OllamaRouterClientJsonContext.Default.AddTargetResponse));
            return 0;
        }

        // The API key value is deliberately never part of this line (type / provider / url only).
        context.Output.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "added target '{0}' ({1}, {2}) — liveness {3}, {4} model(s) discovered{5}",
            name,
            response.Target?.Type ?? type ?? "ollama",
            url,
            response.Liveness,
            response.ModelsDiscovered,
            response.LatencyMs is { } latency
                ? ", " + latency.ToString(CultureInfo.InvariantCulture) + " ms"
                : ""));
        return 0;
    }

    // ------------------------------------------------------------------
    //  remove
    // ------------------------------------------------------------------

    private async Task<int> RemoveAsync(CliContext context, IEnumerable<string> args, CancellationToken ct)
    {
        var name = default(string?);
        var json = false;
        foreach (var arg in args)
        {
            if (arg is "--json" or "-j")
            {
                json = true;
            }
            else if (name is null)
            {
                name = arg;
            }
            else
            {
                Usage(context);
                return 2;
            }
        }

        if (name is null)
        {
            Usage(context);
            return 2;
        }

        var gate = await DaemonGate.EnsureRunningAsync(context, ct);
        if (gate.HasValue)
        {
            return gate.Value;
        }

        DeleteTargetResponse response;
        try
        {
            response = await context.Client.DeleteTargetAsync(name, ct);
        }
        catch (OllamaRouterApiException ex)
        {
            context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
            return 1;
        }

        if (json)
        {
            context.Output.WriteLine(JsonSerializer.Serialize(
                response, OllamaRouterClientJsonContext.Default.DeleteTargetResponse));
            return 0;
        }

        context.Output.WriteLine($"removed target '{name}'");
        return 0;
    }

    // ------------------------------------------------------------------
    //  enable / disable
    // ------------------------------------------------------------------

    private async Task<int> SetAvailabilityAsync(
        CliContext context, IEnumerable<string> args, bool disable, CancellationToken ct)
    {
        var name = default(string?);
        var json = false;
        foreach (var arg in args)
        {
            if (arg is "--json" or "-j")
            {
                json = true;
            }
            else if (name is null)
            {
                name = arg;
            }
            else
            {
                Usage(context);
                return 2;
            }
        }

        if (name is null)
        {
            Usage(context);
            return 2;
        }

        var gate = await DaemonGate.EnsureRunningAsync(context, ct);
        if (gate.HasValue)
        {
            return gate.Value;
        }

        // POST /targets SILENTLY IGNORES unknown names and merely echoes the requested flags —
        // validate against the fleet FIRST so a typo cannot fake success.
        RouterFleetResponse fleet;
        try
        {
            fleet = await context.Client.GetFleetAsync(ct);
        }
        catch (OllamaRouterApiException ex)
        {
            context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
            return 1;
        }

        if (fleet.Targets.All(t => !string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            context.Error.WriteLine($"error: unknown target '{name}' (see ollamarouter target list)");
            return 1;
        }

        Dictionary<string, bool> applied;
        try
        {
            applied = new Dictionary<string, bool>(
                await context.Client.UpdateTargetAvailabilityAsync(
                    new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [name] = !disable }, ct));
        }
        catch (OllamaRouterApiException ex)
        {
            context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
            return 1;
        }

        if (!applied.TryGetValue(name, out var flag))
        {
            context.Error.WriteLine($"error: the daemon did not acknowledge target '{name}'");
            return 1;
        }

        if (json)
        {
            context.Output.WriteLine(JsonSerializer.Serialize(
                applied, OllamaRouterClientJsonContext.Default.DictionaryStringBoolean));
            return 0;
        }

        context.Output.WriteLine($"target '{name}' {(flag ? "enabled" : "disabled")}");
        return 0;
    }

    // ------------------------------------------------------------------
    //  reset-streak
    // ------------------------------------------------------------------

    private async Task<int> ResetStreakAsync(CliContext context, IEnumerable<string> args, CancellationToken ct)
    {
        var name = default(string?);
        var all = false;
        var json = false;
        foreach (var arg in args)
        {
            if (arg is "--all")
            {
                all = true;
            }
            else if (arg is "--json" or "-j")
            {
                json = true;
            }
            else if (name is null)
            {
                name = arg;
            }
            else
            {
                ResetStreakUsage(context);
                return 2;
            }
        }

        // D2: a bare 'target reset-streak' (neither <name> nor --all) is a usage error —
        // no accidental fleet-wide reset; both given at once is one as well.
        if ((name is null) == !all)
        {
            ResetStreakUsage(context);
            return 2;
        }

        var gate = await DaemonGate.EnsureRunningAsync(context, ct);
        if (gate.HasValue)
        {
            return gate.Value;
        }

        if (name is not null)
        {
            ResetTargetStreakResponse response;
            try
            {
                response = await context.Client.ResetTargetStreakAsync(name, ct);
            }
            catch (OllamaRouterApiException ex)
            {
                context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
                return 1;
            }

            if (json)
            {
                context.Output.WriteLine(JsonSerializer.Serialize(
                    response, OllamaRouterClientJsonContext.Default.ResetTargetStreakResponse));
                return 0;
            }

            context.Output.WriteLine($"streak reset for '{name}': streak {response.Streak ?? 0}, circuit {response.Status ?? "-"}");
            return 0;
        }

        ResetFleetStreaksResponse fleetResponse;
        try
        {
            fleetResponse = await context.Client.ResetFleetStreaksAsync(ct);
        }
        catch (OllamaRouterApiException ex)
        {
            context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
            return 1;
        }

        if (json)
        {
            context.Output.WriteLine(JsonSerializer.Serialize(
                fleetResponse, OllamaRouterClientJsonContext.Default.ResetFleetStreaksResponse));
            return 0;
        }

        foreach (var target in fleetResponse.Targets ?? [])
        {
            context.Output.WriteLine($"  {target.Target}: previous streak {target.PreviousStreak}");
        }

        context.Output.WriteLine($"reset {fleetResponse.ResetCount} target(s)");
        return 0;
    }

    // ------------------------------------------------------------------
    //  helpers
    // ------------------------------------------------------------------

    private static void Usage(CliContext context) =>
        context.Error.WriteLine("usage: ollamarouter target list|add|remove|enable|disable|reset-streak");

    private static void ResetStreakUsage(CliContext context) =>
        context.Error.WriteLine("usage: ollamarouter target reset-streak <name> | --all");

    private static int Unknown(CliContext context)
    {
        Usage(context);
        return 2;
    }

    /// <summary>
    /// Consumes the value following a <c>--flag</c> in the argument sequence (advancing
    /// <paramref name="i"/>); returns false when the flag is the LAST argument (the caller
    /// then falls through to the unknown-flag usage error).
    /// </summary>
    private static bool TakeNext(IEnumerable<string> args, ref int i, out string value)
    {
        if (i + 1 < args.Count())
        {
            i++;
            value = args.ElementAt(i);
            return true;
        }

        value = null!;
        return false;
    }
}
