using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Avalonia.Data.Converters;
using Moq;
using OllamaRouter.App;
using OllamaRouter.App.Bootstrap;
using OllamaRouter.App.Connection;
using OllamaRouter.App.Converters;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Endpoints.RouterApi.ApiKeys;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Models;
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

    private static Task GatedDelay(TimeSpan span, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);

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
    /// tick under parallel load still lands in time — no real sleep. A transient
    /// <see cref="InvalidOperationException"/> from the condition (enumerating an
    /// <c>ObservableCollection</c> while a VM rebuilds it on a thread-pool thread — no Avalonia
    /// dispatcher in tests) is treated as "not yet" and re-polled (task #148/#150 flake
    /// lesson); the FINAL assertion stays unguarded.
    /// </summary>
    /// <summary>
    /// Final post-<see cref="WaitUntilAsync"/> assertion for collections a still-running
    /// soft-delay poller can rebuild mid-read (the VM mutates on a thread-pool thread; no
    /// Avalonia dispatcher in tests — production marshals to the UI thread, so the race is
    /// test-host-only). Re-verifies the invariant over a short retry window; a genuine
    /// invariant violation keeps it false and the final assert fails.
    /// </summary>
    private static async Task AssertHoldsEventuallyAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 2_000; i++)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // Collection mid-rebuild: treat as not yet.
            }
            catch (ArgumentOutOfRangeException)
            {
                // Indexer on a collection just Clear()-ed mid-rebuild: treat as not yet.
            }

            await Task.Yield();
        }

        bool holds;
        try
        {
            holds = condition();
        }
        catch (InvalidOperationException)
        {
            holds = false;
        }
        catch (ArgumentOutOfRangeException)
        {
            holds = false;
        }

        Assert.True(holds, what);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int maxBeats = 200_000)
    {
        for (var i = 0; i < maxBeats; i++)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // Collection mid-rebuild (the VM mutates it on a thread-pool thread; production
                // marshals to the UI thread, so the race is test-host-only): treat as not yet.
            }
            catch (ArgumentOutOfRangeException)
            {
                // Indexer on a collection just Clear()-ed mid-rebuild (same test-host-only
                // race): treat as not yet.
            }

            await Task.Yield();
        }

        // The final re-check may itself catch a mid-rebuild read — report it as
        // "condition not met" (a hard fail) instead of a misleading collection exception.
        bool holds;
        try
        {
            holds = condition();
        }
        catch (InvalidOperationException)
        {
            holds = false;
        }
        catch (ArgumentOutOfRangeException)
        {
            holds = false;
        }

        Assert.True(holds, $"condition not met after {maxBeats} cooperative yields: {what}");
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
                // Task #160: the LAN-state seams are fully faked (hermeticity) — no real DNS
                // or LAN socket probes, ever.
                NonLoopbackLocalAddresses = () => LanAddresses,
                LanBindProbe = (ip, port) =>
                {
                    LanProbeCalls.Add((ip, port));
                    return LanProbeResult;
                },
                LaunchctlGetenv = name =>
                {
                    LaunchctlReads.Add(name);
                    return LaunchctlEnvValue;
                },
                PortWaitBudget = TimeSpan.FromHours(1),
            };
            // Task #157: platform-aware default — Windows presumes the tray app present (the
            // builder probes WindowsTrayAppPath and equality-gates it); every test that needs
            // a specific binary (or an absent one) still overrides this explicitly.
            Detection = new OllamaDetection(
                true, isWindows ? LocalOllamaConfigurator.WindowsTrayAppPath : "/usr/local/bin/ollama", false);
        }

        public LocalOllamaConfigurator Configurator { get; }
        public OllamaDetection Detection { get; set; } = new(true, "/usr/local/bin/ollama", false); // ctor overrides per platform
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

        // -- Task #160: the LAN-state seams -------------------------------------------------

        /// <summary>The non-loopback IPv4 addresses served by the fake resolver (empty =
        /// the persisted-env fallback, e.g. an offline machine).</summary>
        public List<IPAddress> LanAddresses { get; set; } = [];

        /// <summary>The scripted LAN-bind probe result (null = probe ERROR → env fallback).
        /// Mutating it inside the fake <c>ProcessStarter</c> scripts the mid-command
        /// re-detection flip (the Step150_Install_BusyDuringScript pattern).</summary>
        public bool? LanProbeResult { get; set; }

        /// <summary>The (ip, port) pairs passed to the fake LAN-bind probe, in order.</summary>
        public List<(string Ip, int Port)> LanProbeCalls { get; } = [];

        /// <summary>The env var names read through the fake macOS <c>launchctl getenv</c>
        /// seam, in order.</summary>
        public List<string> LaunchctlReads { get; } = [];

        /// <summary>The value served by the fake macOS <c>launchctl getenv</c> reader.</summary>
        public string? LaunchctlEnvValue { get; set; }

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
                // Hermetic (task #160): the LAN-state seams never touch DNS/sockets/launchctl.
                NonLoopbackLocalAddresses = () => [],
                LanBindProbe = (_, _) => null,
                LaunchctlGetenv = _ => null,
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
        WizardCloudStepViewModel Cloud,
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
        // Task #151: the new Step 3 (Cloud & External Providers) — entry-refresh only (no
        // poller), so no self-referential tick wiring.
        var cloud = new WizardCloudStepViewModel(() => fake);
        WizardBenchmarkStepViewModel bench = null!;
        bench = new WizardBenchmarkStepViewModel(() => fake, ct => bench.RefreshAsync(ct), delay ?? GatedDelay);
        SetupWizardViewModel root = null!;
        // Task #151: the micro-benchmark is now step index 3 (Step 4).
        var rec = new WizardRecommendationsStepViewModel(() => fake, () => _ = root.GoToAsync(3));
        root = new SetupWizardViewModel(connection, env, targets, cloud, bench, rec, code => exit.Add(code));
        return new RootFixture(root, env, targets, cloud, bench, rec, exit, fake, envConfig, launcher);
    }

    public SetupWizardViewModelsTests()
    {
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
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
    public async Task Root_Next_StepsTo4_FinishEnabled()
    {
        var fx = BuildRoot(new FakeRouterApiClient());
        var root = fx.Root;
        await root.NextCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(1, root.SelectedStep);
        await root.NextCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(2, root.SelectedStep);
        await root.NextCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(3, root.SelectedStep);
        await root.NextCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(4, root.SelectedStep);
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
        await root.GoToAsync(4);
        await root.BackCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(3, root.SelectedStep);
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
        await root.GoToAsync(4);
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
        await root.GoToAsync(4);
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
        await root.GoToAsync(1); // start targets poller (+ auto-Local)
        await root.GoToAsync(2); // enter cloud (entry refresh, no poller)
        await root.GoToAsync(3); // start benchmark poller
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

        // The tray-app start (task #157): ollama app.exe + "--fast-startup hidden" + explicit env (no stale inheritance).
        var app = Assert.Single(h.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath);
        Assert.Equal("--fast-startup hidden", app.Arguments);
        Assert.Equal("127.0.0.1:11435", app.EnvironmentVariables["OLLAMA_HOST"]);
        Assert.Equal("1", app.EnvironmentVariables["OLLAMA_FLASH_ATTENTION"]);
        Assert.Equal("q8_0", app.EnvironmentVariables["OLLAMA_KV_CACHE_TYPE"]);

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
        var app = Assert.Single(h.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath);
        Assert.Equal("0.0.0.0:11435", app.EnvironmentVariables["OLLAMA_HOST"]);
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
        Assert.Contains(h.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath); // the tray-app launch WAS attempted
    }

    // ==================================================================
    //  Step 1 — task #157: Windows tray-app launch (parity with OllamaProcessLauncher)
    // ==================================================================

    /// <summary>AC1: the Windows start path launches the TRAY APP (not a bare `serve` child)
    /// with the pinned argument string + the three explicit env vars; the success message,
    /// the 11435 port-wait and the env-write/kill sequencing are unchanged.</summary>
    [Theory]
    [InlineData(false)] // loopback → 127.0.0.1:11435
    [InlineData(true)]  // LAN mesh  → 0.0.0.0:11435
    public async Task Step157_Windows_Start_TrayAppPresent_LaunchesTrayApp_ExplicitEnv(bool lanMesh)
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false) { PortOpen = true };
        var result = await h.Configurator.StartOllamaOnlyAsync(lanMesh, CancellationToken.None);
        var expectedHost = lanMesh ? "0.0.0.0:11435" : "127.0.0.1:11435";

        Assert.True(result.Success);
        Assert.True(result.OllamaStarted);
        Assert.Equal($"Ollama is serving on {expectedHost}.", result.Message);

        // The pinned launch shape (AC1).
        var app = Assert.Single(h.Starts);
        Assert.Equal(LocalOllamaConfigurator.WindowsTrayAppPath, app.FileName);
        Assert.Equal("--fast-startup hidden", app.Arguments);
        Assert.False(app.UseShellExecute);
        Assert.Equal(expectedHost, app.EnvironmentVariables["OLLAMA_HOST"]);
        Assert.Equal("1", app.EnvironmentVariables["OLLAMA_FLASH_ATTENTION"]);
        Assert.Equal("q8_0", app.EnvironmentVariables["OLLAMA_KV_CACHE_TYPE"]);

        // Port-wait on 11435 ran (probe happened, budget respected) and the tray path was probed last.
        Assert.Contains(11435, h.ProbedPorts);
        Assert.Equal([LocalOllamaConfigurator.WindowsTrayAppPath], h.ProbedPaths[^1]);

        // The pre-launch sequencing (env writes + kills) is unchanged.
        Assert.Equal(3, h.EnvWrites.Count);
        Assert.Contains(("OLLAMA_HOST", expectedHost, EnvironmentVariableTarget.User), h.EnvWrites);
        Assert.Contains(("ollama", 111), h.Kills);
    }

    /// <summary>AC2: tray app absent → readable failure naming the searched location; nothing is
    /// launched, no `serve` fallback, no port-wait — but the env-write + stop sequencing before
    /// the builder still ran.</summary>
    [Fact]
    public async Task Step157_Windows_Start_TrayAppAbsent_FailsReadably_NoLaunchNoPortWait()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            Detection = new OllamaDetection(false, null, false),
        };
        var result = await h.Configurator.StartOllamaOnlyAsync(lanMesh: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(LocalOllamaConfigurator.OllamaNotFoundMessage(windows: true, afterUpdate: false), result.Message);
        Assert.Contains(LocalOllamaConfigurator.WindowsTrayAppPath, result.Message);
        Assert.Contains("start Ollama manually or reinstall it from ollama.com", result.Message);
        Assert.Empty(h.Starts); // nothing launched — no serve fallback
        Assert.Empty(h.ProbedPorts); // no port-wait (fail fast)
        // The env-write + stop sequencing BEFORE the builder still ran.
        Assert.Equal(3, h.EnvWrites.Count);
        Assert.Contains(("ollama", 111), h.Kills);
    }

    /// <summary>AC2 edge: a bare `ollama.exe` found (e.g. via the PATH fallback in
    /// DefaultBinaryProbe) while the tray app is absent must ALSO fail — the launch is
    /// equality-gated on the probed path being the tray app itself (the bare binary does not
    /// accept `--fast-startup hidden`).</summary>
    [Fact]
    public async Task Step157_Windows_BareBinaryOnly_NoServeFallback_Fails()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            Detection = new OllamaDetection(true, @"C:\Program Files\Ollama\ollama.exe", false),
        };
        var result = await h.Configurator.StartOllamaOnlyAsync(lanMesh: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(LocalOllamaConfigurator.OllamaNotFoundMessage(windows: true, afterUpdate: false), result.Message);
        Assert.Empty(h.Starts);
        Assert.Empty(h.ProbedPorts);
    }

    /// <summary>AC5 (Update path): after a successful update script, tray app absent →
    /// the after-update failure message; stop → script ran, but no restart, no port-wait.
    /// </summary>
    [Fact]
    public async Task Step157_Windows_Update_TrayAppAbsent_FailsReadably_NoRestart()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            Detection = new OllamaDetection(false, null, false),
        };
        var result = await h.Configurator.UpdateOllamaAsync(lanMesh: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(LocalOllamaConfigurator.OllamaNotFoundMessage(windows: true, afterUpdate: true), result.Message);
        Assert.Equal(["kill:ollama:111", "script:powershell.exe"], h.Events);
        Assert.Empty(h.Starts);
        Assert.Empty(h.ProbedPorts);
    }

    /// <summary>AC5 (change-port path, VM-level): ChangePort routes through the SAME builder —
    /// tray app absent → the same readable failure (red message), no launch, no port-wait.
    /// </summary>
    [Fact]
    public async Task Step157_Windows_ChangePort_TrayAppAbsent_RedMessageNoLaunch()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, @"C:\Program Files\Ollama\ollama.exe", true); // bare binary, running
        h.PortMap[11434] = true; // Ollama running on the WRONG port → Change port enabled
        await vm.RefreshAsync();
        Assert.True(vm.ChangePortCommand.CanExecute(null));

        await vm.ChangePortCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(vm.MessageIsError);
        Assert.Equal(LocalOllamaConfigurator.OllamaNotFoundMessage(windows: true, afterUpdate: false), vm.Message);
        Assert.Empty(h.Starts); // nothing launched — no serve fallback
        Assert.DoesNotContain(h.Events, e => e.StartsWith("start:")); // the build step failed before any launch / port-wait
        Assert.False(vm.IsBusy);
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
        Assert.False(vm.ShowChangePortButton);
        Assert.False(vm.ChangePortCommand.CanExecute(null));
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
            Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, true),
        };
        var result = await h.Configurator.UpdateOllamaAsync(lanMesh: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.OllamaStarted);
        Assert.Equal($"Ollama updated and serving on :{LocalOllamaConfigurator.OllamaPort}.", result.Message);
        // Contractual ordering: kill → script → tray-app start.
        Assert.Equal(["kill:ollama:111", "script:powershell.exe", $"start:{LocalOllamaConfigurator.WindowsTrayAppPath}"], h.Events);
        // The restart is the tray app (task #157) with the EXPLICIT env (no stale inheritance).
        var app = Assert.Single(h.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath);
        Assert.Equal("--fast-startup hidden", app.Arguments);
        Assert.Equal("127.0.0.1:11435", app.EnvironmentVariables["OLLAMA_HOST"]);
        Assert.Equal("1", app.EnvironmentVariables["OLLAMA_FLASH_ATTENTION"]);
        Assert.Equal("q8_0", app.EnvironmentVariables["OLLAMA_KV_CACHE_TYPE"]);
    }

    [Fact]
    public async Task Step150_Update_ScriptFailure_LeftStopped_NoRestart()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            NextScriptResult = new ProcessRunResult(1, string.Empty, "boom"),
        };
        var result = await h.Configurator.UpdateOllamaAsync(lanMesh: false, CancellationToken.None);
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
        var result = await h.Configurator.UpdateOllamaAsync(lanMesh: false, CancellationToken.None);
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
        var result = await h.Configurator.UpdateOllamaAsync(lanMesh: false, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(LocalOllamaConfigurator.OllamaNotFoundMessage(windows: true, afterUpdate: true), result.Message);
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
        var result = await h.Configurator.UpdateOllamaAsync(lanMesh: false, CancellationToken.None);
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
        var result = await h.Configurator.UpdateOllamaAsync(lanMesh: false, CancellationToken.None);
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

    // ==================================================================
    //  Step 1 — task #160: LAN-state detection (inside the single detection pass)
    // ==================================================================

    /// <summary>Running + a decisive LAN-bind probe: the socket probe ALONE decides the state
    /// (true = connect succeeded → bound to 0.0.0.0 → Lan; false = refused/timeout →
    /// loopback-only), against exactly one non-loopback address at the detected port.
    /// No additional binary probes (the task #147 invariant — socket probes only).</summary>
    [Theory]
    [InlineData(true)]  // connect succeeded → Lan
    [InlineData(false)] // refused/timeout  → Loopback
    public async Task Step160_DetectLanState_RunningProbeDecisive(bool lanProbeResult)
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            PortMap = { [11435] = true },
            Detection = new OllamaDetection(true, "/x", true),
            LanAddresses = [IPAddress.Parse("192.168.1.10")],
            LanProbeResult = lanProbeResult,
        };
        var check = await h.Configurator.DetectPortAsync(CancellationToken.None);
        Assert.True(check.OllamaRunning);
        Assert.Equal(11435, check.DetectedPort);
        Assert.Equal(lanProbeResult ? OllamaLanState.Lan : OllamaLanState.Loopback, check.LanState);
        Assert.Equal([("192.168.1.10", 11435)], h.LanProbeCalls); // exactly one decisive socket probe
    }

    /// <summary>Probe ERROR (null) → falls through to the persisted-env rule.
    /// </summary>
    [Fact]
    public async Task Step160_DetectLanState_ProbeErrorFallsBackToPersistedEnv()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            PortMap = { [11435] = true },
            Detection = new OllamaDetection(true, "/x", true),
            LanAddresses = [IPAddress.Parse("192.168.1.10")],
            LanProbeResult = null, // probe ERROR → env fallback
            OllamaHostValue = "0.0.0.0:11435",
        };
        var check = await h.Configurator.DetectPortAsync(CancellationToken.None);
        Assert.Equal(11435, check.DetectedPort);
        Assert.Equal(OllamaLanState.Lan, check.LanState);
        Assert.Equal([("192.168.1.10", 11435)], h.LanProbeCalls); // the probe WAS attempted (and errored)
    }

    /// <summary>No non-loopback IPv4 (offline machine) → NO socket probe at all; the
    /// persisted-env rule decides (0.0.0.0 → Lan; unset → Ollama's loopback default).
    /// </summary>
    [Theory]
    [InlineData(true)]  // persisted 0.0.0.0 → Lan
    [InlineData(false)] // no persisted value → Loopback
    public async Task Step160_DetectLanState_NoNonLoopbackAddressFallsBackToEnv(bool envIsLan)
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            PortMap = { [11435] = true },
            Detection = new OllamaDetection(true, "/x", true),
            LanAddresses = [], // offline machine → no addresses → env rule
            OllamaHostValue = envIsLan ? "0.0.0.0:11435" : null,
        };
        var check = await h.Configurator.DetectPortAsync(CancellationToken.None);
        Assert.Equal(11435, check.DetectedPort);
        Assert.Equal(envIsLan ? OllamaLanState.Lan : OllamaLanState.Loopback, check.LanState);
        Assert.Empty(h.LanProbeCalls); // never probed: no non-loopback address to probe
    }

    /// <summary>Stopped Ollama → the persisted-env rule decides with ZERO port probes AND
    /// zero LAN socket probes (the task #150 zero-probe contract kept).</summary>
    [Theory]
    [InlineData("0.0.0.0:11435", OllamaLanState.Lan)]
    [InlineData("127.0.0.1:11435", OllamaLanState.Loopback)]
    [InlineData(null, OllamaLanState.Loopback)]
    [InlineData("192.168.1.5:11435", OllamaLanState.Unknown)]
    public async Task Step160_DetectLanState_Stopped_UsesPersistedEnv_ZeroProbes(string? env, OllamaLanState expected)
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            OllamaHostValue = env,
        };
        var check = await h.Configurator.DetectPortAsync(CancellationToken.None); // stopped (harness default)
        Assert.False(check.OllamaRunning);
        Assert.Null(check.DetectedPort);
        Assert.Equal(expected, check.LanState);
        Assert.Empty(h.ProbedPorts); // zero PORT probes (the contract kept)
        Assert.Empty(h.LanProbeCalls); // zero LAN socket probes
    }

    /// <summary>macOS: the launchctl seam is the persisted-env SOURCE (the Windows
    /// OllamaHostReader is ignored on macOS) — exactly one <c>launchctl getenv OLLAMA_HOST</c>
    /// read, and its value wins.
    /// </summary>
    [Fact]
    public async Task Step160_DetectLanState_MacOS_LaunchctlGetenvReader()
    {
        var h = new ConfigHarness(isWindows: false, isMacOS: true)
        {
            OllamaHostValue = "127.0.0.1:11435", // the Windows reader value: IGNORED on macOS
            LaunchctlEnvValue = "0.0.0.0:11435", // the launchctl value: the macOS source
        };
        var check = await h.Configurator.DetectPortAsync(CancellationToken.None); // stopped (harness default)
        Assert.Equal(OllamaLanState.Lan, check.LanState);
        Assert.Equal([LocalOllamaConfigurator.HostEnvVar], h.LaunchctlReads); // exactly one read, by name
    }

    /// <summary>Pure OLLAMA_HOST LAN-state parser (task #160 — byte-pinned matrix).
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0:11435", OllamaLanState.Lan)]
    [InlineData(":::11435", OllamaLanState.Lan)]
    [InlineData("[::]:11435", OllamaLanState.Lan)]
    [InlineData("*:11435", OllamaLanState.Lan)]
    [InlineData("127.0.0.1:11435", OllamaLanState.Loopback)]
    [InlineData("localhost:11435", OllamaLanState.Loopback)]
    [InlineData(":11435", OllamaLanState.Loopback)]
    [InlineData("11435", OllamaLanState.Loopback)]
    [InlineData(null, OllamaLanState.Loopback)]
    [InlineData("", OllamaLanState.Loopback)]
    [InlineData("banana:11435", OllamaLanState.Unknown)]
    [InlineData("192.168.1.5:11435", OllamaLanState.Unknown)]
    public void Step160_TryParseOllamaHostLanState_PureParser(string? host, OllamaLanState expected)
    {
        Assert.Equal(expected, LocalOllamaConfigurator.TryParseOllamaHostLanState(host));
    }

    /// <summary>AC9: the explicit <c>lanMesh</c> parameter flows into BOTH the env write and
    /// the tray-app restart env (no hard-coded loopback anymore — the AC5 defect).
    /// </summary>
    [Fact]
    public async Task Step160_UpdateOllamaAsync_LanMeshTrue_WritesAndStarts0000Host()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            PortOpen = true,
            Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, true),
        };
        var result = await h.Configurator.UpdateOllamaAsync(lanMesh: true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.OllamaStarted);
        Assert.Contains(("OLLAMA_HOST", "0.0.0.0:11435", EnvironmentVariableTarget.User), h.EnvWrites);
        var app = Assert.Single(h.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath);
        Assert.Equal("0.0.0.0:11435", app.EnvironmentVariables["OLLAMA_HOST"]);
    }

    /// <summary>
    /// Task #160 regression (never-throw contract): the Windows update path's NEW
    /// persisted-env write is the one step that can throw (a locked-down User env).
    /// It must surface a READABLE failure (Ollama left stopped, recovery named) — never
    /// escape the command as an unobserved exception (the sibling ConfigureAsync path
    /// already wraps the same write).
    /// </summary>
    [Fact]
    public async Task Step160_Update_WindowsEnvWriteFailure_ReadableFailure_LeftStopped()
    {
        var h = new ConfigHarness(isWindows: true, isMacOS: false)
        {
            PortOpen = true,
            Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, true),
        };
        // Force the persisted-env write to throw (the SetEnvironmentVariable access-denied case).
        h.Configurator.EnvWriter = (_, _, _) => throw new UnauthorizedAccessException("access denied");

        var result = await h.Configurator.UpdateOllamaAsync(lanMesh: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.OllamaStarted); // Ollama was stopped FIRST and is left stopped
        Assert.Contains("Ollama was stopped and the update failed", result.Message);
        Assert.Contains("could not be persisted", result.Message);
        Assert.Empty(h.Starts); // no restart is attempted after the failed env write
    }

    /// <summary>
    /// AC10: the DEFAULT LAN bind probe against real loopback sockets (the same bounded
    /// convention as <c>Step1_DefaultPortProbe_…</c>): a refused port → false (loopback-only),
    /// a genuinely listening port → true (bound). The only bounded real-socket test for the
    /// new seam — everything else goes through the fakes.
    /// </summary>
    [Fact]
    public void Step160_DefaultLanBindProbe_RealLoopback_ListenTrue_RefusedFalse()
    {
        // Port 1 on loopback: nothing listens there — the connect is refused (and completes).
        Assert.False(LocalOllamaConfigurator.DefaultLanBindProbe("127.0.0.1", 1));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(LocalOllamaConfigurator.DefaultLanBindProbe("127.0.0.1", port));
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
    public async Task Step150_State_Linux_InstallAndUpdateOneClick_StartAndChangePortDisabled()
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
        Assert.False(vm.ShowChangePortButton); // Linux: no persistent env path (M1)
        Assert.False(vm.ChangePortCommand.CanExecute(null));
        Assert.False(vm.ShowLanRow); // task #160: the LAN row is hidden on Linux
        Assert.False(vm.ShowLanToggle);
        Assert.False(vm.ToggleLanCommand.CanExecute(null));
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
        // Task #160: byte-pinned — the ✓ moved OUT of the text into the row's right cell.
        Assert.Equal("Ollama is listening on 11435", vm.OllamaPortLabel);
        Assert.False(vm.ShowChangePortButton);
        Assert.False(vm.ChangePortCommand.CanExecute(null));
    }

    [Fact]
    public async Task Step150_Port_WrongPort_WarningNamesPort_ChangePortEnabledOnWindows()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, "/x", true);
        h.PortMap[11434] = true;
        await vm.RefreshAsync();

        Assert.Equal(OllamaPortStatus.WrongPort, vm.OllamaPortStatus);
        Assert.True(vm.ShowPortWrong);
        Assert.Equal(11434, vm.OllamaDetectedPort);
        // Task #160: byte-pinned (the drift now names the port that should be freed).
        Assert.Equal("Ollama is listening on 11434, but this port should be freed for the router.", vm.OllamaPortLabel);
        Assert.True(vm.ShowChangePortButton);
        Assert.True(vm.ChangePortCommand.CanExecute(null));
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
        Assert.Equal(["kill:ollama:111", "script:powershell.exe", $"start:{LocalOllamaConfigurator.WindowsTrayAppPath}"], h.Events);
        Assert.Equal(probesBefore + 2, h.ProbedPaths.Count); // BuildOllamaStartInfo re-detect + the refresh
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Step160_ChangePort_DetectedLoopback_Uses127001Host()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, true);
        h.PortMap[11434] = true; // Ollama running on the WRONG port
        h.LanAddresses = [IPAddress.Parse("192.168.1.10")];
        h.LanProbeResult = false; // decisive LAN-bind probe → detected LOOPBACK
        await vm.RefreshAsync();
        Assert.Equal(OllamaLanState.Loopback, vm.OllamaLanState);
        Assert.True(vm.ChangePortCommand.CanExecute(null));
        h.PortOpen = true; // the port-wait succeeds after the restart

        await vm.ChangePortCommand.ExecuteAsync(CancellationToken.None);

        // Task #160: the DETECTED bind is preserved (loopback → 127.0.0.1:11435).
        Assert.Contains("serving on 127.0.0.1:11435", vm.Message!);
        Assert.False(vm.MessageIsError);
        Assert.Contains(("OLLAMA_HOST", "127.0.0.1:11435", EnvironmentVariableTarget.User), h.EnvWrites);
        var app = Assert.Single(h.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath);
        Assert.Equal("127.0.0.1:11435", app.EnvironmentVariables["OLLAMA_HOST"]);
        Assert.Equal(OllamaPortStatus.Ok, vm.OllamaPortStatus); // re-detect: fixed
    }

    [Fact]
    public async Task Step160_ChangePort_DetectedLan_Uses0000Host()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, true);
        h.PortMap[11434] = true;
        h.LanAddresses = [IPAddress.Parse("192.168.1.10")];
        h.LanProbeResult = true; // decisive LAN-bind probe → detected LAN
        await vm.RefreshAsync();
        Assert.Equal(OllamaLanState.Lan, vm.OllamaLanState);
        h.PortOpen = true;

        await vm.ChangePortCommand.ExecuteAsync(CancellationToken.None);

        // Task #160: the DETECTED LAN bind is preserved (→ 0.0.0.0:11435, no checkbox needed).
        Assert.Contains(("OLLAMA_HOST", "0.0.0.0:11435", EnvironmentVariableTarget.User), h.EnvWrites);
        var app = Assert.Single(h.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath);
        Assert.Equal("0.0.0.0:11435", app.EnvironmentVariables["OLLAMA_HOST"]);
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
        // Router already running: no button — the ✓ lives in the row's RIGHT cell (task #160).
        var running = new FakeDaemonLauncher { Status = FakeDaemonLauncher.RunningStatus() };
        var (vmRunning, _, _) = BuildEnvStep(launcher: running);
        await vmRunning.RefreshAsync();
        Assert.Equal(Port11434Status.RouterRunning, vmRunning.Port11434Status);
        Assert.Equal("Router is running on :11434", vmRunning.RouterStatusLabel); // no ✓ in the text
        Assert.True(vmRunning.ShowRouterRunningCheck);
        Assert.False(vmRunning.ShowStartRouterButton);
        Assert.False(vmRunning.StartRouterCommand.CanExecute(null));

        // Foreign service on :11434: the reworded warning label (task #160: the port row with
        // "Change port" is now ABOVE — no more "below"), no button.
        var foreign = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null, "occupied"),
        };
        var (vmForeign, _, _) = BuildEnvStep(launcher: foreign);
        await vmForeign.RefreshAsync();
        Assert.Equal(Port11434Status.ForeignService, vmForeign.Port11434Status);
        Assert.Equal(
            "Raw Ollama occupies :11434 — free it for the router with \"Change port\" above.",
            vmForeign.RouterStatusLabel);
        Assert.DoesNotContain("below", vmForeign.RouterStatusLabel);
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
        Assert.False(vm.ChangePortCommand.CanExecute(null));
        Assert.False(vm.ToggleLanCommand.CanExecute(null));
        Assert.False(vm.StartRouterCommand.CanExecute(null));
        Assert.False(vm.RecheckCommand.CanExecute(null)); // no concurrent actions while busy

        gate.SetResult(new ProcessRunResult(0, string.Empty, string.Empty));
        await running;

        Assert.False(vm.IsBusy);
        Assert.True(vm.InstallOllamaCommand.CanExecute(null));
        Assert.True(vm.RecheckCommand.CanExecute(null));
    }

    // ==================================================================
    //  Step 1 — task #160: the detected LAN-exposure row + unified row grid (VM level)
    // ==================================================================

    [Fact]
    public async Task Step160_LanRow_HiddenWhenNotInstalled_AndWhenLinux()
    {
        // Windows, NOT installed: the row is hidden, the toggle too, the command disabled.
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(false, null, false);
        await vm.RefreshAsync();
        Assert.False(vm.ShowLanRow);
        Assert.False(vm.ShowLanToggle);
        Assert.False(vm.ToggleLanCommand.CanExecute(null));

        // Linux, installed: the row is hidden as well (no GUI-readable persisted env there).
        var (vmLinux, _, _) = BuildEnvStep(isWindows: false, isMacOS: false);
        await vmLinux.RefreshAsync(); // harness default: installed, not running
        Assert.True(vmLinux.OllamaInstalled);
        Assert.True(vmLinux.IsLinux);
        Assert.False(vmLinux.ShowLanRow);
        Assert.False(vmLinux.ShowLanToggle);
        Assert.False(vmLinux.ToggleLanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Step160_LanRow_LoopbackState_PinnedLabelAndExposeButton()
    {
        var (vm, _, _) = BuildEnvStep(); // installed, stopped, no persisted env → Loopback
        await vm.RefreshAsync();
        Assert.Equal(OllamaLanState.Loopback, vm.OllamaLanState);
        Assert.Equal(
            "Ollama is configured to listen only on loopback. To use it from another computer " +
            "on the network, allow listening on the LAN.",
            vm.LanRowLabel);
        Assert.Equal("Expose on the LAN", vm.LanToggleText);
        Assert.True(vm.ShowLanRow);
        Assert.True(vm.ShowLanToggle);
        Assert.True(vm.ToggleLanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Step160_LanRow_LanState_PinnedLabelAndRestrictButton()
    {
        var (vm, h, _) = BuildEnvStep();
        h.OllamaHostValue = "0.0.0.0:11435"; // stopped + persisted LAN → Lan
        await vm.RefreshAsync();
        Assert.Equal(OllamaLanState.Lan, vm.OllamaLanState);
        Assert.Equal(
            "Ollama is configured to listen on LAN, and can be used by other computers " +
            "on the network.",
            vm.LanRowLabel);
        Assert.Equal("Restrict to localhost", vm.LanToggleText);
        Assert.True(vm.ShowLanRow);
        Assert.True(vm.ShowLanToggle);
        Assert.True(vm.ToggleLanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Step160_LanRow_UnknownState_PinnedLabel_NoButton()
    {
        var (vm, h, _) = BuildEnvStep();
        h.OllamaHostValue = "banana:11435"; // undeterminable host → Unknown
        await vm.RefreshAsync();
        Assert.Equal(OllamaLanState.Unknown, vm.OllamaLanState);
        Assert.Equal("Ollama's LAN configuration could not be determined — try Re-check.", vm.LanRowLabel);
        Assert.True(vm.ShowLanRow); // the row itself stays visible (installed + Windows)
        Assert.False(vm.ShowLanToggle); // …but the Unknown state shows NO button
        Assert.False(vm.ToggleLanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Step160_ToggleLan_LoopbackToLan_Writes0000_Restarts_AndRedetectsFlips()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, true);
        h.PortMap[11435] = true;
        h.LanAddresses = [IPAddress.Parse("192.168.1.10")];
        h.LanProbeResult = false; // decisive probe → detected LOOPBACK
        await vm.RefreshAsync();
        Assert.Equal(OllamaLanState.Loopback, vm.OllamaLanState);
        Assert.Equal("Expose on the LAN", vm.LanToggleText);
        Assert.True(vm.ToggleLanCommand.CanExecute(null));

        // Mid-command flip: the re-detection (inside the command) must see the NEW bind.
        Func<ProcessStartInfo, string> original = h.Configurator.ProcessStarter!;
        h.Configurator.ProcessStarter = info =>
        {
            Assert.True(vm.IsBusy); // busy DURING the restart
            Assert.Contains("Exposing Ollama on the LAN", vm.Message!); // the busy message
            h.LanProbeResult = true; // the restarted Ollama binds 0.0.0.0
            h.PortOpen = true;       // the port-wait succeeds
            return original(info);
        };

        await vm.ToggleLanCommand.ExecuteAsync(CancellationToken.None);

        Assert.Contains("serving on 0.0.0.0:11435", vm.Message!);
        Assert.False(vm.MessageIsError);
        Assert.Contains(("OLLAMA_HOST", "0.0.0.0:11435", EnvironmentVariableTarget.User), h.EnvWrites);
        var app = Assert.Single(h.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath);
        Assert.Equal("0.0.0.0:11435", app.EnvironmentVariables["OLLAMA_HOST"]);
        // The auto re-detect flipped the row to the Lan label/button set.
        Assert.Equal(OllamaLanState.Lan, vm.OllamaLanState);
        Assert.Equal(
            "Ollama is configured to listen on LAN, and can be used by other computers " +
            "on the network.",
            vm.LanRowLabel);
        Assert.Equal("Restrict to localhost", vm.LanToggleText);
        Assert.True(vm.ShowLanToggle);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Step160_ToggleLan_LanToLoopback_Writes127001_Restarts_AndRedetectsFlips()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, true);
        h.PortMap[11435] = true;
        h.LanAddresses = [IPAddress.Parse("192.168.1.10")];
        h.LanProbeResult = true; // decisive probe → detected LAN
        await vm.RefreshAsync();
        Assert.Equal(OllamaLanState.Lan, vm.OllamaLanState);
        Assert.Equal("Restrict to localhost", vm.LanToggleText);
        Assert.True(vm.ToggleLanCommand.CanExecute(null));

        Func<ProcessStartInfo, string> original = h.Configurator.ProcessStarter!;
        h.Configurator.ProcessStarter = info =>
        {
            Assert.True(vm.IsBusy);
            Assert.Contains("Restricting Ollama to localhost", vm.Message!); // the busy message
            h.LanProbeResult = false; // the restarted Ollama binds 127.0.0.1
            h.PortOpen = true;
            return original(info);
        };

        await vm.ToggleLanCommand.ExecuteAsync(CancellationToken.None);

        Assert.Contains("serving on 127.0.0.1:11435", vm.Message!);
        Assert.False(vm.MessageIsError);
        Assert.Contains(("OLLAMA_HOST", "127.0.0.1:11435", EnvironmentVariableTarget.User), h.EnvWrites);
        var app = Assert.Single(h.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath);
        Assert.Equal("127.0.0.1:11435", app.EnvironmentVariables["OLLAMA_HOST"]);
        // The auto re-detect flipped the row back to the Loopback label/button set.
        Assert.Equal(OllamaLanState.Loopback, vm.OllamaLanState);
        Assert.Equal(
            "Ollama is configured to listen only on loopback. To use it from another computer " +
            "on the network, allow listening on the LAN.",
            vm.LanRowLabel);
        Assert.Equal("Expose on the LAN", vm.LanToggleText);
        Assert.True(vm.ShowLanToggle);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Step160_ToggleLan_FromStopped_StartsOllama()
    {
        // Decision 3: the toggle works while Ollama is STOPPED — it starts Ollama.
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, false); // installed, STOPPED
        h.OllamaHostValue = "0.0.0.0:11435"; // persisted LAN → state Lan even while stopped
        await vm.RefreshAsync();
        Assert.Equal(OllamaLanState.Lan, vm.OllamaLanState);
        Assert.Equal(OllamaPortStatus.NotRunning, vm.OllamaPortStatus);
        Assert.Equal("Restrict to localhost", vm.LanToggleText);
        Assert.True(vm.ToggleLanCommand.CanExecute(null));
        h.PortOpen = true; // the start's port-wait succeeds

        await vm.ToggleLanCommand.ExecuteAsync(CancellationToken.None);

        Assert.Contains("serving on 127.0.0.1:11435", vm.Message!);
        Assert.False(vm.MessageIsError);
        Assert.Contains(("OLLAMA_HOST", "127.0.0.1:11435", EnvironmentVariableTarget.User), h.EnvWrites);
        var app = Assert.Single(h.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath);
        Assert.Equal("127.0.0.1:11435", app.EnvironmentVariables["OLLAMA_HOST"]);
        Assert.Contains(11435, h.ProbedPorts); // the port-wait ran (start confirmed)
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Step160_ToggleLan_PortWaitFailure_RedMessage_NoCrash_CommandAvailableAgain()
    {
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, true);
        h.LanAddresses = [IPAddress.Parse("192.168.1.10")];
        h.LanProbeResult = false; // running, port undetectable → env rule (null) → Loopback
        await vm.RefreshAsync();
        Assert.Equal(OllamaLanState.Loopback, vm.OllamaLanState);
        Assert.True(vm.ToggleLanCommand.CanExecute(null));
        h.Configurator.PortWaitBudget = TimeSpan.Zero; // the wait budget elapses immediately
        // PortOpen stays false → the restart never opens the port.

        await vm.ToggleLanCommand.ExecuteAsync(CancellationToken.None); // must not throw

        Assert.True(vm.MessageIsError);
        Assert.Contains("did not open port 11435", vm.Message!);
        Assert.False(vm.IsBusy);
        Assert.True(vm.ToggleLanCommand.CanExecute(null)); // the toggle is retryable
    }

    [Fact]
    public async Task Step160_StartOllama_PreservesDetectedLanBind()
    {
        // Detected LAN (stopped + persisted 0.0.0.0) → Start keeps 0.0.0.0:11435.
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, false);
        h.OllamaHostValue = "0.0.0.0:11435";
        h.PortOpen = true;
        await vm.RefreshAsync();
        Assert.Equal(OllamaLanState.Lan, vm.OllamaLanState);
        Assert.True(vm.StartOllamaCommand.CanExecute(null));

        await vm.StartOllamaCommand.ExecuteAsync(CancellationToken.None);

        Assert.Contains(("OLLAMA_HOST", "0.0.0.0:11435", EnvironmentVariableTarget.User), h.EnvWrites);
        var app = Assert.Single(h.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath);
        Assert.Equal("0.0.0.0:11435", app.EnvironmentVariables["OLLAMA_HOST"]);

        // Unknown → loopback (decision 3: Unknown is treated as loopback for the start).
        var (vm2, h2, _) = BuildEnvStep();
        h2.Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, false);
        h2.OllamaHostValue = "banana:11435"; // undeterminable host → Unknown
        h2.PortOpen = true;
        await vm2.RefreshAsync();
        Assert.Equal(OllamaLanState.Unknown, vm2.OllamaLanState);
        Assert.True(vm2.StartOllamaCommand.CanExecute(null));

        await vm2.StartOllamaCommand.ExecuteAsync(CancellationToken.None);

        var app2 = Assert.Single(h2.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath);
        Assert.Equal("127.0.0.1:11435", app2.EnvironmentVariables["OLLAMA_HOST"]);
    }

    [Fact]
    public async Task Step160_Update_PreservesDetectedLanBind()
    {
        // AC5 regression: the update no longer silently resets a LAN-configured Ollama to
        // loopback — the restart uses the DETECTED bind.
        var (vm, h, _) = BuildEnvStep();
        h.Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, true);
        h.PortMap[11435] = true;
        h.LanAddresses = [IPAddress.Parse("192.168.1.10")];
        h.LanProbeResult = true; // decisive probe → detected LAN
        h.PortOpen = true; // the restart port-wait succeeds
        await vm.RefreshAsync();
        Assert.Equal(OllamaLanState.Lan, vm.OllamaLanState);
        Assert.True(vm.UpdateOllamaCommand.CanExecute(null));

        await vm.UpdateOllamaCommand.ExecuteAsync(CancellationToken.None);

        Assert.Contains($"Ollama updated and serving on :{LocalOllamaConfigurator.OllamaPort}", vm.Message!);
        Assert.False(vm.MessageIsError);
        Assert.Contains(("OLLAMA_HOST", "0.0.0.0:11435", EnvironmentVariableTarget.User), h.EnvWrites);
        var app = Assert.Single(h.Starts, s => s.FileName == LocalOllamaConfigurator.WindowsTrayAppPath);
        Assert.Equal("0.0.0.0:11435", app.EnvironmentVariables["OLLAMA_HOST"]);
    }

    [Fact]
    public async Task Step160_RouterRow_Running_CheckInRightCell_NoButton()
    {
        var (vm, _, _) = BuildEnvStep(launcher: new FakeDaemonLauncher { Status = FakeDaemonLauncher.RunningStatus() });
        await vm.RefreshAsync();
        Assert.Equal(Port11434Status.RouterRunning, vm.Port11434Status);
        Assert.Equal("Router is running on :11434", vm.RouterStatusLabel); // no ✓ in the text
        Assert.True(vm.ShowRouterRunningCheck); // the ✓ is the right cell
        Assert.False(vm.ShowStartRouterButton); // …and the button is gone
    }

    [Fact]
    public async Task Step160_RouterRow_ForeignService_RewordedLabel_EmptyRightCell()
    {
        var (vm, _, _) = BuildEnvStep(launcher: new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null, "occupied"),
        });
        await vm.RefreshAsync();
        Assert.Equal(Port11434Status.ForeignService, vm.Port11434Status);
        // Task #160 rewording (byte-pinned): the "Change port" row is now ABOVE.
        Assert.Equal(
            "Raw Ollama occupies :11434 — free it for the router with \"Change port\" above.",
            vm.RouterStatusLabel);
        Assert.DoesNotContain("below", vm.RouterStatusLabel);
        Assert.False(vm.ShowRouterRunningCheck); // the right cell is EMPTY
        Assert.False(vm.ShowStartRouterButton);
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

    /// <summary>
    /// Task #151 headline dedup regression (Explorer bug #7): a command-side
    /// <c>RefreshAsync</c> overlapping the in-flight 2 s poller tick must NOT interleave with
    /// it — the two <c>Rows.Clear()/Add</c> rebuilds serialize on the view model's single-writer
    /// gate (the task #130 addendum bug class), so the final <c>Rows</c> contains every non-Cloud
    /// fleet target EXACTLY ONCE (payload 2's, as it completes last).
    /// </summary>
    [Fact]
    public async Task Step2_OverlapPollerAndCommand_Refresh_NoDuplicateRows()
    {
        var noHardware = new RouterTargetHardware("nvidia", [], null, 1, null);
        var noExec = new RouterTargetExecution(0, null, "none");
        var payload1 = new RouterFleetResponse([
            new RouterTargetItem("Node1", "http://node1", true, "Online", 1, "local", noHardware, noExec),
            new RouterTargetItem("Cloud", null, true, "Online", null, "cloud", noHardware, noExec),
        ]);
        var payload2 = new RouterFleetResponse([
            new RouterTargetItem("Node1", "http://node1", true, "Online", 1, "local", noHardware, noExec),
            new RouterTargetItem("Node2", "http://node2", true, "Online", 2, "remote", noHardware, noExec),
            new RouterTargetItem("Cloud", null, true, "Online", null, "cloud", noHardware, noExec),
        ]);

        var gate1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeRouterApiClient
        {
            // Per-call FIFO: the poller tick (call 1) serves payload1 / blocks on gate1;
            // the command refresh (call 2) serves payload2 / blocks on gate2.
            FleetResponses = new Queue<RouterFleetResponse>([payload1, payload2]),
            FleetFetchBlocks = new Queue<Task>([gate1.Task, gate2.Task]),
        };
        var vm = BuildTargets(fake);
        await vm.OnStepEnteredAsync(); // auto-Local + poller start: the first tick is in flight on gate1.
        await WaitUntilAsync(
            () => fake.Calls.Count(c => c == "GetFleetAsync") >= 1,
            "the poller's first tick to start its fleet read (in flight on the gate)");

        // The command-side refresh (UI thread in production): it must queue BEHIND the
        // in-flight tick on the single-writer gate, never interleave with its rebuild.
        var commandRefresh = vm.RefreshAsync(CancellationToken.None);
        // Skewed release order: gate2 first (no-op yet — the command refresh still waits for
        // the gate the tick holds), then gate1 (tick rebuilds payload1 and releases; the
        // command refresh then enters and rebuilds payload2 immediately).
        gate2.SetResult();
        gate1.SetResult();
        await WaitUntilAsync(
            () => fake.Calls.Count(c => c == "GetFleetAsync") >= 2,
            "the command-side refresh to reach its fleet read");
        await commandRefresh;
        await vm.OnStepLeftAsync();

        // The final state is the LAST completed rebuild (payload 2, Cloud excluded): every
        // non-Cloud target exactly once — no duplicates from interleaved Clear()/Add.
        Assert.Equal(["Node1", "Node2"], vm.Rows.Select(r => r.Name));
        Assert.DoesNotContain(vm.Rows, r => r.Name == "Cloud");
        await vm.DisposeAsync();
    }

    private static RouterFleetResponse ThreeNodePlusCloudFleet() =>
        new([
            new RouterTargetItem("Local", "http://127.0.0.1:11435", true, "Online", 5, "local",
                new RouterTargetHardware("nvidia", [], null, 1, null), new RouterTargetExecution(0, null, "none")),
            new RouterTargetItem("Remote", "http://192.168.1.50:11435", true, "Online", 20, "remote",
                new RouterTargetHardware("nvidia", [], null, 1, null), new RouterTargetExecution(0, null, "none")),
            new RouterTargetItem("Remote2", "http://192.168.1.51:11435", true, "Offline", null, "remote",
                new RouterTargetHardware("nvidia", [], null, 1, null), new RouterTargetExecution(0, null, "none")),
            // The server ALWAYS appends the Cloud pseudo-target last — the view model must drop it.
            new RouterTargetItem("Cloud", null, true, "Online", null, "cloud",
                new RouterTargetHardware("nvidia", [], null, 1, null), new RouterTargetExecution(0, null, "none")),
        ]);

    /// <summary>
    /// Task #163 (AC1 — the headline Step 2 dedup regression): a flood of ≥ 12 consecutive
    /// tick-equivalent refreshes (the exact <c>RefreshCoreAsync</c> path the 2 s poller tick
    /// runs), fired in overlapping command-side waves PLUS the post-add
    /// <c>RefreshAsync</c> from <c>AddTargetCommand</c>, leaves <see cref="WizardTargetsStepViewModel.Rows"/>
    /// REBUILT — never accumulated: exactly 3 rows, each target name exactly once, in fleet
    /// order, Cloud excluded, at every settle point. (The row Clear()/Add rebuilds are
    /// marshaled to the UI thread inside the single-writer gate — the FleetTargetsViewModel
    /// pattern — so the UI's ItemsControl enumeration never races them.)
    /// </summary>
    [Fact]
    public async Task Step2_TickFlood_AndCommandRefreshes_NeverDuplicateRows()
    {
        var fake = new FakeRouterApiClient { FleetResponse = ThreeNodePlusCloudFleet() };
        var vm = BuildTargets(fake);

        void AssertRebuilt() => Assert.Equal(["Local", "Remote", "Remote2"], vm.Rows.Select(r => r.Name));

        await vm.OnStepEnteredAsync(); // auto-Local + the poller's first tick
        await WaitUntilAsync(() => vm.Rows.Count == 3, "the poller's first tick to populate the rows");
        AssertRebuilt();

        // 12 tick-equivalent refreshes in overlapping waves of 3 (the command-side overlap
        // of the live 2 s tick); the rows are REBUILT to the same 3 rows at every settle.
        var wave = new List<Task>();
        for (var i = 0; i < 12; i++)
        {
            wave.Add(vm.RefreshAsync(CancellationToken.None));
            if (wave.Count == 3)
            {
                await Task.WhenAll(wave);
                wave.Clear();
                AssertRebuilt();
            }
        }
        AssertRebuilt();

        // The post-add refresh (the AddTargetCommand's finally-block RefreshAsync): still
        // rebuilt, not accumulated — each name exactly once.
        await vm.AddTargetCommand.ExecuteAsync(new AddTargetRequest("Extra", "http://extra:11435"));
        AssertRebuilt();

        await vm.OnStepLeftAsync();
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #163 (AC1 — the Step 2 re-entry arm): enter → (tick) → leave → enter → (tick)
    /// → leave → enter → (tick) never accumulates: the fleet table is rebuilt by each
    /// re-entry's poller tick and holds exactly the same 3 rows, no stale names, no doubles.
    /// </summary>
    [Fact]
    public async Task Step2_Reentry_TicksRebuildRows_NeverAccumulate()
    {
        var fake = new FakeRouterApiClient { FleetResponse = ThreeNodePlusCloudFleet() };
        var vm = BuildTargets(fake);

        await vm.OnStepEnteredAsync(); // entry 1: Local absent → added; the first tick rebuilds
        await WaitUntilAsync(() => vm.Rows.Count == 3, "the first entry's fleet tick");
        await vm.OnStepLeftAsync();
        await vm.OnStepEnteredAsync(); // re-entry 1 (fresh poller CTS — the tick must run again)
        await WaitUntilAsync(
            () => fake.Calls.Count(c => c == "GetFleetAsync") >= 2 && vm.Rows.Count == 3,
            "the re-entry's fleet tick to rebuild the rows");
        await vm.OnStepLeftAsync();
        await vm.OnStepEnteredAsync(); // re-entry 2
        await WaitUntilAsync(
            () => fake.Calls.Count(c => c == "GetFleetAsync") >= 3 && vm.Rows.Count == 3,
            "the second re-entry's fleet tick to rebuild the rows");

        Assert.Equal(["Local", "Remote", "Remote2"], vm.Rows.Select(r => r.Name));
        Assert.Equal(3, vm.Rows.Select(r => r.Name).Distinct().Count()); // rebuilt, not accumulated
        await vm.OnStepLeftAsync();
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #151: entering Step 2 with NO <c>Local</c> target in the config auto-adds it
    /// EXACTLY ONCE — <c>Local</c> @ <c>http://127.0.0.1:11435</c> with the server-side
    /// connection probe (<c>testConnectionFirst</c>) — and the rows show it.
    /// </summary>
    [Fact]
    public async Task Step2_Entry_AutoAddsLocal_WhenAbsent()
    {
        var fake = new FakeRouterApiClient { FleetResponse = OneTargetFleet() };
        var vm = BuildTargets(fake);
        await vm.OnStepEnteredAsync();

        var req = Assert.Single(fake.AddTargetCalls);
        Assert.Equal("Local", req.Name);
        Assert.Equal("http://127.0.0.1:11435", req.Url);
        Assert.True(req.TestConnectionFirst);
        Assert.Equal("Local node added.", vm.Message);

        await WaitUntilAsync(() => vm.Rows.Count == 1, "the poller tick to populate the target rows");
        Assert.Equal("Local", Assert.Single(vm.Rows).Name);
        await vm.OnStepLeftAsync();
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #151 idempotency: re-entering the step with <c>Local</c> already present (any URL)
    /// is a silent no-op — exactly ONE add across enter/leave/enter, and no duplicate rows.
    /// </summary>
    [Fact]
    public async Task Step2_Reentry_SingleAdd_NoDuplicates()
    {
        var fake = new FakeRouterApiClient { FleetResponse = OneTargetFleet() };
        var vm = BuildTargets(fake);

        await vm.OnStepEnteredAsync(); // entry 1: Local absent → added
        await WaitUntilAsync(() => vm.Rows.Count == 1, "the first entry's fleet tick to run");
        await vm.OnStepLeftAsync();
        await vm.OnStepEnteredAsync(); // entry 2: Local present in the config → no-op
        // The re-entry's poller must TICK AGAIN (a fresh cancellation source per start —
        // the DashboardPoller stop/start contract) and rebuild the same single row.
        await WaitUntilAsync(
            () => fake.Calls.Count(c => c == "GetFleetAsync") >= 2 && vm.Rows.Count == 1,
            "the re-entry's fleet tick to rebuild the rows");
        await vm.OnStepLeftAsync();

        Assert.Single(fake.AddTargetCalls); // exactly ONE add across both entries
        Assert.Equal(["Local"], vm.Rows.Select(r => r.Name)); // no duplicate rows
        await vm.DisposeAsync();
    }

    /// <summary>Task #151: the concurrent-add race (409 Conflict) is readable, never fatal
    /// and never a duplicate.</summary>
    [Fact]
    public async Task Step2_Local_Conflict_ReadableMessage()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([]),
            AddTargetError = (HttpStatusCode.Conflict, "target already exists: Local"),
        };
        var vm = BuildTargets(fake);
        await vm.OnStepEnteredAsync(); // must not throw
        Assert.Equal("A target named 'Local' already exists — left unchanged.", vm.Message);
        await vm.OnStepLeftAsync();
        await vm.DisposeAsync();
    }

    /// <summary>Task #151: a 422 (Local Ollama unreachable) surfaces the fix-Step-1 hint.</summary>
    [Fact]
    public async Task Step2_Local_Unreachable422_FixStep1Hint()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([]),
            AddTargetError = (HttpStatusCode.UnprocessableEntity, "could not connect"),
        };
        var vm = BuildTargets(fake);
        await vm.OnStepEnteredAsync(); // must not throw
        Assert.Contains("unreachable", vm.Message);
        Assert.Contains("fix Step 1 and retry", vm.Message);
        await vm.OnStepLeftAsync();
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #158: with the router DOWN (the canonical first-run state before Step 1 has
    /// started it), the step entry — auto-Local PLUS the first poll tick — surfaces the
    /// SHARED actionable daemon-down message (byte-exact) and does NOT throw: an exception
    /// escaping the entry path would fault the async-void command bridge and crash the
    /// wizard (the task #103 regression, kept). No <c>Local</c> row is added and the raw
    /// transport text never leaks (the old <c>Connection failed: {ex.Message}</c> / <c
    /// >Connection failed — start the router in Step 1 first.</c> strings are retired).
    /// </summary>
    [Fact]
    public async Task Step2_RouterDown_EntryMessage_NoThrow()
    {
        var mock = new Mock<IOllamaRouterApiClient>();
        mock.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("connection refused"));
        mock.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("connection refused"));
        WizardTargetsStepViewModel vm = null!;
        vm = new WizardTargetsStepViewModel(() => mock.Object, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync(); // auto-Local + poller start: must not throw
        Assert.Equal(
            "The OllamaRouter daemon is not reachable on 127.0.0.1:11434 — go back to Step 1 and click 'Start router'.",
            vm.Message);
        Assert.Empty(vm.Rows); // no Local row added while the daemon is down
        mock.Verify(c => c.AddTargetAsync(It.IsAny<AddTargetRequest>(), It.IsAny<CancellationToken>()), Moq.Times.Never);
        await vm.OnStepLeftAsync();
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #165 (AC4): when the transport faults with the socket-abort class (a raw
    /// <c>SocketException(995)</c> — the Windows shape of a cancel/teardown aborting an
    /// in-flight socket I/O, e.g. the daemon starting then exiting) instead of
    /// <c>HttpRequestException</c>, the step entry and the poll tick surface the SAME shared
    /// daemon-down message (byte-exact, task #158 contract) and never throw — raw transport
    /// text never leaks.
    /// </summary>
    [Fact]
    public async Task Step2_Poll_SocketAbortRaw_TransportFailure_DaemonDownMessage()
    {
        var mock = new Mock<IOllamaRouterApiClient>();
        mock.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Sockets.SocketException(995));
        mock.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Sockets.SocketException(995));
        WizardTargetsStepViewModel vm = null!;
        vm = new WizardTargetsStepViewModel(() => mock.Object, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync(); // entry (auto-Local attempt + poller start): no throw
        await vm.OnStepLeftAsync();
        await vm.RefreshAsync(); // the exact poll-tick entry point (RefreshCoreAsync)

        Assert.Equal(
            "The OllamaRouter daemon is not reachable on 127.0.0.1:11434 — go back to Step 1 and click 'Start router'.",
            vm.Message);
        Assert.Empty(vm.Rows);
        mock.Verify(c => c.AddTargetAsync(It.IsAny<AddTargetRequest>(), It.IsAny<CancellationToken>()), Moq.Times.Never);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #158 (AC1 + AC6 — the headline no-churn guarantee): across ≥ 3 consecutive
    /// FAILED refreshes (the exact <c>RefreshCoreAsync</c> path the 2 s poller tick runs,
    /// plus the entry auto-add attempt) the daemon-down message stays BYTE-IDENTICAL (no
    /// flicker between variants) and <c>PropertyChanged("Message")</c> fires EXACTLY ONCE for
    /// the whole down-state (first set only — the <c>SetFailureMessage</c> guard). The REAL
    /// Windows-style transport text is fed to the mock, proving it never leaks (no
    /// "actively refused" text, no bare parenthesized "(127.0.0.1:11434)" fragment). A NEW
    /// failure type (a later <c>Router error 500</c>) still overwrites the stable text and
    /// fires <c>PropertyChanged</c> again.
    /// </summary>
    [Fact]
    public async Task Step2_Poll_DaemonDown_MessageStable_NoChurnAcrossTicks()
    {
        // The REAL Windows HttpRequestException text (connection refused):
        const string rawWindowsText = "Connection refused (127.0.0.1:11434): actively refused it. (127.0.0.1:11434)";
        var mock = new Mock<IOllamaRouterApiClient>();
        mock.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException(rawWindowsText));
        mock.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException(rawWindowsText));
        WizardTargetsStepViewModel vm = null!;
        vm = new WizardTargetsStepViewModel(() => mock.Object, ct => vm.RefreshAsync(ct), GatedDelay);

        var messageChanges = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WizardTargetsStepViewModel.Message))
            {
                messageChanges++;
            }
        };

        // The persistent down-state: entry auto-add attempt, then stop the poller (deterministic
        // count — the GatedDelay loop cannot be cooperatively scheduled from this test thread
        // under parallel load), then 3 consecutive failed poll-tick refreshes (RefreshAsync is
        // exactly the tick's entry point: the same RefreshCoreAsync transport catch).
        await vm.OnStepEnteredAsync(); // attempt 1 (EnsureLocalAsync transport catch) + poller start
        await vm.OnStepLeftAsync(); // stop the poller: no background ticks racing the count
        await vm.RefreshAsync(); // failed poll tick 1
        await vm.RefreshAsync(); // failed poll tick 2
        await vm.RefreshAsync(); // failed poll tick 3

        // AC1: the message is the SHARED daemon-down text — byte-exact, raw transport text never leaks.
        const string expected =
            "The OllamaRouter daemon is not reachable on 127.0.0.1:11434 — go back to Step 1 and click 'Start router'.";
        Assert.Equal(expected, vm.Message);
        Assert.DoesNotContain("actively refused", vm.Message);
        Assert.DoesNotContain("(127.0.0.1:11434)", vm.Message); // the bare parenthesized fragment never appears
        Assert.Equal(1, CountOccurrences(vm.Message!, "127.0.0.1:11434")); // ! : Assert.Equal above proves non-null; the port appears only in that one sentence

        // AC6: over the WHOLE down-state window (entry attempt + 3 failed ticks), the first
        // set fired PropertyChanged exactly once; every subsequent identical re-set was a
        // no-op (no flicker, no churn).
        Assert.Equal(1, messageChanges);

        // AC6 (new-failure-type still wins): once the daemon comes back with an API error,
        // the DIFFERENT message overwrites the stable daemon-down text and fires again.
        mock.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.InternalServerError, "boom"));
        await vm.RefreshAsync(); // the daemon responded now — a NEW failure type
        Assert.Equal("Router error 500: boom", vm.Message);
        Assert.Equal(2, messageChanges); // the first set + the new failure type, nothing else
        await vm.DisposeAsync();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Task #158 (AC5): a MANUAL add while the daemon is down (Step 2 <c
    /// >AddTargetCommand</c>, via <c>WizardTargetErrors.MapAddTarget</c>'s transport arm) is the
    /// SAME root cause as the poll/auto-add — it surfaces the single daemon-down message, never
    /// the old raw <c>Connection failed: {http.Message}</c> leak.
    /// </summary>
    [Fact]
    public async Task Step2_ManualAdd_DaemonDown_UsesSharedMessage()
    {
        var mock = new Mock<IOllamaRouterApiClient>();
        mock.Setup(c => c.AddTargetAsync(It.IsAny<AddTargetRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException(
                "Connection refused (127.0.0.1:11434): actively refused it. (127.0.0.1:11434)"));
        // The post-add refresh (finally block) hits the same down daemon.
        mock.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("actively refused it. (127.0.0.1:11434)"));
        WizardTargetsStepViewModel vm = null!;
        vm = new WizardTargetsStepViewModel(() => mock.Object, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.AddTargetCommand.ExecuteAsync(new AddTargetRequest("gpu-box", "http://192.168.1.50:11435"));

        Assert.Equal(
            "The OllamaRouter daemon is not reachable on 127.0.0.1:11434 — go back to Step 1 and click 'Start router'.",
            vm.Message);
        Assert.DoesNotContain("actively refused", vm.Message);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #158 (AC7): the daemon endpoint in the message derives from the connection-profile
    /// base URL (the new <c>daemonBaseUrl</c> ctor seam) — a non-default profile URL
    /// (<c>http://127.0.0.1:9999/</c>) renders <c>127.0.0.1:9999</c> in the same sentence.
    /// </summary>
    [Fact]
    public async Task Step2_DaemonDownMessage_EndpointFromProfileBaseUrl()
    {
        var mock = new Mock<IOllamaRouterApiClient>();
        mock.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("actively refused it. (127.0.0.1:9999)"));
        mock.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("actively refused it. (127.0.0.1:9999)"));
        WizardTargetsStepViewModel vm = null!;
        vm = new WizardTargetsStepViewModel(
            () => mock.Object, ct => vm.RefreshAsync(ct), GatedDelay,
            daemonBaseUrl: () => new Uri("http://127.0.0.1:9999/"));

        await vm.OnStepEnteredAsync();

        Assert.Equal(
            "The OllamaRouter daemon is not reachable on 127.0.0.1:9999 — go back to Step 1 and click 'Start router'.",
            vm.Message);
        await vm.OnStepLeftAsync();
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #158: direct unit test of the shared internal mapper
    /// (<c>WizardTargetErrors.MapAddTarget</c> — the single source of truth Step 3 reuses):
    /// transport → the daemon-down message (default endpoint); the 409 / 422 / 400 / other-API
    /// arms and the non-http non-api fallback are byte-identical to pre-task #158.
    /// </summary>
    [Fact]
    public void WizardTargetErrors_MapAddTarget_Transport_IsDaemonDownMessage_AndApiArmsUnchanged()
    {
        const string expected =
            "The OllamaRouter daemon is not reachable on 127.0.0.1:11434 — go back to Step 1 and click 'Start router'.";

        // Transport → the SHARED daemon-down message (the raw leak is gone).
        Assert.Equal(expected, WizardTargetErrors.MapAddTarget(new System.Net.Http.HttpRequestException("actively refused it. (127.0.0.1:11434)")));
        // A non-default daemon base URL flows through the optional parameter.
        Assert.Equal(
            "The OllamaRouter daemon is not reachable on 127.0.0.1:9999 — go back to Step 1 and click 'Start router'.",
            WizardTargetErrors.MapAddTarget(
                new System.Net.Http.HttpRequestException("nope"), daemonBaseUrl: new Uri("http://127.0.0.1:9999/")));

        // API arms — byte-identical to pre-task #158.
        Assert.Equal("Target already exists.", WizardTargetErrors.MapAddTarget(new OllamaRouterApiException(HttpStatusCode.Conflict, "dup")));
        Assert.Equal("unreachable — fix Step 1 and retry", WizardTargetErrors.MapAddTarget(new OllamaRouterApiException(HttpStatusCode.UnprocessableEntity, "nope")));
        Assert.Equal("bad request body", WizardTargetErrors.MapAddTarget(new OllamaRouterApiException(HttpStatusCode.BadRequest, "bad request body")));
        Assert.Equal("Router error 500: boom", WizardTargetErrors.MapAddTarget(new OllamaRouterApiException(HttpStatusCode.InternalServerError, "boom")));

        // The non-http non-api fallback is the plain exception message.
        Assert.Equal("kaboom", WizardTargetErrors.MapAddTarget(new InvalidOperationException("kaboom")));
    }

    /// <summary>
    /// Task #151: the fleet table EXCLUDES the Cloud pseudo-target row (the server always
    /// appends it last) and maps the hardware columns (GPU name / VRAM / bandwidth) from
    /// <c>GET /api/router/fleet</c> — including the empty-text form for nodes without a
    /// hardware profile.
    /// </summary>
    [Fact]
    public async Task Step2_Refresh_ExcludesCloudRow_MapsHardwareColumns()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([
                new RouterTargetItem(
                    "gpu-box", "http://192.168.1.50:11435", true, "Online", 15, "remote",
                    new RouterTargetHardware(
                        "nvidia", ["GPU 0"], 24576, 4, null,
                        GpuName: "NVIDIA GeForce RTX 4090", BandwidthGBs: 1.5),
                    new RouterTargetExecution(0, null, "none")),
                new RouterTargetItem(
                    "cpu-node", "http://cpu:11435", true, "Offline", null, "remote",
                    new RouterTargetHardware("none", [], null, 1, null),
                    new RouterTargetExecution(0, null, "none")),
                // The server ALWAYS appends this last — the view model must drop it.
                new RouterTargetItem(
                    "Cloud", null, true, "Online", null, "cloud",
                    new RouterTargetHardware("nvidia", [], null, 1, null),
                    new RouterTargetExecution(0, null, "none")),
            ]),
        };
        var vm = BuildTargets(fake);
        await vm.RefreshAsync();

        Assert.Equal(["gpu-box", "cpu-node"], vm.Rows.Select(r => r.Name));
        Assert.DoesNotContain(vm.Rows, r => r.Name == "Cloud");

        var gpu = vm.Rows.Single(r => r.Name == "gpu-box");
        Assert.Equal("NVIDIA GeForce RTX 4090", gpu.GpuName);
        Assert.Equal(24576, gpu.VramMb);
        Assert.Equal("24576 MB", gpu.VramText);
        Assert.Equal(1.5, gpu.BandwidthGBs);
        Assert.Equal("1.5 GB/s", gpu.BandwidthText);
        Assert.Equal("Online", gpu.Liveness);
        Assert.Equal(15, gpu.LatencyMs);
        Assert.True(gpu.Enabled);

        var cpu = vm.Rows.Single(r => r.Name == "cpu-node");
        Assert.Equal(string.Empty, cpu.VramText); // no hardware → empty text (never "null")
        Assert.Equal(string.Empty, cpu.BandwidthText);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #151: the dialog-backed add command captures the request (the main app's form
    /// builds it — see <c>WizardDialogLauncher</c>), surfaces the readable success message and
    /// refreshes the fleet table so the new target appears on the next poll.
    /// </summary>
    [Fact]
    public async Task Step2_AddTarget_CapturesRequest_MessagesAndRefreshes()
    {
        var fake = new FakeRouterApiClient { FleetResponse = OneTargetFleet() };
        var vm = BuildTargets(fake);
        await vm.AddTargetCommand.ExecuteAsync(new AddTargetRequest("gpu-box", "http://192.168.1.50:11435"));

        var req = Assert.Single(fake.AddTargetCalls);
        Assert.Equal("gpu-box", req.Name);
        Assert.Equal("http://192.168.1.50:11435", req.Url);
        Assert.Equal("Target 'gpu-box' added.", vm.Message);
        Assert.Contains("GetFleetAsync", fake.Calls); // the post-add refresh
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #151: add errors map to the SHARED readable messages
    /// (<c>WizardTargetErrors.MapAddTarget</c> — the single source of truth Step 3 reuses):
    /// 409 → already exists, 422 → unreachable with the Step 1 hint.
    /// </summary>
    [Fact]
    public async Task Step2_AddTarget_ErrorMaps_ToSharedReadableMessages()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([]),
            AddTargetError = (HttpStatusCode.Conflict, "target already exists: gpu-box"),
        };
        var vm = BuildTargets(fake);
        await vm.AddTargetCommand.ExecuteAsync(new AddTargetRequest("gpu-box", "http://192.168.1.50:11435"));
        Assert.Equal("Target already exists.", vm.Message);
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

    /// <summary>Step 2 refresh with a router API error surfaces the error text (loop survives).</summary>
    [Fact]
    public async Task Step2_Refresh_ApiError_SurfacesMessage_DoesNotThrow()
    {
        var mock = new Mock<IOllamaRouterApiClient>();
        mock.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.InternalServerError, "boom"));
        WizardTargetsStepViewModel vm = null!;
        vm = new WizardTargetsStepViewModel(() => mock.Object, ct => Task.CompletedTask, GatedDelay);

        await vm.RefreshAsync(); // must not throw
        Assert.Contains("500", vm.Message);
        Assert.Contains("boom", vm.Message);
        await vm.DisposeAsync();
    }

    // ==================================================================
    //  Step 3 — WizardCloudStepViewModel (task #151)
    // ==================================================================

    /// <summary>The fake's default <c>GET /api/router/config</c> shape (mirrors
    /// <c>FakeRouterApiClient.ConfigResponse</c>) — tests re-derive the cloud section via
    /// <c>with</c>.</summary>
    private static RouterConfigResponse DefaultConfig() => new(
        [],
        new RouterCloudConfigDto(false, null, "Local", false),
        new RouterDynamicContextConfigDto(true, true, 10, 300),
        new RouterRecordingConfigDto(false, null),
        new RouterResilienceConfigDto(3, 4000),
        new RouterPricingConfigDto(null, null, null, "$", null, null, null),
        new ApiSecurityConfigDto(false, 24));

    private static RouterModelSummary Model(string name) =>
        new(name, "Dense", null, 8192, 8192, true);

    [Theory]
    [InlineData("glm-5.3-flash:cloud", true)]
    [InlineData("glm-4.6:cloud", true)]
    [InlineData("deepseek-v3.1:671b-cloud", true)]
    [InlineData("GLM-5.3-FLASH:CLOUD", true)] // OrdinalIgnoreCase
    [InlineData("llama3:8b", false)]
    [InlineData("qwen2.5:7b-instruct", false)]
    [InlineData("gemma2:2b", false)]
    [InlineData("nomodel", false)]            // no colon at all
    [InlineData("foo:", false)]               // empty tag
    public void Step3_IsCloudModelName_RecognizesCloudTagConvention(string name, bool expected)
        => Assert.Equal(expected, WizardCloudStepViewModel.IsCloudModelName(name));

    /// <summary>
    /// Task #151: on entry the step reflects the ACTUAL current cloud configuration (enabled
    /// + DefaultModel from <c>GET /api/router/config</c>) and the dropdown lists ONLY the
    /// fleet catalog's cloud models (catalog order), defaulting to the current
    /// <c>DefaultModel</c> — with zero config writes on a pure read.
    /// </summary>
    [Fact]
    public async Task Step3_Entry_PopulatesCloudModelsFromCatalog_DefaultSelection()
    {
        var fake = new FakeRouterApiClient
        {
            ConfigResponse = DefaultConfig() with
            {
                Cloud = new RouterCloudConfigDto(true, "glm-5.3-flash:cloud", "Local", true),
            },
            ModelsResponse = new RouterModelsResponse([
                Model("llama3:8b"), Model("glm-5.3-flash:cloud"),
                Model("deepseek-v3.1:671b-cloud"), Model("qwen2.5:7b"),
            ]),
        };
        var vm = new WizardCloudStepViewModel(() => fake);
        await vm.OnStepEnteredAsync();

        Assert.True(vm.CloudEnabled);
        Assert.Equal("glm-5.3-flash:cloud", vm.CloudDefaultModel);
        Assert.True(vm.CloudOverflowConfigured);
        Assert.Equal(["glm-5.3-flash:cloud", "deepseek-v3.1:671b-cloud"], vm.CloudModels);
        Assert.Equal("glm-5.3-flash:cloud", vm.SelectedCloudModel);
        Assert.False(vm.ShowEmptyModelsNote);
        Assert.False(vm.ShowConfiguredNote);
        Assert.Empty(fake.UpdateConfigCalls); // entry is a pure read
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #151: the expected state while the cloud switch is OFF (the fleet catalog only
    /// carries cloud syntheses while cloud is enabled) — empty dropdown with the empty-state
    /// note, and the configured default surfaced as a note when it is absent from the
    /// catalog.
    /// </summary>
    [Fact]
    public async Task Step3_Entry_CloudDisabled_EmptyDropdown_AndConfiguredNote()
    {
        var fake = new FakeRouterApiClient
        {
            ConfigResponse = DefaultConfig() with
            {
                Cloud = new RouterCloudConfigDto(false, "glm-5.3-flash:cloud", "Local", false),
            },
            ModelsResponse = new RouterModelsResponse([Model("llama3:8b"), Model("qwen2.5:7b")]),
        };
        var vm = new WizardCloudStepViewModel(() => fake);
        await vm.OnStepEnteredAsync();

        Assert.False(vm.CloudEnabled);
        Assert.False(vm.CloudOverflowConfigured);
        Assert.Empty(vm.CloudModels);
        Assert.True(vm.ShowEmptyModelsNote);
        Assert.Null(vm.SelectedCloudModel);
        Assert.True(vm.ShowConfiguredNote);
        Assert.Contains("glm-5.3-flash:cloud", vm.ConfiguredDefaultNote);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #161 (was task #151): the toggle flips <c>CloudEnabled</c> OPTIMISTICALLY before
    /// the wire call, PUTs the cloud <c>enabled</c> flip, reflects the (stateful fake's)
    /// response, and re-fetches the config + catalog so the dropdown re-populates/empties
    /// with the switch.
    /// </summary>
    [Fact]
    public async Task Step3_ToggleCloud_PutsEnabled_ReflectsResponse_RefetchesCatalog()
    {
        var fake = new FakeRouterApiClient
        {
            ConfigResponse = DefaultConfig() with
            {
                Cloud = new RouterCloudConfigDto(false, null, "Local", true),
            },
            ModelsResponse = new RouterModelsResponse([Model("llama3:8b"), Model("glm-5.3-flash:cloud")]),
        };
        var vm = new WizardCloudStepViewModel(() => fake);
        await vm.OnStepEnteredAsync();
        await vm.ToggleCloudCommand.ExecuteAsync(CancellationToken.None);

        var req = Assert.Single(fake.UpdateConfigCalls);
        Assert.True(req.Cloud!.Enabled);
        Assert.True(vm.CloudEnabled); // the optimistic flip was confirmed by the response
        Assert.Contains("enabled", vm.Message!);
        // The catalog GAINS the cloud syntheses with the switch: a post-toggle refetch
        // repopulates the dropdown.
        Assert.Equal(2, fake.Calls.Count(c => c == "GetModelsAsync")); // entry + post-toggle
        Assert.Contains("glm-5.3-flash:cloud", vm.CloudModels);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #161: a failed toggle PUT flips <c>CloudEnabled</c> optimistically and then
    /// REVERTS it to the previous value — the exact PropertyChanged sequence <c>[true, false]</c>
    /// (flip-then-revert is what distinguishes the optimistic path from the old no-flip
    /// behavior) — and surfaces the error without throwing.
    /// </summary>
    [Fact]
    public async Task Step3_ToggleCloud_ApiError_RevertsOptimisticFlip_NoThrow()
    {
        var fake = new FakeRouterApiClient
        {
            ConfigResponse = DefaultConfig() with
            {
                Cloud = new RouterCloudConfigDto(false, null, "Local", true),
            },
            UpdateConfigError = (HttpStatusCode.InternalServerError, "boom"),
        };
        var vm = new WizardCloudStepViewModel(() => fake);
        await vm.OnStepEnteredAsync();

        var sequence = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WizardCloudStepViewModel.CloudEnabled))
            {
                sequence.Add(vm.CloudEnabled);
            }
        };
        await vm.ToggleCloudCommand.ExecuteAsync(CancellationToken.None); // must not throw

        Assert.Equal([true, false], sequence); // optimistic flip, then the gated revert
        Assert.False(vm.CloudEnabled); // reverted — the PUT failed
        Assert.Contains("500", vm.Error!);
        Assert.Contains("boom", vm.Error);
        Assert.Empty(fake.UpdateConfigCalls);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #161: a connection failure (transport-level) reverts the optimistic flip to the
    /// previous value with the same flip-then-revert sequence and the existing
    /// "Connection failed" error wording.
    /// </summary>
    [Fact]
    public async Task Step3_ToggleCloud_ConnectionError_RevertsOptimisticFlip_NoThrow()
    {
        var fake = new FakeRouterApiClient
        {
            ConfigResponse = DefaultConfig() with
            {
                Cloud = new RouterCloudConfigDto(false, null, "Local", true),
            },
        };
        var vm = new WizardCloudStepViewModel(() => fake);
        await vm.OnStepEnteredAsync();
        // Armed AFTER the entry refresh (the one-shot exception is consumed by the next
        // wire call — the toggle's PUT).
        fake.NextCallException = new HttpRequestException("connection reset");

        var sequence = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WizardCloudStepViewModel.CloudEnabled))
            {
                sequence.Add(vm.CloudEnabled);
            }
        };
        await vm.ToggleCloudCommand.ExecuteAsync(CancellationToken.None); // must not throw

        Assert.Equal([true, false], sequence); // optimistic flip, then the gated revert
        Assert.False(vm.CloudEnabled); // reverted to the previous value
        Assert.StartsWith("Connection failed", vm.Error!);
        Assert.Empty(fake.UpdateConfigCalls);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #161: the optimistic flip strictly PRECEDES the wire call — at the moment the
    /// fake receives the PUT, the request already carries the new value AND the view-model
    /// state already shows it.
    /// </summary>
    [Fact]
    public async Task Step3_ToggleCloud_FlipsBeforeTheWireCall()
    {
        var fake = new FakeRouterApiClient
        {
            ConfigResponse = DefaultConfig() with
            {
                Cloud = new RouterCloudConfigDto(false, null, "Local", true),
            },
        };
        var vm = new WizardCloudStepViewModel(() => fake);
        await vm.OnStepEnteredAsync();

        (bool RequestEnabled, bool StateAtCall)? captured = null;
        fake.UpdateConfigRequestObserver = req =>
            captured = (req.Cloud?.Enabled ?? false, vm.CloudEnabled);
        await vm.ToggleCloudCommand.ExecuteAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.True(captured!.Value.RequestEnabled, "the PUT must carry the flipped value");
        Assert.True(captured.Value.StateAtCall, "the state must already be flipped at the wire call");
        Assert.True(vm.CloudEnabled);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #161: while the toggle is in flight (the fake holds the wire call), the
    /// optimistic flip is visible AND the command is gated by <c>CanAct</c>/
    /// <c>IsBusy</c> (the auto-disable source of the view's interactive CheckBox); after
    /// completion the command re-enables with the confirmed state.
    /// </summary>
    [Fact]
    public async Task Step3_ToggleCloud_BusyWhileInFlight_CommandGated()
    {
        var fake = new FakeRouterApiClient
        {
            ConfigResponse = DefaultConfig() with
            {
                Cloud = new RouterCloudConfigDto(false, null, "Local", true),
            },
        };
        var blocker = new TaskCompletionSource();
        fake.UpdateConfigBlock = blocker.Task;
        var vm = new WizardCloudStepViewModel(() => fake);
        await vm.OnStepEnteredAsync();

        Assert.True(vm.ToggleCloudCommand.CanExecute(null));
        var running = vm.ToggleCloudCommand.ExecuteAsync(CancellationToken.None);
        // In flight: the optimistic flip is visible before the response, and the command
        // is gated (IsBusy → CanAct false → the CheckBox auto-disables).
        Assert.False(running.IsCompleted, "the in-flight wire call must keep the command running");
        Assert.True(vm.CloudEnabled);
        Assert.False(vm.ToggleCloudCommand.CanExecute(null));

        blocker.SetResult();
        await running;
        Assert.True(vm.ToggleCloudCommand.CanExecute(null));
        Assert.True(vm.CloudEnabled);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #151: selecting a different cloud model PUTs the cloud <c>defaultModel</c> (the
    /// current switch preserved), reflects the success and updates the persisted default.
    /// </summary>
    [Fact]
    public async Task Step3_ModelChange_PutsDefaultModel()
    {
        var fake = new FakeRouterApiClient
        {
            ConfigResponse = DefaultConfig() with
            {
                Cloud = new RouterCloudConfigDto(true, "glm-5.3-flash:cloud", "Local", true),
            },
            ModelsResponse = new RouterModelsResponse(
                [Model("glm-5.3-flash:cloud"), Model("deepseek-v3.1:671b-cloud")]),
        };
        var vm = new WizardCloudStepViewModel(() => fake);
        await vm.OnStepEnteredAsync();
        // The ComboBox drives SelectedCloudModel BEFORE its SelectionChanged handler runs
        // the command — mirror that order so the command sees the user's new selection.
        vm.SelectedCloudModel = "deepseek-v3.1:671b-cloud";
        await vm.SelectCloudModelCommand.ExecuteAsync("deepseek-v3.1:671b-cloud");

        var req = Assert.Single(fake.UpdateConfigCalls);
        Assert.Equal("deepseek-v3.1:671b-cloud", req.Cloud!.DefaultModel);
        Assert.True(req.Cloud.Enabled); // the current switch is preserved on the wire
        Assert.Equal("deepseek-v3.1:671b-cloud", vm.CloudDefaultModel);
        Assert.Equal("deepseek-v3.1:671b-cloud", vm.SelectedCloudModel); // kept (no revert on success)
        Assert.Contains("set to 'deepseek-v3.1:671b-cloud'", vm.Message!);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #151: a failed model-selection PUT surfaces the error and REVERTS the dropdown
    /// to the previous persisted default (the UI never desyncs from the config).
    /// </summary>
    [Fact]
    public async Task Step3_ModelChange_ApiError_RevertsSelection()
    {
        var fake = new FakeRouterApiClient
        {
            ConfigResponse = DefaultConfig() with
            {
                Cloud = new RouterCloudConfigDto(true, "glm-5.3-flash:cloud", "Local", true),
            },
            ModelsResponse = new RouterModelsResponse(
                [Model("glm-5.3-flash:cloud"), Model("deepseek-v3.1:671b-cloud")]),
            UpdateConfigError = (HttpStatusCode.InternalServerError, "boom"),
        };
        var vm = new WizardCloudStepViewModel(() => fake);
        await vm.OnStepEnteredAsync();
        // The ComboBox drives SelectedCloudModel BEFORE its SelectionChanged handler runs
        // the command — mirror that order so the revert is a real state change to observe.
        vm.SelectedCloudModel = "deepseek-v3.1:671b-cloud";
        await vm.SelectCloudModelCommand.ExecuteAsync("deepseek-v3.1:671b-cloud"); // must not throw

        Assert.Contains("500", vm.Error!);
        Assert.Equal("glm-5.3-flash:cloud", vm.SelectedCloudModel); // reverted to the previous default
        Assert.Equal("glm-5.3-flash:cloud", vm.CloudDefaultModel);
        Assert.Empty(fake.UpdateConfigCalls);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #151: with the router DOWN, the entry refresh surfaces the readable
    /// connection-failed error and does not throw (the refresh is awaited by the root
    /// navigation command — an escaping exception would crash the wizard).
    /// </summary>
    [Fact]
    public async Task Step3_RouterDown_RefreshMessage_NoThrow()
    {
        var mock = new Mock<IOllamaRouterApiClient>();
        mock.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("connection refused"));
        var vm = new WizardCloudStepViewModel(() => mock.Object);

        await vm.RefreshAsync(); // must not throw
        Assert.Contains("Connection failed — start the router in Step 1 first.", vm.Error!);
        Assert.Empty(vm.CloudModels);
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #151: the dialog-backed provider add captures the request, names the Step 2
    /// fleet table in the success message and re-fetches the config + catalog (the catalog
    /// can grow with the new provider's models).
    /// </summary>
    [Fact]
    public async Task Step3_AddTarget_CapturesRequest_MessageNamesStep2_AndRefreshesCatalog()
    {
        var fake = new FakeRouterApiClient
        {
            ConfigResponse = DefaultConfig() with
            {
                Cloud = new RouterCloudConfigDto(true, null, "Local", true),
            },
            ModelsResponse = new RouterModelsResponse([Model("glm-5.3-flash:cloud")]),
        };
        var vm = new WizardCloudStepViewModel(() => fake);
        await vm.OnStepEnteredAsync();
        var modelsCallsBefore = fake.Calls.Count(c => c == "GetModelsAsync");

        await vm.AddTargetCommand.ExecuteAsync(new AddTargetRequest(
            "OpenRouter", "https://openrouter.ai/api", Type: "openai", ApiKey: "sk-123", Provider: "OpenRouter"));

        var req = Assert.Single(fake.AddTargetCalls);
        Assert.Equal("OpenRouter", req.Name);
        Assert.Equal("https://openrouter.ai/api", req.Url);
        Assert.Contains("Provider 'OpenRouter' added", vm.Message!);
        Assert.Contains("Step 2", vm.Message);
        Assert.Equal(modelsCallsBefore + 1, fake.Calls.Count(c => c == "GetModelsAsync")); // post-add refetch
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #163 (AC3 — the Step 3 audit arm): repeated <c>RefreshAsync</c> calls (the
    /// entry + command-side post-toggle/post-add refresh shape; no poller), a
    /// <c>ToggleCloudCommand</c> execution (its post-toggle refetch included) and a step
    /// re-entry never accumulate in <see cref="WizardCloudStepViewModel.CloudModels"/> — each
    /// catalog cloud model appears EXACTLY ONCE at every settle point (the Clear()/Add
    /// rebuild is marshaled to the UI thread inside the single-writer gate — the
    /// FleetTargetsViewModel pattern).
    /// </summary>
    [Fact]
    public async Task Step3_Cloud_RefreshFlood_Toggle_AndReentry_CloudModelsNeverDuplicate()
    {
        var fake = new FakeRouterApiClient
        {
            ConfigResponse = DefaultConfig() with
            {
                Cloud = new RouterCloudConfigDto(true, "glm-5.3-flash:cloud", "Local", true),
            },
            ModelsResponse = new RouterModelsResponse([
                Model("llama3:8b"), Model("glm-5.3-flash:cloud"),
                Model("deepseek-v3.1:671b-cloud"), Model("qwen2.5:7b"),
            ]),
        };
        var vm = new WizardCloudStepViewModel(() => fake);

        await vm.OnStepEnteredAsync();
        // 8 consecutive command-side refreshes — the dropdown is REBUILT, never accumulated.
        for (var i = 0; i < 8; i++)
        {
            await vm.RefreshAsync();
            Assert.Equal(["glm-5.3-flash:cloud", "deepseek-v3.1:671b-cloud"], vm.CloudModels);
        }

        // A toggle (with its post-toggle catalog refetch) — still exactly once each.
        await vm.ToggleCloudCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(["glm-5.3-flash:cloud", "deepseek-v3.1:671b-cloud"], vm.CloudModels);

        // A re-entry (OnStepEnteredAsync = RefreshAsync) — still exactly once each.
        await vm.OnStepEnteredAsync();
        Assert.Equal(["glm-5.3-flash:cloud", "deepseek-v3.1:671b-cloud"], vm.CloudModels);
        await vm.DisposeAsync();
    }

    // ==================================================================
    //  Step 4 — WizardBenchmarkStepViewModel (renumbered by task #151)
    // ==================================================================

    private static Mock<IOllamaRouterApiClient> MockClient()
    {
        var mock = new Mock<IOllamaRouterApiClient>();
        // Q2 (task #152): the standalone hardware fetch is no longer Step-4-consumed — this
        // setup stays as a harmless loose-mock default.
        mock.Setup(c => c.GetFleetHardwareAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterFleetHardwareResponse([]));
        // Task #152: the candidate plan loads once per step entry — a small non-empty default
        // plan (one target, one selected candidate) so the Step-4 Run flow has something to
        // benchmark; tests override with their own setup.
        mock.Setup(c => c.GetBenchmarkCandidatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultCandidates());
        mock.Setup(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterBenchmarkResultsResponse([]));
        return mock;
    }

    private static RouterBenchmarkCandidatesResponse DefaultCandidates() =>
        new([new RouterBenchmarkCandidateTarget(
            "Local", null,
            [new RouterBenchmarkCandidateModel(
                "llama3:8b", [UsageRole.BasicModel], "q4", "Dense",
                8_000_000_000, null, 8_192, BenchmarkCandidateFit.Assumed, 8_192)])]);

    private static RouterBenchmarkCandidateModel CandidateModel(string name, BenchmarkCandidateFit fit) =>
        new(name, [UsageRole.BasicModel], "q4", "Dense", 8_000_000_000, null, 8_192, fit, 8_192);

    private static WizardBenchmarkStepViewModel BuildBenchmark(
        Mock<IOllamaRouterApiClient> mock,
        Func<CancellationToken, Task> tick,
        Func<TimeSpan, CancellationToken, Task> delay) =>
        new(() => mock.Object, tick, delay);

    private static BenchmarkContextResult Ctx(string model, UsageRole role, int ctx, double tps, bool viable, string? reason = null) =>
        new(model, role, ctx, 100, 1_000_000_000, tps, 500.0, viable, reason);

    [Fact]
    public async Task Step3_Run_AllSelected_SendsNullRequest()
    {
        var mock = MockClient();
        mock.Setup(c => c.StartBenchmarkAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterBenchmarkRunResponse(true, ["Local", "Remote"]));
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);
        // D5: nothing unchecked → null request → the byte-identical absent-body POST.
        mock.Verify(c => c.StartBenchmarkAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["Local", "Remote"], vm.QueuedTargets);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_Run_UncheckedModels_SendPerTargetExclusions()
    {
        var mock = MockClient();
        mock.Setup(c => c.GetBenchmarkCandidatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new RouterBenchmarkCandidatesResponse(
            [
                new RouterBenchmarkCandidateTarget("Local", null,
                    [CandidateModel("model-a", BenchmarkCandidateFit.Fits),
                     CandidateModel("model-b", BenchmarkCandidateFit.Fits)]),
                new RouterBenchmarkCandidateTarget("Remote", null,
                    [CandidateModel("model-a", BenchmarkCandidateFit.Assumed)]),
            ]));
        RouterBenchmarkRequest? captured = null;
        mock.Setup(c => c.StartBenchmarkAsync(It.IsAny<RouterBenchmarkRequest?>(), It.IsAny<CancellationToken>()))
            .Callback<RouterBenchmarkRequest?, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new RouterBenchmarkRunResponse(true, ["Local", "Remote"]));
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync();
        await WaitUntilAsync(() => vm.CandidateGroups.Count == 2, "both candidate groups");

        // Uncheck ONE model on ONE target only ("Remote" stays fully selected).
        vm.CandidateGroups.Single(g => g.Target == "Local").Models[0].IsSelected = false;

        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.ExcludedModels);
        Assert.Single(captured.ExcludedModels!); // only the target with an unchecked model
        Assert.Equal(["model-a"], captured.ExcludedModels["Local"].ToArray());
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_NothingSelected_RunDisabled_AndDirectExecuteNoops()
    {
        var mock = MockClient();
        var startCalls = 0;
        mock.Setup(c => c.StartBenchmarkAsync(It.IsAny<RouterBenchmarkRequest?>(), It.IsAny<CancellationToken>()))
            .Callback<RouterBenchmarkRequest?, CancellationToken>((_, _) => startCalls++)
            .ReturnsAsync(new RouterBenchmarkRunResponse(true, ["Local"]));
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync();
        await WaitUntilAsync(() => vm.CandidateGroups.Count == 1, "the candidate group");

        // Uncheck EVERY model → Run is disabled (CanExecute false).
        foreach (var row in vm.CandidateGroups[0].Models.ToList())
        {
            row.IsSelected = false;
        }
        Assert.False(vm.RunBenchmarksCommand.CanExecute(null));

        // ExecuteAsync bypasses CanExecute — the flow must still no-op with the hint message.
        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(0, startCalls);
        Assert.Equal("Select at least one candidate model to benchmark.", vm.Message);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_PollerTicks_DoNotResetUserSelections()
    {
        var mock = MockClient();
        int ticks = 0;
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, async ct =>
        {
            await vm.RefreshAsync(ct);
            Interlocked.Increment(ref ticks);
        }, SoftDelay);

        await vm.OnStepEnteredAsync();
        await WaitUntilAsync(() => vm.CandidateGroups.Count == 1, "the candidate group");

        // D7: unchecking survives the 2 s poll — the plan is never re-fetched mid-step.
        var row = vm.CandidateGroups[0].Models[0];
        row.IsSelected = false;
        var before = Volatile.Read(ref ticks);
        await WaitUntilAsync(() => Volatile.Read(ref ticks) >= before + 2, "two more poller ticks");

        Assert.False(row.IsSelected); // same object, still unchecked
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_StepReEntry_ReDefaultsAllSelected()
    {
        var mock = MockClient();
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync();
        await WaitUntilAsync(() => vm.CandidateGroups.Count == 1, "the first-entry candidate group");
        var fetchesBefore = mock.Invocations.Count(i => i.Method.Name == nameof(IOllamaRouterApiClient.GetBenchmarkCandidatesAsync));

        vm.CandidateGroups[0].Models[0].IsSelected = false;
        await vm.OnStepLeftAsync();
        await vm.OnStepEnteredAsync(); // fresh entry = fresh run scope (D7)

        // The entry re-fetches the plan and re-defaults EVERY checkbox to selected.
        await WaitUntilAsync(
            () => mock.Invocations.Count(i => i.Method.Name == nameof(IOllamaRouterApiClient.GetBenchmarkCandidatesAsync)) > fetchesBefore
                && vm.CandidateGroups.Count == 1
                && vm.CandidateGroups[0].Models.Count > 0
                && vm.CandidateGroups[0].Models.All(m => m.IsSelected),
            "the re-entry candidate re-fetch to re-default the selection");
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_CandidatesFetchFailure_RunDisabled_AndErrorShown()
    {
        var mock = MockClient();
        mock.Setup(c => c.GetBenchmarkCandidatesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.InternalServerError, "boom"));
        int ticks = 0;
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, async ct =>
        {
            await vm.RefreshAsync(ct);
            Interlocked.Increment(ref ticks);
        }, SoftDelay);

        await vm.OnStepEnteredAsync();
        await WaitUntilAsync(() => vm.Error is not null && vm.Error!.Contains("boom"),
            "the candidate-load error to surface");
        Assert.Empty(vm.CandidateGroups);
        Assert.False(vm.RunBenchmarksCommand.CanExecute(null));

        // The flag stays false: a later tick RETRIES the fetch.
        var before = Volatile.Read(ref ticks);
        await WaitUntilAsync(() => Volatile.Read(ref ticks) > before, "another poller tick");
        Assert.True(
            mock.Invocations.Count(i => i.Method.Name == nameof(IOllamaRouterApiClient.GetBenchmarkCandidatesAsync)) >= 2,
            "a failed candidate load must be retried on the next tick");
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_Candidates_LoadOnStepEntry_PrunedDefaultsUnselected_OthersSelected_AndHardwareAnnotated()
    {
        var mock = MockClient();
        mock.Setup(c => c.GetBenchmarkCandidatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new RouterBenchmarkCandidatesResponse(
            [
                new RouterBenchmarkCandidateTarget(
                    "Local",
                    new NodeHardwareProfile(
                        "local", "linux", "x64", false, 32_000_000_000, SystemRamType.Ddr5,
                        [new GpuDeviceProfile("nvidia", "RTX 4090", 24_576, "GDDR6X", 384, 1008)],
                        24_576, 1008),
                    [CandidateModel("llama3:8b", BenchmarkCandidateFit.Fits),
                     new RouterBenchmarkCandidateModel(
                         "qwen3.8:27b", [UsageRole.AgenticChat], "q4", "Dense",
                         27_800_000_000, null, 131_072, BenchmarkCandidateFit.Pruned, 0)]),
                // Task #164: the verbatim 2026-11-22 Strix Halo unified profile (93379 MB
                // LPDDR5X, 341.2 GB/s, iGPU 512 MB) — the header must show the fast memory
                // the sizing computation uses, NOT the 512 MB iGPU VRAM.
                new RouterBenchmarkCandidateTarget(
                    "Unified",
                    new NodeHardwareProfile(
                        "unified", "linux", "x64", true, 93_379, SystemRamType.LpDdr5X,
                        [new GpuDeviceProfile("AMD", "Strix Halo", 512, "Unknown", 0, 341.2)],
                        93_379, 341.2),
                    [CandidateModel("llama3:8b", BenchmarkCandidateFit.Fits)]),
                new RouterBenchmarkCandidateTarget("Remote", null,
                    [new RouterBenchmarkCandidateModel(
                        "qwen3.8:27b", [UsageRole.AgenticChat], "q4", "Dense",
                        27_800_000_000, null, 131_072, BenchmarkCandidateFit.Assumed, 131_072)]),
            ]));
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync(); // first tick loads the plan
        await WaitUntilAsync(() => vm.CandidateGroups.Count == 3, "all candidate groups");

        // The profiled groups carry the hardware annotation (task #164: the header is the
        // effective fast memory — TotalFastMemoryMB — that the fit computation consumes);
        // the unknown-profile group carries the note instead.
        var local = vm.CandidateGroups.Single(g => g.Target == "Local");
        Assert.Equal("RTX 4090", local.GpuName);
        Assert.Equal("Fast memory: 24 GB", local.VramText);
        Assert.Equal("BW: 1032192 MB/s", local.BandwidthText); // 1008 GB/s × 1024
        Assert.Null(local.Note);
        Assert.Equal("won't fit this node", local.Models.Single(m => m.Model == "qwen3.8:27b").FitText);
        Assert.Contains("8192", local.Models.Single(m => m.Model == "llama3:8b").FitText);
        var unified = vm.CandidateGroups.Single(g => g.Target == "Unified");
        Assert.Equal("Strix Halo", unified.GpuName);
        Assert.Equal("Fast memory: 91.2 GB", unified.VramText); // 93379 MB — NOT "512 MB" iGPU
        Assert.Equal("BW: 349389 MB/s", unified.BandwidthText); // 341.2 GB/s × 1024
        Assert.Null(unified.Note);
        var remote = vm.CandidateGroups.Single(g => g.Target == "Remote");
        Assert.Equal("No hardware profile detected", remote.Note);
        Assert.Equal(string.Empty, remote.VramText); // no profile → empty (never null)
        Assert.Equal("no profile (assumed)", remote.Models[0].FitText);
        // Task #169 (AC2): the pre-flight Pruned candidate starts UNCHECKED (no pre-checked
        // oversized model on a small node); every Fits/Assumed row starts selected.
        Assert.True(local.Models.Single(m => m.Model == "llama3:8b").IsSelected);      // Fits
        Assert.False(local.Models.Single(m => m.Model == "qwen3.8:27b").IsSelected);  // Pruned
        Assert.True(unified.Models.Single(m => m.Model == "llama3:8b").IsSelected);   // Fits
        Assert.True(remote.Models.Single(m => m.Model == "qwen3.8:27b").IsSelected);  // Assumed
        Assert.True(vm.RunBenchmarksCommand.CanExecute(null));
        await vm.DisposeAsync();
    }

    // ==================================================================
    //  Task #169: curated-catalog class metadata + Pruned default-unselected
    // ==================================================================

    /// <summary>
    /// The 8 GB node profile of the curated-catalog wizard tests (task #169, item 8): 8 GiB
    /// fast memory (discrete VRAM) + 64 GiB system RAM, non-unified — the MoE full-weights
    /// residency tier is fast + RAM. The ladder verdicts below are the COMPUTED TRUTH of
    /// <see cref="CandidateSizingCalculator"/> (the shared derivation) pinned per the #166
    /// anchors: budget = 0.92 × 8_192 = 7_536.64 MiB; Qwen3.6:code (active 3_051.8 + KV) is
    /// the only MoE whose 256k decode exceeds the budget (max rung 196_608); the three
    /// dense/hybrid strict models are weights-pruned at every rung.
    /// </summary>
    private static NodeHardwareProfile CuratedEightGbProfile() => new(
        "local", "linux", "x64", false, 65_536, SystemRamType.Ddr5,
        [new GpuDeviceProfile("nvidia", "RTX 4080", 8_192, "GDDR6X", 256, 717)],
        8_192, 717.0);

    /// <summary>
    /// The one-target curated 7-model candidate plan on the 8 GB node (task #169, item 8):
    /// every field of each row mirrors <see cref="CandidateModelCatalogOptions.DefaultModels"/>
    /// and the (Fit, MaxFitsContext) verdict is RECOMPUTED here via
    /// <see cref="CandidateSizingCalculator"/> over the default ladder — the computed truth,
    /// never a stale hand-pinned number.
    /// </summary>
    private static RouterBenchmarkCandidatesResponse CuratedSevenModelPlan()
    {
        var profile = CuratedEightGbProfile();
        var ladder = new CandidateModelCatalogOptions().BenchmarkContextLadder;
        var rows = new List<RouterBenchmarkCandidateModel>();
        foreach (var model in CandidateModelCatalogOptions.DefaultModels)
        {
            var anySurvived = false;
            var maxSurviving = 0;
            foreach (var ctx in ladder)
            {
                var status = CandidateSizingCalculator.Evaluate(model, ctx, profile).Status;
                if (status is MemoryFitStatus.Fits or MemoryFitStatus.Assumed)
                {
                    anySurvived = true;
                    if (ctx <= model.NativeContext && ctx > maxSurviving)
                    {
                        maxSurviving = ctx;
                    }
                }
            }

            var fit = anySurvived ? BenchmarkCandidateFit.Fits : BenchmarkCandidateFit.Pruned;
            rows.Add(new RouterBenchmarkCandidateModel(
                model.Name, model.Roles, model.Quantization, model.Architecture,
                model.TotalParams, model.ActiveParams, model.NativeContext,
                fit, maxSurviving,
                model.ModelClass.ToString(), model.AllowCpuOffload, model.EstimatedWeightsMB));
        }

        return new RouterBenchmarkCandidatesResponse(
            [new RouterBenchmarkCandidateTarget("Local", profile, rows)]);
    }

    [Fact]
    public async Task Step3_CuratedSevenModelPlan_EightGbNode_ClassSizeRolesAndPrunedDefaults()
    {
        // Task #169 (AC3 + AC2 headline): on an 8 GB node the curated 7-model plan renders the
        // class/offload/size annotation + the role text per D4, and the three pre-flight
        // Pruned models (the 70+ GB Ornith included) start UNCHECKED — no pre-checked
        // oversized model on an 8 GB laptop.
        var mock = MockClient();
        mock.Setup(c => c.GetBenchmarkCandidatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CuratedSevenModelPlan());
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync();
        await WaitUntilAsync(() => vm.CandidateGroups.Count == 1 && vm.CandidateGroups[0].Models.Count == 7,
            "the curated 7-model candidate group");
        var rows = vm.CandidateGroups[0].Models;

        // The node is the 8 GB one (task #164: the header is the fast memory).
        Assert.Equal("Fast memory: 8 GB", vm.CandidateGroups[0].VramText);

        // ClassText (D4): "{modelClass} · {offload} · ~{n.n} GB" from the curated anchors.
        Assert.Equal(
            [
                "MoE · CPU offload OK · ~23.3 GB",   // Flint:q4      (23_859 MiB)
                "MoE · CPU offload OK · ~39.4 GB",   // Flint:q8      (40_346 MiB)
                "MoE · CPU offload OK · ~22.0 GB",   // Qwen3.6:q4    (22_528 MiB)
                "MoE · CPU offload OK · ~40.0 GB",   // Qwen3.6:code  (40_960 MiB)
                "Expert · VRAM only · ~12.0 GB",     // Qwen3.8:iq3   (12_288 MiB)
                "Expert · VRAM only · ~18.5 GB",     // Qwen3.8:latest(18_944 MiB)
                "Expert · VRAM only · ~69.9 GB",     // Ornith        (71_583 MiB)
            ],
            rows.Select(r => r.ClassText).ToArray());

        // RolesText (D4): 5-role MoE = "All roles (primary: X)" (primary = Roles[0]);
        // the strict models keep the comma join in catalog order.
        Assert.Equal(
            [
                "All roles (primary: JuniorDeveloper)",
                "All roles (primary: JuniorDeveloper)",
                "All roles (primary: BasicModel)",
                "All roles (primary: BasicModel)",
                "AgenticChat, SoftwareArchitect, SeniorDeveloper",
                "AgenticChat, SoftwareArchitect, SeniorDeveloper",
                "SoftwareArchitect, SeniorDeveloper",
            ],
            rows.Select(r => r.RolesText).ToArray());

        // FitText + IsSelected from the COMPUTED 8 GB verdicts (pinned computed truth):
        // the three MoEs fit (Qwen3.6:code capped at 196_608); the three strict models are
        // weights-pruned → unchecked.
        Assert.Equal(
            [
                "ctx ≤ 262144", "ctx ≤ 262144", "ctx ≤ 262144", "ctx ≤ 196608",
                "won't fit this node", "won't fit this node", "won't fit this node",
            ],
            rows.Select(r => r.FitText).ToArray());
        Assert.Equal(
            [true, true, true, true, false, false, false],
            rows.Select(r => r.IsSelected).ToArray());
        Assert.False(rows.Single(r => r.Model == "jetelain/Ornith-1.5-122b:latest").IsSelected);
        Assert.True(vm.RunBenchmarksCommand.CanExecute(null));
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_PrunedCandidate_ExcludedByDefault_AndUserOverrideHonored()
    {
        // Task #169 (AC2, item 9): a Pruned row travels in the run-scoped excludedModels map
        // while unchecked (exactly like a user-unchecked row); checking it force-includes it
        // (the run-loop sizing re-prunes — no pull, only sizing-pruned rows).
        var mock = MockClient();
        mock.Setup(c => c.GetBenchmarkCandidatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new RouterBenchmarkCandidatesResponse(
            [
                new RouterBenchmarkCandidateTarget("Local", null,
                    [CandidateModel("fits-model", BenchmarkCandidateFit.Fits),
                     new RouterBenchmarkCandidateModel(
                         "pruned-model", [UsageRole.BasicModel], "q4", "Dense",
                         124_600_000_000, 10_000_000_000, 262_144, BenchmarkCandidateFit.Pruned, 0)]),
            ]));
        RouterBenchmarkRequest? captured = null;
        mock.Setup(c => c.StartBenchmarkAsync(It.IsAny<RouterBenchmarkRequest?>(), It.IsAny<CancellationToken>()))
            .Callback<RouterBenchmarkRequest?, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new RouterBenchmarkRunResponse(true, ["Local"]));
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync();
        await WaitUntilAsync(() => vm.CandidateGroups.Count > 0 && vm.CandidateGroups[0].Models.Count == 2, "both candidate rows");

        // Default state: the Pruned row is unchecked → it is excluded for the next run.
        Assert.True(vm.CandidateGroups[0].Models.Single(m => m.Model == "fits-model").IsSelected);
        Assert.False(vm.CandidateGroups[0].Models.Single(m => m.Model == "pruned-model").IsSelected);
        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);
        Assert.NotNull(captured);
        Assert.NotNull(captured!.ExcludedModels);
        Assert.Equal(["pruned-model"], captured.ExcludedModels!["Local"].ToArray());

        // User override: check the Pruned row → CanRun stays true and the re-run sends it
        // UN-excluded (the map is now empty → the absent-body null request of D5).
        vm.CandidateGroups[0].Models.Single(m => m.Model == "pruned-model").IsSelected = true;
        Assert.True(vm.RunBenchmarksCommand.CanExecute(null));
        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);
        var secondRequest = mock.Invocations
            .Where(i => i.Method.Name == nameof(IOllamaRouterApiClient.StartBenchmarkAsync))
            .Last().Arguments[0] as RouterBenchmarkRequest;
        Assert.Null(secondRequest); // nothing unchecked anymore → null request (D5)
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_AllCandidatesPruned_RunDisabled_UntilUserChecksOne()
    {
        // Task #169 (AC2, item 10): a node where EVERY candidate is Pruned starts with zero
        // selected rows — the Run command is disabled and a direct ExecuteAsync no-ops with
        // the existing "select at least one" hint (zero StartBenchmarkAsync calls); checking
        // ONE row (force-include) flips CanExecute to true.
        var mock = MockClient();
        var startCalls = 0;
        mock.Setup(c => c.GetBenchmarkCandidatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new RouterBenchmarkCandidatesResponse(
            [
                new RouterBenchmarkCandidateTarget("Local", null,
                    [CandidateModel("pruned-a", BenchmarkCandidateFit.Pruned),
                     CandidateModel("pruned-b", BenchmarkCandidateFit.Pruned)]),
            ]));
        mock.Setup(c => c.StartBenchmarkAsync(It.IsAny<RouterBenchmarkRequest?>(), It.IsAny<CancellationToken>()))
            .Callback<RouterBenchmarkRequest?, CancellationToken>((_, _) => startCalls++)
            .ReturnsAsync(new RouterBenchmarkRunResponse(true, ["Local"]));
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync();
        await WaitUntilAsync(() => vm.CandidateGroups.Count > 0 && vm.CandidateGroups[0].Models.Count == 2, "both pruned rows");

        Assert.All(vm.CandidateGroups[0].Models, m => Assert.False(m.IsSelected));
        Assert.False(vm.RunBenchmarksCommand.CanExecute(null));
        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None); // ExecuteAsync bypass
        Assert.Equal(0, startCalls);
        Assert.Equal("Select at least one candidate model to benchmark.", vm.Message);

        // Checking one row (the user's force-include) re-enables Run.
        vm.CandidateGroups[0].Models[0].IsSelected = true;
        Assert.True(vm.RunBenchmarksCommand.CanExecute(null));
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_Reentry_WithPrunedRows_ReDefaultsPrunedUnselected_OthersSelected()
    {
        // Task #169 (AC2, item 11): entry → leave → entry re-defaults the selection WITH the
        // Pruned semantics (extends the all-Fits D7 contract pinned by
        // Step4_Reentry_CandidatePlanReloadedOnce_CheckboxesReDefaulted): the Pruned row is
        // unchecked again, the Fits row re-selected, and the user's mid-run toggles on BOTH
        // rows are discarded.
        var mock = MockClient();
        mock.Setup(c => c.GetBenchmarkCandidatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new RouterBenchmarkCandidatesResponse(
            [
                new RouterBenchmarkCandidateTarget("Local", null,
                    [CandidateModel("fits-model", BenchmarkCandidateFit.Fits),
                     CandidateModel("pruned-model", BenchmarkCandidateFit.Pruned)]),
            ]));
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync();
        await WaitUntilAsync(() => vm.CandidateGroups.Count > 0 && vm.CandidateGroups[0].Models.Count == 2, "both candidate rows (first entry)");

        // Mid-run scope: check the Pruned row AND uncheck the Fits row (both must be discarded).
        vm.CandidateGroups[0].Models.Single(m => m.Model == "pruned-model").IsSelected = true;
        vm.CandidateGroups[0].Models.Single(m => m.Model == "fits-model").IsSelected = false;
        await vm.OnStepLeftAsync();
        await vm.OnStepEnteredAsync(); // fresh entry = fresh run scope (task #152 D7)

        // Guard Count > 0: the fresh entry's rebuild Clear()-ed the groups, so the unguarded
        // [0] indexer throws while the repopulation is still landing (test-host-only race).
        await WaitUntilAsync(
            () => vm.CandidateGroups.Count > 0
                && vm.CandidateGroups[0].Models.Single(m => m.Model == "pruned-model").IsSelected == false
                && vm.CandidateGroups[0].Models.Single(m => m.Model == "fits-model").IsSelected,
            "the re-defaulted selection (Pruned unchecked, Fits selected)");
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_MixedResults_UpdatesRowsAndViability_AndKeepsPolling()
    {
        var mock = MockClient();
        var now = DateTimeOffset.UtcNow;
        mock.Setup(c => c.StartBenchmarkAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterBenchmarkRunResponse(true, ["Local", "Remote"]));
        // Task #152 (Q2): the candidate plan carries the hardware — the old per-tick
        // GetFleetHardwareAsync fetch is gone; the assertions below are retargeted to the
        // candidate groups' hardware annotation.
        mock.Setup(c => c.GetBenchmarkCandidatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new RouterBenchmarkCandidatesResponse(
            [
                new RouterBenchmarkCandidateTarget(
                    "Local",
                    new NodeHardwareProfile(
                        "local", "linux", "x64", false, 32_000_000_000, SystemRamType.Ddr5,
                        [new GpuDeviceProfile("nvidia", "RTX 4090", 24_576, "GDDR6X", 384, 1008)],
                        24_576, 1008),
                    [CandidateModel("llama3:8b", BenchmarkCandidateFit.Fits)]),
                new RouterBenchmarkCandidateTarget("Remote", null,
                    [CandidateModel("qwen2.5:7b", BenchmarkCandidateFit.Assumed)]),
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
        Assert.Contains(vm.CandidateGroups, g => g.GpuName == "RTX 4090");
        Assert.Contains(vm.CandidateGroups, g => g.Note is not null && g.Note.Contains("No hardware profile"));
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

    /// <summary>
    /// Task #165 (AC4, Step 4 mirror): the results tick whose transport faults with the
    /// socket-abort class (a raw <c>SocketException(995)</c>, e.g. the daemon started then
    /// exited mid-poll) surfaces the existing <c>Connection failed: …</c> error shape and does
    /// NOT throw — the loop survives (same family as the <c>HttpRequestException</c> handling).
    /// </summary>
    [Fact]
    public async Task Step3_ResultsPoll_SocketAbortRaw_SetsConnectionFailedAndLoopSurvives()
    {
        var mock = MockClient();
        mock.Setup(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Sockets.SocketException(995));
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync(); // immediate tick hits the raw socket abort
        await WaitUntilAsync(() => vm.Error is not null, "the poll tick to surface the abort error");
        Assert.StartsWith("Connection failed:", vm.Error);
        await vm.DisposeAsync(); // disposal completes despite the faulted tick (no throw)
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

    private static RouterBenchmarkCandidatesResponse ThreeTargetCandidatePlan() =>
        new([
            new RouterBenchmarkCandidateTarget("Local", null,
                [CandidateModel("model-a", BenchmarkCandidateFit.Fits),
                 CandidateModel("model-b", BenchmarkCandidateFit.Fits)]),
            new RouterBenchmarkCandidateTarget("Remote", null,
                [CandidateModel("model-a", BenchmarkCandidateFit.Fits),
                 CandidateModel("model-b", BenchmarkCandidateFit.Assumed)]),
            new RouterBenchmarkCandidateTarget("Remote2", null,
                [CandidateModel("model-b", BenchmarkCandidateFit.Assumed)]),
        ]);

    private static RouterBenchmarkResultsResponse ThreeTargetResults()
    {
        var now = DateTimeOffset.UtcNow;
        return new RouterBenchmarkResultsResponse([
            new BenchmarkTargetResult("Local", BenchmarkRunStatus.Running, now, null, [
                Ctx("model-a", UsageRole.BasicModel, 8192, 25.0, true),
                Ctx("model-a", UsageRole.BasicModel, 32768, 15.0, false, "below-threshold"),
            ]),
            new BenchmarkTargetResult("Remote", BenchmarkRunStatus.Succeeded, now, now, [
                Ctx("model-b", UsageRole.AgenticChat, 4096, 30.0, true),
            ]),
            new BenchmarkTargetResult("Remote2", BenchmarkRunStatus.Running, now, null, [
                Ctx("model-b", UsageRole.AgenticChat, 4096, 40.0, true),
            ]),
        ]);
    }

    /// <summary>
    /// Task #163 (AC2 — the headline Step 4 dedup regression): with a 3-target candidate plan
    /// (each with ≥ 1 candidate model) and benchmark results for all 3 targets, ≥ 10
    /// consecutive poller ticks (the non-terminal rows keep the poller alive; nothing is
    /// queued → no self-stop) plus one command-side <c>ClearResultsCommand</c> interleave
    /// leave <see cref="WizardBenchmarkStepViewModel.CandidateGroups"/> with exactly ONE group
    /// per target (each model once per group) and <see cref="WizardBenchmarkStepViewModel.BenchmarkRows"/> /
    /// <see cref="WizardBenchmarkStepViewModel.ResultRows"/> with exactly ONE row per target / per
    /// (target × model × context) — no duplicates at any settle point (the rebuilds are
    /// marshaled to the UI thread inside the single-writer gate — the FleetTargetsViewModel
    /// pattern).
    /// </summary>
    [Fact]
    public async Task Step4_TickFlood_CandidateGroupsAndResultRows_NeverDuplicate()
    {
        var mock = MockClient();
        mock.Setup(c => c.GetBenchmarkCandidatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThreeTargetCandidatePlan());
        mock.Setup(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThreeTargetResults());

        int ticks = 0;
        WizardBenchmarkStepViewModel vm = null!;
        // The counter increments AFTER the tick's RefreshAsync completes, so a green
        // "ticks >= N" means the collection mutations are done (no read-during-mutation race).
        vm = BuildBenchmark(mock, async ct =>
        {
            await vm.RefreshAsync(ct);
            Interlocked.Increment(ref ticks);
        }, SoftDelay);

        await vm.OnStepEnteredAsync();
        // ≥ 10 consecutive poller ticks (soft rate; nothing queued → no self-stop).
        await WaitUntilAsync(() => Volatile.Read(ref ticks) >= 10, "10 consecutive poller ticks");

        // AC2 invariants: exactly ONE group per target (each model once per group) and exactly
        // ONE row per target / per (target × model × context). A tick may still be MID-
        // REBUILD when the test thread observes the collections (a rebuild is a
        // Clear()+Add loop), so the invariants are RETRIED via WaitUntilAsync — a genuine
        // duplicate would keep them false and the FINAL assert stays unguarded.
        bool Ac2Holds() => vm.CandidateGroups.Count == 3
            && vm.CandidateGroups.Select(g => g.Target).SequenceEqual(["Local", "Remote", "Remote2"])
            && vm.CandidateGroups.All(g => g.Models.Select(m => m.Model).Distinct().Count() == g.Models.Count)
            && vm.BenchmarkRows.Count == 3
            && vm.BenchmarkRows.Select(r => r.Target).Distinct().Count() == 3
            && vm.ResultRows.Count == 4
            && vm.ResultRows.Select(r => (r.Target, r.Model, r.NumCtx)).Distinct().Count() == 4;
        await WaitUntilAsync(Ac2Holds, "the AC2 dedup invariants to hold across 10+ ticks");
        // The flood poller is still ticking, so the FINAL re-check itself may catch a
        // mid-rebuild collection (test-host-only race) — re-verify over a short retry
        // window (a genuine duplicate keeps the invariant false and fails below).
        await AssertHoldsEventuallyAsync(Ac2Holds, "AC2: groups/rows must hold exactly once per key after 10+ ticks");

        // The command-side ClearResults interleave (the tick's rebuilds must never interleave
        // with its Clear()s — single-writer gate + UI marshaling).
        await vm.ClearResultsCommand.ExecuteAsync(CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref ticks) >= 12, "two more poller ticks after the clear");
        await vm.OnStepLeftAsync(); // awaited → the loop fully exits

        // Settle: the cleared rows are REBUILT by the ticks — again exactly once per key
        // (a final in-flight rebuild may still be landing → retried; see the first check).
        await WaitUntilAsync(Ac2Holds, "the AC2 dedup invariants to hold after the clear interleave");
        await AssertHoldsEventuallyAsync(Ac2Holds, "AC2: groups/rows must hold exactly once per key after the clear interleave");
        await vm.DisposeAsync();
    }

    /// <summary>
    /// Task #163 (AC2 — the Step 4 re-entry arm): enter → leave → enter re-fetches the
    /// candidate plan EXACTLY ONCE per entry (task #152 D7) and re-defaults EVERY checkbox to
    /// selected — the 3 groups are rebuilt, never accumulated (no duplicates, no stale
    /// unchecked state from the previous run scope).
    /// </summary>
    [Fact]
    public async Task Step4_Reentry_CandidatePlanReloadedOnce_CheckboxesReDefaulted()
    {
        var mock = MockClient();
        mock.Setup(c => c.GetBenchmarkCandidatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThreeTargetCandidatePlan());
        WizardBenchmarkStepViewModel vm = null!;
        vm = BuildBenchmark(mock, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.OnStepEnteredAsync();
        await WaitUntilAsync(() => vm.CandidateGroups.Count == 3, "all three candidate groups (first entry)");
        var fetchesAfterEntry1 = mock.Invocations.Count(i => i.Method.Name == nameof(IOllamaRouterApiClient.GetBenchmarkCandidatesAsync));
        Assert.Equal(1, fetchesAfterEntry1); // D7: the plan loaded ONCE on the entry's first tick

        vm.CandidateGroups[0].Models[0].IsSelected = false; // a user opt-out, discarded on re-entry
        await vm.OnStepLeftAsync();
        await vm.OnStepEnteredAsync(); // fresh entry = fresh run scope (D7)

        await WaitUntilAsync(
            () => mock.Invocations.Count(i => i.Method.Name == nameof(IOllamaRouterApiClient.GetBenchmarkCandidatesAsync)) == fetchesAfterEntry1 + 1
                && vm.CandidateGroups.Count == 3
                && vm.CandidateGroups.All(g => g.Models.All(m => m.IsSelected)),
            "the re-entry's single candidate re-fetch to re-default every checkbox to selected");

        Assert.Equal(["Local", "Remote", "Remote2"], vm.CandidateGroups.Select(g => g.Target));
        Assert.Equal(3, vm.CandidateGroups.Select(g => g.Target).Distinct().Count());
        await vm.OnStepLeftAsync();
        await vm.DisposeAsync();
    }

    // ==================================================================
    //  Step 5 — WizardRecommendationsStepViewModel (renumbered by task #151)
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

    /// <summary>
    /// Task #169 (AC4, item 12): a fully MoE-assigned 5-role matrix (8 GB-only fleet — the
    /// #168 dynamic role engine resolves EVERY role to a CPU-offload MoE model, node ceilings
    /// ≥ the 128k eligibility floor) renders cleanly with NO code change: the 5 stable alias
    /// rows all benchmarked with their MoE model name + non-empty node text, and the dedup
    /// model index (the matrix's <c>Models</c> dict is keyed by native model name —
    /// structurally deduplicated) shows the 2 MoE models ordinal-sorted.
    /// </summary>
    [Fact]
    public async Task Step5_FullyMoEFiveRoleMatrix_RendersAllRolesAndDeduplicatedModelIndex()
    {
        var qwen = "jetelain/Qwen3.6-35B-A3B:q4";
        var flint = "jetelain/Flint:q4";
        // Hoisted locals: the nested object initializers inside the FleetRecommendations ctor
        // arguments hit a C# parser ambiguity, so build each dictionary first.
        var roles = new Dictionary<string, RoleRecommendation>
        {
            ["basic"] = new RoleRecommendation(
                "basic", qwen, 131_072, "smallest fast general model",
                [new NodeRoleProfile("Local", 131_072, null, 38.0, 4096)]),
            ["agentic_chat"] = new RoleRecommendation(
                "agentic_chat", qwen, 131_072, "agentic chat",
                [new NodeRoleProfile("Local", 131_072, null, 30.0, 4096)]),
            ["junior_developer"] = new RoleRecommendation(
                "junior_developer", flint, 131_072, "junior developer",
                [new NodeRoleProfile("Local", 131_072, null, 28.0, 4096)]),
            ["software_architect"] = new RoleRecommendation(
                "software_architect", flint, 131_072, "software architect",
                [new NodeRoleProfile("Local", 131_072, null, 22.0, 4096)]),
            ["senior_developer"] = new RoleRecommendation(
                "senior_developer", flint, 131_072, "senior developer",
                [new NodeRoleProfile("Local", 131_072, null, 21.0, 4096)]),
        };
        var models = new Dictionary<string, ModelNodeProfile>
        {
            [qwen] = new ModelNodeProfile(
                qwen, "MoE", 262_144, "UD-Q4_K_XL",
                [new NodeRoleProfile("Local", 131_072, null, 38.0, 4096)]),
            [flint] = new ModelNodeProfile(
                flint, "MoE", 262_144, "UD-Q4_K_XL",
                [new NodeRoleProfile("Local", 131_072, null, 28.0, 4096)]),
        };
        var fake = new FakeRouterApiClient
        {
            Recommendations = new FleetRecommendations(DateTimeOffset.UtcNow, roles, models)
        };
        var vm = new WizardRecommendationsStepViewModel(() => fake);
        await vm.RefreshAsync();

        // The 5 role rows in the stable alias order — ALL benchmarked, each with its MoE
        // model name and non-empty node text (ceilings ≥ the 128k floor).
        Assert.Equal(5, vm.RoleRows.Count);
        Assert.Equal(["basic", "agentic_chat", "software_architect", "senior_developer", "junior_developer"],
            vm.RoleRows.Select(r => r.Role));
        Assert.All(vm.RoleRows, r => Assert.True(r.Benchmarked));
        Assert.Equal(qwen, vm.RoleRows.Single(r => r.Role == "basic").Model);
        Assert.Equal(qwen, vm.RoleRows.Single(r => r.Role == "agentic_chat").Model);
        Assert.Equal(flint, vm.RoleRows.Single(r => r.Role == "junior_developer").Model);
        Assert.Equal(flint, vm.RoleRows.Single(r => r.Role == "software_architect").Model);
        Assert.Equal(flint, vm.RoleRows.Single(r => r.Role == "senior_developer").Model);
        Assert.All(vm.RoleRows, r =>
        {
            Assert.NotNull(r.Nodes);
            Assert.Single(r.Nodes);
            Assert.True(r.Nodes[0].ContextCeiling >= 131_072);
            Assert.False(string.IsNullOrWhiteSpace(r.NodesText));
        });

        // The deduplicated model index: exactly the 2 MoE models, ordinal-sorted keys.
        Assert.Equal(2, vm.ModelRows.Count);
        Assert.Equal([flint, qwen], vm.ModelRows.Select(m => m.Model).ToArray());
        Assert.All(vm.ModelRows, m => Assert.Equal("MoE", m.Architecture));
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step4_Generate_ConflictShowsHintAndGoToBenchmark()
    {
        var fake = new FakeRouterApiClient { GenerateError = (HttpStatusCode.Conflict, "no benchmark data") };
        bool goTo = false;
        var vm = new WizardRecommendationsStepViewModel(() => fake, () => goTo = true);
        await vm.GenerateCommand.ExecuteAsync(CancellationToken.None);
        Assert.Contains("run the Step 4 micro-benchmark", vm.Message);
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
    /// Regression (task #103 review): with the daemon DOWN, entering Step 5 (root
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

    /// <summary>
    /// Task #163 (AC3 — the Step 5 audit arm): repeated <c>RefreshAsync</c> calls (the entry
    /// + command-side refresh shape; no poller, no gate — each rebuild is one atomic
    /// UI-marshaled action), <c>GenerateCommand</c> and TWO <c>ApplyCommand</c> executions
    /// never accumulate: <see cref="WizardRecommendationsStepViewModel.RoleRows"/> holds the 5
    /// stable alias rows exactly once, <see cref="WizardRecommendationsStepViewModel.ModelRows"/>
    /// each model exactly once and <see cref="WizardRecommendationsStepViewModel.AppliedTargets"/>
    /// each target exactly once — at every settle point.
    /// </summary>
    [Fact]
    public async Task Step5_RefreshGenerateApply_CollectionsNeverDuplicate()
    {
        var fake = new FakeRouterApiClient(); // default: empty matrix; default generate/apply responses
        var vm = new WizardRecommendationsStepViewModel(() => fake);

        await vm.OnStepEnteredAsync();
        // 8 consecutive command-side refreshes against the (empty) matrix — the 5 stable
        // alias rows are REBUILT, never accumulated.
        for (var i = 0; i < 8; i++)
        {
            await vm.RefreshAsync();
            Assert.Equal(5, vm.RoleRows.Count);
            Assert.Equal(["basic", "agentic_chat", "software_architect", "senior_developer", "junior_developer"],
                vm.RoleRows.Select(r => r.Role));
            Assert.Empty(vm.ModelRows);
        }

        // Generate (fake default: the "basic" role on Local + the llama3:8b model index).
        await vm.GenerateCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(5, vm.RoleRows.Count);
        Assert.Equal(5, vm.RoleRows.Select(r => r.Role).Distinct().Count());
        var models = vm.ModelRows.Select(m => m.Model).ToList();
        Assert.Equal(models.Count, models.Distinct().Count()); // each model exactly once
        Assert.Single(vm.ModelRows, m => m.Model == "llama3:8b");

        // Two applies: the AppliedTargets rebuild REPLACES, never accumulates.
        await vm.ApplyCommand.ExecuteAsync(CancellationToken.None);
        await vm.ApplyCommand.ExecuteAsync(CancellationToken.None);
        Assert.Single(vm.AppliedTargets);
        Assert.Equal("Local", Assert.Single(vm.AppliedTargets).Target);
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

        var error = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(conv.Convert(true, typeof(object), null, culture));
        Assert.Equal(Avalonia.Media.Color.Parse("#F87171"), error.Color);

        var ok = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(conv.Convert(false, typeof(object), null, culture));
        Assert.Equal(Avalonia.Media.Color.Parse("#10B981"), ok.Color);

        var nonBool = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(conv.Convert("bogus", typeof(object), null, culture));
        Assert.Equal(Avalonia.Media.Color.Parse("#10B981"), nonBool.Color);

        // One-way converter: ConvertBack is a no-op for bindings.
        Assert.Throws<System.NotSupportedException>(() => conv.ConvertBack(null, typeof(object), null, culture));
    }

    // ==================================================================
    //  Service lifecycle, busy-gating & Phase 3 step enhancements
    // ==================================================================

    [Fact]
    public async Task Step1_OnStepEntered_FreePort_AutoStartsRouter()
    {
        var launcher = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null),
            AfterStart = FakeDaemonLauncher.RunningStatus(),
        };
        var (vm, _, _) = BuildEnvStep(launcher: launcher);

        await vm.OnStepEnteredAsync();

        Assert.Contains("EnsureRunningAsync", launcher.Calls);
        Assert.Equal(Port11434Status.RouterRunning, vm.Port11434Status);
        Assert.False(vm.MessageIsError);
        Assert.Contains("Router is running on :11434", vm.Message!);
    }

    [Fact]
    public async Task Step1_OnStepEntered_ForeignPort_DoesNotAutoStartRouter()
    {
        var launcher = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null, "occupied"),
        };
        var (vm, _, _) = BuildEnvStep(launcher: launcher);

        await vm.OnStepEnteredAsync();

        Assert.DoesNotContain("EnsureRunningAsync", launcher.Calls);
        Assert.DoesNotContain("StartAsync", launcher.Calls);
        Assert.Equal(Port11434Status.ForeignService, vm.Port11434Status);
    }

    [Fact]
    public async Task Step1_ChangePort_FreesPort_AutoStartsRouter()
    {
        var launcher = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null, "occupied"),
            AfterStart = FakeDaemonLauncher.RunningStatus(),
        };
        var (vm, h, _) = BuildEnvStep(launcher: launcher);
        h.Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, true);
        h.PortMap[11434] = true;
        await vm.RefreshAsync();
        Assert.Equal(Port11434Status.ForeignService, vm.Port11434Status);

        h.Configurator.ProcessStarter = info =>
        {
            h.Starts.Add(info);
            h.PortOpen = true;
            h.PortMap[11434] = false;
            h.PortMap[11435] = true;
            launcher.Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null);
            return "handle-1";
        };

        await vm.ChangePortCommand.ExecuteAsync(CancellationToken.None);

        Assert.Contains("EnsureRunningAsync", launcher.Calls);
        Assert.Equal(Port11434Status.RouterRunning, vm.Port11434Status);
        Assert.False(vm.MessageIsError);
        Assert.Contains("127.0.0.1:11435", vm.Message!);
        Assert.Contains("Router is running on :11434", vm.Message!);
    }

    [Fact]
    public async Task Root_GoToStep1_FromStep0_StartFails_StaysOnStep0()
    {
        var launcher = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null),
            StartException = new RouterDaemonStartTimeoutException(TimeSpan.FromSeconds(15), "/opt/ollamarouter-server"),
        };
        var fx = BuildRoot(new FakeRouterApiClient(), launcher: launcher);
        await fx.Root.InitialStepEntry;

        Assert.Equal(0, fx.Root.SelectedStep);
        Assert.Equal(Port11434Status.Free, fx.Env.Port11434Status);

        await fx.Root.NextCommand.ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, fx.Root.SelectedStep);
        Assert.True(fx.Env.MessageIsError);
        Assert.Contains("Could not start the router", fx.Env.Message!);
        await fx.Root.DisposeAsync();
    }

    [Fact]
    public async Task Root_GoToStep1_FromStep0_ForeignPort_StaysOnStep0WithError()
    {
        var launcher = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null, "occupied"),
        };
        var fx = BuildRoot(new FakeRouterApiClient(), launcher: launcher);
        await fx.Root.InitialStepEntry;

        await fx.Root.NextCommand.ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, fx.Root.SelectedStep);
        Assert.True(fx.Env.MessageIsError);
        Assert.Contains("Free port :11434", fx.Env.Message!);
        await fx.Root.DisposeAsync();
    }

    [Fact]
    public async Task Root_BusyGating_DisablesNavigationWhileStepIsBusy()
    {
        var fx = BuildRoot(new FakeRouterApiClient(), launcher: new FakeDaemonLauncher { Status = FakeDaemonLauncher.RunningStatus() });
        await fx.Root.InitialStepEntry;

        Assert.True(fx.Root.CanGoNext);
        Assert.True(fx.Root.NextCommand.CanExecute(null));

        fx.Env.IsBusy = true;
        Assert.True(fx.Root.IsAnyStepBusy);
        Assert.False(fx.Root.CanGoNext);
        Assert.False(fx.Root.NextCommand.CanExecute(null));

        fx.Env.IsBusy = false;
        Assert.False(fx.Root.IsAnyStepBusy);
        Assert.True(fx.Root.NextCommand.CanExecute(null));

        await fx.Root.GoToAsync(4);
        Assert.True(fx.Root.FinishCommand.CanExecute(null));
        fx.Rec.IsBusy = true;
        Assert.False(fx.Root.FinishCommand.CanExecute(null));
        Assert.False(fx.Root.BackCommand.CanExecute(null));
        fx.Rec.IsBusy = false;
        Assert.True(fx.Root.FinishCommand.CanExecute(null));
        await fx.Root.DisposeAsync();
    }

    [Fact]
    public async Task Root_DaemonBanner_ShowsWhenDownOnStep2_AndStartFromBannerRecovers()
    {
        var launcher = new FakeDaemonLauncher
        {
            Status = FakeDaemonLauncher.RunningStatus(),
        };
        var fake = new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) };
        var fx = BuildRoot(fake, launcher: launcher);
        await fx.Root.InitialStepEntry;
        await fx.Root.GoToAsync(1);
        Assert.False(fx.Root.ShowDaemonWarningBanner);

        // Simulate daemon dropping while on Step 2
        launcher.Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null);
        launcher.AfterStart = FakeDaemonLauncher.RunningStatus();
        await fx.Env.RefreshAsync();

        Assert.True(fx.Root.ShowDaemonWarningBanner);
        Assert.True(fx.Root.ShowInlineStartRouterButton);
        Assert.Equal("● Router Stopped", fx.Root.DaemonBadgeText);

        await fx.Root.StartRouterFromBannerCommand.ExecuteAsync(CancellationToken.None);

        Assert.False(fx.Root.ShowDaemonWarningBanner);
        Assert.True(fx.Root.IsDaemonRunning);
        Assert.Equal("● Router Running (:11434)", fx.Root.DaemonBadgeText);
        await fx.Root.DisposeAsync();
    }

    [Fact]
    public async Task Step2_ErrorMessages_SetMessageIsErrorTrue()
    {
        var fake = new FakeRouterApiClient { NextCallException = new HttpRequestException("refused") };
        var vm = new WizardTargetsStepViewModel(() => fake, ct => Task.CompletedTask, GatedDelay);

        await vm.RefreshAsync();

        Assert.True(vm.MessageIsError);
        Assert.True(vm.HasMessage);

        vm.AddTargetCommand.Execute(new AddTargetRequest("Node1", "http://node1:11435", TestConnectionFirst: false));
        await WaitUntilAsync(() => !vm.MessageIsError, "AddTarget success clears MessageIsError");
        Assert.False(vm.MessageIsError);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_ExternalProviders_PopulatedFromNonOllamaTargets()
    {
        var fake = new FakeRouterApiClient();
        fake.SeedTarget(new RouterTargetConfigDto("Local", "http://127.0.0.1:11435", true, null, 1, "ollama"));
        fake.SeedTarget(new RouterTargetConfigDto("OpenAI", "https://api.openai.com/v1", true, null, 2, "openai", "openai"));
        var vm = new WizardCloudStepViewModel(() => fake);

        await vm.RefreshAsync();

        var provider = Assert.Single(vm.ExternalProviders);
        Assert.Equal("OpenAI", provider.Name);
        Assert.Equal("openai", provider.ProviderType);
        Assert.True(vm.HasExternalProviders);
        Assert.False(vm.HasNoExternalProviders);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step4_SelectFittingOnly_AndSelectNone_AndProgressPercent()
    {
        var fake = new FakeRouterApiClient
        {
            CandidatesResponse = new RouterBenchmarkCandidatesResponse([
                new RouterBenchmarkCandidateTarget(
                    "Local",
                    null,
                    [
                        new RouterBenchmarkCandidateModel("qwen2.5:7b", [UsageRole.BasicModel], "q4_K_M", "qwen2", 4500, null, 8192, BenchmarkCandidateFit.Fits, 8192),
                        new RouterBenchmarkCandidateModel("llama3:70b", [UsageRole.SoftwareArchitect], "q4_K_M", "llama", 42000, null, 8192, BenchmarkCandidateFit.Pruned, 0),
                    ])
            ]),
            StartBenchmarkResponse = new RouterBenchmarkRunResponse(true, ["Local"]),
            BenchmarkResults = new RouterBenchmarkResultsResponse([
                new BenchmarkTargetResult("Local", BenchmarkRunStatus.Succeeded, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [])
            ]),
        };
        WizardBenchmarkStepViewModel vm = null!;
        vm = new WizardBenchmarkStepViewModel(() => fake, ct => vm.RefreshAsync(ct), GatedDelay);

        await vm.RefreshAsync();
        Assert.True(vm.HasCandidates);

        vm.SelectNoneCommand.Execute(null);
        Assert.All(vm.CandidateGroups.SelectMany(g => g.Models), m => Assert.False(m.IsSelected));
        Assert.False(vm.RunBenchmarksCommand.CanExecute(null));

        vm.SelectFittingOnlyCommand.Execute(null);
        var models = vm.CandidateGroups.SelectMany(g => g.Models).ToList();
        Assert.True(models[0].IsSelected);
        Assert.False(models[1].IsSelected);
        Assert.True(vm.RunBenchmarksCommand.CanExecute(null));

        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(100.0, vm.ProgressPercent);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step5_GenerateAndApply_GeneratesAndAppliesInOneClick_AndSetsMessageIsErrorOn409()
    {
        var fake = new FakeRouterApiClient();
        var vm = new WizardRecommendationsStepViewModel(() => fake);

        await vm.GenerateAndApplyCommand.ExecuteAsync(CancellationToken.None);

        Assert.False(vm.MessageIsError);
        Assert.True(vm.HasMessage);
        Assert.True(vm.HasModels);
        Assert.True(vm.HasAppliedTargets);
        Assert.Single(vm.AppliedTargets);

        var conflictFake = new FakeRouterApiClient { GenerateError = (HttpStatusCode.Conflict, "none") };
        var conflictVm = new WizardRecommendationsStepViewModel(() => conflictFake);
        await conflictVm.GenerateAndApplyCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(conflictVm.MessageIsError);
        Assert.True(conflictVm.GoToBenchmarkCommand.CanExecute(null));
        await vm.DisposeAsync();
        await conflictVm.DisposeAsync();
    }

    // ==================================================================
    //  Converters & Review-Finding Regressions
    // ==================================================================

    [Fact]
    public void Converters_NewSemanticConverters_MapValuesAndRejectConvertBack()
    {
        var culture = CultureInfo.InvariantCulture;

        // BoolToBannerBackgroundConverter
        var bgConv = BoolToBannerBackgroundConverter.Instance;
        Assert.Equal(Avalonia.Media.Color.Parse("#26EF4444"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(bgConv.Convert(true, typeof(object), null, culture)).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#2610B981"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(bgConv.Convert(false, typeof(object), null, culture)).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#2610B981"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(bgConv.Convert("other", typeof(object), null, culture)).Color);
        Assert.Throws<NotSupportedException>(() => bgConv.ConvertBack(null, typeof(object), null, culture));

        // BoolToBannerBorderConverter
        var borderConv = BoolToBannerBorderConverter.Instance;
        Assert.Equal(Avalonia.Media.Color.Parse("#EF4444"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(borderConv.Convert(true, typeof(object), null, culture)).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#10B981"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(borderConv.Convert(false, typeof(object), null, culture)).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#10B981"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(borderConv.Convert(null, typeof(object), null, culture)).Color);
        Assert.Throws<NotSupportedException>(() => borderConv.ConvertBack(null, typeof(object), null, culture));

        // FitTextToBrushConverter
        var fitConv = FitTextToBrushConverter.Instance;
        Assert.Equal(Avalonia.Media.Color.Parse("#10B981"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(fitConv.Convert("ctx ≤ 8192", typeof(object), null, culture)).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#10B981"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(fitConv.Convert("CTX 4096", typeof(object), null, culture)).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#F59E0B"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(fitConv.Convert("won't fit this node", typeof(object), null, culture)).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#94A3B8"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(fitConv.Convert("no profile (assumed)", typeof(object), null, culture)).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#94A3B8"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(fitConv.Convert(null, typeof(object), null, culture)).Color);
        Assert.Throws<NotSupportedException>(() => fitConv.ConvertBack(null, typeof(object), null, culture));

        // LivenessToBrushConverter
        var liveConv = LivenessToBrushConverter.Instance;
        foreach (var online in new[] { "Online", "healthy", "READY" })
        {
            Assert.Equal(Avalonia.Media.Color.Parse("#10B981"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(liveConv.Convert(online, typeof(object), null, culture)).Color);
        }
        foreach (var offline in new[] { "Offline", "unhealthy", "Down", "ERROR" })
        {
            Assert.Equal(Avalonia.Media.Color.Parse("#F87171"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(liveConv.Convert(offline, typeof(object), null, culture)).Color);
        }
        foreach (var other in new object?[] { "Unknown", "Degraded", null, 42 })
        {
            Assert.Equal(Avalonia.Media.Color.Parse("#F59E0B"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(liveConv.Convert(other, typeof(object), null, culture)).Color);
        }
        Assert.Throws<NotSupportedException>(() => liveConv.ConvertBack(null, typeof(object), null, culture));

        // WizardStepForegroundConverter
        var stepConv = WizardStepForegroundConverter.Instance;
        Assert.Equal(Avalonia.Media.Color.Parse("#A5B4FC"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(stepConv.Convert(2, typeof(object), "2", culture)).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#10B981"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(stepConv.Convert(2, typeof(object), 1, culture)).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#94A3B8"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(stepConv.Convert(2, typeof(object), "3", culture)).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#94A3B8"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(stepConv.Convert(2, typeof(object), "bad", culture)).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#94A3B8"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(stepConv.Convert("2", typeof(object), 2, culture)).Color);
        Assert.Throws<NotSupportedException>(() => stepConv.ConvertBack(null, typeof(object), null, culture));
    }

    [Fact]
    public async Task Root_GoToStep1_FromStep0_UnknownPort_ProbesAndBlocksOrAdvances()
    {
        var occupiedLauncher = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null, "occupied"),
        };
        var fxBlocked = BuildRoot(new FakeRouterApiClient(), launcher: occupiedLauncher);
        await fxBlocked.Root.InitialStepEntry;
        fxBlocked.Env.Port11434Status = Port11434Status.Unknown;

        await fxBlocked.Root.NextCommand.ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, fxBlocked.Root.SelectedStep);
        Assert.Equal(Port11434Status.ForeignService, fxBlocked.Env.Port11434Status);
        Assert.True(fxBlocked.Env.MessageIsError);
        Assert.Contains("Free port :11434", fxBlocked.Env.Message!);
        await fxBlocked.Root.DisposeAsync();

        var runningLauncher = new FakeDaemonLauncher { Status = FakeDaemonLauncher.RunningStatus() };
        var fxReady = BuildRoot(new FakeRouterApiClient(), launcher: runningLauncher);
        await fxReady.Root.InitialStepEntry;
        fxReady.Env.Port11434Status = Port11434Status.Unknown;

        await fxReady.Root.NextCommand.ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, fxReady.Root.SelectedStep);
        Assert.Equal(Port11434Status.RouterRunning, fxReady.Env.Port11434Status);
        await fxReady.Root.DisposeAsync();
    }

    [Fact]
    public async Task Root_DaemonDropOnSteps2To5_UpdatesBannerOnStepTransitionAndStepError()
    {
        var launcher = new FakeDaemonLauncher { Status = FakeDaemonLauncher.RunningStatus() };
        var fake = new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) };
        var fx = BuildRoot(fake, launcher: launcher);
        await fx.Root.InitialStepEntry;
        await fx.Root.GoToAsync(1);
        Assert.False(fx.Root.ShowDaemonWarningBanner);

        // Daemon stops, then user navigates to Step 3
        launcher.Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null);
        await fx.Root.GoToAsync(2);

        Assert.Equal(2, fx.Root.SelectedStep);
        Assert.True(fx.Root.ShowDaemonWarningBanner);
        Assert.True(fx.Root.ShowInlineStartRouterButton);
        Assert.False(fx.Root.IsDaemonPortConflict);

        // Port becomes occupied by raw Ollama and Step 3 reports an error -> background probe updates banner
        launcher.Status = new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null, "occupied");
        fake.ModelsException = new OllamaRouterApiException(HttpStatusCode.BadGateway, "down");
        await fx.Cloud.RefreshAsync();

        await WaitUntilAsync(() => fx.Root.IsDaemonPortConflict, "background router probe to detect foreign service");
        Assert.False(fx.Root.ShowInlineStartRouterButton);
        Assert.Contains("Change port", fx.Root.DaemonBannerMessage);
        await fx.Root.DisposeAsync();
    }

    [Fact]
    public async Task Root_StartRouterFromBanner_FailureBusyGateAndSuccessRefreshWork()
    {
        var launcher = new FakeDaemonLauncher { Status = FakeDaemonLauncher.RunningStatus() };
        var fake = new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) };
        var fx = BuildRoot(fake, launcher: launcher);
        await fx.Root.InitialStepEntry;
        await fx.Root.GoToAsync(1);
        await WaitUntilAsync(() => fake.Calls.Contains("GetFleetAsync"), "initial Step 2 poll tick");

        // Daemon drops while on Step 2
        launcher.Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null);
        launcher.StartException = new RouterDaemonStartTimeoutException(TimeSpan.FromSeconds(15), "/opt/ollamarouter");
        await fx.Env.RefreshRouterStatusAsync();
        Assert.True(fx.Root.ShowDaemonWarningBanner);

        // Failure surfaces error in DaemonBannerMessage
        await fx.Root.StartRouterFromBannerCommand.ExecuteAsync(CancellationToken.None);
        Assert.True(fx.Root.ShowDaemonWarningBanner);
        Assert.Contains("Could not start the router", fx.Root.DaemonBannerMessage);

        // Busy-gating disables StartRouterFromBannerCommand
        fx.Targets.IsBusy = true;
        Assert.False(fx.Root.StartRouterFromBannerCommand.CanExecute(null));
        var callsBeforeBusyAttempt = launcher.Calls.Count;
        await fx.Root.StartRouterFromBannerCommand.ExecuteAsync(CancellationToken.None);
        Assert.Equal(callsBeforeBusyAttempt, launcher.Calls.Count);
        fx.Targets.IsBusy = false;

        // Recovery succeeds and refreshes Step 2 immediately (even with GatedDelay poller)
        launcher.StartException = null;
        launcher.AfterStart = FakeDaemonLauncher.RunningStatus();
        fake.FleetResponse = OneTargetFleet();
        var fleetCallsBefore = fake.Calls.Count(c => c == "GetFleetAsync");

        await fx.Root.StartRouterFromBannerCommand.ExecuteAsync(CancellationToken.None);

        Assert.False(fx.Root.ShowDaemonWarningBanner);
        Assert.True(fx.Root.IsDaemonRunning);
        await WaitUntilAsync(
            () => fake.Calls.Count(c => c == "GetFleetAsync") > fleetCallsBefore && fx.Targets.Rows.Count == 1,
            "Step 2 poller to restart and refresh rows after banner recovery");
        await fx.Root.DisposeAsync();
    }

    [Fact]
    public async Task Root_GoToStep1Command_NavigatesToStep0AndClearsBannerError()
    {
        var launcher = new FakeDaemonLauncher { Status = FakeDaemonLauncher.RunningStatus() };
        var fake = new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) };
        var fx = BuildRoot(fake, launcher: launcher);
        await fx.Root.InitialStepEntry;
        await fx.Root.GoToAsync(1);

        launcher.Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null);
        launcher.StartException = new RouterDaemonStartTimeoutException(TimeSpan.FromSeconds(15), "/opt/ollamarouter");
        await fx.Env.RefreshRouterStatusAsync();
        await fx.Root.StartRouterFromBannerCommand.ExecuteAsync(CancellationToken.None);
        Assert.Contains("Could not start the router", fx.Root.DaemonBannerMessage);

        await fx.Root.GoToStep1Command.ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, fx.Root.SelectedStep);
        Assert.Equal(
            "The OllamaRouter daemon is not running on :11434. Start it now to continue setup.",
            fx.Root.DaemonBannerMessage);
        await fx.Root.DisposeAsync();
    }

    [Fact]
    public async Task Step1_OnStepEntered_FreePort_AutoStartsRouter_WithSingleBinaryProbe()
    {
        var launcher = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null),
            AfterStart = FakeDaemonLauncher.RunningStatus(),
        };
        var envConfig = new FakeEnvConfigurator(new OllamaDetection(true, "/usr/local/bin/ollama", true));
        var vm = new WizardEnvironmentStepViewModel(envConfig.Configurator, FakeConnection(null, launcher));

        await vm.OnStepEnteredAsync();

        Assert.Equal(1, envConfig.Probes);
        Assert.Contains("EnsureRunningAsync", launcher.Calls);
        Assert.Equal(Port11434Status.RouterRunning, vm.Port11434Status);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step1_StartOllama_FreePort_AutoStartsRouter_AndHandlesStartFailure()
    {
        var launcher = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null),
            AfterStart = FakeDaemonLauncher.RunningStatus(),
        };
        var (vm, h, _) = BuildEnvStep(launcher: launcher);
        h.Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, false);
        h.PortOpen = false;
        h.PortMap[11434] = false;
        h.Configurator.ProcessStarter = info =>
        {
            h.Starts.Add(info);
            h.PortMap[11435] = true;
            return "handle-1";
        };

        await vm.RefreshAsync();
        await vm.StartOllamaCommand.ExecuteAsync(CancellationToken.None);

        Assert.Contains("EnsureRunningAsync", launcher.Calls);
        Assert.Equal(Port11434Status.RouterRunning, vm.Port11434Status);
        Assert.False(vm.MessageIsError);
        Assert.Contains("Router is running on :11434", vm.Message!);

        // Now verify router auto-start failure combines messages and flags MessageIsError
        var failLauncher = new FakeDaemonLauncher
        {
            Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null),
            StartException = new RouterDaemonStartTimeoutException(TimeSpan.FromSeconds(15), "/opt/ollamarouter"),
        };
        var (vmFail, hFail, _) = BuildEnvStep(launcher: failLauncher);
        hFail.Detection = new OllamaDetection(true, LocalOllamaConfigurator.WindowsTrayAppPath, false);
        hFail.PortMap[11434] = false;
        hFail.Configurator.ProcessStarter = info =>
        {
            hFail.Starts.Add(info);
            hFail.PortMap[11435] = true;
            return "handle-1";
        };

        await vmFail.RefreshAsync();
        await vmFail.StartOllamaCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(vmFail.MessageIsError);
        Assert.Contains("Ollama is serving on", vmFail.Message!);
        Assert.Contains("Could not start the router", vmFail.Message!);
        await vm.DisposeAsync();
        await vmFail.DisposeAsync();
    }

    [Fact]
    public async Task Step2_RecoveryAndHasNoRows_ClearsTransientDaemonError_PreservesAddTargetAndLocalAdded()
    {
        var fake = new FakeRouterApiClient { NextCallException = new HttpRequestException("refused") };
        var vm = new WizardTargetsStepViewModel(() => fake, ct => Task.CompletedTask, GatedDelay);
        Assert.True(vm.HasNoRows);

        // Transient poll error sets MessageIsError; subsequent healthy poll clears it
        await vm.RefreshAsync();
        Assert.True(vm.MessageIsError);
        Assert.True(vm.HasMessage);

        fake.FleetResponse = OneTargetFleet();
        await vm.RefreshAsync();
        Assert.False(vm.MessageIsError);
        Assert.False(vm.HasMessage);
        Assert.Null(vm.Message);
        Assert.False(vm.HasNoRows);

        // Empty fleet flips HasNoRows back to true
        fake.FleetResponse = new RouterFleetResponse([]);
        await vm.RefreshAsync();
        Assert.True(vm.HasNoRows);

        // AddTarget 409 error must survive AddTarget's finally RefreshAsync
        fake.AddTargetError = (HttpStatusCode.Conflict, "duplicate");
        await vm.AddTargetCommand.ExecuteAsync(new AddTargetRequest("Node1", "http://node1:11435"));
        Assert.True(vm.MessageIsError);
        Assert.Equal("Target already exists.", vm.Message);
        fake.AddTargetError = null;

        // Local auto-add success message must survive subsequent healthy poll ticks
        await vm.OnStepEnteredAsync();
        Assert.False(vm.MessageIsError);
        Assert.Equal("Local node added.", vm.Message);
        await vm.RefreshAsync();
        Assert.Equal("Local node added.", vm.Message);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step3_Refresh_CanSelectCloudModelPropertyChanged_AndExternalProviderFilterAndFlags()
    {
        var fake = new FakeRouterApiClient
        {
            ConfigResponse = DefaultConfig() with
            {
                Cloud = new RouterCloudConfigDto(true, null, "Local", false),
            },
            ModelsResponse = new RouterModelsResponse([Model("glm-5.3-flash:cloud")]),
        };
        fake.SeedTarget(new RouterTargetConfigDto("NoType", "http://127.0.0.1:11435", true, null, 1, ""));
        fake.SeedTarget(new RouterTargetConfigDto("Local", "http://127.0.0.1:11435", true, null, 1, "ollama"));
        fake.SeedTarget(new RouterTargetConfigDto("OpenAI", "https://api.openai.com/v1", true, null, 2, "openai", "OpenAI API"));
        fake.SeedTarget(new RouterTargetConfigDto("CustomOai", "https://custom/v1", true, null, 2, "openai", null));

        var vm = new WizardCloudStepViewModel(() => fake);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.RefreshAsync();

        Assert.Contains(nameof(WizardCloudStepViewModel.CanSelectCloudModel), changed);
        Assert.True(vm.CanSelectCloudModel);
        Assert.True(vm.ShowRelaySignInWarning);
        Assert.False(vm.ShowRelaySignInHint);

        Assert.Equal(2, vm.ExternalProviders.Count);
        Assert.Equal("OpenAI API", vm.ExternalProviders[0].DisplayProvider);
        Assert.Equal("openai", vm.ExternalProviders[1].DisplayProvider);

        // Check remaining CloudEnabled x CloudOverflowConfigured states
        vm.CloudEnabled = false;
        Assert.False(vm.ShowRelaySignInWarning);
        Assert.True(vm.ShowRelaySignInHint);

        vm.CloudOverflowConfigured = true;
        Assert.False(vm.ShowRelaySignInWarning);
        Assert.False(vm.ShowRelaySignInHint);

        // Failed ToggleCloud clears stale Message
        await vm.ToggleCloudCommand.ExecuteAsync(CancellationToken.None);
        Assert.True(vm.HasMessage);
        fake.UpdateConfigError = (HttpStatusCode.InternalServerError, "boom");
        await vm.ToggleCloudCommand.ExecuteAsync(CancellationToken.None);
        Assert.True(vm.HasError);
        Assert.False(vm.HasMessage);
        Assert.Null(vm.Message);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Step4_Progress_AndClearResultsHttpFailure_WorkDeterministically()
    {
        var fake = new FakeRouterApiClient
        {
            CandidatesResponse = new RouterBenchmarkCandidatesResponse([
                new RouterBenchmarkCandidateTarget(
                    "local",
                    null,
                    [new RouterBenchmarkCandidateModel("qwen2.5:7b", [UsageRole.BasicModel], "q4_K_M", "qwen2", 4500, null, 8192, BenchmarkCandidateFit.Fits, 8192)])
            ]),
            StartBenchmarkResponse = new RouterBenchmarkRunResponse(true, ["local", "gpu-2"]),
            BenchmarkResults = new RouterBenchmarkResultsResponse([
                new BenchmarkTargetResult("Local", BenchmarkRunStatus.Succeeded, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []),
                new BenchmarkTargetResult("gpu-2", BenchmarkRunStatus.Running, DateTimeOffset.UtcNow, null, []),
            ]),
        };

        WizardBenchmarkStepViewModel vm = null!;
        vm = new WizardBenchmarkStepViewModel(() => fake, ct => vm.RefreshAsync(ct), GatedDelay);

        // Without RunBenchmarks (_queuedTargets empty), mid-benchmark entry reports partial progress
        await vm.RefreshAsync();
        Assert.True(vm.IsBenchmarkRunning);
        Assert.Equal(50.0, vm.ProgressPercent);
        Assert.Equal("1 / 2 target(s) finished", vm.ProgressSummaryText);

        // With _queuedTargets matching case-insensitively ("local" vs "Local")
        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);
        Assert.True(vm.IsBenchmarkRunning);
        Assert.Equal(50.0, vm.ProgressPercent);
        Assert.Equal("1 / 2 target(s) finished", vm.ProgressSummaryText);

        // All terminal without _queuedTargets reports 100%
        fake.BenchmarkResults = new RouterBenchmarkResultsResponse([
            new BenchmarkTargetResult("Local", BenchmarkRunStatus.Succeeded, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []),
            new BenchmarkTargetResult("gpu-2", BenchmarkRunStatus.Succeeded, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []),
        ]);
        await vm.RefreshAsync();
        Assert.False(vm.IsBenchmarkRunning);
        Assert.Equal(100.0, vm.ProgressPercent);
        Assert.Equal("2 / 2 target(s) finished", vm.ProgressSummaryText);
        await vm.DisposeAsync();

        // ClearResults HttpRequestException clears Message and sets Error without throwing
        var mock = new Mock<IOllamaRouterApiClient>();
        mock.Setup(c => c.ClearBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("refused"));
        mock.Setup(c => c.GetBenchmarkCandidatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterBenchmarkCandidatesResponse([]));
        mock.Setup(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterBenchmarkResultsResponse([]));

        var vmErr = new WizardBenchmarkStepViewModel(() => mock.Object, _ => Task.CompletedTask, GatedDelay);
        vmErr.Message = "stale success";
        await vmErr.ClearResultsCommand.ExecuteAsync(CancellationToken.None);
        Assert.Null(vmErr.Message);
        Assert.Contains("Connection failed", vmErr.Error!);
        await vmErr.DisposeAsync();
    }

    [Fact]
    public async Task Step5_Apply409Distinct_IsBusyGatesGoToBenchmark_AndRecoveryClearsError()
    {
        var fake = new FakeRouterApiClient { GenerateError = (HttpStatusCode.Conflict, "none") };
        var vm = new WizardRecommendationsStepViewModel(() => fake);

        await vm.GenerateAndApplyCommand.ExecuteAsync(CancellationToken.None);
        Assert.True(vm.MessageIsError);
        Assert.Contains("run the Step 4 micro-benchmark first", vm.Message!);
        Assert.True(vm.GoToBenchmarkCommand.CanExecute(null));

        // IsBusy gates GoToBenchmarkCommand
        vm.IsBusy = true;
        Assert.False(vm.GoToBenchmarkCommand.CanExecute(null));
        vm.IsBusy = false;
        Assert.True(vm.GoToBenchmarkCommand.CanExecute(null));

        // Apply 409 after Generate succeeds uses distinct message and resets GoToBenchmarkCommand
        fake.GenerateError = null;
        fake.ApplyError = (HttpStatusCode.Conflict, "missing");
        await vm.GenerateAndApplyCommand.ExecuteAsync(CancellationToken.None);
        Assert.True(vm.MessageIsError);
        Assert.Contains("Recommendations were generated, but could not be applied", vm.Message!);
        Assert.False(vm.GoToBenchmarkCommand.CanExecute(null));

        // RefreshAsync recovery clears MessageIsError
        await vm.RefreshAsync();
        Assert.False(vm.MessageIsError);
        Assert.Null(vm.Message);

        // GenerateCommand success also resets GoToBenchmarkCommand after prior 409
        fake.GenerateError = (HttpStatusCode.Conflict, "none");
        await vm.GenerateCommand.ExecuteAsync(CancellationToken.None);
        Assert.True(vm.GoToBenchmarkCommand.CanExecute(null));
        fake.GenerateError = null;
        await vm.GenerateCommand.ExecuteAsync(CancellationToken.None);
        Assert.False(vm.GoToBenchmarkCommand.CanExecute(null));

        await vm.DisposeAsync();
    }
}
