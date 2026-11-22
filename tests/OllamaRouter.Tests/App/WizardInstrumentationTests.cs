using System.Net;
using System.Net.Http;
using OllamaRouter.App;
using OllamaRouter.App.Connection;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Options;
using OllamaRouter.Tests.TestHelpers;
using Serilog.Events;
using Xunit;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Task #159 — wizard instrumentation tests (the approved test plan, step 8): every step
/// VM's log lines through INJECTED capturing loggers (never the static Serilog.Log, never
/// the user profile). Includes the AC6 core gate test — persistent identical poll failures
/// log exactly ONE state-change entry, never a per-tick spam. All delays are manual
/// gates / soft yields — no test sleeps.
/// </summary>
public sealed class WizardInstrumentationTests
{
    private static Task GatedDelay(TimeSpan span, CancellationToken ct) => new Task(static () => { }, ct);

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
                // Collection mid-rebuild on a thread-pool thread: treat as not yet.
            }

            await Task.Yield();
        }

        Assert.True(condition(), $"condition not met after {maxBeats} cooperative yields: {what}");
    }

    // A hermetic LocalOllamaConfigurator (the SetupWizardViewModelsTests.ConfigHarness shape,
    // trimmed to what the Step 1 instrumentation tests need).
    private sealed class ScriptedConfigurator
    {
        public ScriptedConfigurator()
        {
            Configurator = new LocalOllamaConfigurator
            {
                IsWindows = _ => true,
                IsMacOS = _ => false,
                BinaryProbe = _ => Detection,
                ProcessNames = name => name == "ollama" ? ["111"] : [],
                ProcessKiller = (_, _) => true,
                ProcessStarter = _ => "handle-1",
                PortProbe = _ => false,
                Delay = _ => Task.CompletedTask,
                ScriptRunner = (_, _) => Task.FromResult(NextScriptResult),
                OllamaHostReader = () => null,
                PortWaitBudget = TimeSpan.FromHours(1),
            };
        }

        public LocalOllamaConfigurator Configurator { get; }
        public OllamaDetection Detection { get; set; } = new(true, "/usr/local/bin/ollama", false);
        public ProcessRunResult NextScriptResult { get; set; } = new(0, string.Empty, string.Empty);
        public Exception? ScriptException { get; set; }
    }

    private static IAppConnectionService FakeConnection(FakeRouterApiClient client, FakeDaemonLauncher? launcher = null)
    {
        var mock = new Moq.Mock<IAppConnectionService>();
        mock.SetupGet(c => c.GetClient).Returns(() => client);
        mock.SetupGet(c => c.Client).Returns(() => client);
        mock.SetupGet(c => c.DaemonLauncher).Returns(launcher);
        return mock.Object;
    }

    // ==================================================================
    //  Step 1 — WizardEnvironmentStepViewModel
    // ==================================================================

    [Fact]
    public async Task Step1_InstallSuccess_InformationStartAndOutcomeWithMessage()
    {
        var capturing = new CapturingLogger();
        var scripted = new ScriptedConfigurator();
        var vm = new WizardEnvironmentStepViewModel(scripted.Configurator, FakeConnection(new FakeRouterApiClient()), logger: capturing.Logger);

        await vm.InstallOllamaCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(capturing.Sink.RendersContaining("Wizard step 1: install started"), "the start line");
        var outcome = capturing.Sink.FirstRendered("Wizard step 1: install completed:");
        Assert.NotNull(outcome);
        Assert.Equal(LogEventLevel.Information, outcome!.Level);
        Assert.Contains("Ollama installer finished", Rendered(outcome));
    }

    [Fact]
    public async Task Step1_InstallFailure_WarningOutcome()
    {
        var capturing = new CapturingLogger();
        var scripted = new ScriptedConfigurator { NextScriptResult = new ProcessRunResult(1, "tail", "err") };
        var vm = new WizardEnvironmentStepViewModel(scripted.Configurator, FakeConnection(new FakeRouterApiClient()), logger: capturing.Logger);

        await vm.InstallOllamaCommand.ExecuteAsync(CancellationToken.None);

        var failure = capturing.Sink.FirstRendered("Wizard step 1: install failed:");
        Assert.NotNull(failure);
        Assert.Equal(LogEventLevel.Warning, failure!.Level);
        Assert.Contains("exited with code 1", Rendered(failure));
    }

    [Fact]
    public async Task Step1_StartRouter_Success_InformationTransition()
    {
        var capturing = new CapturingLogger();
        var launcher = new FakeDaemonLauncher(); // Status Stopped → StartAsync → Running
        var vm = new WizardEnvironmentStepViewModel(
            new ScriptedConfigurator().Configurator, FakeConnection(new FakeRouterApiClient(), launcher),
            logger: capturing.Logger);

        await vm.StartRouterCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(capturing.Sink.RendersContaining("Wizard step 1: router start requested"));
        Assert.True(capturing.Sink.RendersContaining("Wizard step 1: router start → Running"));
    }

    [Fact]
    public async Task Step1_StartRouter_ThrowingLauncher_WarningWithException()
    {
        var capturing = new CapturingLogger();
        var launcher = new FakeDaemonLauncher { StartException = new RouterDaemonStartTimeoutException(TimeSpan.FromMinutes(1), "/opt/router/ollamarouter-server") };
        var vm = new WizardEnvironmentStepViewModel(
            new ScriptedConfigurator().Configurator, FakeConnection(new FakeRouterApiClient(), launcher),
            logger: capturing.Logger);

        await vm.StartRouterCommand.ExecuteAsync(CancellationToken.None);

        var failure = capturing.Sink.EventsAt(LogEventLevel.Warning).FirstOrDefault(e =>
            Rendered(e).Contains("Wizard step 1: router start failed:", StringComparison.Ordinal));
        Assert.NotNull(failure);
        Assert.NotNull(failure!.Exception);
        Assert.IsType<RouterDaemonStartTimeoutException>(failure.Exception);
    }

    [Fact]
    public async Task Step1_Refresh_DebugDetectionLineWithAllFields()
    {
        var capturing = new CapturingLogger();
        var scripted = new ScriptedConfigurator();
        // A launcher is required: the Debug line sits after the null-launcher early return.
        var vm = new WizardEnvironmentStepViewModel(scripted.Configurator,
            FakeConnection(new FakeRouterApiClient(), new FakeDaemonLauncher()), logger: capturing.Logger);

        await vm.RefreshAsync(CancellationToken.None);

        var debugEvents = capturing.Sink.Events
            .Where(e => e.Level == LogEventLevel.Debug && Rendered(e).Contains("Wizard step 1 detection:", StringComparison.Ordinal))
            .ToList();
        Assert.Single(debugEvents);
        var rendered = Rendered(debugEvents[0]);
        // Serilog renders booleans lowercase (true/false) and the port enums as their
        // plain names (the production log sites pass .ToString() — unquoted in the file).
        Assert.Contains("installed=true", rendered);
        Assert.Contains("running=false", rendered);
        Assert.Contains("effectivePort=", rendered);
        Assert.Contains("portStatus=NotRunning", rendered);
        Assert.Contains("routerPort=Free", rendered);
    }

    // ==================================================================
    //  Step 2 — WizardTargetsStepViewModel (auto-add + AddTarget + GATE)
    // ==================================================================

    [Fact]
    public async Task Step2_EnsureLocal_Present_NoOpLogged()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient();
        fake.SeedTarget(new RouterTargetConfigDto("Local", "http://127.0.0.1:11435", true, null, 0, "ollama", null, null, false, null, null, null));
        var vm = new WizardTargetsStepViewModel(() => fake, ct => Task.CompletedTask, GatedDelay, logger: capturing.Logger);

        await vm.OnStepEnteredAsync(CancellationToken.None);
        await vm.OnStepLeftAsync();

        Assert.True(capturing.Sink.RendersContaining("Wizard step 2: local node auto-add → already-present (no-op)"));
    }

    [Fact]
    public async Task Step2_EnsureLocal_Absent_AddedLogged()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient();
        var vm = new WizardTargetsStepViewModel(() => fake, ct => Task.CompletedTask, GatedDelay, logger: capturing.Logger);

        await vm.OnStepEnteredAsync(CancellationToken.None);
        await vm.OnStepLeftAsync();

        Assert.True(capturing.Sink.RendersContaining("Wizard step 2: local node auto-add → added"));
        Assert.Contains("Local", fake.Targets.Keys.ToList());
    }

    [Fact]
    public async Task Step2_EnsureLocal_422_WarningLogged()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient { AddTargetError = (HttpStatusCode.UnprocessableEntity, "local ollama unreachable") };
        var vm = new WizardTargetsStepViewModel(() => fake, ct => Task.CompletedTask, GatedDelay, logger: capturing.Logger);

        await vm.OnStepEnteredAsync(CancellationToken.None);
        await vm.OnStepLeftAsync();

        Assert.True(capturing.Sink.RendersContaining("Wizard step 2: local node auto-add → 422 local unreachable"));
    }

    [Fact]
    public async Task Step2_EnsureLocal_TransportFault_RouterDownWarningLogged()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient();
        // A transport failure on the config read = the router-down first-run state.
        fake.NextCallException = new HttpRequestException("connection refused");
        var vm = new WizardTargetsStepViewModel(() => fake, ct => Task.CompletedTask, GatedDelay, logger: capturing.Logger);

        await vm.OnStepEnteredAsync(CancellationToken.None);
        await vm.OnStepLeftAsync();

        Assert.True(capturing.Sink.RendersContaining("Wizard step 2: local node auto-add → router down (retry on next entry)"));
    }

    [Fact]
    public async Task Step2_AddTarget_Success_InformationLines()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient();
        var vm = new WizardTargetsStepViewModel(() => fake, ct => Task.CompletedTask, GatedDelay, logger: capturing.Logger);

        await vm.AddTargetCommand.ExecuteAsync(
            new AddTargetRequest("Node2", "http://node2.lan:11435", TestConnectionFirst: false));

        Assert.True(capturing.Sink.RendersContaining("Wizard step 2: adding target 'Node2'"));
        Assert.True(capturing.Sink.RendersContaining("Wizard step 2: target 'Node2' added"));
    }

    [Fact]
    public async Task Step2_AddTarget_Conflict_WarningWithMappedError()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient();
        fake.SeedTarget(new RouterTargetConfigDto("Dup", "http://dup:11435", true, null, 0, "ollama", null, null, false, null, null, null));
        var vm = new WizardTargetsStepViewModel(() => fake, ct => Task.CompletedTask, GatedDelay, logger: capturing.Logger);

        await vm.AddTargetCommand.ExecuteAsync(
            new AddTargetRequest("Dup", "http://dup:11435", TestConnectionFirst: false));

        var failure = capturing.Sink.FirstRendered("Wizard step 2: adding target 'Dup' failed:");
        Assert.NotNull(failure);
        Assert.Equal(LogEventLevel.Warning, failure!.Level);
    }

    [Fact]
    public async Task Step2_PollGate_Fail3ThenSucceedThenFail_Exactly3StateEntries()
    {
        // AC6 CORE TEST: the 2 s poll logs STATE CHANGES only. Sequence: fail, fail, fail,
        // succeed, fail → exactly 3 gate entries (unreachable, reachable, unreachable); the
        // 3 identical failures in a row produce ZERO extra entries.
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient();
        WizardTargetsStepViewModel vm = null!;
        vm = new WizardTargetsStepViewModel(() => fake, ct => vm.RefreshAsync(ct), GatedDelay, logger: capturing.Logger);

        // 3 consecutive failures:
        for (var i = 0; i < 3; i++)
        {
            fake.NextCallException = new HttpRequestException($"connection refused #{i + 1}");
            await vm.RefreshAsync(CancellationToken.None);
        }

        var gateEvents = () => capturing.Sink.Events
            .Where(e => Rendered(e).Contains("Wizard step 2: fleet poll — daemon", StringComparison.Ordinal))
            .ToList();

        Assert.Single(gateEvents()); // first failure → "unknown → unreachable"; failures 2–3 silent

        // Success:
        await vm.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, gateEvents().Count);
        Assert.True(capturing.Sink.RendersContaining("unreachable → reachable"), "the back-to-reachable transition");

        // Failure again:
        fake.NextCallException = new HttpRequestException("connection refused #4");
        await vm.RefreshAsync(CancellationToken.None);
        Assert.Equal(3, gateEvents().Count); // the whole sequence emits EXACTLY 3 poll-state entries
    }

    // ==================================================================
    //  Step 3 — WizardCloudStepViewModel
    // ==================================================================

    [Fact]
    public async Task Step3_ToggleCloud_Success_Information()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient();
        var vm = new WizardCloudStepViewModel(() => fake, capturing.Logger);

        await vm.ToggleCloudCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(capturing.Sink.RendersContaining("Wizard step 3: cloud overflow → on"), "the outcome line");
    }

    [Fact]
    public async Task Step3_ToggleCloud_Scripted500_Warning()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient { UpdateConfigError = (HttpStatusCode.InternalServerError, "boom") };
        var vm = new WizardCloudStepViewModel(() => fake, capturing.Logger);

        await vm.ToggleCloudCommand.ExecuteAsync(CancellationToken.None);

        var failure = capturing.Sink.FirstRendered("Wizard step 3: cloud toggle failed:");
        Assert.NotNull(failure);
        Assert.Equal(LogEventLevel.Warning, failure!.Level);
    }

    [Fact]
    public async Task Step3_SelectCloudModel_Persist_InformationWithModelName()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient
        {
            ModelsResponse = new RouterModelsResponse([Model("glm-5.3-flash:cloud"), Model("llama3:8b")]),
        };
        var vm = new WizardCloudStepViewModel(() => fake, capturing.Logger);
        await vm.RefreshAsync(CancellationToken.None); // seeds the catalog

        await vm.SelectCloudModelCommand.ExecuteAsync("glm-5.3-flash:cloud");

        Assert.True(
            capturing.Sink.RendersContaining("Wizard step 3: cloud default model → 'glm-5.3-flash:cloud'"),
            "model NAMES are not secrets and may appear in the log");
    }

    [Fact]
    public async Task Step3_SelectCloudModel_Scripted500_Warning()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient { UpdateConfigError = (HttpStatusCode.InternalServerError, "boom") };
        var vm = new WizardCloudStepViewModel(() => fake, capturing.Logger);

        await vm.SelectCloudModelCommand.ExecuteAsync("glm-5.3-flash:cloud");

        var failure = capturing.Sink.EventsAt(LogEventLevel.Warning).FirstOrDefault(e =>
            Rendered(e).Contains("Wizard step 3: persisting cloud default model", StringComparison.Ordinal));
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task Step3_Refresh_Failure_WarningLogged()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient();
        fake.NextCallException = new HttpRequestException("connection refused");
        var vm = new WizardCloudStepViewModel(() => fake, capturing.Logger);

        await vm.RefreshAsync(CancellationToken.None);

        Assert.True(capturing.Sink.RendersContaining("Wizard step 3: refresh failed:"));
    }

    // ==================================================================
    //  Step 4 — WizardBenchmarkStepViewModel (run/clear + GATE)
    // ==================================================================

    /// <summary>
    /// A non-empty candidate plan (one node, one SELECTED candidate) so the Step 4
    /// <c>RunBenchmarks</c> command is executable (CanRun = HasSelectedCandidate) and the
    /// one-shot lazy candidate load succeeds.
    /// </summary>
    private static RouterBenchmarkCandidatesResponse CandidatePlan() => new([
        new RouterBenchmarkCandidateTarget(
            "Node1", null,
            [new RouterBenchmarkCandidateModel(
                "llama3:8b", [UsageRole.BasicModel], "q8_0", "llama", 8_000_000_000, null, 8192,
                BenchmarkCandidateFit.Fits, 8192)]),
    ]);

    [Fact]
    public async Task Step4_RunBenchmarks_TwoQueued_InformationLine()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient
        {
            CandidatesResponse = CandidatePlan(),
            StartBenchmarkResponse = new RouterBenchmarkRunResponse(true, ["Node1", "Node2"]),
        };
        WizardBenchmarkStepViewModel vm = null!;
        vm = new WizardBenchmarkStepViewModel(() => fake, ct => vm.RefreshAsync(ct), GatedDelay, logger: capturing.Logger);

        // Load the candidate plan first (CanRun = HasSelectedCandidate), then run.
        await vm.RefreshAsync(CancellationToken.None);
        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(capturing.Sink.RendersContaining("Wizard step 4: benchmark run started"));
        Assert.True(capturing.Sink.RendersContaining("2 target(s) queued"), "the queued-count outcome line");
    }

    [Fact]
    public async Task Step4_RunBenchmarks_ZeroQueued_InformationHint()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient
        {
            CandidatesResponse = CandidatePlan(),
            StartBenchmarkResponse = new RouterBenchmarkRunResponse(true, []),
        };
        WizardBenchmarkStepViewModel vm = null!;
        vm = new WizardBenchmarkStepViewModel(() => fake, ct => vm.RefreshAsync(ct), GatedDelay, logger: capturing.Logger);

        // Load the candidate plan first (CanRun = HasSelectedCandidate), then run.
        await vm.RefreshAsync(CancellationToken.None);
        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(capturing.Sink.RendersContaining("Wizard step 4: benchmark run completed (0 target(s) queued"));
    }

    [Fact]
    public async Task Step4_RunBenchmarks_ScriptedApiFault_Warning()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient
        {
            CandidatesResponse = CandidatePlan(),
        };
        WizardBenchmarkStepViewModel vm = null!;
        vm = new WizardBenchmarkStepViewModel(() => fake, ct => vm.RefreshAsync(ct), GatedDelay, logger: capturing.Logger);

        // Load the candidate plan FIRST (clean) so the scripted fault below lands on the
        // StartBenchmarkAsync call, not the one-shot lazy candidate load.
        await vm.RefreshAsync(CancellationToken.None);
        fake.NextCallException = new OllamaRouterApiException(HttpStatusCode.ServiceUnavailable, "bench down");

        await vm.RunBenchmarksCommand.ExecuteAsync(CancellationToken.None);

        var failure = capturing.Sink.FirstRendered("Wizard step 4: benchmark run failed:");
        Assert.NotNull(failure);
        Assert.Equal(LogEventLevel.Warning, failure!.Level);
    }

    [Fact]
    public async Task Step4_PollGate_PersistentFailure_NoPerTickSpam()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient
        {
            CandidatesResponse = CandidatePlan(),
        };
        var vm = new WizardBenchmarkStepViewModel(() => fake, ct => Task.CompletedTask, GatedDelay, logger: capturing.Logger);

        // Clean entry: loads the candidate plan + logs the first observation
        // ("unknown → reachable").
        await vm.RefreshAsync(CancellationToken.None);

        // 3 consecutive identical failures: the candidates are already loaded, so every
        // failure lands on the results fetch. The FIRST logs the transition back; the
        // following two must stay silent (AC6 anti-spam).
        for (var i = 0; i < 3; i++)
        {
            fake.NextCallException = new HttpRequestException($"refused #{i + 1}");
            await vm.RefreshAsync(CancellationToken.None);
        }

        var gateEvents = capturing.Sink.Events
            .Where(e => Rendered(e).Contains("Wizard step 4: benchmark poll — daemon", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, gateEvents.Count); // one entry per STATE CHANGE only — no per-tick spam (AC6)
        var unreachable = gateEvents
            .Where(e => Rendered(e).Contains("→ unreachable", StringComparison.Ordinal))
            .ToList();
        Assert.Single(unreachable);
    }

    // ==================================================================
    //  Step 5 — WizardRecommendationsStepViewModel
    // ==================================================================

    [Fact]
    public async Task Step5_Generate_Success_Information()
    {
        var capturing = new CapturingLogger();
        var vm = new WizardRecommendationsStepViewModel(() => new FakeRouterApiClient(), logger: capturing.Logger);

        await vm.GenerateCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(capturing.Sink.RendersContaining("Wizard step 5: recommendations generated (1 role(s), 1 node(s))"));
    }

    [Fact]
    public async Task Step5_Generate_409_InformationBenchmarkFirst()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient { GenerateError = (HttpStatusCode.Conflict, "no benchmark results") };
        var vm = new WizardRecommendationsStepViewModel(() => fake, logger: capturing.Logger);

        await vm.GenerateCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(capturing.Sink.RendersContaining("Wizard step 5: recommendations generate — none available (409"));
    }

    [Fact]
    public async Task Step5_Apply_Success_InformationWithSummary()
    {
        var capturing = new CapturingLogger();
        var vm = new WizardRecommendationsStepViewModel(() => new FakeRouterApiClient(), logger: capturing.Logger);

        await vm.ApplyCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(capturing.Sink.RendersContaining("Wizard step 5: recommendations applied ("));
    }

    [Fact]
    public async Task Step5_Apply_409_Warning()
    {
        var capturing = new CapturingLogger();
        var fake = new FakeRouterApiClient { ApplyError = (HttpStatusCode.Conflict, "nothing to apply") };
        var vm = new WizardRecommendationsStepViewModel(() => fake, logger: capturing.Logger);

        await vm.ApplyCommand.ExecuteAsync(CancellationToken.None);

        Assert.True(capturing.Sink.RendersContaining("Wizard step 5: recommendations apply failed (409"));
    }

    private static RouterModelSummary Model(string name) => new(name, "Dense", null, 8192, 8192, true);

    /// <summary>Renders a LogEvent EXACTLY like the file sink's {Message:lj} token
    /// (literal, unquoted) and appends the exception — the assertion surface for the
    /// injected capturing logger.</summary>
    private static readonly Serilog.Formatting.Display.MessageTemplateTextFormatter SinkMessageFormatter =
        new("{Message:lj}");

    private static string Rendered(LogEvent e)
    {
        using var writer = new System.IO.StringWriter();
        SinkMessageFormatter.Format(e, writer);
        return new System.Text.StringBuilder(writer.ToString())
            .AppendLine(e.Exception?.ToString() ?? string.Empty).ToString();
    }
}
