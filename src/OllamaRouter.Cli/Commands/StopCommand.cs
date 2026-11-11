using OllamaRouter.Client.Process;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// <c>ollamarouter stop</c>: graceful daemon stop via
/// <see cref="IOllamaRouterDaemonLauncher.StopAsync"/> (no-op when stopped); prints the final
/// probe state. Exits 1 only when the launcher refuses/throws (e.g. a foreign port owner —
/// the launcher NEVER kills a process it does not own).
/// </summary>
public sealed class StopCommand : IRouterCommand
{
    public string Name => "stop";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Stop the Ollama Router daemon gracefully.";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count != 0)
        {
            context.Error.WriteLine("usage: ollamarouter stop");
            return 2;
        }

        try
        {
            var status = await context.Launcher.StopAsync(ct);
            DaemonStatusOutput.Print(context.Output, status, context.BaseUrl);
            return 0;
        }
        catch (RouterDaemonException ex)
        {
            context.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }
}
