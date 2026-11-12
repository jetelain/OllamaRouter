namespace OllamaRouter.Cli.Commands;

/// <summary>
/// <c>ollamarouter help</c> (and <c>-h</c>): prints the usage text — every command, its
/// aliases and the exit-code contract — and exits 0.
/// </summary>
public sealed class HelpCommand : IRouterCommand
{
    public string Name => "help";
    public IReadOnlyList<string> Aliases => ["-h"];
    public string Description => "Show this help.";

    public Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        UsageText.Write(context.Output);
        return Task.FromResult(0);
    }

    /// <summary>The full usage text (shared with the dispatcher's usage-error path).</summary>
    public static class UsageText
    {
        public static void Write(System.IO.TextWriter writer)
        {
            writer.WriteLine("ollamarouter — Ollama Router daemon CLI (task #100)");
            writer.WriteLine();
            writer.WriteLine("Usage:");
            writer.WriteLine("  ollamarouter <command> [options]");
            writer.WriteLine();
            writer.WriteLine("Commands:");
            writer.WriteLine("  start                  Start the Ollama Router daemon (idempotent).");
            writer.WriteLine("  stop                   Stop the Ollama Router daemon gracefully.");
            writer.WriteLine("  restart                Stop then start the Ollama Router daemon.");
            writer.WriteLine("  status [--json]        Show daemon status + per-target fleet summary.");
            writer.WriteLine("  monitor [--interval <s>]   Launch the live activity monitor TUI (alias: top).");
            writer.WriteLine("                           Auto-starts the daemon. q/Ctrl+C exits the TUI");
            writer.WriteLine("                           (the daemon keeps running); r = reclaim VRAM;");
            writer.WriteLine("                           1-9 = toggle the Nth fleet target.");
            writer.WriteLine("  logs [--follow|-f]     Tail the latest logs/ollamarouter-*.log file.");
            writer.WriteLine("       [--lines|-n N]           (default last 50 lines)");
            writer.WriteLine("  setup [--wizard] [--wait]  Launch the app in Setup Wizard mode");
            writer.WriteLine("                           (--wait propagates the wizard exit code).");
            writer.WriteLine("  help                   Show this help.");
            writer.WriteLine("  --version              Print the CLI version.");
            writer.WriteLine();
            writer.WriteLine("No arguments (interactive): launches 'monitor'.");
            writer.WriteLine();
            writer.WriteLine("Environment:");
            writer.WriteLine("  OLLAMAROUTER_URL       Daemon base URL override (default http://127.0.0.1:11434/).");
            writer.WriteLine();
            writer.WriteLine("Exit codes:");
            writer.WriteLine("  0  success (status of a stopped daemon is still success)");
            writer.WriteLine("  1  daemon or runtime failure (foreign port owner, unreachable API,");
            writer.WriteLine("     missing log file)");
            writer.WriteLine("  2  usage error");
        }
    }
}
