using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.ApiKeys;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Info;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Endpoints.RouterApi.Preload;
using OllamaRouter.Endpoints.RouterApi.SessionAffinity;
using OllamaRouter.Endpoints.RouterApi.Sessions;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Endpoints.RouterApi.Vram;
using OllamaRouter.Security;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Tests.Client;

/// <summary>
/// Unit tests for <see cref="OllamaRouterDaemonLauncher"/> (task #99): the probe
/// classification matrix (router 200 / 401 key-locked / 404 + 200-non-router →
/// <c>PortOccupiedByOtherService</c> / refused + timeout → <c>Stopped</c>), idempotent start,
/// detached-launch assertions (no shell / no window / drained pipes), the name × root
/// executable resolution (with <c>UseDefaultSearchRoots=false</c> so a real OllamaRouter
/// installed on a developer/CI machine can never leak in), the start-timeout / bind-race child
/// cleanup, and the stop fallbacks (graceful → wait → <c>Kill(entireProcessTree)</c> → final
/// probe). NO real child processes: every process interaction goes through the internal
/// <c>ProcessStarter</c>/<c>ProcessLookup</c> seams; every probe goes through a fake
/// <see cref="HttpMessageHandler"/> or an in-memory fake client; every budget is sub-second.
/// </summary>
public sealed class OllamaRouterDaemonLauncherTests : IDisposable
{
    private const string RouterInfoJson = """
        {"version":"2026.11.11","isRouter":true,
         "fleet":{"totalTargets":1,"onlineTargets":1,"cloudEnabled":false},
         "features":{"dynamicContext":true,"autoShrink":true,"failover":true,"antiThrashingAffinity":true},
         "processId":4242,"startedAtUtc":"2026-11-10T12:00:00.000Z","uptimeSeconds":3600.5}
        """;

    private static readonly string ExeSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    private readonly string _tempRoot;
    private readonly string _configDir;

    public OllamaRouterDaemonLauncherTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ollamarouter-daemon-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _configDir = Path.Combine(_tempRoot, "config");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp-dir cleanup.
        }
    }

    // ------------------------------------------------------------------
    //  Probe classification matrix
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStatus_RouterIdentity200_ClassifiedRunning_WithTelemetrySurfaced()
    {
        var handler = new ProbeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, RouterInfoJson)));
        var launcher = CreateRealClientLauncher(handler, probeTimeout: TimeSpan.FromSeconds(2));

        var status = await launcher.GetStatusAsync();

        Assert.Equal(RouterDaemonState.Running, status.State);
        Assert.NotNull(status.Info);
        Assert.Equal(4242, status.ProcessId);
        Assert.Equal("2026.11.11", status.Version);
        Assert.Single(handler.Responses);
    }

    [Fact]
    public async Task GetStatus_NonRouter404_ClassifiedPortOccupiedByOtherService()
    {
        // A raw Ollama daemon on :11434 404s the router identity route — the canonical
        // discriminator this repo already relies on.
        var handler = new ProbeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""")));
        var launcher = CreateRealClientLauncher(handler);

        var status = await launcher.GetStatusAsync();

        Assert.Equal(RouterDaemonState.PortOccupiedByOtherService, status.State);
        Assert.Null(status.Info);
        Assert.Contains("404", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatus_200WithNonRouterPayload_ClassifiedPortOccupiedByOtherService()
    {
        // 200 + non-JSON (unparseable) body → the client surfaces a JsonException → occupied.
        var handler = new ProbeHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not a router</html>") }));
        var launcher = CreateRealClientLauncher(handler);

        var status = await launcher.GetStatusAsync();

        Assert.Equal(RouterDaemonState.PortOccupiedByOtherService, status.State);
        Assert.NotNull(status.Detail);
    }

    [Fact]
    public async Task GetStatus_401_KeyLockedRouter_AfterClientRetry_ClassifiedRunning_KeyRequired()
    {
        // Key-locked router: only OUR router 401s /api/*; the client re-reads the key and
        // retries EXACTLY ONCE (2 requests), then the probe classifies the port as Running
        // (auto-start must not fork a second daemon).
        WriteKeyFile("probe-key");
        var handler = new ProbeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}""")));
        var launcher = CreateRealClientLauncher(handler, keyReader: KeyReader());

        var status = await launcher.GetStatusAsync();

        Assert.Equal(RouterDaemonState.Running, status.State);
        Assert.Null(status.Info);
        Assert.Contains("key required", status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Responses.Count); // keyless-ish first attempt + one re-read retry
    }

    [Fact]
    public async Task GetStatus_ConnectionRefused_ClassifiedStopped()
    {
        var handler = new ProbeHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        var launcher = CreateRealClientLauncher(handler);

        var status = await launcher.GetStatusAsync();

        Assert.Equal(RouterDaemonState.Stopped, status.State);
        Assert.Contains("connection refused", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStatus_ProbeTimeout_ClassifiedStopped()
    {
        // The handler never answers: the launcher's own probe budget (sub-second) fires.
        var handler = new ProbeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var launcher = CreateRealClientLauncher(handler, probeTimeout: TimeSpan.FromMilliseconds(300));

        var status = await launcher.GetStatusAsync();

        Assert.Equal(RouterDaemonState.Stopped, status.State);
        Assert.Contains("timed out", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStatus_ConnectionRefused_RealClient_DeadLoopbackPort()
    {
        // A real (loopback) connection to a port with no listener → Stopped (refused).
        var port = GetFreePort();
        var launcher = new OllamaRouterDaemonLauncher(new Uri($"http://127.0.0.1:{port}/"));

        var status = await launcher.GetStatusAsync();

        Assert.Equal(RouterDaemonState.Stopped, status.State);
    }

    [Fact]
    public async Task GetStatus_CallerCancellation_Propagates()
    {
        var client = new FakeRouterClient();
        client.GetInfo = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return RunningInfo;
        };
        var launcher = new OllamaRouterDaemonLauncher(client, probeTimeout: TimeSpan.FromSeconds(2));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.GetStatusAsync(cts.Token));
    }

    [Fact]
    public void DefaultBaseUrl_IsLoopbackOllamaPort()
    {
        Assert.Equal("http://127.0.0.1:11434/", OllamaRouterDaemonLauncher.DefaultBaseUrl.ToString());
    }

    [Fact]
    public void Constructor_NonPositiveTimeouts_ThrowArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OllamaRouterDaemonLauncher(probeTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OllamaRouterDaemonLauncher(startPollInterval: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OllamaRouterDaemonLauncher(startTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OllamaRouterDaemonLauncher(stopExitTimeout: TimeSpan.FromSeconds(-5)));
    }

    // ------------------------------------------------------------------
    //  Start / EnsureRunning
    // ------------------------------------------------------------------

    [Fact]
    public async Task Start_AlreadyRunning_ReturnsStatus_WithoutLaunching()
    {
        var handler = new ProbeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, RouterInfoJson)));
        var starterCalls = 0;
        var launcher = CreateRealClientLauncher(handler);
        launcher.ProcessStarter = _ =>
        {
            starterCalls++;
            throw new InvalidOperationException("must not launch");
        };

        var status = await launcher.StartAsync();

        Assert.Equal(RouterDaemonState.Running, status.State);
        Assert.Equal(0, starterCalls);
    }

    [Fact]
    public async Task Start_PortOccupied_ThrowsPortOccupied_WithoutLaunching()
    {
        var handler = new ProbeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""")));
        var starterCalls = 0;
        var launcher = CreateRealClientLauncher(handler);
        launcher.ProcessStarter = _ =>
        {
            starterCalls++;
            throw new InvalidOperationException("must not launch");
        };

        var exception = await Assert.ThrowsAsync<RouterDaemonPortOccupiedException>(() => launcher.StartAsync());

        Assert.Equal(0, starterCalls);
        Assert.Equal(RouterDaemonState.PortOccupiedByOtherService, exception.Status.State);
    }

    [Fact]
    public async Task EnsureRunning_PortOccupied_ReturnsStatus_WithoutThrowing()
    {
        var handler = new ProbeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}""")));
        var launcher = CreateRealClientLauncher(handler);

        var status = await launcher.EnsureRunningAsync();

        Assert.Equal(RouterDaemonState.PortOccupiedByOtherService, status.State);
    }

    [Fact]
    public async Task Start_NoExecutableInAnyRoot_ThrowsWithFullSearchedLocationList()
    {
        var installDir = Path.Combine(_tempRoot, "install-empty");
        Directory.CreateDirectory(installDir);
        var handler = new ProbeHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        var launcher = CreateRealClientLauncher(handler, installDirectory: installDir);
        launcher.UseDefaultSearchRoots = false;

        var exception = await Assert.ThrowsAsync<RouterDaemonExecutableNotFoundException>(() => launcher.StartAsync());

        var expected = new List<string>
        {
            Path.Combine(installDir, "ollamarouter-server" + ExeSuffix),
            Path.Combine(installDir, "OllamaRouter" + ExeSuffix)
        };
        Assert.Equal(expected, exception.SearchedLocations);
    }

    [Fact]
    public async Task Start_Success_DetachedLaunch_PollsToRunning_AndRetainsTrackedHandle()
    {
        var installDir = PrepareInstallDir();
        var exePath = Path.Combine(installDir, "ollamarouter-server" + ExeSuffix);
        var started = new FakeRouterProcess(777);
        var handler = new ProbeHttpMessageHandler((count, _) =>
        {
            if (count <= 2)
            {
                throw new HttpRequestException("connection refused"); // pre-launch + one stopped poll
            }

            if (count == 3)
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, RouterInfoJson)); // healthy
            }

            if (count == 4)
            {
                // The tracked-handle stop below: the daemon exits on the graceful stop signal.
                started.SetExited(true);
                return Task.FromResult(JsonResponse(HttpStatusCode.Accepted, """{"stopping":true,"processId":4242}"""));
            }

            throw new HttpRequestException("connection refused"); // the daemon is gone
        });
        var launcher = CreateRealClientLauncher(handler, installDirectory: installDir,
            startPollInterval: TimeSpan.FromMilliseconds(20), stopExitTimeout: TimeSpan.FromMilliseconds(500));
        ProcessStartInfo? capturedStartInfo = null;
        launcher.ProcessStarter = psi =>
        {
            capturedStartInfo = psi;
            return started;
        };

        var status = await launcher.StartAsync();

        Assert.Equal(RouterDaemonState.Running, status.State);
        Assert.Equal(4242, status.ProcessId);

        // Detached headless launch contract.
        var psi = capturedStartInfo!;
        Assert.Equal(exePath, psi.FileName);
        Assert.Equal(installDir, psi.WorkingDirectory);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);

        // The tracked handle is retained for StopAsync (graceful → exits on its own → no kill).
        var stopped = await launcher.StopAsync();
        Assert.Equal(RouterDaemonState.Stopped, stopped.State);
        Assert.Equal(0, started.KillCalls); // it exited on its own — no kill
        Assert.True(started.Disposed);
        Assert.Equal(2, handler.Responses.Count); // the info probe + the graceful shutdown answered
    }

    [Fact]
    public async Task Start_Timeout_KillsChild_AndThrowsStartTimeoutWithExecutablePath()
    {
        var installDir = PrepareInstallDir();
        var exePath = Path.Combine(installDir, "ollamarouter-server" + ExeSuffix);
        var handler = new ProbeHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        var launcher = CreateRealClientLauncher(handler, installDirectory: installDir,
            startPollInterval: TimeSpan.FromMilliseconds(20), startTimeout: TimeSpan.FromMilliseconds(300));
        var started = new FakeRouterProcess(888);
        launcher.ProcessStarter = _ => started;

        var exception = await Assert.ThrowsAsync<RouterDaemonStartTimeoutException>(() => launcher.StartAsync());

        Assert.Equal(exePath, exception.ExecutablePath);
        Assert.Equal(300.0, exception.Timeout.TotalMilliseconds);
        Assert.Equal(1, started.KillCalls); // child cleaned up
        Assert.True(started.Disposed);
    }

    [Fact]
    public async Task Start_BindRaceLost_MidPoll_KillsChild_AndThrowsPortOccupied()
    {
        var installDir = PrepareInstallDir();
        var handler = new ProbeHttpMessageHandler((count, _) =>
        {
            if (count < 2)
            {
                throw new HttpRequestException("connection refused"); // pre-launch probe
            }

            // Mid-poll: a foreign owner now serves the port (404 = raw Ollama discriminator).
            return Task.FromResult(JsonResponse(HttpStatusCode.NotFound, """{"error":"not found"}"""));
        });
        var launcher = CreateRealClientLauncher(handler, installDirectory: installDir);
        var started = new FakeRouterProcess(999);
        launcher.ProcessStarter = _ => started;

        var exception = await Assert.ThrowsAsync<RouterDaemonPortOccupiedException>(() => launcher.StartAsync());

        Assert.Equal(RouterDaemonState.PortOccupiedByOtherService, exception.Status.State);
        Assert.Equal(1, started.KillCalls);
        Assert.True(started.Disposed);
    }

    [Fact]
    public async Task Resolution_PrefersOllamaRouterServerName_OverLegacyName()
    {
        var installDir = Path.Combine(_tempRoot, "install-both");
        Directory.CreateDirectory(installDir);
        var preferred = Path.Combine(installDir, "ollamarouter-server" + ExeSuffix);
        var legacy = Path.Combine(installDir, "OllamaRouter" + ExeSuffix);
        File.WriteAllText(preferred, "");
        File.WriteAllText(legacy, "");

        var handler = new ProbeHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        var launcher = CreateRealClientLauncher(handler, installDirectory: installDir,
            startPollInterval: TimeSpan.FromMilliseconds(20), startTimeout: TimeSpan.FromMilliseconds(300));
        ProcessStartInfo? captured = null;
        launcher.ProcessStarter = psi =>
        {
            captured = psi;
            return new FakeRouterProcess(1);
        };

        _ = await Assert.ThrowsAsync<RouterDaemonStartTimeoutException>(() => launcher.StartAsync());

        Assert.Equal(preferred, captured!.FileName);
    }

    [Fact]
    public async Task Resolution_UseDefaultSearchRootsFalse_SkipsEnvironmentAndPlatformDefaultRoots()
    {
        // With the guard off, ONLY the explicit install dir is probed (no env var, platform
        // default, or test-host sibling root) — a real install or apphost can never leak in.
        var installDir = Path.Combine(_tempRoot, "install-none");
        Directory.CreateDirectory(installDir);
        var handler = new ProbeHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        var launcher = CreateRealClientLauncher(handler, installDirectory: installDir);
        launcher.UseDefaultSearchRoots = false;

        var exception = await Assert.ThrowsAsync<RouterDaemonExecutableNotFoundException>(() => launcher.StartAsync());

        // Exactly the 2 names × 1 root matrix — no env var / platform default / sibling entries.
        Assert.Equal(2, exception.SearchedLocations.Count);
        Assert.All(exception.SearchedLocations, path =>
            Assert.StartsWith(installDir, path));
    }

    // ------------------------------------------------------------------
    //  Stop fallbacks
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stop_Stopped_NoOp_NoShutdown_NoLookup()
    {
        var client = new FakeRouterClient(); // default: GetInfo refused → Stopped
        client.Shutdown = _ =>
        {
            throw new InvalidOperationException("must not call shutdown");
        };
        var launcher = new OllamaRouterDaemonLauncher(client, stopExitTimeout: TimeSpan.FromMilliseconds(300));
        launcher.ProcessLookup = _ => throw new InvalidOperationException("must not look up");

        var status = await launcher.StopAsync();

        Assert.Equal(RouterDaemonState.Stopped, status.State);
        Assert.Equal(0, client.ShutdownCalls);
    }

    [Fact]
    public async Task Stop_PortOccupied_ThrowsPortOccupied_NeverKills()
    {
        var client = new FakeRouterClient
        {
            GetInfo = _ => throw new OllamaRouterApiException(HttpStatusCode.NotFound, "not found"),
        };
        var launcher = new OllamaRouterDaemonLauncher(client);
        var process = new FakeRouterProcess(4242);
        launcher.ProcessLookup = _ => process;

        var exception = await Assert.ThrowsAsync<RouterDaemonPortOccupiedException>(() => launcher.StopAsync());

        Assert.Equal(RouterDaemonState.PortOccupiedByOtherService, exception.Status.State);
        Assert.Equal(0, client.ShutdownCalls);
        Assert.Equal(0, process.KillCalls);
    }

    [Fact]
    public async Task Stop_GracefulShutdown_ExitsWithinBudget_NoKill_FinalProbeStopped()
    {
        var process = new FakeRouterProcess(4242);
        var client = new FakeRouterClient { GetInfo = _ => Task.FromResult(RunningInfo) };
        client.Shutdown = _ =>
        {
            // The daemon exits right after the graceful stop signal.
            process.SetExited(true);
            client.GetInfo = ct => throw new HttpRequestException("connection refused");
            return Task.FromResult(new RouterShutdownResponse(true, 4242));
        };
        var launcher = new OllamaRouterDaemonLauncher(client, stopExitTimeout: TimeSpan.FromSeconds(2));
        launcher.ProcessLookup = _ => process;

        var status = await launcher.StopAsync();

        Assert.Equal(RouterDaemonState.Stopped, status.State);
        Assert.Equal(1, client.ShutdownCalls);
        Assert.Equal(0, process.KillCalls);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Stop_GracefulFailsWith401_FallsThrough_ToWaitAndKill()
    {
        // Key-locked router + no readable key: ShutdownAsync 401s — best-effort, and the stop
        // MUST still complete via the wait/kill fallback.
        var process = new FakeRouterProcess(4242); // never exits on its own
        var client = new FakeRouterClient { GetInfo = _ => Task.FromResult(RunningInfo) };
        client.Shutdown = _ =>
        {
            // The server is already dying after the failed graceful exchange.
            client.GetInfo = ct => throw new HttpRequestException("connection refused");
            throw new OllamaRouterApiException(HttpStatusCode.Unauthorized, "unauthorized");
        };
        var launcher = new OllamaRouterDaemonLauncher(client, stopExitTimeout: TimeSpan.FromMilliseconds(300));
        launcher.ProcessLookup = _ => process;

        var status = await launcher.StopAsync();

        Assert.Equal(RouterDaemonState.Stopped, status.State);
        Assert.Equal(1, client.ShutdownCalls);
        Assert.Equal(1, process.KillCalls);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Stop_DaemonHangs_BeyondStopExitTimeout_KillEntireProcessTree()
    {
        var client = new FakeRouterClient { GetInfo = _ => Task.FromResult(RunningInfo) };
        client.Shutdown = _ =>
        {
            // The graceful stop was accepted, but the daemon hangs past the budget.
            client.GetInfo = ct => throw new HttpRequestException("connection refused");
            return Task.FromResult(new RouterShutdownResponse(true, 4242));
        };
        var launcher = new OllamaRouterDaemonLauncher(client, stopExitTimeout: TimeSpan.FromMilliseconds(300));
        var process = new FakeRouterProcess(4242); // hangs forever
        launcher.ProcessLookup = _ => process;

        var status = await launcher.StopAsync();

        Assert.Equal(RouterDaemonState.Stopped, status.State);
        Assert.Equal(1, process.KillCalls);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Stop_KillFails_AndProcessStillAlive_ThrowsStopFailed()
    {
        var client = new FakeRouterClient { GetInfo = _ => Task.FromResult(RunningInfo) };
        var launcher = new OllamaRouterDaemonLauncher(client, stopExitTimeout: TimeSpan.FromMilliseconds(300));
        var process = new FakeRouterProcess(4242) { KillException = new UnauthorizedAccessException("access denied") };
        launcher.ProcessLookup = _ => process;

        var exception = await Assert.ThrowsAsync<RouterDaemonStopFailedException>(() => launcher.StopAsync());

        Assert.Equal(4242, exception.ProcessId);
        Assert.Equal(1, process.KillCalls);
    }

    [Fact]
    public async Task Stop_UnknownProcessId_NoTrackedHandle_FinalProbeStillReturned()
    {
        // A Running probe WITHOUT telemetry (e.g. an older daemon without the processId field)
        // cannot be tracked: the graceful stop is still issued and the final probe decides.
        var noPidInfo = new RouterInfoResponse("2026.11.11", true,
            new RouterFleetSummary(0, 0, false), new RouterFeatures(false, false, false, false));
        var client = new FakeRouterClient { GetInfo = _ => Task.FromResult(noPidInfo) };
        client.Shutdown = _ =>
        {
            // After the graceful stop, the daemon is gone.
            client.GetInfo = ct => throw new HttpRequestException("connection refused");
            return Task.FromResult(new RouterShutdownResponse(true, 1));
        };
        var launcher = new OllamaRouterDaemonLauncher(client, stopExitTimeout: TimeSpan.FromMilliseconds(300));
        launcher.ProcessLookup = _ => throw new InvalidOperationException("must not look up (no pid)");

        var status = await launcher.StopAsync();

        Assert.Equal(RouterDaemonState.Stopped, status.State);
        Assert.Equal(1, client.ShutdownCalls);
    }

    // ------------------------------------------------------------------
    //  Helpers + fakes
    // ------------------------------------------------------------------

    private static RouterInfoResponse RunningInfo =>
        new("2026.11.11", true, new RouterFleetSummary(1, 1, false),
            new RouterFeatures(true, true, true, true), ProcessId: 4242,
            StartedAtUtc: new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero), UptimeSeconds: 3600.5);

    private OllamaRouterDaemonLauncher CreateRealClientLauncher(
        ProbeHttpMessageHandler handler,
        string? installDirectory = null,
        IDefaultKeyReader? keyReader = null,
        TimeSpan? probeTimeout = null,
        TimeSpan? startPollInterval = null,
        TimeSpan? startTimeout = null,
        TimeSpan? stopExitTimeout = null)
    {
        var launcher = new OllamaRouterDaemonLauncher(
            new Uri("http://127.0.0.1:59999/"),
            null,
            keyReader,
            new HttpClient(handler),
            installDirectory,
            probeTimeout,
            startPollInterval,
            startTimeout,
            stopExitTimeout);
        launcher.UseDefaultSearchRoots = false;
        return launcher;
    }

    /// <summary>Creates a temp install dir containing BOTH candidate executables (empty files).</summary>
    private string PrepareInstallDir()
    {
        var installDir = Path.Combine(_tempRoot, "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "ollamarouter-server" + ExeSuffix), "");
        File.WriteAllText(Path.Combine(installDir, "OllamaRouter" + ExeSuffix), "");
        return installDir;
    }

    private DefaultKeyReader KeyReader()
    {
        Directory.CreateDirectory(_configDir);
        return new DefaultKeyReader(_configDir, new PlainKeyProtector(), _ => null);
    }

    private void WriteKeyFile(string key)
    {
        Directory.CreateDirectory(_configDir);
        var envelope = $"{{\"key\":\"{key}\",\"generatedAt\":\"2026-11-10T12:00:00.000Z\"}}";
        File.WriteAllText(Path.Combine(_configDir, "default-key.dat"), envelope);
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ------------------------------------------------------------------ fakes

    private sealed class ProbeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _responder;

        public ProbeHttpMessageHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> responder) => _responder = responder;

        /// <summary>Every HTTP response that actually came back (thrown probes are excluded).</summary>
        public List<HttpResponseMessage> Responses { get; } = [];

        private int _callCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _callCount);
            var response = await _responder(count, cancellationToken);
            Responses.Add(response);
            return response;
        }
    }

    /// <summary>
    /// Recording <see cref="OllamaRouterDaemonLauncher.RouterDaemonProcess"/> stand-in (never a
    /// real child process).
    /// </summary>
    private sealed class FakeRouterProcess : OllamaRouterDaemonLauncher.RouterDaemonProcess
    {
        private volatile bool _hasExited;

        public FakeRouterProcess(int id, bool hasExited = false)
        {
            Id = id;
            _hasExited = hasExited;
        }

        public int Id { get; }

        public bool HasExited => _hasExited;

        public int KillCalls { get; private set; }

        public bool Disposed { get; private set; }

        public Exception? KillException { get; set; }

        public void SetExited(bool exited) => _hasExited = exited;

        public void KillEntireProcessTree()
        {
            KillCalls++;
            if (KillException is { } exception)
            {
                throw exception;
            }
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// In-memory <see cref="IOllamaRouterApiClient"/> fake: <see cref="GetInfo"/> /
    /// <see cref="Shutdown"/> are test-programmed; every other route-table member is
    /// unavailable (throws if touched).
    /// </summary>
    private sealed class FakeRouterClient : IOllamaRouterApiClient
    {
        public Func<CancellationToken, Task<RouterInfoResponse>>? GetInfo { get; set; }

        public Func<CancellationToken, Task<RouterShutdownResponse>>? Shutdown { get; set; }

        public int ShutdownCalls { get; private set; }

        public Task<RouterInfoResponse> GetInfoAsync(CancellationToken ct = default) =>
            GetInfo is { } get ? get(ct) : Task.FromException<RouterInfoResponse>(new HttpRequestException("connection refused"));

        public Task<RouterShutdownResponse> ShutdownAsync(CancellationToken ct = default)
        {
            ShutdownCalls++;
            return Shutdown is { } stop
                ? stop(ct)
                : Task.FromResult(new RouterShutdownResponse(true, 1));
        }

        // Unavailable route-table members.
        public Task<MonitorStateResponse> GetMonitorStateAsync(CancellationToken ct = default) => Unavailable<MonitorStateResponse>();
        public Task<IReadOnlyDictionary<string, bool>> UpdateTargetAvailabilityAsync(IReadOnlyDictionary<string, bool> flags, CancellationToken ct = default)
            => Unavailable<IReadOnlyDictionary<string, bool>>();
        public Task<RouterFleetResponse> GetFleetAsync(CancellationToken ct = default) => Unavailable<RouterFleetResponse>();
        public Task<RouterFleetHardwareResponse> GetFleetHardwareAsync(CancellationToken ct = default) => Unavailable<RouterFleetHardwareResponse>();
        public Task<NodeHardwareProfile> UpdateTargetHardwareAsync(string target, NodeHardwareProfile profile, CancellationToken ct = default)
            => Unavailable<NodeHardwareProfile>();
        public Task<RouterCartographyResponse> GetCartographyAsync(CancellationToken ct = default) => Unavailable<RouterCartographyResponse>();
        public Task<RouterModelsResponse> GetModelsAsync(CancellationToken ct = default) => Unavailable<RouterModelsResponse>();
        public Task<RouterModelDetailResponse> GetModelDetailAsync(string modelName, CancellationToken ct = default) => Unavailable<RouterModelDetailResponse>();
        public Task<RouterModelPreloadResponse> PreloadModelAsync(string modelName, RouterModelPreloadRequest? request = null, CancellationToken ct = default)
            => Unavailable<RouterModelPreloadResponse>();
        public Task<RouterModelPreloadResponse> PreviewPreloadAsync(string model, int? keepAliveSeconds = null, int? contextIntentTokens = null, string? target = null, CancellationToken ct = default)
            => Unavailable<RouterModelPreloadResponse>();
        public Task<RouterVramReclaimResponse> ReclaimVramAsync(RouterVramReclaimRequest? request = null, CancellationToken ct = default)
            => Unavailable<RouterVramReclaimResponse>();
        public Task<RouterConfigResponse> GetConfigAsync(CancellationToken ct = default) => Unavailable<RouterConfigResponse>();
        public Task<UpdateRouterConfigResponse> UpdateConfigAsync(UpdateRouterConfigRequest request, CancellationToken ct = default)
            => Unavailable<UpdateRouterConfigResponse>();
        public Task<AddTargetResponse> AddTargetAsync(AddTargetRequest request, CancellationToken ct = default) => Unavailable<AddTargetResponse>();
        public Task<UpdateTargetResponse> UpdateTargetAsync(string name, UpdateTargetRequest request, CancellationToken ct = default)
            => Unavailable<UpdateTargetResponse>();
        public Task<DeleteTargetResponse> DeleteTargetAsync(string name, CancellationToken ct = default) => Unavailable<DeleteTargetResponse>();
        public Task<RouterBenchmarkRunResponse> StartBenchmarkAsync(RouterBenchmarkRequest? request = null, CancellationToken ct = default)
            => Unavailable<RouterBenchmarkRunResponse>();
        public Task<RouterBenchmarkResultsResponse> GetBenchmarkResultsAsync(CancellationToken ct = default) => Unavailable<RouterBenchmarkResultsResponse>();
        public Task<RouterBenchmarkClearedResponse> ClearBenchmarkResultsAsync(CancellationToken ct = default) => Unavailable<RouterBenchmarkClearedResponse>();
        public Task<RouterSessionUsageListResponse> GetSessionsAsync(CancellationToken ct = default) => Unavailable<RouterSessionUsageListResponse>();
        public Task<RouterSessionUsageDetailResponse> GetSessionDetailAsync(string sessionId, CancellationToken ct = default)
            => Unavailable<RouterSessionUsageDetailResponse>();
        public Task<RouterSessionAffinityReleaseResponse> ReleaseSessionAffinityAsync(string sessionId, CancellationToken ct = default)
            => Unavailable<RouterSessionAffinityReleaseResponse>();
        public Task<ApiKeysListResponse> GetApiKeysAsync(CancellationToken ct = default) => Unavailable<ApiKeysListResponse>();
        public Task<ApiKeyCreatedResponse> CreateApiKeyAsync(ApiKeyCreateRequest request, CancellationToken ct = default) => Unavailable<ApiKeyCreatedResponse>();
        public Task<ApiKeyDeletedResponse> DeleteApiKeyAsync(string name, CancellationToken ct = default) => Unavailable<ApiKeyDeletedResponse>();
        public Task<ApiKeyRotatedResponse> RotateDefaultKeyAsync(CancellationToken ct = default) => Unavailable<ApiKeyRotatedResponse>();

        private static Task<T> Unavailable<T>() =>
            Task.FromException<T>(new NotImplementedException("not part of the launcher contract"));
    }
}
