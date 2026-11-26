using OllamaRouter.App.Connection;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Endpoints.RouterApi.Info;
using OllamaRouter.Security;
using Pmad.LocalApi.Security;
using Serilog;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Scripted <see cref="IOllamaRouterDaemonLauncher"/> double (task #102): a mutable
/// <see cref="Status"/> state machine plus an invocation log. NO real processes are ever
/// started. <see cref="StartException"/> / <see cref="StopException"/> script the typed
/// launcher failures.
/// </summary>
internal sealed class FakeDaemonLauncher : IOllamaRouterDaemonLauncher
{
    private readonly object _gate = new();
    /// <summary>The ordered log of every invoked method.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The current (or, after <see cref="StartAsync"/>, resulting) status.</summary>
    public RouterDaemonStatus Status { get; set; } = new(RouterDaemonState.Stopped, null, null);

    /// <summary>The status produced by a successful start (defaults to a Running status).</summary>
    public RouterDaemonStatus? AfterStart { get; set; }

    /// <summary>Scripted <see cref="IOllamaRouterDaemonLauncher.StartAsync"/> failure.</summary>
    public Exception? StartException { get; set; }

    /// <summary>Scripted <see cref="IOllamaRouterDaemonLauncher.StopAsync"/> failure.</summary>
    public Exception? StopException { get; set; }

    /// <summary>Scripted <see cref="IOllamaRouterDaemonLauncher.GetStatusAsync"/> failure
    /// (task #173: the generic-probe → Disconnected state seam).</summary>
    public Exception? GetStatusException { get; set; }

    /// <summary>The base URL the launcher was built with (set by the harness factory).</summary>
    public Uri? CapturedBaseUrl { get; set; }

    /// <summary>A prebuilt Running status (test setup helper).</summary>
    public static RouterDaemonStatus RunningStatus(
        int processId = 0, string? version = null, double uptimeSeconds = 0) =>
        new(
            RouterDaemonState.Running,
            new RouterInfoResponse(version ?? "0.0.0-fake", true,
                new RouterFleetSummary(1, 1, false),
                new RouterFeatures(true, true, true, true),
                processId, DateTimeOffset.UtcNow.AddSeconds(-uptimeSeconds), uptimeSeconds),
            null);

    /// <inheritdoc />
    public Task<RouterDaemonStatus> GetStatusAsync(CancellationToken ct = default)
    {
        Log(nameof(GetStatusAsync));
        if (GetStatusException is { } exception)
        {
            throw exception;
        }

        return Task.FromResult(Status);
    }

    /// <inheritdoc />
    public Task<RouterDaemonStatus> StartAsync(CancellationToken ct = default)
    {
        Log(nameof(StartAsync));
        if (StartException is { } exception)
        {
            throw exception;
        }

        if (Status.State == RouterDaemonState.PortOccupiedByOtherService)
        {
            throw new RouterDaemonPortOccupiedException(Status);
        }

        Status = AfterStart ?? RunningStatus();
        return Task.FromResult(Status);
    }

    /// <inheritdoc />
    public Task<RouterDaemonStatus> EnsureRunningAsync(CancellationToken ct = default)
    {
        Log(nameof(EnsureRunningAsync));
        if (Status.State == RouterDaemonState.Running ||
            Status.State == RouterDaemonState.PortOccupiedByOtherService)
        {
            // Already running, or a foreign port (RETURNED, never thrown).
            return Task.FromResult(Status);
        }

        return StartAsync(ct);
    }

    /// <inheritdoc />
    public Task<RouterDaemonStatus> StopAsync(CancellationToken ct = default)
    {
        Log(nameof(StopAsync));
        if (StopException is { } exception)
        {
            throw exception;
        }

        if (Status.State == RouterDaemonState.PortOccupiedByOtherService)
        {
            throw new RouterDaemonPortOccupiedException(Status);
        }

        Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null);
        return Task.FromResult(Status);
    }

    private void Log(string method)
    {
        lock (_gate)
        {
            Calls.Add(method);
        }
    }
}

/// <summary>
/// The <see cref="AppConnectionService"/> test harness (task #102): wraps the service with
/// capturing client/launcher factories — records the constructor arguments (base URL /
/// explicit key / key reader) of EVERY created client (the assertion surface for the
/// "local client is built with the key reader and a null explicit key" contract) and tracks
/// disposed clients.
/// </summary>
internal sealed class CapturedClient : IDisposable
{
    private bool _disposed;

    public CapturedClient(
        IOllamaRouterApiClient client, Uri baseUrl, string? apiKey, IDefaultKeyReader? keyReader)
    {
        Client = client;
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        KeyReader = keyReader;
    }

    /// <summary>The created client instance.</summary>
    public IOllamaRouterApiClient Client { get; }

    /// <summary>The base URL the client was built with.</summary>
    public Uri BaseUrl { get; }

    /// <summary>The explicit API key the client was built with (null in local mode).</summary>
    public string? ApiKey { get; }

    /// <summary>The key reader the client was built with (local mode only).</summary>
    public IDefaultKeyReader? KeyReader { get; }

    /// <summary>
    /// Whether disposal was observed: the service disposes the underlying client through
    /// <c>IDisposable</c>, so the wrapper mirrors the fake's own disposal flag.
    /// </summary>
    public bool Disposed => _disposed || (Client as FakeRouterApiClient)?.Disposed == true;

    /// <inheritdoc />
    public void Dispose() => _disposed = true;
}

/// <summary>The fake <see cref="IAppConnectionService"/> test wiring.</summary>
internal sealed class AppConnectionHarness : IDisposable
{
    /// <summary>The fake client factory captures (baseUrl, apiKey, keyReader) per client.</summary>
    internal delegate IOllamaRouterApiClient Factory(Uri baseUrl, string? apiKey, IDefaultKeyReader? keyReader);

    private readonly List<CapturedClient> _clients = [];
    private readonly List<FakeDaemonLauncher> _launchers = [];
    private readonly AppConnectionService _service;

    /// <summary>Creates the harness (default initial profile = Local).</summary>
    public AppConnectionHarness(
        IDefaultKeyReader keyReader,
        Func<FakeRouterApiClient> clientFactory,
        Func<FakeDaemonLauncher> launcherFactory,
        ILogger? logger = null)
    {
        KeyReader = keyReader;
        var clientDelegate = (AppConnectionService.ClientFactory)((baseUrl, apiKey, reader) =>
        {
            // Every switch re-creates the client through the shared fake (the constructor-
            // ARGUMENT capture is what the assertions inspect; the instance is shared so the
            // scripted state (InfoResponse/NextCallException) stays visible to the tests).
            var client = clientFactory();
            _clients.Add(new CapturedClient(client, baseUrl, apiKey, reader));
            return client;
        });
        var launcherDelegate = (AppConnectionService.LauncherFactory)((baseUrl, reader) =>
        {
            var launcher = launcherFactory();
            launcher.CapturedBaseUrl = baseUrl;
            _launchers.Add(launcher);
            return launcher;
        });
        _service = new AppConnectionService(keyReader, null, clientDelegate, launcherDelegate, logger);
    }

    /// <summary>The key reader the service was built with.</summary>
    public IDefaultKeyReader KeyReader { get; }

    /// <summary>The service under test.</summary>
    public AppConnectionService Service => _service;

    /// <summary>Every client created so far (constructor-argument capture).</summary>
    public IReadOnlyList<CapturedClient> Clients => _clients;

    /// <summary>Every daemon launcher created so far.</summary>
    public IReadOnlyList<FakeDaemonLauncher> Launchers => _launchers;

    /// <summary>The most recently created launcher (the active one in local mode).</summary>
    public FakeDaemonLauncher? LastLauncher => _launchers.Count > 0 ? _launchers[^1] : null;

    /// <inheritdoc />
    public void Dispose() => _service.Dispose();
}
