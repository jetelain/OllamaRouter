namespace OllamaRouter.Cli;

/// <summary>
/// <c>ollamarouter</c> entry point (task #100): builds the production <see cref="CliContext"/>
/// (base URL from <c>OLLAMAROUTER_URL</c> or the conventional default; real client + daemon
/// launcher sharing it) and hands off to the hand-rolled <see cref="CliDispatcher"/>.
/// <para>
/// The process-wide Ctrl+C behavior is deliberately the OS/CLI default (cancel → non-zero
/// exit) for every command EXCEPT the TUI, where <c>DetachedTerminalMonitorRunner</c> installs
/// its own clean-quit path (<c>Console.TreatControlCAsInput</c>).
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        CliContext context;
        try
        {
            context = CliContext.Create();
        }
        catch (UriFormatException ex)
        {
            Console.Error.WriteLine($"error: invalid {CliContext.UrlEnvironmentVariable} value: {ex.Message}");
            return 2;
        }

        return await CliDispatcher.DispatchAsync(args, context, CancellationToken.None);
    }
}
