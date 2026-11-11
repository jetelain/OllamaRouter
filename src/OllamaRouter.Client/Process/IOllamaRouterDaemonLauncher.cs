using OllamaRouter.Endpoints.RouterApi.Info;

namespace OllamaRouter.Client.Process;

/// <summary>
/// The tri-state classification of the daemon port probed by
/// <see cref="OllamaRouterDaemonLauncher.GetStatusAsync"/> (task #99).
/// </summary>
public enum RouterDaemonState
{
    /// <summary>
    /// Nothing router-shaped answers on the port: the connection is refused, reset, or the
    /// probe timed out.
    /// </summary>
    Stopped,

    /// <summary>
    /// The router answers its identity route (<c>GET /api/router/info</c> → 200 with the
    /// router payload, or 401 in key-locked mode — only OllamaRouter 401s the <c>/api/*</c>
    /// surface; a raw Ollama daemon 404s it).
    /// </summary>
    Running,

    /// <summary>
    /// A non-router service occupies the port (any other HTTP payload — e.g. a raw Ollama
    /// daemon 404ing <c>/api/router/info</c>, or a 200 with a non-router body). The launcher
    /// NEVER launches against or kills the owner of such a port.
    /// </summary>
    PortOccupiedByOtherService
}

/// <summary>
/// The result of a daemon-port probe (task #99): the <see cref="State"/> classification, the
/// parsed <see cref="RouterInfoResponse"/> (non-null only when the router served the identity
/// payload) and an optional human-readable <see cref="Detail"/> cause/hint.
/// </summary>
public sealed record RouterDaemonStatus(
    RouterDaemonState State,
    RouterInfoResponse? Info,
    string? Detail)
{
    /// <summary>The daemon process id (only known when the router reported its telemetry).</summary>
    public int? ProcessId => Info?.ProcessId;

    /// <summary>The router version (only known when the router reported its identity).</summary>
    public string? Version => Info?.Version;
}

/// <summary>
/// Probes, auto-starts and gracefully stops the OllamaRouter background user-process daemon
/// (task #99) from the CLI, the Avalonia app and external tooling (NightCodeForge). The
/// daemon is a detached **user** process only (no OS service registration) — the launcher
/// never touches a port it did not classify as its own.
/// </summary>
public interface IOllamaRouterDaemonLauncher
{
    /// <summary>
    /// Probes the daemon port and classifies it as <see cref="RouterDaemonState.Stopped"/> /
    /// <see cref="RouterDaemonState.Running"/> /
    /// <see cref="RouterDaemonState.PortOccupiedByOtherService"/>. Never launches or kills.
    /// </summary>
    Task<RouterDaemonStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Idempotent start: returns the current status when already running; otherwise launches
    /// the daemon detached and polls to healthy. Throws
    /// <see cref="RouterDaemonPortOccupiedException"/> when the port is foreign-owned,
    /// <see cref="RouterDaemonExecutableNotFoundException"/> when no daemon executable can be
    /// located and <see cref="RouterDaemonStartTimeoutException"/> when the probe never
    /// reports running within the start timeout.
    /// </summary>
    Task<RouterDaemonStatus> StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Auto-start semantics for UI callers: identical to <see cref="StartAsync"/> except that
    /// a foreign-owned port is RETURNED (<see cref="RouterDaemonState.PortOccupiedByOtherService"/>)
    /// instead of throwing — the caller decides how to surface it.
    /// </summary>
    Task<RouterDaemonStatus> EnsureRunningAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the daemon: a no-op when stopped, refuses (throws
    /// <see cref="RouterDaemonPortOccupiedException"/>) when the port is foreign-owned.
    /// Otherwise issues the graceful <c>POST /api/router/admin/shutdown</c> (best-effort),
    /// waits for the process to exit (tracked handle or <c>processId</c> lookup) and falls
    /// back to killing the entire process tree. Returns the final probe status.
    /// </summary>
    Task<RouterDaemonStatus> StopAsync(CancellationToken ct = default);
}
