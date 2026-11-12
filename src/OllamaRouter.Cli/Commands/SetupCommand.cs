using System.Diagnostics;
using OllamaRouter.Cli;
using OllamaRouter.Client.Protocol;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// A launched <c>ollamarouter-app</c> process abstraction (task #103) so the setup command's
/// launch and exit-wait logic is unit-testable without spawning a real process. The default
/// implementation wraps <see cref="Process"/>; tests inject a fake.
/// </summary>
internal interface IAppProcess
{
    /// <summary>The child exit code (valid only after <see cref="WaitUntilExitedAsync"/> completes).</summary>
    int ExitCode { get; }

    /// <summary>Waits until the process has exited (no timeout — the wizard is interactive).</summary>
    Task WaitUntilExitedAsync(CancellationToken ct);

    /// <summary>Releases the process handle (does NOT terminate a detached child).</summary>
    void Dispose();
}

/// <summary>
/// The <c>setup</c> command (task #103): <c>ollamarouter setup [--wizard] [--wait]</c>.
/// <para>
/// Per maintainer decision D1, <c>setup</c> ALWAYS launches <c>ollamarouter-app --wizard</c>;
/// <c>--wizard</c> is an accepted no-op marker and <c>--wait</c> waits for exit and propagates the
/// child's exit code (0 = finished, 1 = abandoned). The app executable is probed in order:
/// <c>$OLLAMAROUTER_HOME</c>, <c>AppContext.BaseDirectory</c> (side-by-side install), then the
/// platform default install dir. App not found ⇒ exit 1; unknown flag ⇒ exit 2.
/// </para>
/// </summary>
internal sealed class SetupCommand : IRouterCommand
{
    private static readonly string[] AppExecutableNames =
    [
        "ollamarouter-app",
        "ollamarouter-app.exe",
    ];

    /// <inheritdoc />
    public string Name => "setup";

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases => [];

    /// <inheritdoc />
    public string Description => "Launch the app in Setup Wizard mode.";

    /// <summary>Process-launch seam (tests: fake <see cref="IAppProcess"/>).</summary>
    internal Func<ProcessStartInfo, IAppProcess>? ProcessStarter { get; set; }

    /// <summary>File-exists seam (tests: point the resolver at temp dirs without env mutation).</summary>
    internal Func<string, bool>? FileExistsProbe { get; set; }

    /// <summary>
    /// Protocol-registration seam (tests: recording stub — production path registers the user-scoped
    /// <c>ollamarouter://</c> handler via <see cref="ProtocolRegistrationService.Register(string?)"/>;
    /// best-effort, never blocks the wizard).
    /// </summary>
    internal Action<string>? ProtocolRegistrar { get; set; }

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        var wait = false;
        foreach (var arg in args)
        {
            if (arg is "--wizard")
            {
                // No-op marker (D1): the command always launches the app in wizard mode.
            }
            else if (arg is "--wait")
            {
                wait = true;
            }
            else
            {
                context.Error.WriteLine($"usage: ollamarouter setup [--wizard] [--wait]");
                return 2;
            }
        }

        var appPath = ResolveAppExecutable();
        if (appPath is null)
        {
            context.Error.WriteLine("error: could not find the ollamarouter-app executable.");
            context.Error.WriteLine("probed the following locations:");
            foreach (var dir in CandidateDirs())
            {
                context.Error.WriteLine($"  {Path.Combine(dir, "ollamarouter-app")}");
                context.Error.WriteLine($"  {Path.Combine(dir, "ollamarouter-app.exe")}");
            }
            context.Error.WriteLine(
                "install the desktop app, or set OLLAMAROUTER_HOME to the folder containing ollamarouter-app.");
            return 1;
        }

        // Best-effort: (re-)register the user-scoped ollamarouter:// protocol handler for the
        // resolved app executable (task #107) — a failure never blocks the wizard launch.
        var registrar = ProtocolRegistrar ?? (path => ProtocolRegistrationService.Register(path));
        try
        {
            registrar(appPath);
        }
        catch
        {
            // Best-effort: silent, non-blocking by contract.
        }

        var startInfo = new ProcessStartInfo(appPath, "--wizard")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var starter = ProcessStarter ?? (info =>
        {
            var process = Process.Start(info)
                ?? throw new InvalidOperationException("failed to start the setup wizard process.");
            return new ProcessAdapter(process);
        });

        IAppProcess process;
        try
        {
            process = starter(startInfo);
        }
        catch (Exception ex)
        {
            context.Error.WriteLine($"error: failed to launch the setup wizard: {ex.Message}");
            return 1;
        }

        if (!wait)
        {
            context.Output.WriteLine("Setup wizard launched (use --wait to propagate its exit code).");
            process.Dispose();
            return 0;
        }

        await process.WaitUntilExitedAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>Resolves the app executable across the candidate dirs (first existing file wins).</summary>
    private string? ResolveAppExecutable()
    {
        var exists = FileExistsProbe ?? (path => File.Exists(path));
        foreach (var dir in CandidateDirs())
        {
            foreach (var name in AppExecutableNames)
            {
                var path = Path.Combine(dir, name);
                if (exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>The candidate dirs in probe order: OLLAMAROUTER_HOME, base dir, platform default.</summary>
    private static IEnumerable<string> CandidateDirs()
    {
        var home = Environment.GetEnvironmentVariable("OLLAMAROUTER_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return home;
        }

        yield return AppContext.BaseDirectory;

        yield return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OllamaRouter")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollamarouter");
    }

    /// <summary>The default <see cref="IAppProcess"/> adapter over a real <see cref="Process"/>.</summary>
    private sealed class ProcessAdapter : IAppProcess
    {
        private readonly Process _process;

        public ProcessAdapter(Process process) => _process = process;

        public int ExitCode => _process.ExitCode;

        public Task WaitUntilExitedAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

        public void Dispose() => _process.Dispose();
    }
}
