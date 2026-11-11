namespace OllamaRouter.Cli.Commands;

/// <summary>
/// One <c>ollamarouter</c> subcommand (task #100). Commands are hand-rolled (AOT-safe: no CLI
/// parser library); the dispatcher in <c>Program.cs</c> matches <see cref="Name"/> +
/// <see cref="Aliases"/> case-insensitively and hands over the remaining arguments verbatim.
/// The returned exit code follows the CLI contract: 0 success, 1 daemon/runtime failure,
/// 2 usage error.
/// </summary>
public interface IRouterCommand
{
    /// <summary>The canonical command name (lowercase).</summary>
    string Name { get; }

    /// <summary>Case-insensitive aliases (e.g. <c>top</c> for <c>monitor</c>; empty when none).</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>One-line description for the usage text.</summary>
    string Description { get; }

    /// <summary>Executes the command; returns the process exit code.</summary>
    Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct);
}
