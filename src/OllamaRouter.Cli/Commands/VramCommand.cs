using System.Text.Json;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Vram;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// <c>ollamarouter vram reclaim [--keep-enabled] [--target &lt;name&gt;] [--json|-j]</c>: auto-starts
/// the daemon (same gate) and invokes <c>POST /api/router/vram/reclaim</c> with
/// <see cref="RouterVramReclaimRequest"/> (<c>disableTarget: !keepEnabled</c> — a plain
/// <c>vram reclaim</c> ALSO administratively disables the processed targets). Prints the
/// unloaded models and the disabled flag per target plus the fleet total. The wire response
/// carries NO reclaimed-VRAM byte count — per-node post-reclaim residency is inspectable via
/// <c>ollamarouter cartography</c>. <c>success: false</c> (e.g. "no targets configured") and
/// API errors (404 unknown <c>--target</c>) exit 1.
/// </summary>
public sealed class VramCommand : IRouterCommand
{
    public string Name => "vram";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Reclaim local GPU VRAM: unload resident models (--keep-enabled to leave targets enabled).";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || !string.Equals(args[0], "reclaim", StringComparison.OrdinalIgnoreCase))
        {
            Usage(context);
            return 2;
        }

        var keepEnabled = false;
        var target = default(string?);
        var json = false;
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--keep-enabled")
            {
                keepEnabled = true;
            }
            else if (arg is "--target" && i + 1 < args.Count)
            {
                target = args[++i];
            }
            else if (arg.StartsWith("--target=", StringComparison.Ordinal))
            {
                target = arg["--target=".Length..];
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

        RouterVramReclaimResponse response;
        try
        {
            response = await context.Client.ReclaimVramAsync(
                new RouterVramReclaimRequest(target, null, !keepEnabled), ct);
        }
        catch (OllamaRouterApiException ex)
        {
            context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
            return 1;
        }

        if (json)
        {
            // The raw document goes to stdout even in the failure case (exit 1 either way).
            context.Output.WriteLine(JsonSerializer.Serialize(
                response, OllamaRouterClientJsonContext.Default.RouterVramReclaimResponse));
            if (!response.Success)
            {
                context.Error.WriteLine($"error: {response.Message ?? "the reclaim did not execute"}");
            }

            return response.Success ? 0 : 1;
        }

        if (!response.Success)
        {
            context.Error.WriteLine($"error: {response.Message ?? "the reclaim did not execute"}");
            return 1;
        }

        foreach (var result in response.Targets)
        {
            var models = result.UnloadedModels.Count == 0
                ? ""
                : ": " + string.Join(", ", result.UnloadedModels);
            context.Output.WriteLine($"{result.Target}: unloaded {result.UnloadedModels.Count} model(s){models} ; disabled: {(result.Disabled ? "yes" : "no")}");
        }

        context.Output.WriteLine($"total: {response.TotalUnloaded} model(s) unloaded (per-node residency: ollamarouter cartography)");
        return 0;
    }

    private static void Usage(CliContext context) =>
        context.Error.WriteLine("usage: ollamarouter vram reclaim [--keep-enabled] [--target <name>] [--json|-j]");
}
