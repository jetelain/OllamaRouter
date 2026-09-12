namespace OllamaRouter.Services;

/// <summary>
/// Point-in-time view of which routing targets are currently enabled.
/// </summary>
public readonly record struct TargetAvailabilitySnapshot(bool Local, bool Remote, bool Cloud);

/// <summary>
/// In-memory, thread-safe source of truth for which routing targets (Local/Remote/Cloud) are
/// currently enabled. Loaded from a dedicated JSON state file (in the application data folder)
/// at startup. <see cref="Update"/> applies the new flags in memory immediately (so routing
/// decisions and the monitoring UI see the change instantly) and then persists them to the
/// state file on a best-effort basis, so the choice survives an app restart.
/// </summary>
public interface ITargetAvailabilityService
{
    bool IsEnabled(RoutingTarget target);

    TargetAvailabilitySnapshot GetSnapshot();

    /// <summary>
    /// Applies the given flags in memory immediately, then persists them to the state file on a
    /// best-effort ("failsafe") basis: if the file cannot be written, the change still takes
    /// effect in memory and a warning is logged.
    /// </summary>
    void Update(bool local, bool remote, bool cloud);
}
