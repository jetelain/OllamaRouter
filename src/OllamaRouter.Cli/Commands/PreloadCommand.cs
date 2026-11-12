using System.Globalization;
using System.Text.Json;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Preload;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// <c>ollamarouter preload &lt;model&gt; [--context &lt;tokens&gt;] [--keep-alive &lt;seconds&gt;]
/// [--target &lt;name&gt;] [--preview] [--json|-j]</c>: auto-starts the daemon (same gate) and invokes
/// <c>GET /api/router/preload/preview</c> (side-effect-free, under <c>--preview</c>) or
/// <c>POST /api/router/models/{model}/preload</c>. Prints the selected target, the applied
/// <c>num_ctx</c> / <c>keep_alive</c>, the load-acceptance flags (load path) and the candidate
/// list with skip reasons. 200 is fail-soft (always exit 0, flags printed explicitly);
/// 400 (bad keep-alive/context) / 404 (unknown model or pin) / 503 (no eligible candidate)
/// exit 1 with the server error text.
/// </summary>
public sealed class PreloadCommand : IRouterCommand
{
    public string Name => "preload";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Preload a model on the selected target (--preview: side-effect-free selection).";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        var model = default(string?);
        var contextTokens = default(int?);
        var keepAliveSeconds = default(int?);
        var target = default(string?);
        var preview = false;
        var json = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--context" && i + 1 < args.Count &&
                int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ctxTokens))
            {
                contextTokens = ctxTokens;
            }
            else if (arg.StartsWith("--context=", StringComparison.Ordinal) &&
                     int.TryParse(arg["--context=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attachedCtx))
            {
                contextTokens = attachedCtx;
            }
            else if (arg is "--keep-alive" && i + 1 < args.Count &&
                     int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var keepAlive))
            {
                keepAliveSeconds = keepAlive;
            }
            else if (arg.StartsWith("--keep-alive=", StringComparison.Ordinal) &&
                     int.TryParse(arg["--keep-alive=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attachedKeepAlive))
            {
                keepAliveSeconds = attachedKeepAlive;
            }
            else if (arg is "--target" && i + 1 < args.Count)
            {
                target = args[++i];
            }
            else if (arg.StartsWith("--target=", StringComparison.Ordinal))
            {
                target = arg["--target=".Length..];
            }
            else if (arg is "--preview")
            {
                preview = true;
            }
            else if (arg is "--json" or "-j")
            {
                json = true;
            }
            else if (arg.Length > 0 && arg[0] == '-')
            {
                // Any remaining dash-leading token is an unknown (or value-less) flag.
                Usage(context);
                return 2;
            }
            else
            {
                if (model is not null)
                {
                    Usage(context);
                    return 2;
                }

                model = arg;
            }
        }

        if (model is null)
        {
            Usage(context);
            return 2;
        }

        var gate = await DaemonGate.EnsureRunningAsync(context, ct);
        if (gate.HasValue)
        {
            return gate.Value;
        }

        RouterModelPreloadResponse response;
        try
        {
            response = preview
                ? await context.Client.PreviewPreloadAsync(model, keepAliveSeconds, contextTokens, target, ct)
                : await context.Client.PreloadModelAsync(model,
                    new RouterModelPreloadRequest(keepAliveSeconds, contextTokens, target), ct);
        }
        catch (OllamaRouterApiException ex)
        {
            context.Error.WriteLine($"error: {ex.Error ?? ex.Message}");
            return 1;
        }

        if (json)
        {
            context.Output.WriteLine(JsonSerializer.Serialize(
                response, OllamaRouterClientJsonContext.Default.RouterModelPreloadResponse));
            return 0;
        }

        var writer = context.Output;
        writer.WriteLine($"preload: {model} → {response.SelectedTarget ?? "no eligible target"}");
        writer.WriteLine($"keep_alive: {DaemonGate.FormatKeepAlive(response.AppliedKeepAliveSeconds)}");
        if (response.AppliedNumCtx is { } numCtx)
        {
            writer.WriteLine("num_ctx: " + numCtx.ToString(CultureInfo.InvariantCulture) +
                (response.ContextIntentTokens is { } intent
                    ? " (intent: " + intent.ToString(CultureInfo.InvariantCulture) + ")"
                    : ""));
        }

        if (response.LoadAccepted is { } loadAccepted)
        {
            writer.WriteLine("load accepted: " + (loadAccepted ? "yes" : "no"));
        }

        if (response.ResidencyConfirmed is { } confirmed)
        {
            writer.WriteLine("residency confirmed: " + (confirmed ? "yes" : "no"));
        }

        if (response.PreloadDurationMs is { } durationMs)
        {
            writer.WriteLine("duration: " + durationMs.ToString(CultureInfo.InvariantCulture) + " ms");
        }

        if (response.Reason is { } reason)
        {
            writer.WriteLine("reason: " + reason);
        }

        if (response.Candidates.Count > 0)
        {
            writer.WriteLine("candidates:");
            foreach (var candidate in response.Candidates)
            {
                writer.WriteLine(candidate.Eligible
                    ? $"  [x] {candidate.Target}"
                    : $"  [ ] {candidate.Target} — skipped: {candidate.SkipReason ?? "unknown reason"}");
            }
        }

        return 0;
    }

    private static void Usage(CliContext context) =>
        context.Error.WriteLine(
            "usage: ollamarouter preload <model> [--context <tokens>] [--keep-alive <seconds>] [--target <name>] [--preview] [--json|-j]");
}
