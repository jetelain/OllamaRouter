using OllamaRouter.Client.Process;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// <c>ollamarouter start</c>: idempotent daemon start via
/// <see cref="IOllamaRouterDaemonLauncher.StartAsync"/>; prints state, version, PID and bind
/// address. Already-running returns success with the live info (no second daemon).
/// </summary>
public sealed class StartCommand : IRouterCommand
{
    public string Name => "start";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Start the Ollama Router daemon (idempotent).";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count != 0)
        {
            UsageError(context);
            return 2;
        }

        try
        {
            var status = await context.Launcher.StartAsync(ct);
            DaemonStatusOutput.Print(context.Output, status, context.BaseUrl);
            return 0;
        }
        catch (RouterDaemonException ex)
        {
            context.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    internal static void UsageError(CliContext context) =>
        context.Error.WriteLine("usage: ollamarouter start");
}
