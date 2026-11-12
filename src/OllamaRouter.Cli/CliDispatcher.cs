using System.Reflection;
using OllamaRouter.Cli.Commands;

namespace OllamaRouter.Cli;

/// <summary>
/// Hand-rolled, AOT-safe CLI dispatcher (task #100): case-insensitive command matching with
/// aliases (<c>monitor</c> ≡ <c>top</c>), <c>-h</c>/<c>help</c> → usage (exit 0),
/// <c>-v</c>/<c>--version</c> → the informational version line, unknown verb → usage on
/// stderr (exit 2). No arguments: interactive → <c>monitor</c>; non-interactive → usage on
/// stderr (exit 2).
/// </summary>
internal static class CliDispatcher
{
    /// <summary>Dispatches the default console-interactive invocation (production path).</summary>
    public static Task<int> DispatchAsync(
        IReadOnlyList<string> args, CliContext context, CancellationToken ct) =>
        DispatchAsync(args, context, static () => !Console.IsInputRedirected && !Console.IsOutputRedirected, ct);

    /// <summary>
    /// Dispatches with an injectable interactivity probe (test seam: unit tests never touch
    /// the real console).
    /// </summary>
    public static async Task<int> DispatchAsync(
        IReadOnlyList<string> args, CliContext context, Func<bool> isInteractive, CancellationToken ct)
    {
        var commands = new IRouterCommand[]
        {
            new StartCommand(),
            new StopCommand(),
            new RestartCommand(),
            new StatusCommand(),
            new MonitorCommand(),
            new LogsCommand(),
            new SetupCommand(),
            new HelpCommand(),
        };

        // No arguments: the interactive default action is the live TUI.
        if (args.Count == 0)
        {
            if (isInteractive())
            {
                return await new MonitorCommand().ExecuteAsync(context, [], ct);
            }

            Commands.HelpCommand.UsageText.Write(context.Error);
            return 2;
        }

        var verb = args[0];
        var rest = args.Skip(1).ToList();

        if (verb is "-v" or "--version")
        {
            context.Output.WriteLine(VersionLine);
            return 0;
        }

        if (verb is "-h" or "help")
        {
            Commands.HelpCommand.UsageText.Write(context.Output);
            return 0;
        }

        var command = commands.FirstOrDefault(c =>
            string.Equals(c.Name, verb, StringComparison.OrdinalIgnoreCase) ||
            c.Aliases.Any(a => string.Equals(a, verb, StringComparison.OrdinalIgnoreCase)));
        if (command is null)
        {
            context.Error.WriteLine($"error: unknown command '{verb}'");
            context.Error.WriteLine();
            Commands.HelpCommand.UsageText.Write(context.Error);
            return 2;
        }

        return await command.ExecuteAsync(context, rest, ct);
    }

    /// <summary>
    /// The <c>--version</c> line: <c>ollamarouter &lt;AssemblyInformationalVersion&gt;</c>
    /// (attribute read — AOT-safe, the same version source as the server's identity route).
    /// </summary>
    public static string VersionLine => "ollamarouter " + (
        typeof(CliDispatcher).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
}
