using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Security;

namespace OllamaRouter.App.Connection;

/// <summary>
/// Owns the active <see cref="IOllamaRouterApiClient"/> (and, in local mode, the
/// <see cref="IOllamaRouterDaemonLauncher"/>) of the desktop application (task #102) and
/// switches between local and remote profiles at runtime.
/// <para>
/// LOCAL mode: <c>BaseUrl = OllamaRouterDaemonLauncher.DefaultBaseUrl</c>, <c>ApiKey = null</c>
/// and the <see cref="IDefaultKeyReader"/> is wired into the client (no API-key prompt; the key
/// is re-read from <c>default-key.dat</c> per request). REMOTE mode: the user URL + explicit
/// key; the key is held in process memory for the session only (never persisted to disk).
/// </para>
/// </summary>
public interface IAppConnectionService : IDisposable
{
    /// <summary>The currently active connection profile.</summary>
    RouterConnectionProfile Profile { get; }

    /// <summary>The active client (swapped on <see cref="SwitchConnectionAsync"/>).</summary>
    IOllamaRouterApiClient Client { get; }

    /// <summary>The default-key reader (non-null only in local mode).</summary>
    IDefaultKeyReader? KeyReader { get; }

    /// <summary>The daemon launcher (non-null only in local mode).</summary>
    IOllamaRouterDaemonLauncher? DaemonLauncher { get; }

    /// <summary>
    /// A stable accessor to the CURRENT client: reconnections swap the underlying client
    /// without rebuilding the tab view-model tree (view models read through this on every
    /// call).
    /// </summary>
    Func<IOllamaRouterApiClient> GetClient { get; }

    /// <summary>Raised after a successful profile switch (subscribers refresh their state).</summary>
    event Action? ConnectionChanged;

    /// <summary>
    /// Switches to <paramref name="profile"/>: disposes the previous client, installs the new
    /// client (and, in local mode, a new daemon launcher) and raises
    /// <see cref="ConnectionChanged"/>.
    /// </summary>
    Task SwitchConnectionAsync(RouterConnectionProfile profile, CancellationToken ct = default);
}
