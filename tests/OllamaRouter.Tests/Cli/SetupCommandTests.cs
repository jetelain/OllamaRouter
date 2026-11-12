using System.Diagnostics;
using System.IO;
using Moq;
using OllamaRouter.Cli;
using OllamaRouter.Cli.Commands;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;

namespace OllamaRouter.Tests.Cli;

/// <summary>
/// Task #103: unit tests of <c>ollamarouter setup [--wizard] [--wait]</c> (the NightCodeForge
/// handoff). Uses the <see cref="SetupCommand.ProcessStarter"/> / <see cref="SetupCommand.FileExistsProbe"/>
/// seams — NO real processes are ever spawned. Per maintainer decision D1, <c>setup</c> ALWAYS
/// launches the app in wizard mode; <c>--wait</c> propagates the child exit code (Finish=0 /
/// abandoned=1). Serialized against the app tests (shared collection) because one case mutates
/// the process-global <c>OLLAMAROUTER_HOME</c> environment variable.
/// </summary>
[Collection("ollamarouter-env")]
public class SetupCommandTests
{
    private static readonly Uri BaseUrl = new("http://127.0.0.1:11434/");

    private sealed class FakeAppProcess : IAppProcess
    {
        public int ExitCode { get; init; }

        public bool Waited { get; private set; }

        public bool Disposed { get; private set; }

        public Task WaitUntilExitedAsync(CancellationToken ct)
        {
            Waited = true;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    private static CliContext Context(out StringWriter output, out StringWriter error)
    {
        output = new StringWriter();
        error = new StringWriter();
        return CliContext.Create(
            BaseUrl, new Mock<IOllamaRouterApiClient>().Object,
            new Mock<IOllamaRouterDaemonLauncher>().Object, output, error);
    }

    /// <summary>
    /// A <see cref="SetupCommand"/> whose resolver reports the app present at <paramref name="appPath"/>
    /// and whose starter captures the <see cref="ProcessStartInfo"/> and returns a scripted
    /// <see cref="FakeAppProcess"/>.
    /// </summary>
    private static (SetupCommand Command, List<ProcessStartInfo> StartInfos, FakeAppProcess Process) BuildCommand(
        string appPath, int exitCode)
    {
        var startInfos = new List<ProcessStartInfo>();
        var process = new FakeAppProcess { ExitCode = exitCode };
        var command = new SetupCommand
        {
            FileExistsProbe = path => string.Equals(path, appPath, StringComparison.Ordinal),
            ProcessStarter = info =>
            {
                startInfos.Add(info);
                return process;
            },
            // Never touch the real host registry / XDG desktop entry from unit tests (task #107).
            ProtocolRegistrar = _ => { },
        };
        return (command, startInfos, process);
    }

    // ------------------------------------------------------------------
    //  D1: setup ALWAYS launches the app in wizard mode
    // ------------------------------------------------------------------

    [Fact]
    public async Task Setup_NoFlags_LaunchesWizard_Returns0_NoWait()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "ollamarouter-app");
        var (command, startInfos, process) = BuildCommand(appPath, exitCode: 0);
        var context = Context(out var output, out _);

        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(process.Waited, "no --wait must not wait on the child");
        Assert.True(process.Disposed, "the detached launch releases the process handle");
        Assert.Single(startInfos);
        Assert.EndsWith("ollamarouter-app", startInfos[0].FileName, StringComparison.OrdinalIgnoreCase);
        Assert.False(startInfos[0].UseShellExecute);
        Assert.Contains("Setup wizard launched", output.ToString());
    }

    [Fact]
    public async Task Setup_WizardMarker_IsNoOpStillLaunchesWizard()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "ollamarouter-app");
        var (command, startInfos, _) = BuildCommand(appPath, exitCode: 0);
        var context = Context(out _, out _);

        var exit = await command.ExecuteAsync(context, ["--wizard"], CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("--wizard", startInfos[0].Arguments);
    }

    // ------------------------------------------------------------------
    //  --wait propagates the child exit code (D2 handoff)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Setup_Wait_PropagatesChildExitCode(int childExit)
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "ollamarouter-app");
        var (command, _, process) = BuildCommand(appPath, exitCode: childExit);
        var context = Context(out _, out _);

        var exit = await command.ExecuteAsync(context, ["--wizard", "--wait"], CancellationToken.None);

        Assert.Equal(childExit, exit);
        Assert.True(process.Waited, "--wait must wait for the child to exit");
    }

    [Fact]
    public async Task Setup_WaitAlone_StillWizardAndWait()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "ollamarouter-app");
        var (command, startInfos, process) = BuildCommand(appPath, exitCode: 1);
        var context = Context(out _, out _);

        var exit = await command.ExecuteAsync(context, ["--wait"], CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(process.Waited);
        Assert.Equal("--wizard", startInfos[0].Arguments);
    }

    // ------------------------------------------------------------------
    //  captured ProcessStartInfo contract
    // ------------------------------------------------------------------

    [Fact]
    public async Task Setup_CapturesAppPathAndWizardArgument_AndNoShell()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "ollamarouter-app");
        var (command, startInfos, _) = BuildCommand(appPath, exitCode: 0);
        var context = Context(out _, out _);

        _ = await command.ExecuteAsync(context, [], CancellationToken.None);

        var info = startInfos[0];
        Assert.Equal(appPath, info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal("--wizard", info.Arguments);
    }

    // ------------------------------------------------------------------
    //  app not found ⇒ exit 1 with probed locations
    // ------------------------------------------------------------------

    [Fact]
    public async Task Setup_AppNotFound_Exit1ListsProbedLocations()
    {
        var command = new SetupCommand
        {
            FileExistsProbe = _ => false, // nothing exists in any probed location
        };
        var context = Context(out _, out var error);

        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(1, exit);
        var text = error.ToString();
        Assert.Contains("could not find the ollamarouter-app executable", text);
        Assert.Contains("ollamarouter-app", text);
        Assert.Contains(AppContext.BaseDirectory, text);
    }

    // ------------------------------------------------------------------
    //  unknown flag ⇒ exit 2 + usage
    // ------------------------------------------------------------------

    [Fact]
    public async Task Setup_UnknownFlag_Exit2WithUsage()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "ollamarouter-app");
        var (command, startInfos, _) = BuildCommand(appPath, exitCode: 0);
        var context = Context(out _, out var error);

        var exit = await command.ExecuteAsync(context, ["--bogus"], CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.True(startInfos.Count == 0, "an unknown flag must NOT launch the app");
        Assert.Contains("usage: ollamarouter setup", error.ToString());
    }

    // ------------------------------------------------------------------
    //  resolver order: OLLAMAROUTER_HOME > base dir > platform default
    // ------------------------------------------------------------------

    [Fact]
    public async Task Setup_Resolver_BaseDirWinsWhenHomeIsEmpty()
    {
        // The probe reports the app ONLY in AppContext.BaseDirectory → the resolver must pick it
        // (OLLAMAROUTER_HOME is unset / has no app; the platform default is checked last).
        var appPath = Path.Combine(AppContext.BaseDirectory, "ollamarouter-app");
        var (command, startInfos, _) = BuildCommand(appPath, exitCode: 0);
        var context = Context(out _, out _);

        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(appPath, startInfos[0].FileName);
    }

    [Fact]
    public async Task Setup_Resolver_OllamaRouterHomeWinsOverBaseDir()
    {
        await using var home = new TempHome();
        var homeApp = Path.Combine(home.Path, "ollamarouter-app");
        var baseApp = Path.Combine(AppContext.BaseDirectory, "ollamarouter-app");

        var startInfos = new List<ProcessStartInfo>();
        var process = new FakeAppProcess { ExitCode = 0 };
        var command = new SetupCommand
        {
            // The app "exists" in BOTH the home dir and the base dir; home must win (probe order).
            FileExistsProbe = path =>
                string.Equals(path, homeApp, StringComparison.Ordinal) ||
                string.Equals(path, baseApp, StringComparison.Ordinal),
            ProcessStarter = info =>
            {
                startInfos.Add(info);
                return process;
            },
            // Never touch the real host registry / XDG desktop entry from unit tests (task #107).
            ProtocolRegistrar = _ => { },
        };
        var context = Context(out _, out _);

        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(homeApp, startInfos[0].FileName);
    }

    // ------------------------------------------------------------------
    //  task #107: setup (re-)registers the ollamarouter:// protocol handler
    // ------------------------------------------------------------------

    [Fact]
    public async Task Setup_RegistersProtocolHandlerWithResolvedAppPath_BeforeLaunch()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "ollamarouter-app");
        var registeredPaths = new List<string>();
        var process = new FakeAppProcess { ExitCode = 0 };
        var command = new SetupCommand
        {
            FileExistsProbe = path => string.Equals(path, appPath, StringComparison.Ordinal),
            ProcessStarter = _ => process,
            ProtocolRegistrar = path => registeredPaths.Add(path),
        };
        var context = Context(out _, out _);

        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal([appPath], registeredPaths); // registered exactly once, for the RESOLVED path
    }

    [Fact]
    public async Task Setup_RegistrarThrows_WizardStillLaunches()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "ollamarouter-app");
        var process = new FakeAppProcess { ExitCode = 0 };
        var command = new SetupCommand
        {
            FileExistsProbe = path => string.Equals(path, appPath, StringComparison.Ordinal),
            ProcessStarter = _ => process,
            ProtocolRegistrar = _ => throw new InvalidOperationException("registration failed"),
        };
        var context = Context(out _, out _);

        // Registration is best-effort: a throwing registrar must never block the wizard launch.
        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(process.Disposed);
    }

    /// <summary>A self-deleting temp dir installed as <c>OLLAMAROUTER_HOME</c> (restored on dispose).</summary>
    private sealed class TempHome : IAsyncDisposable
    {
        private readonly string? _previous;
        private readonly bool _wasSet;

        public TempHome()
        {
            Path = Directory.CreateTempSubdirectory("ollamarouter-setuphome-").FullName;
            _previous = Environment.GetEnvironmentVariable("OLLAMAROUTER_HOME");
            _wasSet = _previous is not null;
            Environment.SetEnvironmentVariable("OLLAMAROUTER_HOME", Path);
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable("OLLAMAROUTER_HOME", _wasSet ? _previous : null);
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }

            return ValueTask.CompletedTask;
        }
    }
}
