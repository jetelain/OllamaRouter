using OllamaRouter.Client.Process;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// <c>ollamarouter restart</c>: executes <c>StopAsync()</c> followed by <c>StartAsync()</c> in
/// that exact order and reports both phases. A stop-phase failure (e.g. foreign port owner)
/// aborts with exit 1 before the start is attempted.
/// </summary>
public sealed class RestartCommand : IRouterCommand
{
    public string Name => "restart";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Stop then start the Ollama Router daemon.";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count > 0)
        {
            context.Error.WriteLine("usage: ollamarouter restart");
            return 2;
        }

        try
        {
            var stopped = await context.Launcher.StopAsync(ct);
            context.Output.WriteLine("stop:   " + stopped.State);

            var started = await context.Launcher.StartAsync(ct);
            context.Output.WriteLine("start:");
            DaemonStatusOutput.Print(context.Output, started, context.BaseUrl);
            return 0;
        }
        catch (RouterDaemonException ex)
        {
            context.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }
}
