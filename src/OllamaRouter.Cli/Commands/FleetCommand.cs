using System.Globalization;
using System.Text.Json;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Fleet;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// <c>ollamarouter fleet</c> (alias <c>ps</c>): auto-starts the daemon via
/// <see cref="DaemonGate.EnsureRunningAsync"/> (the SAME gate as <c>monitor</c> — a foreign port
/// owner or failed auto-start exits 1 BEFORE any API call) and renders a formatted table of the
/// fleet nodes from <c>GET /api/router/fleet</c> (liveness, enabled, location, hardware VRAM,
/// context allocation, in-flight, resident models, keep-alive policy, failure streak / circuit
/// state / cooldown) — or the raw <see cref="RouterFleetResponse"/> JSON document when
/// <c>--json</c> is passed.
/// </summary>
public sealed class FleetCommand : IRouterCommand
{
    public string Name => "fleet";
    public IReadOnlyList<string> Aliases => ["ps"];
    public string Description => "Show the fleet target summary (alias: ps).";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        var json = false;
        foreach (var arg in args)
        {
            if (arg is not ("--json" or "-j"))
            {
                context.Error.WriteLine("usage: ollamarouter fleet [--json|-j]");
                return 2;
            }

            json = true;
        }

        var gate = await DaemonGate.EnsureRunningAsync(context, ct);
        if (gate.HasValue)
        {
            return gate.Value;
        }

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

        if (json)
        {
            context.Output.WriteLine(JsonSerializer.Serialize(
                fleet, OllamaRouterClientJsonContext.Default.RouterFleetResponse));
            return 0;
        }

        context.Output.WriteLine($"Fleet: {context.BaseUrl.ToString().TrimEnd('/')} — {fleet.Targets.Count} target(s)");
        context.Output.WriteLine();
        context.Output.WriteLine(FleetDisplay.NodeRow(
            "Name", "Liveness", "Enabled", "Location", "GPU", "VRAM", "Ctx", "In-flt", "Keep-alive", "Streak", "Circuit"));

        foreach (var target in fleet.Targets)
        {
            context.Output.WriteLine(FleetDisplay.NodeRow(
                target.Name,
                FleetDisplay.Liveness(target.Liveness, target.LatencyMs),
                target.Enabled ? "yes" : "no",
                target.Location,
                FleetDisplay.GpuLabel(target.Hardware.GpuVendor, target.Hardware.GpuName),
                target.Hardware.MaxVramMB?.ToString(CultureInfo.InvariantCulture) ?? "-",
                target.Execution.ContextAllocation,
                target.Execution.InFlightRequests.ToString(CultureInfo.InvariantCulture),
                FleetDisplay.KeepAlivePolicy(target.Preload),
                target.FailureStreak.ToString(CultureInfo.InvariantCulture),
                FleetDisplay.CircuitState(target.CircuitState, target.CooldownRemainingSeconds)));

            var resident = target.Execution.ResidentModels;
            if (resident is { Count: > 0 })
            {
                context.Output.WriteLine("     resident: " + string.Join(", ", resident));
            }
        }

        return 0;
    }
}

/// <summary>
/// <c>ollamarouter cartography</c>: auto-starts the daemon (same gate) and renders the full
/// <c>GET /api/router/cartography</c> view in three sections — the fleet node table, the flat
/// loaded-model-process list, and the per-model deployment matrix (presence, capability,
/// residency, context ceilings, veto reasons) — or the raw <see cref="RouterCartographyResponse"/>
/// JSON document when <c>--json</c> is passed.
/// </summary>
public sealed class CartographyCommand : IRouterCommand
{
    public string Name => "cartography";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Show the full cartography: nodes, loaded models and the per-model deployment matrix.";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        var json = false;
        foreach (var arg in args)
        {
            if (arg is not ("--json" or "-j"))
            {
                context.Error.WriteLine("usage: ollamarouter cartography [--json|-j]");
                return 2;
            }

            json = true;
        }

        var gate = await DaemonGate.EnsureRunningAsync(context, ct);
        if (gate.HasValue)
        {
            return gate.Value;
        }

        RouterCartographyResponse cartography;
        try
        {
            cartography = await context.Client.GetCartographyAsync(ct);
        }
        catch (OllamaRouterApiException ex)
        {
            context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
            return 1;
        }

        if (json)
        {
            context.Output.WriteLine(JsonSerializer.Serialize(
                cartography, OllamaRouterClientJsonContext.Default.RouterCartographyResponse));
            return 0;
        }

        var writer = context.Output;
        writer.WriteLine($"Cartography: {context.BaseUrl.ToString().TrimEnd('/')} — {cartography.Nodes.Count} node(s)");
        writer.WriteLine();

        writer.WriteLine("Nodes:");
        foreach (var node in cartography.Nodes)
        {
            writer.WriteLine(FleetDisplay.NodeRow(
                node.Name,
                node.Stale ? node.Liveness + " (stale)" : node.Liveness,
                node.Enabled ? "yes" : "no",
                node.Location,
                FleetDisplay.GpuLabel(node.Hardware?.GpuVendor, node.Hardware?.GpuName),
                node.Hardware?.TotalVramMB?.ToString(CultureInfo.InvariantCulture) ?? "-",
                node.ContextAllocation,
                node.InFlightRequests.ToString(CultureInfo.InvariantCulture),
                "-",
                node.FailureStreak.ToString(CultureInfo.InvariantCulture),
                FleetDisplay.CircuitState(node.CircuitState, node.CooldownRemainingSeconds)));
            writer.WriteLine("     loaded: " + DaemonGate.FormatMb(node.TotalLoadedVramBytes));
        }

        writer.WriteLine();
        writer.WriteLine("Loaded models:");
        foreach (var model in cartography.LoadedModels)
        {
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-32} {1,-12} {2,10}  {3,-8} ctx {4}",
                model.Name,
                model.Target,
                DaemonGate.FormatMb(model.SizeVramBytes),
                model.Quantization ?? "-",
                model.ContextLength?.ToString(CultureInfo.InvariantCulture) ?? "-"));
        }

        writer.WriteLine();
        writer.WriteLine("Models:");
        foreach (var model in cartography.Models)
        {
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0}  ({1}, {2}, {3}, native ctx {4}, recommended ctx {5}, cloud overflow: {6})",
                model.Name,
                model.Architecture,
                model.Quantization ?? "?",
                DaemonGate.FormatMb(model.WeightSizeBytes),
                model.NativeContextWindow.ToString(CultureInfo.InvariantCulture),
                model.RecommendedContextWindow.ToString(CultureInfo.InvariantCulture),
                model.CloudOverflowAvailable ? "yes" : "no"));

            foreach (var deployment in model.Deployments)
            {
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "     {0,-12} present {1,-3} capable {2,-3} resident {3,-3} ceiling {4,8} {5}{6}",
                    deployment.Target,
                    deployment.Present ? "yes" : "no",
                    deployment.Capable ? "yes" : "no",
                    deployment.IsResident ? "yes" : "no",
                    deployment.ContextCeiling.ToString(CultureInfo.InvariantCulture),
                    deployment.IsResident && deployment.LoadedSizeVramBytes is { } loaded
                        ? DaemonGate.FormatMb(loaded) + "  "
                        : "",
                    deployment.VetoReason is { } veto ? "veto: " + veto : ""));
            }
        }

        return 0;
    }
}

/// <summary>
/// Shared display formatting of the fleet / cartography tables (task #101): invariant
/// culture, stable column geometry, <c>-</c> for absent data.
/// </summary>
internal static class FleetDisplay
{
    private const string NodeRowFormat =
        "  {0,-12} {1,-14} {2,-7} {3,-8} {4,-26} {5,8} {6,-7} {7,9} {8,-11} {9,6} {10,-18}";

    internal static string NodeRow(
        string name, string liveness, string enabled, string location, string gpu,
        string vram, string ctx, string inFlight, string keepAlive, string streak, string circuit) =>
        string.Format(CultureInfo.InvariantCulture, NodeRowFormat,
            name, liveness, enabled, location, gpu, vram, ctx, inFlight, keepAlive, streak, circuit);

    /// <summary><c>Online</c> or <c>Online (5ms)</c> when the probe latency is known.</summary>
    internal static string Liveness(string liveness, long? latencyMs) =>
        latencyMs is null
            ? liveness
            : liveness + " (" + latencyMs.Value.ToString(CultureInfo.InvariantCulture) + "ms)";

    /// <summary><c>&lt;vendor&gt; &lt;gpuName&gt;</c> (absent parts dropped) or <c>-</c> when neither is known.</summary>
    internal static string GpuLabel(string? vendor, string? gpuName)
    {
        var hasVendor = !string.IsNullOrWhiteSpace(vendor);
        var hasName = !string.IsNullOrWhiteSpace(gpuName);
        if (!hasVendor && !hasName)
        {
            return "-";
        }

        if (!hasVendor)
        {
            return gpuName!;
        }

        return hasName ? vendor! + " " + gpuName! : vendor!;
    }

    /// <summary><c>900s class</c> style policy (or <c>indefinite class</c>) — <c>-</c> for Cloud.</summary>
    internal static string KeepAlivePolicy(RouterTargetPreload? preload) =>
        preload is null
            ? "-"
            : DaemonGate.FormatKeepAlive(preload.KeepAliveSeconds) + " " + preload.KeepAliveSource;

    /// <summary>The circuit state, with the remaining cooldown suffix while <c>Cooldown</c> (<c>Cooldown (42s)</c>).</summary>
    internal static string CircuitState(string state, double? cooldownRemainingSeconds) =>
        state == "Cooldown" && cooldownRemainingSeconds is { } remaining
            ? state + " (" + remaining.ToString("0", CultureInfo.InvariantCulture) + "s)"
            : state;
}
