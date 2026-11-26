namespace OllamaRouter.App.ViewModels;

/// <summary>The connection-bar daemon state (task #173) — the single source of truth for the
/// status text and the status-dot brush.</summary>
public enum DaemonStatus
{
    Connecting,
    Running,
    Remote,
    PortConflict,
    Stopped,
    Disconnected,
}
