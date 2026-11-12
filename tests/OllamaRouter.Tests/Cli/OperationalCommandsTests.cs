using System.IO;
using System.Net;
using System.Text.Json;
using Moq;
using OllamaRouter.Cli;
using OllamaRouter.Cli.Commands;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Endpoints.RouterApi.ApiKeys;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Info;
using OllamaRouter.Endpoints.RouterApi.Preload;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Endpoints.RouterApi.Vram;
using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;

namespace OllamaRouter.Tests.Cli;

/// <summary>
/// Task #101: unit tests of the operational CLI subcommands (fleet/ps, cartography, vram
/// reclaim, preload, target list|add|remove|enable|disable|reset-streak, benchmark
/// run|status|clear) — fake <c>IOllamaRouterApiClient</c> + <c>IOllamaRouterDaemonLauncher</c>
/// (Moq), <c>CliContext</c> with <see cref="StringWriter"/> output; the benchmark wait loop is
/// bounded by the <c>PollInterval</c>/<c>IsInteractive</c> test seams, scripted poll sequences
/// and cancellation tokens (no test may hang, no real console/processes).
/// </summary>
public class OperationalCommandsTests
{
    private static readonly Uri TestBaseUrl = new("http://127.0.0.1:11434/");

    // ------------------------------------------------------------------
    //  harness
    // ------------------------------------------------------------------

    private static CliContext BuildContext(
        Mock<IOllamaRouterApiClient> client,
        Mock<IOllamaRouterDaemonLauncher> launcher,
        out StringWriter output,
        out StringWriter error)
    {
        output = new StringWriter();
        error = new StringWriter();
        return CliContext.Create(
            baseUrl: TestBaseUrl,
            client: client.Object,
            launcher: launcher.Object,
            output: output,
            error: error);
    }

    private static RouterDaemonStatus RunningStatus() =>
        new(
            RouterDaemonState.Running,
            new RouterInfoResponse("9.9.9-test", true,
                new RouterFleetSummary(2, 1, true), new RouterFeatures(true, true, true, true),
                4242, DateTimeOffset.UtcNow.AddSeconds(-3723), 3723.0),
            null);

    private static Mock<IOllamaRouterDaemonLauncher> RunningLauncher()
    {
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.EnsureRunningAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningStatus());
        return launcher;
    }

    private static Mock<IOllamaRouterApiClient> NewClient() => new();

    // ------------------------------------------------------------------
    //  DTO fixtures
    // ------------------------------------------------------------------

    private static RouterFleetResponse TwoTargetFleet() =>
        new(new[]
        {
            new RouterTargetItem("Local", "http://127.0.0.1:11434", true, "Online", 5, "local",
                new RouterTargetHardware("nvidia", new[] { "GPU 0" }, 16000, 4, 1.0, GpuName: "RTX 4090"),
                new RouterTargetExecution(2, "llama3:8b", "4k", ResidentModels: new[] { "llama3:8b" }),
                Preload: new RouterTargetPreload(900, "class"),
                FailureStreak: 3, CircuitState: "Cooldown", CooldownRemainingSeconds: 42.0),
            new RouterTargetItem("Remote", "http://192.168.1.2:11434", false, "Offline", null, "remote",
                new RouterTargetHardware("amd", new[] { "GPU 0" }, 32000, 8, null),
                new RouterTargetExecution(0, null, "-")),
        });

    private static RouterFleetResponse SingleTargetFleet(string name) =>
        new(new[]
        {
            new RouterTargetItem(name, $"http://{name.ToLowerInvariant()}:11434", true, "Online", 5, "local",
                new RouterTargetHardware("nvidia", new[] { "GPU 0" }, 16000, 4, 1.0),
                new RouterTargetExecution(0, null, "4k")),
        });

    private static RouterCartographyResponse CartographyFixture() =>
        new(
            Nodes: new[]
            {
                new CartographyNodeRow("Local", "local", true, "Online", Stale: true,
                    "4k", InFlightRequests: 2, TotalLoadedVramBytes: 8L * 1024 * 1024 * 1024,
                    LoadedModels: new[]
                    {
                        new CartographyNodeLoadedModel("llama3:8b", 8L * 1024 * 1024 * 1024, "Q4_K_M", 8192),
                    },
                    Hardware: new CartographyNodeHardware(false, "RTX 4090", "nvidia", 16000),
                    FailureStreak: 3, CircuitState: "Cooldown", CooldownRemainingSeconds: 42.0),
            },
            Models: new[]
            {
                new CartographyModelRow("llama3:8b", "llama", 4934L * 1024 * 1024, 8192, 4096, true,
                    Deployments: new[]
                    {
                        new CartographyDeploymentRow("Local", true, true, 4096, true, 1,
                            LoadedSizeVramBytes: 4934L * 1024 * 1024, Quantization: "Q4_K_M"),
                        new CartographyDeploymentRow("Remote", true, false, 0, false, 0,
                            VetoReason: "insufficient VRAM"),
                    },
                    Quantization: "Q4_K_M"),
            },
            LoadedModels: new[]
            {
                new CartographyLoadedModelRow("llama3:8b", "Local", 8L * 1024 * 1024 * 1024,
                    "4k", "Online", true, "Q4_K_M", 8192),
            });

    private static RouterConfigResponse ConfigFixture() =>
        new(
            Targets: new[]
            {
                new RouterTargetConfigDto("Local", "http://127.0.0.1:11434", true, "RTX 4090", 4,
                    "ollama", Models: new[] { new TargetModelDto("llama3:8b", 8192) }),
                new RouterTargetConfigDto("Remote", "http://192.168.1.2:11434", false, null, 8,
                    "openai", "OpenAI-compatible", PowerWatts: 150.0),
            },
            Cloud: new RouterCloudConfigDto(false, null, "Local", false),
            DynamicContext: new RouterDynamicContextConfigDto(true, true, 2, 300),
            Recording: new RouterRecordingConfigDto(false, null),
            Resilience: new RouterResilienceConfigDto(),
            Pricing: new RouterPricingConfigDto(null, null, null),
            ApiSecurity: new ApiSecurityConfigDto(false, 24));

    private static RouterVramReclaimResponse ReclaimSuccess(bool disabled = true) =>
        new(true, 2, new[]
        {
            new RouterVramReclaimTargetResult("Local", new[] { "llama3:8b", "qwen:7b" }, disabled),
        });

    private static RouterModelPreloadResponse PreloadResponse(
        bool loadPath, string? reason = "selected by class")
    {
        var candidates = new[]
        {
            new RouterModelPreloadCandidate("Local", false, 3, true, 8192, false, true, "busy"),
            new RouterModelPreloadCandidate("Remote", true, 0, false, 16384, true, false),
        };
        return new RouterModelPreloadResponse(
            "llama3", "Remote", -1, 4096, 8192,
            loadPath ? true : null,
            loadPath ? false : null,
            loadPath ? 1234L : null,
            candidates,
            reason);
    }

    private static readonly DateTimeOffset StartedAt = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FinishedAt = new(2026, 11, 10, 10, 5, 0, TimeSpan.Zero);

    private static RouterBenchmarkRunResponse RunResponse() =>
        new(true, new[] { "Local", "Remote" });

    private static RouterBenchmarkResultsResponse RunningResults() =>
        new(new[]
        {
            new BenchmarkTargetResult("Local", BenchmarkRunStatus.Running, StartedAt, null, []),
            new BenchmarkTargetResult("Remote", BenchmarkRunStatus.Running, StartedAt, null, []),
        });

    private static RouterBenchmarkResultsResponse FinishedResults() =>
        new(new[]
        {
            new BenchmarkTargetResult("Local", BenchmarkRunStatus.Succeeded, StartedAt, FinishedAt, new[]
            {
                new BenchmarkContextResult("llama3:8b", UsageRole.AgenticChat, 4096, 200,
                    4_000_000_000L, 51.5, 1200.3, Viable: true, Reason: null),
                new BenchmarkContextResult("llama3:8b", UsageRole.AgenticChat, 8192, 0,
                    0L, 0.0, 0.0, Viable: false, Reason: "oom"),
            }),
            new BenchmarkTargetResult("Remote", BenchmarkRunStatus.Succeeded, StartedAt, FinishedAt, []),
        });

    // ------------------------------------------------------------------
    //  fleet / cartography
    // ------------------------------------------------------------------

    [Fact]
    public async Task Fleet_HumanTable_ShowsEveryRequiredDatum()
    {
        var client = NewClient();
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoTargetFleet());

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new FleetCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Fleet: http://127.0.0.1:11434 — 2 target(s)", text);
        Assert.Contains("Name", text);
        Assert.Contains("Online (5ms)", text);
        Assert.Contains("Offline", text);
        Assert.Contains("yes", text);
        Assert.Contains("no", text);
        Assert.Contains("local", text);
        Assert.Contains("remote", text);
        Assert.Contains("nvidia RTX 4090", text);
        Assert.Contains("16000", text);
        Assert.Contains("32000", text);
        Assert.Contains("900s class", text);
        Assert.Contains("Cooldown (42s)", text);
        Assert.Contains("     resident: llama3:8b", text);
        client.Verify(c => c.GetFleetAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Fleet_Json_PrintsExactWireDocument()
    {
        var client = NewClient();
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoTargetFleet());

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new FleetCommand().ExecuteAsync(context, ["--json"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().Trim().Split('\n');
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        var targets = doc.RootElement.GetProperty("targets");
        Assert.Equal(2, targets.GetArrayLength());
        Assert.Equal("Local", targets[0].GetProperty("name").GetString());
        Assert.Equal("Cooldown", targets[0].GetProperty("circuitState").GetString());
        Assert.Equal(42.0, targets[0].GetProperty("cooldownRemainingSeconds").GetDouble());
        Assert.Equal(900, targets[0].GetProperty("preload").GetProperty("keepAliveSeconds").GetInt32());
        Assert.Equal("class", targets[0].GetProperty("preload").GetProperty("keepAliveSource").GetString());
        Assert.Equal(5, targets[0].GetProperty("latencyMs").GetInt64());
    }

    [Fact]
    public async Task Fleet_UnknownFlag_Exit2WithUsageAndZeroClientCalls()
    {
        var client = NewClient();
        var context = BuildContext(client, RunningLauncher(), out _, out var error);

        var exitCode = await new FleetCommand().ExecuteAsync(context, ["--bogus"], CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal("usage: ollamarouter fleet [--json|-j]", error.ToString().Trim());
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Cartography_Human_PrintsAllThreeSections()
    {
        var client = NewClient();
        client.Setup(c => c.GetCartographyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CartographyFixture());

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new CartographyCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Cartography: http://127.0.0.1:11434 — 1 node(s)", text);
        Assert.Contains("Nodes:", text);
        Assert.Contains("Online (stale)", text);
        Assert.Contains("     loaded: 8192 MB", text);
        Assert.Contains("Cooldown (42s)", text);
        Assert.Contains("Loaded models:", text);
        Assert.Contains("llama3:8b", text);
        Assert.Contains("Q4_K_M", text);
        Assert.Contains("ctx 8192", text);
        Assert.Contains("Models:", text);
        Assert.Contains(
            "  llama3:8b  (llama, Q4_K_M, 4934 MB, native ctx 8192, recommended ctx 4096, cloud overflow: yes)",
            text);
        Assert.Contains("resident yes", text);
        Assert.Contains("capable no", text);
        Assert.Contains("veto: insufficient VRAM", text);
    }

    [Fact]
    public async Task Cartography_Json_PrintsExactWireDocument()
    {
        var client = NewClient();
        client.Setup(c => c.GetCartographyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CartographyFixture());

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new CartographyCommand().ExecuteAsync(context, ["-j"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().Trim().Split('\n');
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("nodes").GetArrayLength());
        Assert.Equal(1, root.GetProperty("loadedModels").GetArrayLength());
        var deployment = root.GetProperty("models")[0].GetProperty("deployments")[1];
        Assert.Equal("insufficient VRAM", deployment.GetProperty("vetoReason").GetString());
        Assert.Equal("llama3:8b", root.GetProperty("loadedModels")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Fleet_Gate_PortOccupiedByOtherService_Exit1BeforeAnyApiCall()
    {
        var client = NewClient();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.EnsureRunningAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterDaemonStatus(RouterDaemonState.PortOccupiedByOtherService, null,
                "port 11434 is already in use by another process"));

        var context = BuildContext(client, launcher, out _, out var error);

        var exitCode = await new FleetCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal("error: port 11434 is already in use by another process", error.ToString().Trim());
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BenchmarkStatus_Gate_DaemonException_Exit1BeforeAnyApiCall()
    {
        var client = NewClient();
        var launcher = new Mock<IOllamaRouterDaemonLauncher>();
        launcher.Setup(l => l.EnsureRunningAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RouterDaemonException("cannot launch the daemon: executable not found"));

        var context = BuildContext(client, launcher, out _, out var error);

        var exitCode = await new BenchmarkCommand().ExecuteAsync(context, ["status"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal("error: cannot launch the daemon: executable not found", error.ToString().Trim());
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Dispatcher_PsAlias_RoutesToFleetCommand()
    {
        var client = NewClient();
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoTargetFleet());
        var launcher = RunningLauncher();

        var context = BuildContext(client, launcher, out _, out _);

        var exitCode = await CliDispatcher.DispatchAsync(
            ["ps"], context, () => false, CancellationToken.None);

        Assert.Equal(0, exitCode);
        launcher.Verify(l => l.EnsureRunningAsync(It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetFleetAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ------------------------------------------------------------------
    //  vram reclaim
    // ------------------------------------------------------------------

    [Fact]
    public async Task VramReclaim_Default_DisablesTargetsAndPrintsPerTargetAndTotal()
    {
        RouterVramReclaimRequest? captured = null;
        var client = NewClient();
        client.Setup(c => c.ReclaimVramAsync(It.IsAny<RouterVramReclaimRequest?>(), It.IsAny<CancellationToken>()))
            .Callback<RouterVramReclaimRequest?, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(ReclaimSuccess(disabled: true));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new VramCommand().ExecuteAsync(context, ["reclaim"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        // disableTarget: !keepEnabled — a plain reclaim disables the processed targets.
        Assert.Equal(new RouterVramReclaimRequest(null, null, true), captured);
        var text = output.ToString();
        Assert.Contains("Local: unloaded 2 model(s): llama3:8b, qwen:7b ; disabled: yes", text);
        Assert.Contains("total: 2 model(s) unloaded (per-node residency: ollamarouter cartography)", text);
    }

    [Fact]
    public async Task VramReclaim_KeepEnabledAndTarget_PinTheRequest()
    {
        RouterVramReclaimRequest? captured = null;
        var client = NewClient();
        client.Setup(c => c.ReclaimVramAsync(It.IsAny<RouterVramReclaimRequest?>(), It.IsAny<CancellationToken>()))
            .Callback<RouterVramReclaimRequest?, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(ReclaimSuccess(disabled: false));

        var context = BuildContext(client, RunningLauncher(), out _, out _);

        var exitCode = await new VramCommand().ExecuteAsync(
            context, ["reclaim", "--keep-enabled", "--target", "Local"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(new RouterVramReclaimRequest("Local", null, false), captured);
    }

    [Fact]
    public async Task VramReclaim_SuccessFalse_Human_PrintsMessageToStderrExit1()
    {
        var client = NewClient();
        client.Setup(c => c.ReclaimVramAsync(It.IsAny<RouterVramReclaimRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterVramReclaimResponse(false, 0, [], "no targets configured"));

        var context = BuildContext(client, RunningLauncher(), out var output, out var error);

        var exitCode = await new VramCommand().ExecuteAsync(context, ["reclaim"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal("error: no targets configured", error.ToString().Trim());
        Assert.True(output.ToString().Trim().Length == 0, "the human mode prints nothing on stdout");
    }

    [Fact]
    public async Task VramReclaim_SuccessFalse_Json_PrintsDocumentToStdoutAndExits1()
    {
        var client = NewClient();
        client.Setup(c => c.ReclaimVramAsync(It.IsAny<RouterVramReclaimRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterVramReclaimResponse(false, 0, [], "no targets configured"));

        var context = BuildContext(client, RunningLauncher(), out var output, out var error);

        var exitCode = await new VramCommand().ExecuteAsync(
            context, ["reclaim", "--json"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        using var doc = JsonDocument.Parse(output.ToString().Trim());
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("no targets configured", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("error: no targets configured", error.ToString().Trim());
    }

    [Fact]
    public async Task VramReclaim_Api404_Exit1WithServerError()
    {
        var client = NewClient();
        client.Setup(c => c.ReclaimVramAsync(It.IsAny<RouterVramReclaimRequest?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.NotFound, "target not found: Nope"));

        var context = BuildContext(client, RunningLauncher(), out _, out var error);

        var exitCode = await new VramCommand().ExecuteAsync(
            context, ["reclaim", "--target", "Nope"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("error: target not found: Nope", error.ToString());
    }

    [Fact]
    public async Task Vram_UsageErrors_Exit2()
    {
        string[][] subArgsCases =
        {
            [],
            ["frobnicate"],
            ["reclaim", "--target"],   // --target without a value
            ["reclaim", "--bogus"],
        };
        foreach (var subArgs in subArgsCases)
        {
            var client = NewClient();
            var context = BuildContext(client, RunningLauncher(), out _, out var error);

            var exitCode = await new VramCommand().ExecuteAsync(context, subArgs, CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal(
                "usage: ollamarouter vram reclaim [--keep-enabled] [--target <name>] [--json|-j]",
                error.ToString().Trim());
            client.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task VramReclaim_Json_Success_ParsesAsReclaimResponse()
    {
        var client = NewClient();
        client.Setup(c => c.ReclaimVramAsync(It.IsAny<RouterVramReclaimRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReclaimSuccess(disabled: false));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new VramCommand().ExecuteAsync(
            context, ["reclaim", "--keep-enabled", "-j"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(output.ToString().Trim());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(2, root.GetProperty("totalUnloaded").GetInt32());
        Assert.False(root.GetProperty("targets")[0].GetProperty("disabled").GetBoolean());
    }

    // ------------------------------------------------------------------
    //  preload
    // ------------------------------------------------------------------

    [Fact]
    public async Task Preload_UsageErrors_Exit2()
    {
        string[][] subArgsCases =
        {
            [],
            ["a", "b"],
            ["m", "--keep-alive", "abc"],
            ["m", "--context", "xyz"],
            ["m", "--bogus"],
        };
        foreach (var subArgs in subArgsCases)
        {
            var client = NewClient();
            var context = BuildContext(client, RunningLauncher(), out _, out var error);

            var exitCode = await new PreloadCommand().ExecuteAsync(context, subArgs, CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Contains(
                "usage: ollamarouter preload <model> [--context <tokens>] [--keep-alive <seconds>] [--target <name>] [--preview] [--json|-j]",
                error.ToString());
            client.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task Preload_LoadPath_CapturesRequestAndPrintsEveryField()
    {
        string? capturedModel = null;
        RouterModelPreloadRequest? capturedRequest = null;
        var client = NewClient();
        client.Setup(c => c.PreloadModelAsync(
                It.IsAny<string>(), It.IsAny<RouterModelPreloadRequest?>(), It.IsAny<CancellationToken>()))
            .Callback<string, RouterModelPreloadRequest?, CancellationToken>(
                (model, request, _) =>
                {
                    capturedModel = model;
                    capturedRequest = request;
                })
            .ReturnsAsync(PreloadResponse(loadPath: true));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new PreloadCommand().ExecuteAsync(
            context, ["llama3", "--keep-alive", "-1", "--context", "8192", "--target", "Remote"],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("llama3", capturedModel);
        Assert.Equal(new RouterModelPreloadRequest(-1, 8192, "Remote"), capturedRequest);
        var text = output.ToString();
        Assert.Contains("preload: llama3 → Remote", text);
        Assert.Contains("keep_alive: indefinite", text);
        Assert.Contains("num_ctx: 4096 (intent: 8192)", text);
        Assert.Contains("load accepted: yes", text);
        Assert.Contains("residency confirmed: no", text);
        Assert.Contains("duration: 1234 ms", text);
        Assert.Contains("reason: selected by class", text);
        Assert.Contains("candidates:", text);
        Assert.Contains("  [ ] Local — skipped: busy", text);
        Assert.Contains("  [x] Remote", text);
    }

    [Fact]
    public async Task Preload_LoadPath_FailSoft_LoadNotAccepted_StillExit0()
    {
        var client = NewClient();
        client.Setup(c => c.PreloadModelAsync(
                It.IsAny<string>(), It.IsAny<RouterModelPreloadRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterModelPreloadResponse(
                "llama3", "Local", 900, null, null,
                LoadAccepted: false, ResidencyConfirmed: null, PreloadDurationMs: null,
                Candidates: [], Reason: "cold load continued server-side"));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new PreloadCommand().ExecuteAsync(
            context, ["llama3"], CancellationToken.None);

        // 200 is fail-soft: the daemon proceeds server-side even when the bounded load call
        // is missed — the CLI exits 0 and prints the flags explicitly.
        Assert.Equal(0, exitCode);
        Assert.Contains("load accepted: no", output.ToString());
    }

    [Fact]
    public async Task Preload_Preview_CallsPreviewEndpointOnly()
    {
        var client = NewClient();
        client.Setup(c => c.PreviewPreloadAsync("llama3", -1, 8192, "Remote", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PreloadResponse(loadPath: false, "preview"));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new PreloadCommand().ExecuteAsync(
            context, ["llama3", "--keep-alive", "-1", "--context", "8192", "--target", "Remote", "--preview"],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        client.Verify(c => c.PreviewPreloadAsync("llama3", -1, 8192, "Remote", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.PreloadModelAsync(
            It.IsAny<string>(), It.IsAny<RouterModelPreloadRequest?>(), It.IsAny<CancellationToken>()), Times.Never);
        var text = output.ToString();
        Assert.Contains("preload: llama3 → Remote", text);
        Assert.DoesNotContain("load accepted", text);
        Assert.DoesNotContain("residency confirmed", text);
        Assert.DoesNotContain("duration:", text);
    }

    [Fact]
    public async Task Preload_ServerRejection_400OnKeepAliveZero_Exit1()
    {
        var client = NewClient();
        client.Setup(c => c.PreloadModelAsync(
                It.IsAny<string>(), It.IsAny<RouterModelPreloadRequest?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.BadRequest,
                "keepAliveSeconds must be -1 (indefinitely) or positive"));

        var context = BuildContext(client, RunningLauncher(), out _, out var error);

        // No client-side pre-validation: 0 is a legal integer, the SERVER rejects it with 400.
        var exitCode = await new PreloadCommand().ExecuteAsync(
            context, ["m", "--keep-alive", "0"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("error: keepAliveSeconds must be -1 (indefinitely) or positive", error.ToString());
    }

    [Fact]
    public async Task Preload_Server404_UnknownModel_Exit1()
    {
        var client = NewClient();
        client.Setup(c => c.PreloadModelAsync(
                It.IsAny<string>(), It.IsAny<RouterModelPreloadRequest?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.NotFound, "unknown model: nope"));

        var context = BuildContext(client, RunningLauncher(), out _, out var error);

        var exitCode = await new PreloadCommand().ExecuteAsync(context, ["nope"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("error: unknown model: nope", error.ToString());
    }

    [Fact]
    public async Task Preload_Json_PrintsExactWireDocument()
    {
        var client = NewClient();
        client.Setup(c => c.PreloadModelAsync(
                It.IsAny<string>(), It.IsAny<RouterModelPreloadRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PreloadResponse(loadPath: true));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new PreloadCommand().ExecuteAsync(
            context, ["llama3", "--json"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().Trim().Split('\n');
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        Assert.Equal("llama3", root.GetProperty("model").GetString());
        Assert.Equal("Remote", root.GetProperty("selectedTarget").GetString());
        Assert.Equal(-1, root.GetProperty("keepAliveSeconds").GetInt32());
        Assert.Equal(4096, root.GetProperty("numCtx").GetInt32());
        Assert.Equal(8192, root.GetProperty("contextIntentTokens").GetInt32());
        Assert.True(root.GetProperty("loadAccepted").GetBoolean());
        Assert.False(root.GetProperty("residencyConfirmed").GetBoolean());
    }

    // ------------------------------------------------------------------
    //  target list
    // ------------------------------------------------------------------

    [Fact]
    public async Task TargetList_JoinsConfigWithFleetRuntimeAndPrintsRoster()
    {
        var client = NewClient();
        client.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfigFixture());
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoTargetFleet());

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new TargetCommand().ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        client.Verify(c => c.GetConfigAsync(It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetFleetAsync(It.IsAny<CancellationToken>()), Times.Once);
        var text = output.ToString();
        Assert.Contains("Targets: 2 configured", text);
        Assert.Contains("Liveness", text);
        Assert.Contains("MaxVRAM", text);
        Assert.Contains("Speed", text);
        Assert.Contains("Power", text);
        Assert.Contains("Online (5ms)", text);
        Assert.Contains("16000", text);
        Assert.Contains("1.0", text);       // effective speed factor, one decimal
        Assert.Contains("150", text);       // powerWatts from config
        Assert.Contains("-", text);         // powerWatts unset for Local
        Assert.Contains("     models: llama3:8b (ctx 8192)", text);
    }

    [Fact]
    public async Task TargetList_FleetFailure_BestEffort_Exit0PersistentOnly()
    {
        var client = NewClient();
        client.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfigFixture());
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.InternalServerError, "boom"));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new TargetCommand().ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Targets: 2 configured", text);
        Assert.Contains("Power", text);
        Assert.DoesNotContain("Liveness", text);
        Assert.DoesNotContain("MaxVRAM", text);
    }

    [Fact]
    public async Task TargetList_Json_PrintsPersistentConfigOnly()
    {
        var client = NewClient();
        client.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfigFixture());
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoTargetFleet());

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new TargetCommand().ExecuteAsync(context, ["list", "--json"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().Trim().Split('\n');
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        var targets = doc.RootElement.GetProperty("targets");
        Assert.Equal(2, targets.GetArrayLength());
        Assert.Equal("openai", targets[1].GetProperty("type").GetString());
        Assert.Equal(150.0, targets[1].GetProperty("powerWatts").GetDouble());
    }

    [Fact]
    public async Task TargetList_UnknownFlag_Exit2()
    {
        var client = NewClient();
        var context = BuildContext(client, RunningLauncher(), out _, out var error);

        var exitCode = await new TargetCommand().ExecuteAsync(context, ["list", "--wat"], CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("usage: ollamarouter target list [--json]", error.ToString());
    }

    // ------------------------------------------------------------------
    //  target add / remove
    // ------------------------------------------------------------------

    [Fact]
    public async Task TargetAdd_CapturesEveryWireFieldAndNeverEchoesTheApiKey()
    {
        AddTargetRequest? captured = null;
        var client = NewClient();
        client.Setup(c => c.AddTargetAsync(It.IsAny<AddTargetRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AddTargetRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new AddTargetResponse(true, true, "Online", 3, null, 12));

        var context = BuildContext(client, RunningLauncher(), out var output, out var error);

        var exitCode = await new TargetCommand().ExecuteAsync(
            context,
            ["add", "MyT", "http://h:1", "--type", "openai", "--provider", "x",
             "--api-key", "SECRET", "--power-watts", "250"],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal("MyT", captured!.Name);
        Assert.Equal("http://h:1", captured.Url);
        Assert.Equal("openai", captured.Type);
        Assert.Equal("x", captured.Provider);
        Assert.Equal("SECRET", captured.ApiKey);
        Assert.Equal(250.0, captured.PowerWatts);
        var text = output.ToString();
        Assert.Contains(
            "added target 'MyT' (openai, http://h:1) — liveness Online, 3 model(s) discovered, 12 ms",
            text);
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Fact]
    public async Task TargetAdd_TypeDefaultsToNullWithoutFlag()
    {
        AddTargetRequest? captured = null;
        var client = NewClient();
        client.Setup(c => c.AddTargetAsync(It.IsAny<AddTargetRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AddTargetRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new AddTargetResponse(true, true, "Online", 0, null, null));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new TargetCommand().ExecuteAsync(
            context, ["add", "T", "http://t:1"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Null(captured!.Type);
        Assert.Null(captured.PowerWatts);
        // type falls back: response target null → requested flag null → "ollama".
        Assert.Contains("added target 'T' (ollama, http://t:1) — liveness Online, 0 model(s) discovered", output.ToString());
    }

    [Fact]
    public async Task TargetAdd_Conflict_Exit1WithServerError()
    {
        var client = NewClient();
        client.Setup(c => c.AddTargetAsync(It.IsAny<AddTargetRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.Conflict,
                "target name already exists: MyT"));

        var context = BuildContext(client, RunningLauncher(), out _, out var error);

        var exitCode = await new TargetCommand().ExecuteAsync(
            context, ["add", "MyT", "http://h:1"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("error: target name already exists: MyT", error.ToString());
    }

    [Fact]
    public async Task TargetAdd_MissingUrl_Exit2()
    {
        var client = NewClient();
        var context = BuildContext(client, RunningLauncher(), out _, out var error);

        var exitCode = await new TargetCommand().ExecuteAsync(context, ["add", "MyT"], CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("usage: ollamarouter target list|add|remove|enable|disable|reset-streak", error.ToString());
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TargetRemove_PrintsRemovalLine()
    {
        var client = NewClient();
        client.Setup(c => c.DeleteTargetAsync("T", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteTargetResponse(true, "removed"));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new TargetCommand().ExecuteAsync(context, ["remove", "T"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("removed target 'T'", output.ToString());
    }

    [Fact]
    public async Task TargetRemove_NotFound_Exit1()
    {
        var client = NewClient();
        client.Setup(c => c.DeleteTargetAsync("Nope", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.NotFound, "target not found: Nope"));

        var context = BuildContext(client, RunningLauncher(), out _, out var error);

        var exitCode = await new TargetCommand().ExecuteAsync(context, ["remove", "Nope"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("error: target not found: Nope", error.ToString());
    }

    // ------------------------------------------------------------------
    //  target enable / disable
    // ------------------------------------------------------------------

    [Fact]
    public async Task TargetEnable_ValidatesNameAgainstFleetCaseInsensitivelyAndPostsTrue()
    {
        Dictionary<string, bool>? captured = null;
        var client = NewClient();
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SingleTargetFleet("Local"));
        client.Setup(c => c.UpdateTargetAvailabilityAsync(
                It.IsAny<IReadOnlyDictionary<string, bool>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyDictionary<string, bool>, CancellationToken>((flags, _) => captured = new Dictionary<string, bool>(flags))
            .ReturnsAsync(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["local"] = true });

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        // The fleet has 'Local'; 't' is NOT in the fleet → the CLI must reject BEFORE POST /targets.
        var unknownExit = await new TargetCommand().ExecuteAsync(context, ["enable", "Nope"], CancellationToken.None);
        Assert.Equal(1, unknownExit);
        Assert.Contains("error: unknown target 'Nope' (see ollamarouter target list)", context.Error.ToString());
        client.Verify(c => c.UpdateTargetAvailabilityAsync(
            It.IsAny<IReadOnlyDictionary<string, bool>>(), It.IsAny<CancellationToken>()), Times.Never,
            "an unknown name must NEVER reach POST /targets");

        // Case-insensitive match: 'local' is in the fleet.
        var exitCode = await new TargetCommand().ExecuteAsync(context, ["enable", "local"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Single(captured!);
        Assert.True(captured!.ContainsKey("local"));
        Assert.True(captured["local"]);
        Assert.Contains("target 'local' enabled", output.ToString());
    }

    [Fact]
    public async Task TargetDisable_PostsFalse()
    {
        Dictionary<string, bool>? captured = null;
        var client = NewClient();
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SingleTargetFleet("Local"));
        client.Setup(c => c.UpdateTargetAvailabilityAsync(
                It.IsAny<IReadOnlyDictionary<string, bool>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyDictionary<string, bool>, CancellationToken>((flags, _) => captured = new Dictionary<string, bool>(flags))
            .ReturnsAsync(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["Local"] = false });

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new TargetCommand().ExecuteAsync(context, ["disable", "Local"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Single(captured!);
        Assert.False(captured!["Local"]);
        Assert.Contains("target 'Local' disabled", output.ToString());
    }

    [Fact]
    public async Task TargetEnable_RequestedKeyMissingFromResponse_Exit1()
    {
        var client = NewClient();
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SingleTargetFleet("Local"));
        client.Setup(c => c.UpdateTargetAvailabilityAsync(
                It.IsAny<IReadOnlyDictionary<string, bool>>(), It.IsAny<CancellationToken>()))
            // The daemon echoed something else entirely — treat it as an error.
            .ReturnsAsync(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["Other"] = true });

        var context = BuildContext(client, RunningLauncher(), out _, out var error);

        var exitCode = await new TargetCommand().ExecuteAsync(context, ["enable", "Local"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("error: the daemon did not acknowledge target 'Local'", error.ToString());
    }

    // ------------------------------------------------------------------
    //  target reset-streak
    // ------------------------------------------------------------------

    [Fact]
    public async Task TargetResetStreak_PerTarget_PrintsStreakAndCircuit()
    {
        var client = NewClient();
        client.Setup(c => c.ResetTargetStreakAsync("T", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResetTargetStreakResponse(true, "T", 0, "Closed"));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new TargetCommand().ExecuteAsync(
            context, ["reset-streak", "T"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("streak reset for 'T': streak 0, circuit Closed", output.ToString());
    }

    [Fact]
    public async Task TargetResetStreak_PerTarget_404_Exit1()
    {
        var client = NewClient();
        client.Setup(c => c.ResetTargetStreakAsync("Nope", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.NotFound, "target not found: Nope"));

        var context = BuildContext(client, RunningLauncher(), out _, out var error);

        var exitCode = await new TargetCommand().ExecuteAsync(
            context, ["reset-streak", "Nope"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("error: target not found: Nope", error.ToString());
    }

    [Fact]
    public async Task TargetResetStreak_All_PrintsPerTargetRowsAndSummary()
    {
        var client = NewClient();
        client.Setup(c => c.ResetFleetStreaksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResetFleetStreaksResponse(true, 2, new[]
            {
                new ResetFleetStreaksTarget("T", 3, true),
                new ResetFleetStreaksTarget("R", 0, false),
            }));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new TargetCommand().ExecuteAsync(
            context, ["reset-streak", "--all"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("  T: previous streak 3", text);
        Assert.Contains("  R: previous streak 0", text);
        Assert.Contains("reset 2 target(s)", text);
    }

    [Fact]
    public async Task TargetResetStreak_BareOrBoth_UsageExit2()
    {
        // D2: a bare invocation (neither <name> nor --all) AND both at once are usage errors.
        string[][] subArgsCases = { [], ["T", "--all"] };
        foreach (var subArgs in subArgsCases)
        {
            var client = NewClient();
            var context = BuildContext(client, RunningLauncher(), out _, out var error);

            var exitCode = await new TargetCommand().ExecuteAsync(
                context, ["reset-streak", ..subArgs], CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal("usage: ollamarouter target reset-streak <name> | --all", error.ToString().Trim());
            client.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task Target_MissingOrUnknownSubverb_Exit2()
    {
        string[][] subArgsCases = { [], ["frobnicate"], ["remove", "A", "B"] };
        foreach (var subArgs in subArgsCases)
        {
            var client = NewClient();
            var context = BuildContext(client, RunningLauncher(), out _, out var error);

            var exitCode = await new TargetCommand().ExecuteAsync(context, subArgs, CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Contains("usage: ollamarouter target list|add|remove|enable|disable|reset-streak", error.ToString());
            client.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task Target_JsonVariants_PrintExactWireDocuments()
    {
        var client = NewClient();
        client.Setup(c => c.ResetTargetStreakAsync("T", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResetTargetStreakResponse(true, "T", 0, "Closed"));
        client.Setup(c => c.ResetFleetStreaksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResetFleetStreaksResponse(true, 2, new[] { new ResetFleetStreaksTarget("T", 3, true) }));
        client.Setup(c => c.DeleteTargetAsync("T", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteTargetResponse(true, "removed"));
        client.Setup(c => c.GetFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SingleTargetFleet("T"));
        client.Setup(c => c.UpdateTargetAvailabilityAsync(
                It.IsAny<IReadOnlyDictionary<string, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["T"] = true });

        var launcher = RunningLauncher();

        // reset-streak T --json
        var context = BuildContext(client, launcher, out var out1, out _);
        Assert.Equal(0, await new TargetCommand().ExecuteAsync(context, ["reset-streak", "T", "--json"], CancellationToken.None));
        using (var doc1 = JsonDocument.Parse(out1.ToString().Trim()))
        {
            Assert.Equal(0, doc1.RootElement.GetProperty("streak").GetInt32());
            Assert.Equal("Closed", doc1.RootElement.GetProperty("status").GetString());
        }

        // reset-streak --all --json
        context = BuildContext(client, launcher, out var out2, out _);
        Assert.Equal(0, await new TargetCommand().ExecuteAsync(context, ["reset-streak", "--all", "--json"], CancellationToken.None));
        using (var doc2 = JsonDocument.Parse(out2.ToString().Trim()))
        {
            Assert.Equal(2, doc2.RootElement.GetProperty("resetCount").GetInt32());
        }

        // remove T --json
        context = BuildContext(client, launcher, out var out3, out _);
        Assert.Equal(0, await new TargetCommand().ExecuteAsync(context, ["remove", "T", "--json"], CancellationToken.None));
        using (var doc3 = JsonDocument.Parse(out3.ToString().Trim()))
        {
            Assert.True(doc3.RootElement.GetProperty("success").GetBoolean());
        }

        // enable T --json → the applied Dictionary<string, bool>
        context = BuildContext(client, launcher, out var out4, out _);
        Assert.Equal(0, await new TargetCommand().ExecuteAsync(context, ["enable", "T", "--json"], CancellationToken.None));
        using var doc4 = JsonDocument.Parse(out4.ToString().Trim());
        Assert.True(doc4.RootElement.GetProperty("T").GetBoolean());
    }

    // ------------------------------------------------------------------
    //  benchmark
    // ------------------------------------------------------------------

    [Fact]
    public async Task BenchmarkRun_NonInteractive_FireAndForget_NeverPolls()
    {
        RouterBenchmarkRequest? captured = null;
        var client = NewClient();
        client.Setup(c => c.StartBenchmarkAsync(It.IsAny<RouterBenchmarkRequest?>(), It.IsAny<CancellationToken>()))
            .Callback<RouterBenchmarkRequest?, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(RunResponse());

        var context = BuildContext(client, RunningLauncher(), out var output, out _);
        var command = new BenchmarkCommand { IsInteractive = () => false };

        var exitCode = await command.ExecuteAsync(context, ["run"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Null(captured!.Target);
        Assert.Null(captured.Model);
        client.Verify(c => c.StartBenchmarkAsync(It.IsAny<RouterBenchmarkRequest?>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()), Times.Never);
        var text = output.ToString();
        Assert.Contains("benchmark queued: Local, Remote", text);
        Assert.Contains("use 'ollamarouter benchmark status' to track progress", text);
    }

    [Fact]
    public async Task BenchmarkRun_Wait_Human_PrintsOneProgressLinePerPollThenFinalMatrix()
    {
        var client = NewClient();
        client.Setup(c => c.StartBenchmarkAsync(It.IsAny<RouterBenchmarkRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunResponse());
        client.SetupSequence(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningResults())
            .ReturnsAsync(FinishedResults());

        var context = BuildContext(client, RunningLauncher(), out var output, out _);
        var command = new BenchmarkCommand
        {
            IsInteractive = () => true,
            PollInterval = TimeSpan.FromMilliseconds(10),
        };

        var exitCode = await command.ExecuteAsync(context, ["run", "--wait"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        client.Verify(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        var lines = output.ToString().Split('\n');
        Assert.Contains("benchmark: Local=Running Remote=Running", lines);
        Assert.Contains("benchmark: Local=Succeeded Remote=Succeeded", lines);
        Assert.Contains("benchmark: finished", lines);
        // Exactly ONE progress line per poll (two polls → two 'benchmark: T=' lines).
        Assert.Equal(2, lines.Count(l => l.StartsWith("benchmark: ", StringComparison.Ordinal)
            && l.Contains("=", StringComparison.Ordinal)));
        // The final viability matrix (same table as 'benchmark status').
        Assert.Contains(
            "Local (Succeeded, started 2026-11-10 10:00:00Z, finished 2026-11-10 10:05:00Z)",
            output.ToString());
        Assert.Contains("AgenticChat", output.ToString());
        Assert.Contains("4096", output.ToString());
        Assert.Contains("51.5 dec/s", output.ToString());
        Assert.Contains("1200.3 pre/s", output.ToString());
        Assert.Contains("viable yes", output.ToString());
        Assert.Contains("viable no  oom", output.ToString());
    }

    [Fact]
    public async Task BenchmarkRun_Wait_Json_PrintsExactlyOneFinalDocument()
    {
        var client = NewClient();
        client.Setup(c => c.StartBenchmarkAsync(It.IsAny<RouterBenchmarkRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunResponse());
        client.SetupSequence(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningResults())
            .ReturnsAsync(FinishedResults());

        var context = BuildContext(client, RunningLauncher(), out var output, out _);
        var command = new BenchmarkCommand
        {
            IsInteractive = () => true,
            PollInterval = TimeSpan.FromMilliseconds(10),
        };

        var exitCode = await command.ExecuteAsync(context, ["run", "--wait", "--json"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.DoesNotContain("benchmark:", text);   // NO progress lines in --json mode
        var lines = text.Trim().Split('\n');
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        var targets = doc.RootElement.GetProperty("targets");
        Assert.Equal(2, targets.GetArrayLength());
        Assert.Equal("Succeeded", targets[0].GetProperty("status").GetString());
        Assert.Equal("oom", targets[0].GetProperty("results")[1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task BenchmarkRun_TargetAndModel_CaptureTheRequest()
    {
        RouterBenchmarkRequest? captured = null;
        var client = NewClient();
        client.Setup(c => c.StartBenchmarkAsync(It.IsAny<RouterBenchmarkRequest?>(), It.IsAny<CancellationToken>()))
            .Callback<RouterBenchmarkRequest?, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(RunResponse());

        var context = BuildContext(client, RunningLauncher(), out _, out _);
        var command = new BenchmarkCommand { IsInteractive = () => false };

        var exitCode = await command.ExecuteAsync(
            context, ["run", "--target", "X", "--model", "Y"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("X", captured!.Target);
        Assert.Equal("Y", captured!.Model);
    }

    [Fact]
    public async Task BenchmarkRun_SingleFlight409_Exit1WithStatusHint()
    {
        var client = NewClient();
        client.Setup(c => c.StartBenchmarkAsync(It.IsAny<RouterBenchmarkRequest?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.Conflict,
                "benchmark run already in progress"));

        var context = BuildContext(client, RunningLauncher(), out _, out var error);
        var command = new BenchmarkCommand { IsInteractive = () => false };

        var exitCode = await command.ExecuteAsync(context, ["run"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        var text = error.ToString();
        Assert.Contains("error: benchmark run already in progress", text);
        Assert.Contains("hint: a run is already in progress — track it with 'ollamarouter benchmark status'", text);
        client.Verify(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BenchmarkRun_TransientPollFailure_OneWarningThenContinues()
    {
        var client = NewClient();
        client.Setup(c => c.StartBenchmarkAsync(It.IsAny<RouterBenchmarkRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunResponse());
        client.SetupSequence(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.InternalServerError, "transient"))
            .ReturnsAsync(FinishedResults());

        var context = BuildContext(client, RunningLauncher(), out var output, out var error);
        var command = new BenchmarkCommand
        {
            IsInteractive = () => true,
            PollInterval = TimeSpan.FromMilliseconds(10),
        };

        var exitCode = await command.ExecuteAsync(context, ["run", "--wait"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var warnings = error.ToString().Split('\n')
            .Where(l => l.Contains("warning: benchmark poll failed; retrying")).ToList();
        Assert.Single(warnings);
        Assert.Contains("benchmark: finished", output.ToString());
    }

    [Fact]
    public async Task BenchmarkRun_Cancelled_Exit0CleanlyWithoutFinalMatrix()
    {
        var client = NewClient();
        client.Setup(c => c.StartBenchmarkAsync(It.IsAny<RouterBenchmarkRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunResponse());
        // The polls stay 'Running' forever (Moq sticks the last value) — only the CTS bounds
        // the loop, so this test cannot hang.
        client.Setup(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunningResults());

        var context = BuildContext(client, RunningLauncher(), out var output, out _);
        var command = new BenchmarkCommand
        {
            IsInteractive = () => false,
            PollInterval = TimeSpan.FromMilliseconds(10),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var exitCode = await command.ExecuteAsync(context, ["run", "--wait"], cts.Token);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("benchmark: finished", output.ToString());
        Assert.DoesNotContain("viable", output.ToString());
    }

    [Fact]
    public async Task BenchmarkStatus_NonEmpty_PrintsViabilityTable()
    {
        var client = NewClient();
        client.Setup(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FinishedResults());

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new BenchmarkCommand().ExecuteAsync(context, ["status"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Local (Succeeded, started 2026-11-10 10:00:00Z, finished 2026-11-10 10:05:00Z)", text);
        Assert.Contains("Remote (Succeeded, started 2026-11-10 10:00:00Z, finished 2026-11-10 10:05:00Z)", text);
        Assert.Contains("AgenticChat", text);
        Assert.Contains("viable yes", text);
        Assert.Contains("viable no  oom", text);
    }

    [Fact]
    public async Task BenchmarkStatus_Empty_PrintsRunHintAndExits0()
    {
        var client = NewClient();
        client.Setup(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterBenchmarkResultsResponse([]));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new BenchmarkCommand().ExecuteAsync(context, ["status"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("no benchmark results (run: ollamarouter benchmark run)", output.ToString().Trim());
    }

    [Fact]
    public async Task BenchmarkStatus_Json_PrintsExactWireDocument()
    {
        var client = NewClient();
        client.Setup(c => c.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FinishedResults());

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new BenchmarkCommand().ExecuteAsync(context, ["status", "--json"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().Trim().Split('\n');
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal(2, doc.RootElement.GetProperty("targets").GetArrayLength());
    }

    [Fact]
    public async Task BenchmarkClear_PrintsClearedLine()
    {
        var client = NewClient();
        client.Setup(c => c.ClearBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterBenchmarkClearedResponse(true));

        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await new BenchmarkCommand().ExecuteAsync(context, ["clear"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("benchmark results cleared", output.ToString().Trim());
    }

    [Fact]
    public async Task BenchmarkClear_Json_AndServerError()
    {
        var client = NewClient();
        client.Setup(c => c.ClearBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouterBenchmarkClearedResponse(true));

        var launcher = RunningLauncher();
        var context = BuildContext(client, launcher, out var output, out _);
        var command = new BenchmarkCommand();

        var exitCode = await command.ExecuteAsync(context, ["clear", "--json"], CancellationToken.None);
        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(output.ToString().Trim());
        Assert.True(doc.RootElement.GetProperty("cleared").GetBoolean());

        var failing = NewClient();
        failing.Setup(c => c.ClearBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterApiException(HttpStatusCode.InternalServerError, "cannot clear results"));
        context = BuildContext(failing, launcher, out _, out var error);
        exitCode = await command.ExecuteAsync(context, ["clear"], CancellationToken.None);
        Assert.Equal(1, exitCode);
        Assert.Contains("error: cannot clear results", error.ToString());
    }

    [Fact]
    public async Task Benchmark_UsageErrors_Exit2()
    {
        string[][] subArgsCases =
        {
            [],
            ["frobnicate"],
            ["run", "--bogus"],
            ["status", "--wat"],
            ["run", "--target"],   // --target without a value
        };
        foreach (var subArgs in subArgsCases)
        {
            var client = NewClient();
            var context = BuildContext(client, RunningLauncher(), out _, out var error);

            var exitCode = await new BenchmarkCommand().ExecuteAsync(context, subArgs, CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal("usage: ollamarouter benchmark run|status|clear", error.ToString().Trim());
            client.VerifyNoOtherCalls();
        }
    }

    // ------------------------------------------------------------------
    //  dispatcher + help
    // ------------------------------------------------------------------

    [Fact]
    public async Task Help_ListsEveryNewOperationalCommand()
    {
        var client = NewClient();
        var context = BuildContext(client, RunningLauncher(), out var output, out _);

        var exitCode = await CliDispatcher.DispatchAsync(
            ["help"], context, () => false, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("fleet", text);
        Assert.Contains("(alias: ps)", text);
        Assert.Contains("cartography", text);
        Assert.Contains("vram reclaim", text);
        Assert.Contains("--keep-enabled", text);
        Assert.Contains("preload", text);
        Assert.Contains("target", text);
        Assert.Contains("benchmark", text);
        Assert.Contains("config.json-only", text);
    }

    [Fact]
    public async Task Dispatcher_RoutesEveryOperationalCommandToItsClientMethod()
    {
        (string[] Args, Action<Mock<IOllamaRouterApiClient>> Setup)[] routes =
        {
            (["fleet"], mock => mock.Setup(m => m.GetFleetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(TwoTargetFleet())),
            (["ps"], mock => mock.Setup(m => m.GetFleetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(TwoTargetFleet())),
            (["cartography"], mock => mock.Setup(m => m.GetCartographyAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(CartographyFixture())),
            (["vram", "reclaim"], mock => mock.Setup(
                    m => m.ReclaimVramAsync(It.IsAny<RouterVramReclaimRequest?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ReclaimSuccess())),
            (["preload", "m"], mock => mock.Setup(m => m.PreloadModelAsync(
                    "m", It.IsAny<RouterModelPreloadRequest?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(PreloadResponse(loadPath: true))),
            (["target", "list"], mock =>
            {
                mock.Setup(m => m.GetConfigAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(ConfigFixture());
                mock.Setup(m => m.GetFleetAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(TwoTargetFleet());
            }),
            (["benchmark", "status"], mock => mock.Setup(m => m.GetBenchmarkResultsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RouterBenchmarkResultsResponse([]))),
        };

        foreach (var (args, setup) in routes)
        {
            var client = NewClient();
            setup(client);
            var launcher = RunningLauncher();
            var context = BuildContext(client, launcher, out _, out _);

            var exitCode = await CliDispatcher.DispatchAsync(
                args, context, () => false, CancellationToken.None);

            Assert.Equal(0, exitCode);
            launcher.Verify(l => l.EnsureRunningAsync(It.IsAny<CancellationToken>()), Times.Once,
                $"every operational command must auto-start the daemon exactly once ({string.Join(' ', args)})");
        }
    }
}
