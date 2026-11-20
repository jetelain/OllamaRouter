using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Avalonia.Data.Converters;
using Moq;
using OllamaRouter.App;
using OllamaRouter.App.Bootstrap;
using OllamaRouter.App.Converters;
using OllamaRouter.App.Connection;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Recommendations;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Recommendations;
using Xunit;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Task #103 — Setup Wizard unit tests (spec §6). All side effects (processes, env vars,
/// ports, polling delays) are behind injectable seams; nothing here spawns a process or sleeps
/// (the poller delay is a manual gate / no-op task).
/// </summary>
// The single environment-variable test mutates OLLAMAROUTER_HOME, so the whole class joins the
// shared env-serialization collection (the same one SetupCommandTests uses).
[Collection("ollamarouter-env")]
public sealed class SetupWizardViewModelsTests
{
    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static Task GatedDelay(TimeSpan span, CancellationToken ct) => new Task(static () => { }, ct);

    /// <summary>
    /// A soft (NOT max-speed) inter-tick pause: yields a bounded number of times and returns
    /// early on cancellation. Keeps a non-terminal poller ticking at a controlled rate and lets
    /// the fire-and-forget <c>StopAsync</c> land quickly — no real sleep, no starved max-speed spin.
    /// </summary>
    private static async Task SoftDelay(TimeSpan _, CancellationToken ct)
    {
        for (var i = 0; i < 60 && !ct.IsCancellationRequested; i++)
        {
            await Task.Yield();
        }
    }

    /// <summary>
    /// Bounded cooperative settle wait: yields until the condition holds (up to
    /// <paramref name="maxBeats"/> <see cref="Task.Yield"/> beats) and then asserts with a
    /// self-describing failure (task #130 addendum) — a silent timeout previously produced
    /// misleading downstream errors (e.g. an <c>IndexOutOfRangeException</c> on an empty
    /// collection). The budget is generous (200 k beats) so a lagging fire-and-forget poller
    /// tick under parallel load still lands in time — no real sleep.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what, int maxBeats = 200_000)
    {
        for (var i = 0; i < maxBeats && !condition(); i++)
        {
            await Task.Yield();
        }

        Assert.True(condition(), $"condition not met after {maxBeats} cooperative yields: {what}");
    }

    private sealed class TempDir : IAsyncDisposable
    {
        public TempDir() => Path = Directory.CreateTempSubdirectory("ollamarouter-wiz-").FullName;
        public string Path { get; }
        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TempHome : IAsyncDisposable
    {
        private readonly string? _previous;
        private readonly bool _wasSet;
        public TempHome(string path)
        {
            Path = path;
            _previous = Environment.GetEnvironmentVariable("OLLAMAROUTER_HOME");
            _wasSet = _previous is not null;
            Environment.SetEnvironmentVariable("OLLAMAROUTER_HOME", path);
        }
        public string Path { get; }
        public ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable("OLLAMAROUTER_HOME", _wasSet ? _previous : null);
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
            return ValueTask.CompletedTask;
        }
    }

    internal static IAppConnectionService FakeConnection(FakeRouterApiClient? client = null, FakeDaemonLauncher? launcher = null)
    {
        var mock = new Mock<IAppConnectionService>();
        if (client is not null)
        {
            mock.SetupGet(c => c.GetClient).Returns(() => client);
            mock.SetupGet(c => c.Client).Returns(() => client);
        }
        mock.SetupGet(c => c.DaemonLauncher).Returns(launcher);
        return mock.Object;
    }

    private sealed class ConfigHarness
    {
        public ConfigHarness(bool isWindows, bool isMacOS)
        {
            Configurator = new LocalOllamaConfigurator
            {
                IsWindows = _ => isWindows,
                IsMacOS = _ => isMacOS,
                BinaryProbe = paths =>
                {
                    ProbedPaths.Add(paths);
                    return Detection;
                },
                EnvWriter = (n, v, t) => EnvWrites.Add((n, v, t)),
                ProcessNames = name =>
                {
                    QueriedNames.Add(name);
                    return name == "ollama" ? ["111"] : [];
                },
                ProcessKiller = (n, pid) =>
                {
                    Kills.Add((n, pid));
                    Events.Add($"kill:{n}:{pid}");
                    return true;
                },
                ProcessStarter = info =>
                {
                    Starts.Add(info);
                    Events.Add($"start:{info.FileName}");
                    return "handle-1";
                },
                PortProbe = port =>
                {
                    ProbedPorts.Add(port);
                    // Task #150: per-port overrides win; the plain bool stays the default so
                    // every pre-existing test behaves exactly as before.
                    return PortMap.TryGetValue(port, out var open) ? open : PortOpen;
                },
                Delay = _ => { Delays++; return Task.CompletedTask; },
                ScriptRunner = (info, _) =>
                {
                    ScriptStarts.Add(info);
                    Events.Add($"script:{info.FileName}");
                    if (ScriptException is not null)
                    {
                        return Task.FromException<ProcessRunResult>(ScriptException);
                    }

                    return Task.FromResult(NextScriptResult);
                },
                OllamaHostReader = () => OllamaHostValue,
                PortWaitBudget = TimeSpan.FromHours(1),
            };
        }

        public LocalOllamaConfigurator Configurator { get; }
        public OllamaDetection Detection { get; set; } = new(true, "/usr/local/bin/ollama", false);
        public bool PortOpen { get; set; }
        /// <summary>Per-port probe results (task #150); falls back to <see cref="PortOpen"/>.</summary>
        public Dictionary<int, bool> PortMap { get; } = [];
        public List<(string Name, string Value, EnvironmentVariableTarget Target)> EnvWrites { get; } = [];
        public List<(string Name, int Pid)> Kills { get; } = [];
        public List<string> QueriedNames { get; } = [];
        public List<ProcessStartInfo> Starts { get; } = [];
        public List<int> ProbedPorts { get; } = [];
        /// <summary>The candidate-path lists passed to the binary probe (task #150: asserts
        /// the per-OS search paths, e.g. the Linux fix in <c>BuildOllamaStartInfo</c>).</summary>
        public List<IReadOnlyList<string>> ProbedPaths { get; } = [];
        public int Delays { get; private set; }

        // -- Task #150: the official-script seams -------------------------------------------

        /// <summary>The launch infos passed to the (fake) script runner, in order.</summary>
        public List<ProcessStartInfo> ScriptStarts { get; } = [];

        /// <summary>The scripted script outcome (default: exit 0, empty tails).</summary>
        public ProcessRunResult NextScriptResult { get; set; } = new(0, string.Empty, string.Empty);

        /// <summary>Script a script-launch failure (e.g. a declined UAC prompt).</summary>
        public Exception? ScriptException { get; set; }

        /// <summary>The persisted OLLAMA_HOST value served by the (fake) reader.</summary>
        public string? OllamaHostValue { get; set; }

        /// <summary>
        /// The shared ordered side-effect log (task #150): <c>kill:{name}:{pid}</c> /
        /// <c>script:{fileName}</c> / <c>start:{fileName}</c> — the contractual
        /// stop → script → restart ordering is asserted on this list.
        /// </summary>
        public List<string> Events { get; } = [];
    }

    /// <summary>
    /// Task #147 — hermetic <see cref="LocalOllamaConfigurator"/> for the root fixtures: every
    /// OS seam is faked (no PATH scan, no process enumeration, no TCP probe) and every
    /// <c>BinaryProbe</c> call (i.e. every detection) is counted so "the initial entry runs
    /// exactly once" is assertable.
    /// </summary>
    private sealed class FakeEnvConfigurator
    {
        private int _probes;

        public FakeEnvConfigurator(OllamaDetection detection)
        {
            Detection = detection;
            Configurator = new LocalOllamaConfigurator
            {
                IsWindows = _ => true,
                IsMacOS = _ => false,
                BinaryProbe = _ =>
                {
                    Interlocked.Increment(ref _probes);
                    return Detection;
                },
                ProcessNames = _ => [],
                PortProbe = _ => false,
                Delay = _ => Task.CompletedTask,
                // Hermetic (task #150): never read the real persisted OLLAMA_HOST in tests.
                OllamaHostReader = () => null,
            };
        }

        public LocalOllamaConfigurator Configurator { get; }
        public OllamaDetection Detection { get; set; }

        /// <summary>The number of detection probes performed so far.</summary>
        public int Probes => Volatile.Read(ref _probes);
    }

    private sealed record RootFixture(
        SetupWizardViewModel Root,
        WizardEnvironmentStepViewModel Env,
        WizardTargetsStepViewModel Targets,
        WizardBenchmarkStepViewModel Bench,
        WizardRecommendationsStepViewModel Rec,
        List<int> ExitCodes,
        FakeRouterApiClient Fake,
        FakeEnvConfigurator EnvConfig,
        FakeDaemonLauncher? Launcher);

    private static RootFixture BuildRoot(
        FakeRouterApiClient fake,
        FakeDaemonLauncher? launcher = null,
        OllamaDetection? detection = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var exit = new List<int>();
        // Task #147 hygiene: the wizard now runs the Step 1 detection at construction, so the
        // env step must get a FAKE-SEAMS configurator here — never a real one (no PATH scans /
        // process enumeration / TCP probes in unit tests).
        var envConfig = new FakeEnvConfigurator(detection ?? new OllamaDetection(true, "/usr/local/bin/ollama", false));
        var connection = FakeConnection(fake, launcher);
        var env = new WizardEnvironmentStepViewModel(envConfig.Configurator, connection);
        WizardTargetsStepViewModel targets = null!;
        targets = new WizardTargetsStepViewModel(() => fake, ct => targets.RefreshAsync(ct), delay ?? GatedDelay);
        WizardBenchmarkStepViewModel bench = null!;
        bench = new WizardBenchmarkStepViewModel(() => fake, ct => bench.RefreshAsync(ct), delay ?? GatedDelay);
        SetupWizardViewModel root = null!;
        var rec = new WizardRecommendationsStepViewModel(() => fake, () => _ = root.GoToAsync(2));
        root = new SetupWizardViewModel(connection, env, targets, bench, rec, code => exit.Add(code));
        return new RootFixture(root, env, targets, bench, rec, exit, fake, envConfig, launcher);
    }

    // ==================================================================
    //  Root SetupWizardViewModel
    // ==================================================================

    [Fact]
    public async Task Root_Step0_BackAndFinishDisabled_NextEnabled()
    {
        var fx = BuildRoot(new FakeRouterApiClient());
        var root = fx.Root;
        Assert.Equal(0, root.SelectedStep);
        Assert.False(root.CanGoBack);
        Assert.False(root.CanFinish);
        Assert.True(root.CanGoNext);
        Assert.False(root.BackCommand.CanExecute(null));
        Assert.False(root.FinishCommand.CanExecute(null));
        Assert.True(root.NextCommand.CanExecute(null));
        await root.DisposeAsync();
    }

    [Fact]
    public async Task Root_Next_StepsTo3_FinishEnabled()
    {
        var fx = BuildRoot(new FakeRouterApiClient());
        var root = fx.Root;
        await root.NextCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(1, root.SelectedStep);
        await root.NextCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(2, root.SelectedStep);
        await root.NextCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(3, root.SelectedStep);
        Assert.False(root.NextCommand.CanExecute(null));
        Assert.True(root.CanGoNext == false);
        Assert.True(root.FinishCommand.CanExecute(null));
        Assert.True(root.BackCommand.CanExecute(null));
        await root.DisposeAsync();
    }

    [Fact]
    public async Task Root_Back_StepsDown()
    {
        var fx = BuildRoot(new FakeRouterApiClient());
        var root = fx.Root;
        await root.GoToAsync(3);
        await root.BackCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(2, root.SelectedStep);
        await root.BackCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(1, root.SelectedStep);
        await root.BackCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(0, root.SelectedStep);
        Assert.False(root.BackCommand.CanExecute(null));
        await root.DisposeAsync();
    }

    [Fact]
    public async Task Root_Finish_SetsFinishedAndSignalsExit0()
    {
        var fx = BuildRoot(new FakeRouterApiClient());
        var root = fx.Root;
        await root.GoToAsync(3);
        root.Finish();
        Assert.True(root.Finished);
        Assert.Equal([0], fx.ExitCodes);
        await root.DisposeAsync();
    }

    [Fact]
    public async Task Root_Abandon_SignalsExit1()
    {
        var fx = BuildRoot(new FakeRouterApiClient());
        var root = fx.Root;
        root.Abandon();
        Assert.False(root.Finished);
        Assert.Equal([1], fx.ExitCodes);
        await root.DisposeAsync();
    }

    [Fact]
    public async Task Root_Abandon_IsNoOpAfterFinish()
    {
        var fx = BuildRoot(new FakeRouterApiClient());
        var root = fx.Root;
        await root.GoToAsync(3);
        root.Finish();
        root.Abandon(); // must be a no-op now
        Assert.Equal([0], fx.ExitCodes);
        await root.DisposeAsync();
    }

    [Fact]
    public async Task Root_Next_EnteringTargets_RefreshesFleet()
    {
        var fake = new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) };
        var fx = BuildRoot(fake);
        await fx.Root.NextCommand.ExecuteAsync(CancellationToken.None); // 0 -> 1 (targets)
        Assert.Equal(1, fx.Root.SelectedStep);
        await WaitUntilAsync(() => fake.Calls.Contains("GetFleetAsync"), "the fleet poll tick to run");
        Assert.Contains("GetFleetAsync", fake.Calls);
        await fx.Root.DisposeAsync();
    }

    [Fact]
    public async Task Root_Dispose_IsIdempotentAndDoesNotThrow()
    {
        var fx = BuildRoot(new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) });
        var root = fx.Root;
        await root.GoToAsync(1); // start targets poller
        await root.GoToAsync(2); // start benchmark poller
        await root.DisposeAsync();
        await root.DisposeAsync(); // idempotent
    }

    /// <summary>
    /// Task #147 (AC1 + AC2): the initial Step 1 detection runs exactly ONCE at construction —
    /// the false "Ollama not found" on a fresh start was the step's default state, because
    /// <c>OnStepEnteredAsync</c> never fired for the initial step. After awaiting
    /// <see cref="SetupWizardViewModel.InitialStepEntry"/>, Step 1 shows the real (faked)
    /// detection and port state, and an explicit <c>GoToAsync(0)</c> is a guard no-op (no
    /// double entry).
    /// </summary>
    [Fact]
    public async Task Root_InitialStep_EntryRunsOnceAtConstruction_AndPopulatesStep1()
    {
        var fx = BuildRoot(
            new FakeRouterApiClient(),
            launcher: new FakeDaemonLauncher { Status = FakeDaemonLauncher.RunningStatus() },
            detection: new OllamaDetection(true, "/fake/ollama", true));
        var root = fx.Root;
        await root.InitialStepEntry;

        Assert.Equal(1, fx.EnvConfig.Probes);
        Assert.True(fx.Env.OllamaInstalled);
        Assert.True(fx.Env.OllamaRunning);
        Assert.Equal("/fake/ollama", fx.Env.OllamaBinaryPath);
        Assert.Equal(Port11434Status.RouterRunning, fx.Env.Port11434Status);

        await root.GoToAsync(0); // already on step 0: guard no-op, no double entry
        Assert.Equal(1, fx.EnvConfig.Probes);
        await root.DisposeAsync();
    }

    /// <summary>
    /// Task #147 (AC3): navigating away and back re-enters step 0 normally (one
    /// <c>OnStepLeft</c> + one <c>OnStepEntered</c>, no extra calls) — and the targets
    /// poller is actually stopped after leaving (no further fleet ticks).
    /// </summary>
    [Fact]
    public async Task Root_InitialStep_LeaveAndBackReentersStep0Once()
    {
        var fake = new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) };
        var fx = BuildRoot(fake, delay: SoftDelay); // soft ticks: the poller keep-tick is real
        var root = fx.Root;
        await root.InitialStepEntry;
        Assert.Equal(1, fx.EnvConfig.Probes);

        await root.GoToAsync(1); // leave step 0 — the targets poller starts ticking
        await WaitUntilAsync(() => fake.Calls.Contains("GetFleetAsync"), "the targets fleet tick to run");
        await root.GoToAsync(0); // back: OnStepLeft (stop poller) + OnStepEntered (+1 probe)
        Assert.Equal(2, fx.EnvConfig.Probes);

        // GoToAsync awaited OnStepLeftAsync (StopAsync) — the poller is fully stopped; let any
        // in-flight work settle, then assert NO further fleet ticks (two stable samples).
        for (var i = 0; i < 10_000; i++)
        {
            await Task.Yield();
        }

        var fleetCalls = fake.Calls.Count(c => c == "GetFleetAsync");
        for (var i = 0; i < 10_000; i++)
        {
            await Task.Yield();
        }

        Assert.Equal(fleetCalls, fake.Calls.Count(c => c == "GetFleetAsync"));
        await root.DisposeAsync();
    }

    /// <summary>
    /// Task #147 (AC1, remote mode): with no daemon launcher the port state stays Unknown with
    /// the explanatory label after the initial entry.
    /// </summary>
    [Fact]
    public async Task Root_InitialStep_NoLauncher_PortStaysUnknown()
    {
        var fx = BuildRoot(new FakeRouterApiClient()); // FakeConnection: DaemonLauncher = null
        await fx.Root.InitialStepEntry;
        Assert.Equal(Port11434Status.Unknown, fx.Env.Port11434Status);
        Assert.Contains("Router launcher unavailable", fx.Env.RouterStatusLabel);
        await fx.Root.DisposeAsync();
    }

    /// <summary>
    /// Task #147 (AC5): disposing while the initial entry is still in flight is safe — the
    /// in-flight bounded detection self-completes harmlessly on the disposed view models (step 0
    /// owns no poller/timer), and disposal stays idempotent.
    /// </summary>
    [Fact]
    public async Task Root_Dispose_BeforeInitialEntryCompletes_IsSafe()
    {
        var fx = BuildRoot(new FakeRouterApiClient());
        // Dispose WITHOUT awaiting the initial entry first.
        await fx.Root.DisposeAsync();
        await fx.Root.InitialStepEntry; // completes (detection failures are swallowed)
        await fx.Root.DisposeAsync(); // idempotent
    }

    // ==================================================================
    //  Step 1 — LocalOllamaConfigurator (all seams faked)
    // ==================================================================

    [Fact]
    public async Task Step1_Windows_Loopback_FullConfigure()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false) { PortOpen = true };
        var launcher = new FakeDaemonLauncher { AfterStart = FakeDaemonLauncher.RunningStatus() };
        var connection = FakeConnection(null, launcher);
        var result = await h.Configurator.ConfigureForRouterAsync(lanMesh: false, connection, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.OllamaStarted);
        Assert.True(result.RouterStarted);

        // Env writes (User target on Windows): 3 vars.
        Assert.Equal(3, h.EnvWrites.Count);
        Assert.Contains(("OLLAMA_HOST", "127.0.0.1:11435", EnvironmentVariableTarget.User), h.EnvWrites);
        Assert.Contains(("OLLAMA_FLASH_ATTENTION", "1", EnvironmentVariableTarget.User), h.EnvWrites);
        Assert.Contains(("OLLAMA_KV_CACHE_TYPE", "q8_0", EnvironmentVariableTarget.User), h.EnvWrites);

        // Only ollama / "ollama app" process names were probed and killed (pid 111 for "ollama").
        Assert.All(h.QueriedNames, n => Assert.True(n is "ollama" or "ollama app"));
        Assert.Contains(("ollama", 111), h.Kills);

        // The serve start: binary + "serve" + explicit env (no stale inheritance).
        var serve = Assert.Single(h.Starts, s => s.FileName == "/usr/local/bin/ollama");
        Assert.Contains("serve", serve.Arguments);
        Assert.Equal("127.0.0.1:11435", serve.EnvironmentVariables["OLLAMA_HOST"]);
        Assert.Equal("1", serve.EnvironmentVariables["OLLAMA_FLASH_ATTENTION"]);
        Assert.Equal("q8_0", serve.EnvironmentVariables["OLLAMA_KV_CACHE_TYPE"]);

        // Router started via the daemon launcher.
        Assert.Contains("StartAsync", launcher.Calls);
    }

    [Fact]
    public async Task Step1_Windows_LanMesh_Uses0000Host()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false) { PortOpen = true };
        var connection = FakeConnection(null, new FakeDaemonLauncher { AfterStart = FakeDaemonLauncher.RunningStatus() });
        var result = await h.Configurator.ConfigureForRouterAsync(lanMesh: true, connection, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains(("OLLAMA_HOST", "0.0.0.0:11435", EnvironmentVariableTarget.User), h.EnvWrites);
        var serve = Assert.Single(h.Starts, s => s.FileName == "/usr/local/bin/ollama");
        Assert.Equal("0.0.0.0:11435", serve.EnvironmentVariables["OLLAMA_HOST"]);
    }

    [Fact]
    public async Task Step1_MacOS_LaunchctlSetenvAndOpenApp()
    {
        var h = new ConfigHarness(isWindows: false, isMacOS: true)
        {
            PortOpen = true,
            Detection = new OllamaDetection(true, "/Applications/Ollama.app", false),
        };
        var launcher = new FakeDaemonLauncher { AfterStart = FakeDaemonLauncher.RunningStatus() };
        var connection = FakeConnection(null, launcher);
        var result = await h.Configurator.ConfigureForRouterAsync(lanMesh: false, connection, CancellationToken.None);

        Assert.True(result.Success);

        // Env via `launchctl setenv <name> <value>` (3 invocations, launchctl as FileName).
        var launchctl = h.Starts.Where(s => s.FileName == "launchctl").ToList();
        Assert.Equal(3, launchctl.Count);
        Assert.Contains(launchctl, s => s.Arguments == "setenv OLLAMA_HOST 127.0.0.1:11435");
        Assert.Contains(launchctl, s => s.Arguments == "setenv OLLAMA_FLASH_ATTENTION 1");
        Assert.Contains(launchctl, s => s.Arguments == "setenv OLLAMA_KV_CACHE_TYPE q8_0");

        // Ollama.app restart via `open -g --env OLLAMA_HOST=... -a <app>`.
        var open = Assert.Single(h.Starts, s => s.FileName == "open");
        Assert.Equal("-g --env OLLAMA_HOST=127.0.0.1:11435 -a /Applications/Ollama.app", open.Arguments);

        Assert.Contains("StartAsync", launcher.Calls);
    }

    [Fact]
    public async Task Step1_MacOS_ServeFallbackWhenNoAppBundle()
    {
        var h = new ConfigHarness(isWindows: false, isMacOS: true)
        {
            PortOpen = true,
            Detection = new OllamaDetection(true, "/usr/local/bin/ollama", false), // not an .app bundle
        };
        var connection = FakeConnection(null, new FakeDaemonLauncher { AfterStart = FakeDaemonLauncher.RunningStatus() });
        var result = await h.Configurator.ConfigureForRouterAsync(lanMesh: false, connection, CancellationToken.None);
        Assert.True(result.Success);
        var serve = Assert.Single(h.Starts, s => s.FileName == "/usr/local/bin/ollama");
        Assert.Contains("serve", serve.Arguments);
        Assert.Equal("127.0.0.1:11435", serve.EnvironmentVariables["OLLAMA_HOST"]);
    }

    [Fact]
    public async Task Step1_PortWaitTimeout_FailsAndRouterNotStarted()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false) { PortOpen = false };
        h.Configurator.PortWaitBudget = TimeSpan.Zero; // elapses immediately
        var launcher = new FakeDaemonLauncher { Status = FakeDaemonLauncher.RunningStatus() };
        var connection = FakeConnection(null, launcher);
        var result = await h.Configurator.ConfigureForRouterAsync(lanMesh: false, connection, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("did not open port 11435", result.Message);
        Assert.False(result.OllamaStarted); // the port never opened ⇒ Ollama is not confirmed up
        Assert.False(result.RouterStarted);
        Assert.DoesNotContain("StartAsync", launcher.Calls);
        Assert.Contains(h.Starts, s => s.FileName == "/usr/local/bin/ollama"); // the serve launch WAS attempted
    }

    [Fact]
    public async Task Step1_Linux_NoOneClick_AndCommandDisabled()
    {
        var h = new ConfigHarness(isWindows: false, isMacOS: false);
        Assert.False(h.Configurator.SupportsOneClick);

        var launcher = new FakeDaemonLauncher { AfterStart = FakeDaemonLauncher.RunningStatus() };
        var connection = FakeConnection(null, launcher);
        var result = await h.Configurator.ConfigureForRouterAsync(lanMesh: false, connection, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Windows and macOS only", result.Message);
        Assert.Empty(h.EnvWrites);
        Assert.Empty(h.Starts);

        // The Step 1 view-model: Start (the existing one-click path) is disabled on Linux, while
        // Install/Update stay one-click (task #150, M1) — the configurator-level "Windows and
        // macOS only" assertion above keeps the legacy primitive's contract.
        var vm = new WizardEnvironmentStepViewModel(h.Configurator, connection);
        await vm.RefreshAsync(); // detection: installed (harness default), not running
        Assert.True(vm.IsLinux);
        Assert.False(vm.ShowStartButton);
        Assert.False(vm.StartOllamaCommand.CanExecute(null));
        Assert.False(vm.ShowFixPortButton);
        Assert.False(vm.FixPortCommand.CanExecute(null));
        Assert.True(vm.ShowUpdateButton);
        Assert.True(vm.UpdateOllamaCommand.CanExecute(null));
        Assert.True(vm.ShowRunningCheck == false);
        // Flip to uninstalled: Install is one-click on Linux too (M1).
        h.Detection = new OllamaDetection(false, null, false);
        await vm.RefreshAsync();
        Assert.True(vm.ShowInstallButton);
        Assert.True(vm.InstallOllamaCommand.CanExecute(null));
    }

    [Fact]
    public async Task Step1_Refresh_RunningIsRouterRunning()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false);
        var vm = new WizardEnvironmentStepViewModel(h.Configurator, FakeConnection(null, new FakeDaemonLauncher { Status = FakeDaemonLauncher.RunningStatus() }));
        await vm.RefreshAsync();
        Assert.Equal(Port11434Status.RouterRunning, vm.Port11434Status);
        Assert.Contains("Router is running", vm.RouterStatusLabel);
    }

    [Fact]
    public async Task Step1_Refresh_PortOccupiedByRawOllama_IsForeignService()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false);
        var vm = new WizardEnvironmentStepViewModel(h.Configurator,
            FakeConnection(null, new FakeDaemonLauncher { Status = new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null, "occupied") }));
        await vm.RefreshAsync();
        Assert.Equal(Port11434Status.ForeignService, vm.Port11434Status);
        Assert.Contains("Raw Ollama occupies", vm.RouterStatusLabel);
    }

    [Fact]
    public async Task Step1_Refresh_Stopped_IsFree()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false);
        var vm = new WizardEnvironmentStepViewModel(h.Configurator,
            FakeConnection(null, new FakeDaemonLauncher { Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null) }));
        await vm.RefreshAsync();
        Assert.Equal(Port11434Status.Free, vm.Port11434Status);
        Assert.Contains("free", vm.RouterStatusLabel);
    }

    /// <summary>
    /// Regression (task #103 review): the DEFAULT port probe must report a CLOSED port as
    /// false. A failed TCP connect also COMPLETES the BeginConnect operation, so the old
    /// implementation (WaitOne-only) returned true for refused ports and the port-wait stage
    /// could never time out. Also verifies a genuinely listening port probes true.
    /// </summary>
    [Fact]
    public void Step1_DefaultPortProbe_RefusedPortIsFalse_ListeningPortIsTrue()
    {
        // Port 1 on loopback: nothing listens there — the connect is refused (and completes).
        Assert.False(LocalOllamaConfigurator.DefaultPortProbe(1));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(LocalOllamaConfigurator.DefaultPortProbe(port));
    }

    // ==================================================================
    //  Step 1 — install / update / port check (task #150)
    // ==================================================================

    [Fact]
    public async Task Step150_Install_Windows_PowershellInstallScript_UacElevated()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false);
        var result = await h.Configurator.InstallOllamaAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.OllamaStarted);
        Assert.Contains("installer finished", result.Message);
        var info = Assert.Single(h.ScriptStarts);
        Assert.Equal("powershell.exe", info.FileName);
        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -Command \"irm https://ollama.com/install.ps1 | iex\"", info.Arguments);
        Assert.True(info.UseShellExecute);
        Assert.Equal("runas", info.Verb);
        // An install runs ONLY the script: no env writes, no kills, no serve starts.
        Assert.Empty(h.EnvWrites);
        Assert.Empty(h.Kills);
        Assert.Empty(h.Starts);
        Assert.Equal(["script:powershell.exe"], h.Events);
    }

    [Theory]
    [InlineData(false, true)]   // macOS
    [InlineData(false, false)]  // Linux
    public async Task Step150_Install_MacOSAndLinux_ShCurlInstallSh_Redirected(bool isWindows, bool isMacOS)
    {
        var h = new ConfigHarness(isWindows, isMacOS);
        var result = await h.Configurator.InstallOllamaAsync(CancellationToken.None);
        Assert.True(result.Success);
        var info = Assert.Single(h.ScriptStarts);
        Assert.Equal("/bin/sh", info.FileName);
        Assert.Equal("-c \"curl -fsSL https://ollama.com/install.sh | sh\"", info.Arguments);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Empty(h.EnvWrites);
        Assert.Empty(h.Kills);
        Assert.Empty(h.Starts);
    }

    [Fact]
    public async Task Step150_Install_NonZeroExit_FailureNamesCodeAndTails()
    {
        var h = new ConfigHarness(isWindows: false, isMacOS: true)
        {
            NextScriptResult = new ProcessRunResult(1, "downloading ollama…", "curl: (6) Could not resolve host: ollama.com"),
        };
        var result = await h.Configurator.InstallOllamaAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("The Ollama installer exited with code 1", result.Message);
        Assert.Contains("downloading ollama…", result.Message);
        Assert.Contains("Could not resolve host", result.Message);
    }

    [Fact]
    public async Task Step150_Install_LaunchThrows_UacDeclined_ReadableFailureNoThrow()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            ScriptException = new System.ComponentModel.Win32Exception(1223, "The operation was canceled by the user."),
        };
        var result = await h.Configurator.InstallOllamaAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("The Ollama installer could not be started", result.Message);
        Assert.Contains("canceled by the user", result.Message);
    }

    [Fact]
    public async Task Step150_Update_StopScriptRestart_Ordering_AndExplicitEnvRestart()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            PortOpen = true, // the post-update restart port-wait succeeds immediately
            Detection = new OllamaDetection(true, "/usr/local/bin/ollama", true),
        };
        var result = await h.Configurator.UpdateOllamaAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.OllamaStarted);
        Assert.Equal($"Ollama updated and serving on :{LocalOllamaConfigurator.OllamaPort}.", result.Message);
        // Contractual ordering: kill → script → serve start.
        Assert.Equal(["kill:ollama:111", "script:powershell.exe", "start:/usr/local/bin/ollama"], h.Events);
        // The restart is the EXPLICIT-env serve form (no stale inheritance).
        var serve = Assert.Single(h.Starts, s => s.FileName == "/usr/local/bin/ollama");
        Assert.Contains("serve", serve.Arguments);
        Assert.Equal("127.0.0.1:11435", serve.EnvironmentVariables["OLLAMA_HOST"]);
        Assert.Equal("1", serve.EnvironmentVariables["OLLAMA_FLASH_ATTENTION"]);
        Assert.Equal("q8_0", serve.EnvironmentVariables["OLLAMA_KV_CACHE_TYPE"]);
    }

    [Fact]
    public async Task Step150_Update_ScriptFailure_LeftStopped_NoRestart()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            NextScriptResult = new ProcessRunResult(1, string.Empty, "boom"),
        };
        var result = await h.Configurator.UpdateOllamaAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(result.OllamaStarted);
        Assert.Contains("Ollama was stopped and the update failed", result.Message);
        Assert.Contains("exited with code 1", result.Message);
        Assert.Contains("Retry Update, or start Ollama again manually", result.Message);
        // NO serve start after a failed script — Ollama is honestly left stopped.
        Assert.Equal(["kill:ollama:111", "script:powershell.exe"], h.Events);
        Assert.Empty(h.Starts);
    }

    [Fact]
    public async Task Step150_Update_LaunchFailure_LeftStopped_Message()
    {
        var h = new ConfigHarness(isWindows: false, isMacOS: true)
        {
            ScriptException = new InvalidOperationException("spawn /bin/sh failed"),
        };
        var result = await h.Configurator.UpdateOllamaAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Ollama was stopped and the update failed", result.Message);
        Assert.Contains("could not be started", result.Message);
        Assert.Contains("spawn /bin/sh failed", result.Message);
        // The launch attempt was made (and failed); NO serve start follows a launch failure.
        Assert.Equal(["kill:ollama:111", "script:/bin/sh"], h.Events);
        Assert.DoesNotContain(h.Events, e => e.StartsWith("start:"));
        Assert.Empty(h.Starts);
    }

    [Fact]
    public async Task Step150_Update_BinaryNotFoundAfterScript_Message()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            Detection = new OllamaDetection(false, null, false), // the script removed/moved the binary
        };
        var result = await h.Configurator.UpdateOllamaAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("No local Ollama installation was found after the update script", result.Message);
        Assert.Empty(h.Starts);
    }

    [Fact]
    public async Task Step150_Update_Linux_RestartViaServe_ExplicitEnv_LinuxSearchPaths()
    {
        var h = new ConfigHarness(isWindows: false, isMacOS: false)
        {
            PortOpen = true,
            Detection = new OllamaDetection(true, "/usr/local/bin/ollama", true),
        };
        var result = await h.Configurator.UpdateOllamaAsync(CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(result.OllamaStarted);
        // The re-detection used the LINUX search paths (the task #150 path-selection fix).
        var paths = Assert.Single(h.ProbedPaths);
        Assert.Contains("/usr/local/bin/ollama", paths);
        Assert.Contains("/snap/bin/ollama", paths);
        // <binary> serve with EXPLICIT env — no persistent env write on Linux (M1).
        var serve = Assert.Single(h.Starts, s => s.FileName == "/usr/local/bin/ollama");
        Assert.Contains("serve", serve.Arguments);
        Assert.Equal("127.0.0.1:11435", serve.EnvironmentVariables["OLLAMA_HOST"]);
        Assert.Empty(h.EnvWrites);
        Assert.Equal(["kill:ollama:111", "script:/bin/sh", "start:/usr/local/bin/ollama"], h.Events);
    }

    [Fact]
    public async Task Step150_Update_PortWaitTimeout_BudgetMessage()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false) { PortOpen = false };
        h.Configurator.PortWaitBudget = TimeSpan.Zero; // elapses immediately
        var result = await h.Configurator.UpdateOllamaAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(result.OllamaStarted);
        Assert.Contains("did not open port 11435", result.Message);
    }

    [Fact]
    public async Task Step150_DetectPort_NotRunning_ZeroPortProbes()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false);
        h.Detection = new OllamaDetection(true, "/usr/local/bin/ollama", false);
        var check = await h.Configurator.DetectPortAsync(CancellationToken.None);
        Assert.False(check.OllamaRunning);
        Assert.Null(check.DetectedPort);
        Assert.Empty(h.ProbedPorts);
    }

    [Fact]
    public async Task Step150_DetectPort_ConfirmedEnvValueWins_SingleProbe()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            OllamaHostValue = "0.0.0.0:11434",
            PortMap = { [11434] = true, [11435] = false },
            Detection = new OllamaDetection(true, "/x", true),
        };
        var check = await h.Configurator.DetectPortAsync(CancellationToken.None);
        Assert.True(check.OllamaRunning);
        Assert.Equal(11434, check.DetectedPort);
        Assert.Equal([11434], h.ProbedPorts); // the confirmed-env probe is the only one
    }

    [Fact]
    public async Task Step150_DetectPort_EnvProbeClosed_FallsThroughToCandidates()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            OllamaHostValue = "127.0.0.1:11500",
            PortMap = { [11500] = false, [11435] = true },
            Detection = new OllamaDetection(true, "/x", true),
        };
        var check = await h.Configurator.DetectPortAsync(CancellationToken.None);
        Assert.Equal(11435, check.DetectedPort);
        Assert.Equal([11500, 11435], h.ProbedPorts);
    }

    [Fact]
    public async Task Step150_DetectPort_FirstOpenCandidateWins_FastPathAndDrift()
    {
        // Fast path: 11435 open → exactly one probe.
        var fast = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            PortMap = { [11435] = true },
            Detection = new OllamaDetection(true, "/x", true),
        };
        Assert.Equal(11435, (await fast.Configurator.DetectPortAsync(CancellationToken.None)).DetectedPort);
        Assert.Equal([11435], fast.ProbedPorts);

        // Drift to 11434 (the router port — the canonical conflict).
        var driftRouter = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            PortMap = { [11435] = false, [11434] = true },
            Detection = new OllamaDetection(true, "/x", true),
        };
        Assert.Equal(11434, (await driftRouter.Configurator.DetectPortAsync(CancellationToken.None)).DetectedPort);
        Assert.Equal([11435, 11434], driftRouter.ProbedPorts);

        // Drift to 11436.
        var driftAlt = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            PortMap = { [11435] = false, [11434] = false, [11436] = true },
            Detection = new OllamaDetection(true, "/x", true),
        };
        Assert.Equal(11436, (await driftAlt.Configurator.DetectPortAsync(CancellationToken.None)).DetectedPort);
    }

    [Fact]
    public async Task Step150_DetectPort_RunningNothingOpen_NotDetected()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false);
        h.Detection = new OllamaDetection(true, "/x", true);
        var check = await h.Configurator.DetectPortAsync(CancellationToken.None);
        Assert.True(check.OllamaRunning);
        Assert.Null(check.DetectedPort);
        Assert.Equal(7, h.ProbedPorts.Count); // every candidate probed, none open
        Assert.Contains(11435, h.ProbedPorts);
    }

    [Theory]
    [InlineData("127.0.0.1:11435", 11435)]
    [InlineData("0.0.0.0:11435", 11435)]
    [InlineData(":11435", 11435)]
    [InlineData("11435", 11435)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("junk", null)]
    [InlineData("127.0.0.1:", null)]
    [InlineData("70000", null)]
    [InlineData("0", null)]
    public void Step150_TryParseOllamaHostPort(string? host, int? expected)
    {
        Assert.Equal(expected, LocalOllamaConfigurator.TryParseOllamaHostPort(host));
    }

    // ------------------------------------------------------------------
    //  Step 1 — view model: state machine, port states, commands (task #150)
    // ------------------------------------------------------------------

    private static (WizardEnvironmentStepViewModel Vm, ConfigHarness H, FakeDaemonLauncher? Launcher) BuildEnvStep(
        bool isWindows = true, bool isMacOS = false, FakeDaemonLauncher? launcher = null)
    {
        var h = new ConfigHarness(isWindows, isMacOS);
        var vm = new WizardEnvironmentStepViewModel(h.Configurator, FakeConnection(null, launcher));
        return (vm, h, launcher);
    }

    [Fact]
    public async Task Step150_State_NotInstalled_InstallOnly()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(false, null, false);
        await vm.RefreshAsync();

        Assert.False(vm.OllamaInstalled);
        Assert.True(vm.ShowInstallButton);
        Assert.False(vm.ShowStartButton);
        Assert.False(vm.ShowUpdateButton);
        Assert.False(vm.ShowRunningCheck);
        Assert.True(vm.InstallOllamaCommand.CanExecute(null));
        Assert.False(vm.StartOllamaCommand.CanExecute(null));
        Assert.False(vm.UpdateOllamaCommand.CanExecute(null));
        Assert.True(vm.RecheckCommand.CanExecute(null));
        Assert.Equal(OllamaPortStatus.NotRunning, vm.OllamaPortStatus);
    }

    [Fact]
    public async Task Step150_State_InstalledStopped_StartAndUpdate()
    {
        var (vm, h, _) = BuildEnvStep(); // harness default: installed, not running
        await vm.RefreshAsync();

        Assert.True(vm.OllamaInstalled);
        Assert.False(vm.OllamaRunning);
        Assert.False(vm.ShowInstallButton);
        Assert.True(vm.ShowStartButton);
        Assert.True(vm.ShowUpdateButton);
        Assert.False(vm.ShowRunningCheck);
        Assert.False(vm.InstallOllamaCommand.CanExecute(null));
        Assert.True(vm.StartOllamaCommand.CanExecute(null));
        Assert.True(vm.UpdateOllamaCommand.CanExecute(null));
    }

    [Fact]
    public async Task Step150_State_InstalledRunning_CheckAndUpdate_NoStart()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, "/usr/local/bin/ollama", true);
        h.PortMap[11435] = true;
        await vm.RefreshAsync();

        Assert.True(vm.OllamaRunning);
        Assert.True(vm.ShowRunningCheck);
        Assert.False(vm.ShowInstallButton);
        Assert.False(vm.ShowStartButton);
        Assert.True(vm.ShowUpdateButton);
        Assert.False(vm.StartOllamaCommand.CanExecute(null));
        Assert.True(vm.UpdateOllamaCommand.CanExecute(null));
        Assert.Equal(OllamaPortStatus.Ok, vm.OllamaPortStatus);
    }

    [Fact]
    public async Task Step150_State_Linux_InstallAndUpdateOneClick_StartAndFixPortDisabled()
    {
        var (vm, h, _) = BuildEnvStep(isWindows: false, isMacOS: false);
        Assert.True(vm.IsLinux);
        h.Detection = new OllamaDetection(false, null, false);
        await vm.RefreshAsync();
        Assert.True(vm.ShowInstallButton);
        Assert.True(vm.InstallOllamaCommand.CanExecute(null));

        h.Detection = new OllamaDetection(true, "/usr/local/bin/ollama", true);
        h.PortMap[11436] = true; // a drifted port
        await vm.RefreshAsync();
        Assert.True(vm.ShowUpdateButton);
        Assert.True(vm.UpdateOllamaCommand.CanExecute(null));
        Assert.False(vm.ShowStartButton);
        Assert.False(vm.StartOllamaCommand.CanExecute(null));
        Assert.Equal(OllamaPortStatus.WrongPort, vm.OllamaPortStatus);
        Assert.True(vm.ShowPortWrong);
        Assert.False(vm.ShowFixPortButton); // Linux: no persistent env path (M1)
        Assert.False(vm.FixPortCommand.CanExecute(null));
    }

    [Fact]
    public async Task Step150_Port_Ok_GreenLabel_ContainsPortAndCheck()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, "/x", true);
        h.PortMap[11435] = true;
        await vm.RefreshAsync();

        Assert.Equal(OllamaPortStatus.Ok, vm.OllamaPortStatus);
        Assert.True(vm.ShowPortOk);
        Assert.Equal(11435, vm.OllamaDetectedPort);
        Assert.Equal("Ollama is serving on :11435 ✓", vm.OllamaPortLabel);
        Assert.False(vm.ShowFixPortButton);
        Assert.False(vm.FixPortCommand.CanExecute(null));
    }

    [Fact]
    public async Task Step150_Port_WrongPort_WarningNamesPort_FixPortEnabledOnWindows()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, "/x", true);
        h.PortMap[11434] = true;
        await vm.RefreshAsync();

        Assert.Equal(OllamaPortStatus.WrongPort, vm.OllamaPortStatus);
        Assert.True(vm.ShowPortWrong);
        Assert.Equal(11434, vm.OllamaDetectedPort);
        Assert.Equal("Ollama is serving on :11434 — the router expects :11435", vm.OllamaPortLabel);
        Assert.True(vm.ShowFixPortButton);
        Assert.True(vm.FixPortCommand.CanExecute(null));
    }

    [Fact]
    public async Task Step150_Port_NotRunning_AndNotDetected_Labels()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, "/x", false); // stopped
        await vm.RefreshAsync();
        Assert.Equal(OllamaPortStatus.NotRunning, vm.OllamaPortStatus);
        Assert.True(vm.ShowPortNotRunning);
        Assert.Equal("Ollama is not running — start it in the section above", vm.OllamaPortLabel);
        Assert.Empty(h.ProbedPorts); // stopped: zero port probes

        h.Detection = new OllamaDetection(true, "/x", true); // running, nothing open
        await vm.RefreshAsync();
        Assert.Equal(OllamaPortStatus.NotDetected, vm.OllamaPortStatus);
        Assert.True(vm.ShowPortNotDetected);
        Assert.Equal("Ollama is running but its port could not be detected — try Re-check", vm.OllamaPortLabel);
        Assert.Null(vm.OllamaDetectedPort);
    }

    [Fact]
    public async Task Step150_Install_BusyDuringScript_SuccessMessage_AndRedetectReflectsInstalled()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(false, null, false); // not installed → Install available
        await vm.RefreshAsync();
        Assert.True(vm.InstallOllamaCommand.CanExecute(null));
        var probesBefore = h.ProbedPaths.Count;

        h.Configurator.ScriptRunner = (info, _) =>
        {
            h.ScriptStarts.Add(info);
            Assert.True(vm.IsBusy); // busy DURING the script run
            Assert.Contains("several minutes", vm.Message!);
            h.Detection = new OllamaDetection(true, "/usr/local/bin/ollama", false); // the install landed
            return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
        };

        await vm.InstallOllamaCommand.ExecuteAsync(CancellationToken.None);

        Assert.Contains("installer finished", vm.Message!);
        Assert.False(vm.MessageIsError);
        Assert.True(vm.OllamaInstalled); // the re-detect reflected the new state automatically
        Assert.False(vm.ShowInstallButton);
        Assert.True(vm.ShowUpdateButton);
        Assert.True(vm.UpdateOllamaCommand.CanExecute(null));
        Assert.False(vm.IsBusy);
        Assert.Equal(probesBefore + 1, h.ProbedPaths.Count); // the re-detect is exactly ONE detection
    }

    [Fact]
    public async Task Step150_Install_Failure_RedMessage_RetryAvailable()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(false, null, false);
        await vm.RefreshAsync();
        h.NextScriptResult = new ProcessRunResult(1, string.Empty, "curl: (6) Could not resolve host: ollama.com");

        await vm.InstallOllamaCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(vm.MessageIsError);
        Assert.Contains("exited with code 1", vm.Message!);
        Assert.Contains("Could not resolve host", vm.Message);
        Assert.False(vm.OllamaInstalled);
        Assert.True(vm.ShowInstallButton); // retry stays available
        Assert.True(vm.InstallOllamaCommand.CanExecute(null));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Step150_Update_BusyAndMessage_OrderingAndRedetect()
    {
        var (vm, h, _) = BuildEnvStep(); // installed, not running
        await vm.RefreshAsync();
        h.PortOpen = true; // the restart port-wait succeeds immediately
        var probesBefore = h.ProbedPaths.Count;

        await vm.UpdateOllamaCommand.ExecuteAsync(CancellationToken.None);

        Assert.Equal($"Ollama updated and serving on :{LocalOllamaConfigurator.OllamaPort}.", vm.Message);
        Assert.False(vm.MessageIsError);
        Assert.Equal(["kill:ollama:111", "script:powershell.exe", "start:/usr/local/bin/ollama"], h.Events);
        Assert.Equal(probesBefore + 2, h.ProbedPaths.Count); // BuildOllamaStartInfo re-detect + the refresh
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Step150_FixPort_StartOllamaOnlyPath_LoopbackHost()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, "/usr/local/bin/ollama", true);
        h.PortMap[11434] = true; // Ollama running on the WRONG port
        await vm.RefreshAsync();
        Assert.True(vm.FixPortCommand.CanExecute(null));
        h.PortOpen = true; // the port-wait succeeds after the restart

        await vm.FixPortCommand.ExecuteAsync(CancellationToken.None);

        Assert.Contains("serving on 127.0.0.1:11435", vm.Message!);
        Assert.False(vm.MessageIsError);
        Assert.Contains(("OLLAMA_HOST", "127.0.0.1:11435", EnvironmentVariableTarget.User), h.EnvWrites);
        var serve = Assert.Single(h.Starts, s => s.FileName == "/usr/local/bin/ollama");
        Assert.Equal("127.0.0.1:11435", serve.EnvironmentVariables["OLLAMA_HOST"]);
        Assert.Equal(OllamaPortStatus.Ok, vm.OllamaPortStatus); // re-detect: fixed
    }

    [Fact]
    public async Task Step150_FixPort_LanMeshChecked_Uses0000Host()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, "/usr/local/bin/ollama", true);
        h.PortMap[11434] = true;
        await vm.RefreshAsync();
        vm.LanMeshEnabled = true;
        h.PortOpen = true;

        await vm.FixPortCommand.ExecuteAsync(CancellationToken.None);

        Assert.Contains(("OLLAMA_HOST", "0.0.0.0:11435", EnvironmentVariableTarget.User), h.EnvWrites);
        var serve = Assert.Single(h.Starts, s => s.FileName == "/usr/local/bin/ollama");
        Assert.Equal("0.0.0.0:11435", serve.EnvironmentVariables["OLLAMA_HOST"]);
    }

    [Fact]
    public async Task Step150_StartRouter_StartsDaemon_StatusFlipsToRouterRunning()
    {
        var launcher = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null),
            AfterStart = FakeDaemonLauncher.RunningStatus(),
        };
        var (vm, _, l) = BuildEnvStep(launcher: launcher);
        await vm.RefreshAsync();
        Assert.Equal(Port11434Status.Free, vm.Port11434Status);
        Assert.True(vm.ShowStartRouterButton);
        Assert.True(vm.StartRouterCommand.CanExecute(null));

        await vm.StartRouterCommand.ExecuteAsync(CancellationToken.None);

        Assert.Contains("StartAsync", launcher.Calls);
        Assert.Contains("Router is running on :11434", vm.Message!);
        Assert.False(vm.MessageIsError);
        Assert.Equal(Port11434Status.RouterRunning, vm.Port11434Status);
        Assert.False(vm.ShowStartRouterButton); // the button disappears once running
        Assert.False(vm.StartRouterCommand.CanExecute(null));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Step150_StartRouter_TypedLauncherFailure_RedMessage_NoThrow()
    {
        var launcher = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null),
            StartException = new RouterDaemonPortOccupiedException(
                new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null, "busy")),
        };
        var (vm, _, _) = BuildEnvStep(launcher: launcher);
        await vm.RefreshAsync();
        Assert.True(vm.StartRouterCommand.CanExecute(null));

        await vm.StartRouterCommand.ExecuteAsync(CancellationToken.None); // must not throw

        Assert.True(vm.MessageIsError);
        Assert.Contains("Could not start the router", vm.Message!);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Step150_StartRouter_DisabledWhenRunningForeignOrNoLauncher()
    {
        // Router already running: no button.
        var running = new FakeDaemonLauncher { Status = FakeDaemonLauncher.RunningStatus() };
        var (vmRunning, _, _) = BuildEnvStep(launcher: running);
        await vmRunning.RefreshAsync();
        Assert.Equal(Port11434Status.RouterRunning, vmRunning.Port11434Status);
        Assert.False(vmRunning.ShowStartRouterButton);
        Assert.False(vmRunning.StartRouterCommand.CanExecute(null));

        // Foreign service on :11434: the warning label, no button.
        var foreign = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null, "occupied"),
        };
        var (vmForeign, _, _) = BuildEnvStep(launcher: foreign);
        await vmForeign.RefreshAsync();
        Assert.Equal(Port11434Status.ForeignService, vmForeign.Port11434Status);
        Assert.Contains("Raw Ollama occupies", vmForeign.RouterStatusLabel);
        Assert.False(vmForeign.ShowStartRouterButton);

        // No launcher (remote mode): the existing "unavailable" label, no button.
        var (vmRemote, _, _) = BuildEnvStep(launcher: null);
        await vmRemote.RefreshAsync();
        Assert.Equal(Port11434Status.Unknown, vmRemote.Port11434Status);
        Assert.Contains("Router launcher unavailable", vmRemote.RouterStatusLabel);
        Assert.False(vmRemote.ShowStartRouterButton);
    }

    [Fact]
    public async Task Step150_Busy_DisablesEveryStep1Action()
    {
        var launcher = new FakeDaemonLauncher { Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null) };
        var (vm, h, _) = BuildEnvStep(launcher: launcher);
        h.Detection = new OllamaDetection(false, null, false); // Install available + router Free
        await vm.RefreshAsync();
        Assert.True(vm.InstallOllamaCommand.CanExecute(null));
        Assert.True(vm.StartRouterCommand.CanExecute(null));
        Assert.True(vm.RecheckCommand.CanExecute(null));

        var gate = new TaskCompletionSource<ProcessRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Configurator.ScriptRunner = (info, _) =>
        {
            h.ScriptStarts.Add(info);
            return gate.Task; // hold the install open until the test releases it
        };

        var running = vm.InstallOllamaCommand.ExecuteAsync(CancellationToken.None);
        await Task.Yield(); // let the command reach the script gate

        Assert.True(vm.IsBusy);
        Assert.False(vm.InstallOllamaCommand.CanExecute(null));
        Assert.False(vm.StartOllamaCommand.CanExecute(null));
        Assert.False(vm.UpdateOllamaCommand.CanExecute(null));
        Assert.False(vm.FixPortCommand.CanExecute(null));
        Assert.False(vm.StartRouterCommand.CanExecute(null));
        Assert.False(vm.RecheckCommand.CanExecute(null)); // no concurrent actions while busy

        gate.SetResult(new ProcessRunResult(0, string.Empty, string.Empty));
        await running;

        Assert.False(vm.IsBusy);
        Assert.True(vm.InstallOllamaCommand.CanExecute(null));
        Assert.True(vm.RecheckCommand.CanExecute(null));
    }

    // ==================================================================
    //  Step 2 — WizardTargetsStepViewModel
    // ==================================================================

    private static WizardTargetsStepViewModel BuildTargets(FakeRouterApiClient fake)
    {
        WizardTargetsStepViewModel vm = null!;
        vm = new WizardTargetsStepViewModel(() => fake, ct => vm.RefreshAsync(ct), GatedDelay);
        return vm;
    }

    private static RouterFleetResponse OneTargetFleet() => new([
        new RouterTargetItem(
            "Local", "http://127.0.0.1:11435", true, "Online", 12, "local",
            new RouterTargetHardware("nvidia", ["GPU 0"], 16000, 4, 1.0),
            new RouterTargetExecution(2, "llama3:8b", "4k")),
    ]);

    [Fact]
    public async Task Step2_AddLocal_CapturesLocalTargetRequest()
    {
        var fake = new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) };
        var vm = BuildTargets(fake);
        await vm.AddLocalCommand.ExecuteAsync(CancellationToken.None);
        var req = Assert.Single(fake.AddTargetCalls);
        Assert.Equal("Local", req.Name);
        Assert.Equal("http://127.0.0.1:11435", req.Url);
        Assert.True(req.TestConnectionFirst);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step2_AddLocal_ConflictSurfacesAlreadyExists()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([]),
            AddTargetError = (HttpStatusCode.Conflict, "target already exists: Local"),
        };
        var vm = BuildTargets(fake);
        await vm.AddLocalCommand.ExecuteAsync(CancellationToken.None);
        Assert.Contains("already exists", vm.Message);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step2_AddLocal_UnreachableShowsHint()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([]),
            AddTargetError = (HttpStatusCode.UnprocessableEntity, "could not connect"),
        };
        var vm = BuildTargets(fake);
        await vm.AddLocalCommand.ExecuteAsync(CancellationToken.None);
        Assert.Contains("unreachable", vm.Message);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step2_AddRemote_CapturesCustomNameAndUrl()
    {
        var fake = new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) };
        var vm = BuildTargets(fake);
        vm.RemoteName = "gpu-box";
        vm.RemoteUrl = "http://192.168.1.50:11435";
        await vm.AddRemoteCommand.ExecuteAsync(CancellationToken.None);
        var req = Assert.Single(fake.AddTargetCalls);
        Assert.Equal("gpu-box", req.Name);
        Assert.Equal("http://192.168.1.50:11435", req.Url);
        Assert.True(req.TestConnectionFirst);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step2_ToggleCloud_SendsEnabledTrueAndReflectsResponse()
    {
        var fake = new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) };
        var vm = BuildTargets(fake);
        vm.CloudEnabled = false;
        await vm.ToggleCloudCommand.ExecuteAsync(CancellationToken.None);
        var req = Assert.Single(fake.UpdateConfigCalls);
        Assert.True(req.Cloud!.Enabled);
        Assert.True(vm.CloudEnabled); // reflected from the echo
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step2_OpenRouterPreset_AddsOpenAiTarget()
    {
        var fake = new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) };
        var vm = BuildTargets(fake);
        vm.UseOpenRouterCommand.Execute(null);
        vm.RemoteApiKey = "sk-123";
        await vm.AddRemoteCommand.ExecuteAsync(CancellationToken.None);
        var req = Assert.Single(fake.AddTargetCalls);
        Assert.Equal("OpenRouter", req.Name);
        Assert.Equal("openai", req.Type);
        Assert.Equal("OpenRouter", req.Provider);
        Assert.Equal("sk-123", req.ApiKey);
        Assert.Equal("https://openrouter.ai/api", req.Url);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step2_Poller_RefreshesLivenessFromFleet()
    {
        var fake = new FakeRouterApiClient { FleetResponse = OneTargetFleet() };
        var vm = BuildTargets(fake);
        await vm.OnStepEnteredAsync(); // immediate tick
        await WaitUntilAsync(() => vm.Rows.Count == 1, "the poller tick to populate the target rows");
        var row = Assert.Single(vm.Rows);
        Assert.Equal("Local", row.Name);
        Assert.Equal("Online", row.Liveness);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Regression (task #103 review): with the daemon DOWN (the canonical first-run state
    /// before Step 1 has started the router), a failed refresh must surface a message and NOT
    /// throw. The fleet read used to be unguarded: the exception escaped the <c>AddLocal</c>
    /// command (via its post-action refresh), faulted the async-void command bridge
    /// (<c>AsyncRelayCommand</c> rethrows on the dispatcher) and crashed the wizard.
    /// </summary>
    [Fact]
    public async Task Step2_DaemonDown_RefreshAndAddLocal_SurfaceMessage_DoesNotThrow()
    {
        var mock = new Mock<IOllamaRouterApiClient>();
        mock.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("connection refused"));
        mock.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("connection refused"));
        mock.Setup(c => c.AddTargetAsync(It.IsAny<AddTargetRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("connection refused"));
        WizardTargetsStepViewModel vm = null!;
        vm = new WizardTargetsStepViewModel(() => mock.Object, ct => Task.CompletedTask, GatedDelay);

        await vm.RefreshAsync(); // must not throw
        Assert.Contains("Connection failed", vm.Message);

        // The original crash scenario: the full AddLocal command must complete, not fault.
        await vm.AddLocalCommand.ExecuteAsync(CancellationToken.None); // must not throw
        Assert.Contains("Connection failed", vm.Message);
        await vm.DisposeAsync();
    }

    /// <summary>Step 2 refresh with a router API error surfaces the error text (loop survives).</summary>
    [Fact]
    public async Task Step2_Refresh_ApiError_SurfacesMessage_DoesNotThrow()
    {
        var mock = new Mock<IOllamaRouterApiClient>();
        mock.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.InternalServerError, "boom"));
        mock.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.InternalServerError, "boom"));
        WizardTargetsStepViewModel vm = null!;
        vm = new WizardTargetsStepViewModel(() => mock.Object, ct => Task.CompletedTask, GatedDelay);

        await vm.RefreshAsync(); // must not throw
        Assert.Contains("500", vm.Message);
        Assert.Contains("boom", vm.Message);
        await vm.DisposeAsync();
    }

    // ==================================================================
    //  Step 3 — WizardBenchmarkStepViewModel
    // ==================================================================

    private static Mock<IOllamaRouterApiClient> MockClient()
    {
        var mock = new Mock<IOllamaRouterApiClient>();
        mock.Setup(c => c.GetFleetHardwareAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterFleetHardwareResponse([]));
        mock.Setup(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterBenchmarkResultsResponse([]));
        return mock;
    }

    private static WizardBenchmarkStepViewModel BuildBenchmark(
        Mock<IOllamaRouterApiClient> mock,
        Func<CancellationToken, Task> tick,
        Func<TimeSpan, CancellationToken, Task> delay) =>
        new(() => mock.Object, tick, delay);

    private static BenchmarkContextResult Ctx(string model, UsageRole role, int ctx, double tps, bool viable, string? reason = null) =>
        new(model, role, ctx, 100, 1_000_000_000, tps, 500.0, viable, reason);

    [Fact]
    public async Task Step3_Run_CapturesStartBenchmarkForAllTargetsAndRecordsQueue()
    {
        var mock = MockClient();
        mock.Setup(c => c.StartBenchmarkAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterBenchmarkRunResponse(true, ["Local", "Remote"]));
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);
        mock.Verify(c => c.StartBenchmarkAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["Local", "Remote"], vm.QueuedTargets);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_MixedResults_UpdatesRowsAndViability_AndKeepsPolling()
    {
        var mock = MockClient();
        var now = DateTimeOffset.UtcNow;
        mock.Setup(c => c.StartBenchmarkAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterBenchmarkRunResponse(true, ["Local", "Remote"]));
        mock.Setup(c => c.GetFleetHardwareAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new RouterFleetHardwareResponse([
            new FleetHardwareNode("Local", "detected", new NodeHardwareProfile(
                "local", "linux", "x64", false, 32_000_000_000, SystemRamType.Ddr5,
                [new GpuDeviceProfile("nvidia", "RTX 4090", 24576, "GDDR6X", 384, 1008)], 24576, 1008), true),
            new FleetHardwareNode("Remote", "unknown", null, false),
        ]));
        mock.Setup(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new RouterBenchmarkResultsResponse([
            new BenchmarkTargetResult("Local", BenchmarkRunStatus.Running, now, null, [
                Ctx("llama3:8b", UsageRole.BasicModel, 8192, 25.0, true),
                Ctx("llama3:8b", UsageRole.BasicModel, 32768, 15.0, false, "below-threshold"),
            ]),
            new BenchmarkTargetResult("Remote", BenchmarkRunStatus.Succeeded, now, now, [
                Ctx("qwen2.5:7b", UsageRole.AgenticChat, 4096, 30.0, true),
            ]),
        ]));

        int ticks = 0;
        WizardBenchmarkStepViewModel vm = null!;
        // The counter increments AFTER the tick's RefreshAsync completes, so a green "ticks >= N"
        // means the collection mutations are done (no read-during-mutation race).
        vm = BuildBenchmark(mock, async ct =>
        {
            await vm.RefreshAsync(ct);
            Interlocked.Increment(ref ticks);
        }, SoftDelay);

        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);
        // Local is still Running (non-terminal) => the poller keeps ticking at the soft rate.
        await WaitUntilAsync(() => Volatile.Read(ref ticks) >= 2, "the poller to reach its second tick");
        var s1 = Volatile.Read(ref ticks);
        await WaitUntilAsync(() => Volatile.Read(ref ticks) > s1, "a non-terminal fleet to keep polling", maxBeats: 20000);
        Assert.True(Volatile.Read(ref ticks) > s1, "a non-terminal fleet keeps polling");

        // Stop the poller (awaited => the loop fully exits, no in-flight tick) so the observable
        // collections are stable, then assert their contents.
        await vm.OnStepLeftAsync();
        Assert.Contains(vm.HardwareRows, r => r.GpuName == "RTX 4090");
        Assert.Contains(vm.HardwareRows, r => r.Note is not null && r.Note.Contains("No hardware profile"));
        Assert.Contains(vm.ResultRows, r => r.DecodeTokensPerSecond >= 20 && r.Viable);
        Assert.Contains(vm.ResultRows, r => r.DecodeTokensPerSecond < 20 && !r.Viable);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_AllTerminal_SelfStopsThePoller()
    {
        var mock = MockClient();
        var now = DateTimeOffset.UtcNow;
        mock.Setup(c => c.StartBenchmarkAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterBenchmarkRunResponse(true, ["Local"]));
        mock.Setup(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new RouterBenchmarkResultsResponse([
            new BenchmarkTargetResult("Local", BenchmarkRunStatus.Succeeded, now, now, [
                Ctx("llama3:8b", UsageRole.BasicModel, 8192, 25.0, true),
            ]),
        ]));

        int ticks = 0;
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, async ct =>
        {
            await vm.RefreshAsync(ct);
            Interlocked.Increment(ref ticks);
        }, SoftDelay);

        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref ticks) >= 1, "the first poller tick");
        // Settle: let the fire-and-forget StopAsync land and the loop observe the cancellation.
        for (var i = 0; i < 10000; i++)
        {
            await Task.Yield();
        }

        var settled1 = Volatile.Read(ref ticks);
        for (var i = 0; i < 10000; i++)
        {
            await Task.Yield();
        }

        var settled2 = Volatile.Read(ref ticks);
        await vm.DisposeAsync();
        Assert.True(settled1 <= 8, $"poller must self-stop quickly once all queued targets are terminal (ticks={settled1})");
        Assert.True(settled1 == settled2, $"a self-stopped poller must not resume ticking (settled1={settled1}, settled2={settled2})");
    }

    [Fact]
    public async Task Step3_ResultsPollError_SetsErrorAndLoopSurvives()
    {
        var mock = MockClient();
        mock.Setup(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.InternalServerError, "boom"));
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync(); // immediate tick hits the 500
        await WaitUntilAsync(() => vm.Error is not null, "the poll tick to surface the 500 error");
        Assert.Contains("500", vm.Error);
        Assert.Contains("boom", vm.Error);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_StepLeftStopsPoller_AndDisposeIsIdempotent()
    {
        var mock = MockClient();
        var vm = BuildBenchmark(mock, ct => Task.CompletedTask, GatedDelay);
        await vm.OnStepEnteredAsync();
        await vm.OnStepLeftAsync(); // stops the poller
        await vm.DisposeAsync();    // idempotent
        await vm.DisposeAsync();
    }

    // ==================================================================
    //  Step 4 — WizardRecommendationsStepViewModel
    // ==================================================================

    [Fact]
    public async Task Step4_GetEmptyMatrix_AllFiveRolesUnbenchmarked()
    {
        var fake = new FakeRouterApiClient(); // default = empty matrix
        var vm = new WizardRecommendationsStepViewModel(() => fake);
        await vm.RefreshAsync();
        Assert.Equal(5, vm.RoleRows.Count);
        Assert.Equal(["basic", "agentic_chat", "software_architect", "senior_developer", "junior_developer"],
            vm.RoleRows.Select(r => r.Role));
        Assert.All(vm.RoleRows, r => Assert.False(r.Benchmarked));
        Assert.Empty(vm.ModelRows);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step4_Generate_ConflictShowsHintAndGoToBenchmark()
    {
        var fake = new FakeRouterApiClient { GenerateError = (HttpStatusCode.Conflict, "no benchmark data") };
        bool goTo = false;
        var vm = new WizardRecommendationsStepViewModel(() => fake, () => goTo = true);
        await vm.GenerateCommand.ExecuteAsync(CancellationToken.None);
        Assert.Contains("run the Step 3 micro-benchmark", vm.Message);
        Assert.False(goTo);
        Assert.True(vm.GoToBenchmarkCommand.CanExecute(null));
        vm.GoToBenchmarkCommand.Execute(null);
        Assert.True(goTo);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step4_Generate_Ok_PopulatesMatrixAndCounts()
    {
        var fake = new FakeRouterApiClient();
        var vm = new WizardRecommendationsStepViewModel(() => fake);
        await vm.GenerateCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(1, vm.RolesAssigned);
        Assert.Equal(1, vm.NodesConfigured);
        Assert.Equal(1, vm.TotalContextCeilings);
        Assert.Equal(5, vm.RoleRows.Count);
        Assert.True(vm.RoleRows.First(r => r.Role == "basic").Benchmarked);
        Assert.Equal("llama3:8b", vm.RoleRows.First(r => r.Role == "basic").Model);
        Assert.False(vm.RoleRows.First(r => r.Role == "agentic_chat").Benchmarked);
        Assert.Single(vm.ModelRows);
        Assert.Equal("llama3:8b", vm.ModelRows[0].Model);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step4_Apply_Ok_ShowsPerTargetCeilings()
    {
        var fake = new FakeRouterApiClient();
        var vm = new WizardRecommendationsStepViewModel(() => fake);
        await vm.ApplyCommand.ExecuteAsync(CancellationToken.None);
        var applied = Assert.Single(vm.AppliedTargets);
        Assert.Equal("Local", applied.Target);
        Assert.Equal(4096, applied.MaxContextCeiling);
        Assert.Contains("Local", vm.Message);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step4_Apply_ConflictShowsGenerateFirstHint()
    {
        var fake = new FakeRouterApiClient { ApplyError = (HttpStatusCode.Conflict, "no recommendations") };
        var vm = new WizardRecommendationsStepViewModel(() => fake);
        await vm.ApplyCommand.ExecuteAsync(CancellationToken.None);
        Assert.Contains("generate them first", vm.Message);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Regression (task #103 review): with the daemon DOWN, entering Step 4 (root
    /// <c>GoToAsync</c> → <c>OnStepEnteredAsync</c> → <c>RefreshAsync</c>) must surface a message
    /// and NOT throw — the unguarded <c>GetRecommendationsAsync</c> read used to escape the
    /// navigation command, fault the async-void command bridge and crash the wizard.
    /// </summary>
    [Fact]
    public async Task Step4_DaemonDown_RefreshSurfacesMessage_DoesNotThrow()
    {
        var mock = new Mock<IOllamaRouterApiClient>();
        mock.Setup(c => c.GetRecommendationsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("connection refused"));
        var vm = new WizardRecommendationsStepViewModel(() => mock.Object);

        await vm.RefreshAsync(); // must not throw
        Assert.Contains("Connection failed", vm.Message);
        Assert.Empty(vm.RoleRows);
        await vm.DisposeAsync();
    }

    // ==================================================================
    //  FirstRunDetector
    // ==================================================================

    [Fact]
    public async Task FirstRun_ConfigInAnyRoot_IsNotFirstRun()
    {
        await using var home = new TempDir();
        await using var baseDir = new TempDir();
        await using var platform = new TempDir();
        Directory.CreateDirectory(Path.Combine(home.Path, "config"));
        await File.WriteAllTextAsync(Path.Combine(home.Path, "config", "config.json"), "{}");
        Assert.False(FirstRunDetector.Probe(new[] { home.Path, baseDir.Path, platform.Path }));
    }

    [Fact]
    public async Task FirstRun_NoConfigAnywhere_IsFirstRun()
    {
        await using var a = new TempDir();
        await using var b = new TempDir();
        Assert.True(FirstRunDetector.Probe(new[] { a.Path, b.Path }));
    }

    [Fact]
    public async Task FirstRun_OllamaRouterHome_IsHonored()
    {
        var home = Path.Combine(Directory.CreateTempSubdirectory("ollamarouter-home-").FullName);
        Directory.CreateDirectory(Path.Combine(home, "config"));
        await File.WriteAllTextAsync(Path.Combine(home, "config", "config.json"), "{}");
        await using var env = new TempHome(home);
        Assert.False(FirstRunDetector.NeedsSetupWizard());
    }

    // ==================================================================
    //  WizardArgs
    // ==================================================================

    [Fact]
    public void WizardArgs_DetectsWizardToken_CaseInsensitive()
    {
        Assert.True(WizardArgs.IsWizard(new[] { "--wizard" }));
        Assert.True(WizardArgs.IsWizard(new[] { "whatever", "--WIZARD" }));
        Assert.True(WizardArgs.IsWizard(new[] { "--wizard", "--other" }));
    }

    [Fact]
    public void WizardArgs_WithoutToken_IsNotWizard()
    {
        Assert.False(WizardArgs.IsWizard(new[] { "--other" }));
        Assert.False(WizardArgs.IsWizard(Array.Empty<string>()));
        Assert.False(WizardArgs.IsWizard(null));
    }

    // ==================================================================
    //  IntEqualityToVisibilityConverter
    // ==================================================================

    [Fact]
    public void Converter_Equality_ReturnsTrue_MismatchReturnsFalse()
    {
        var conv = IntEqualityToVisibilityConverter.Instance;
        var targetType = typeof(bool);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(true, conv.Convert(2, targetType, 2, culture));
        Assert.Equal(false, conv.Convert(2, targetType, 3, culture));
        Assert.Equal(false, conv.Convert("2", targetType, 2, culture)); // non-int value
    }

    /// <summary>
    /// Regression (task #103 review): compiled Avalonia XAML materializes
    /// <c>ConverterParameter="0"</c> as the STRING <c>"0"</c> (verified in the generated binding
    /// code of <c>SetupWizardWindow</c>). The converter MUST accept the string form, otherwise
    /// every step pane (and header highlight) stays hidden and the wizard renders a blank body.
    /// </summary>
    [Fact]
    public void Converter_StringParameter_AsEmittedByCompiledXaml_MatchesIntParameter()
    {
        var conv = IntEqualityToVisibilityConverter.Instance;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(true, conv.Convert(0, typeof(bool), "0", culture));
        Assert.Equal(true, conv.Convert(3, typeof(bool), "3", culture));
        Assert.Equal(false, conv.Convert(3, typeof(bool), "2", culture));
        Assert.Equal(false, conv.Convert(3, typeof(bool), "bogus", culture));
    }

    [Fact]
    public void FontWeightConverter_StringParameter_BoldOnMatch_NormalOtherwise()
    {
        var conv = IntEqualityToFontWeightConverter.Instance;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(Avalonia.Media.FontWeight.Bold, conv.Convert(2, typeof(Avalonia.Media.FontWeight), "2", culture));
        Assert.Equal(Avalonia.Media.FontWeight.Normal, conv.Convert(2, typeof(Avalonia.Media.FontWeight), "3", culture));
        Assert.Equal(Avalonia.Media.FontWeight.Normal, conv.Convert(2, typeof(Avalonia.Media.FontWeight), "bogus", culture));
        Assert.Equal(Avalonia.Media.FontWeight.Bold, conv.Convert(2, typeof(Avalonia.Media.FontWeight), 2, culture)); // int form still works
    }

    // ==================================================================
    //  BoolToMessageForegroundConverter (task #150)
    // ==================================================================

    [Fact]
    public void BoolToMessageForegroundConverter_TrueSoftRed_FalseAndNonBoolGreen()
    {
        var conv = BoolToMessageForegroundConverter.Instance;
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        var error = Assert.IsType<Avalonia.Media.SolidColorBrush>(conv.Convert(true, typeof(object), null, culture));
        Assert.Equal(Avalonia.Media.Color.Parse("#F2A0A0"), error.Color);

        var ok = Assert.IsType<Avalonia.Media.SolidColorBrush>(conv.Convert(false, typeof(object), null, culture));
        Assert.Equal(Avalonia.Media.Color.Parse("#8BC34A"), ok.Color);

        var nonBool = Assert.IsType<Avalonia.Media.SolidColorBrush>(conv.Convert("bogus", typeof(object), null, culture));
        Assert.Equal(Avalonia.Media.Color.Parse("#8BC34A"), nonBool.Color);

        // One-way converter: ConvertBack is a no-op for bindings.
        Assert.Throws<System.NotSupportedException>(() => conv.ConvertBack(null, typeof(object), null, culture));
    }
}
