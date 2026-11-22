using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.App.Logging;
using Pmad.LocalApi.Security;
using Serilog;

namespace OllamaRouter.App.Connection;

/// <summary>
/// Default <see cref="IAppConnectionService"/> (task #102). The client / launcher factories
/// are injectable so tests run against fakes (no network, no real processes); the defaults
/// construct the real SDK types.
/// </summary>
public sealed class AppConnectionService : IAppConnectionService
{
    private readonly IDefaultKeyReader _keyReader;

    /// <summary>
    /// Creates the client from the profile: local mode wires the
    /// <paramref name="keyReader"/> with a NULL explicit key (the client re-reads
    /// <c>default-key.dat</c> per request), remote mode uses the explicit key only (no
    /// key reader — a remote router's default key is not on this machine).
    /// </summary>
    internal delegate IOllamaRouterApiClient ClientFactory(Uri baseUrl, string? apiKey, IDefaultKeyReader? keyReader);

    /// <summary>Creates the daemon launcher (local mode only).</summary>
    internal delegate IOllamaRouterDaemonLauncher LauncherFactory(Uri baseUrl, IDefaultKeyReader keyReader);

    private static ClientFactory DefaultClientFactory { get; } =
        static (baseUrl, apiKey, keyReader) => new OllamaRouterApiClient(baseUrl, apiKey, keyReader);

    private static LauncherFactory DefaultLauncherFactory { get; } =
        static (baseUrl, keyReader) => new OllamaRouterDaemonLauncher(baseUrl, null, keyReader);

    private readonly ClientFactory _clientFactory;
    private readonly LauncherFactory _launcherFactory;
    // Task #159: the resolved logger (the seam defaults to the static Serilog.Log.Logger).
    private readonly ILogger _logger;

    private IOllamaRouterApiClient _client;
    private IOllamaRouterDaemonLauncher? _daemonLauncher;
    private RouterConnectionProfile _profile;

    /// <summary>
    /// Creates the service on the <paramref name="initial"/> profile (default: Local on the
    /// conventional daemon URL) using the real SDK client/launcher factories.
    /// </summary>
    public AppConnectionService(
        IDefaultKeyReader keyReader,
        RouterConnectionProfile? initial = null,
        ILogger? logger = null)
        : this(keyReader, initial, DefaultClientFactory, DefaultLauncherFactory, logger)
    {
    }

    /// <summary>
    /// Creates the service with injectable client / launcher factories (test seam; the
    /// production <see cref="CompositionRoot"/> uses the default constructor).
    /// </summary>
    internal AppConnectionService(
        IDefaultKeyReader keyReader,
        RouterConnectionProfile? initial,
        ClientFactory clientFactory,
        LauncherFactory launcherFactory,
        ILogger? logger = null)
    {
        _keyReader = keyReader ?? throw new ArgumentNullException(nameof(keyReader));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _launcherFactory = launcherFactory ?? throw new ArgumentNullException(nameof(launcherFactory));
        _logger = logger ?? Log.Logger;
        _profile = Normalize(initial ?? LocalProfile());
        (_client, _daemonLauncher) = Build(_profile);
        GetClient = () => _client;
    }

    /// <inheritdoc />
    public RouterConnectionProfile Profile => _profile;

    /// <inheritdoc />
    public IOllamaRouterApiClient Client => _client;

    /// <inheritdoc />
    public IDefaultKeyReader? KeyReader => _profile.Mode == RouterConnectionMode.Local ? _keyReader : null;

    /// <inheritdoc />
    public IOllamaRouterDaemonLauncher? DaemonLauncher => _daemonLauncher;

    /// <inheritdoc />
    public Func<IOllamaRouterApiClient> GetClient { get; }

    /// <inheritdoc />
    public event Action? ConnectionChanged;

    /// <summary>The canonical local profile: default daemon URL, no explicit key.</summary>
    public static RouterConnectionProfile LocalProfile() =>
        new(RouterConnectionMode.Local, OllamaRouterDaemonLauncher.DefaultBaseUrl, null);

    /// <inheritdoc />
    public Task SwitchConnectionAsync(RouterConnectionProfile profile, CancellationToken ct = default)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        var normalized = NormalizeOrLog(profile);
        var fromMode = _profile.Mode;
        DisposeCurrent();
        (_profile, (_client, _daemonLauncher)) = (normalized, Build(normalized));
        // Task #159 connection lifecycle: the API key value NEVER reaches the log directly —
        // only through SecretsMasker.Mask (AC7 privacy choke point).
        _logger.Information(
            "Connection profile switched: {FromMode} → {ToMode} at {BaseUrl} (API key: {ApiKey})",
            fromMode.ToString(), normalized.Mode.ToString(), normalized.BaseUrl, SecretsMasker.Mask(normalized.ApiKey));
        ConnectionChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Task #159: <see cref="Normalize"/> with a logged (but rethrown — behavior unchanged)
    /// failure: an invalid remote profile is an Error entry, never a silent throw.
    /// </summary>
    private RouterConnectionProfile NormalizeOrLog(RouterConnectionProfile profile)
    {
        try
        {
            return Normalize(profile);
        }
        catch (ArgumentException ex)
        {
            _logger.Error(ex, "Connection profile switch rejected: {Message}", ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeCurrent();
    }

    // ------------------------------------------------------------------

    private void DisposeCurrent()
    {
        (_client as IDisposable)?.Dispose();
        (_daemonLauncher as IDisposable)?.Dispose();
        _client = null!;
        _daemonLauncher = null;
    }

    private (IOllamaRouterApiClient, IOllamaRouterDaemonLauncher?) Build(RouterConnectionProfile profile)
    {
        var client = _clientFactory(profile.BaseUrl, profile.ApiKey,
            profile.Mode == RouterConnectionMode.Local ? _keyReader : null);
        var launcher = profile.Mode == RouterConnectionMode.Local
            ? _launcherFactory(profile.BaseUrl, _keyReader)
            : null;
        return (client, launcher);
    }

    private static RouterConnectionProfile Normalize(RouterConnectionProfile profile)
    {
        if (profile.Mode == RouterConnectionMode.Local)
        {
            // The local profile is canonical: daemon URL + key-reader auth (never an
            // explicit key).
            return LocalProfile();
        }

        if (!profile.BaseUrl.IsAbsoluteUri ||
            (profile.BaseUrl.Scheme != Uri.UriSchemeHttp && profile.BaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The remote base URL must be an absolute http(s) URI.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            throw new ArgumentException("The remote profile requires a non-empty API key.", nameof(profile));
        }

        return profile;
    }
}
