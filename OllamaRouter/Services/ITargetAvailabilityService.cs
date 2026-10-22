namespace OllamaRouter.Services;

/// <summary>
/// Point-in-time view of which routing targets are currently enabled, keyed by target.
/// </summary>
public readonly record struct TargetAvailabilitySnapshot(
    IReadOnlyDictionary<RoutingTarget, bool> Flags);

/// <summary>
/// In-memory, thread-safe source of truth for which routing targets are currently enabled.
/// Loaded from a dedicated JSON state file (in the application data folder) at startup.
/// <see cref="Update"/> applies the new flags in memory immediately (so routing decisions and
/// the monitoring UI see the change instantly) and then persists them to the state file on a
/// best-effort basis, so the choice survives an app restart.
/// </summary>
public interface ITargetAvailabilityService
{
    bool IsEnabled(RoutingTarget target);

    TargetAvailabilitySnapshot GetSnapshot();

    /// <summary>
    /// Applies the given per-target flags in memory immediately, then persists them to the state
    /// file on a best-effort ("failsafe") basis: if the file cannot be written, the change still
    /// takes effect in memory and a warning is logged. Keys are matched against the known target
    /// names case-insensitively; unknown keys are ignored and targets missing from the payload
    /// keep their current value.
    /// </summary>
    void Update(IReadOnlyDictionary<string, bool> flags);
}
