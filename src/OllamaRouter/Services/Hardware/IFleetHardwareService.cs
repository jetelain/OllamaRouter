namespace OllamaRouter.Services.Hardware;

/// <summary>
/// Fleet hardware topology store: serves, probes and persists the hardware profile of
/// every effective fleet target. Manual entries (pushed via the PUT endpoint or external
/// wizards) are authoritative — peer refresh never overwrites them.
/// </summary>
public interface IFleetHardwareService
{
    /// <summary>
    /// Returns the stored profile for the named target (case-insensitive); null when unknown.
    /// Never performs a network call.
    /// </summary>
    NodeHardwareProfile? GetProfile(string targetName);

    /// <summary>
    /// Returns the effective maximum VRAM (in MB) for the specified target: uses the target's
    /// explicit <see cref="Options.OllamaTargetOptions.MaxVramMB"/> when set, otherwise the cached
    /// <see cref="NodeHardwareProfile.TotalFastMemoryMB"/> from the fleet store.
    /// Returns null when unknown.
    /// </summary>
    int? GetEffectiveMaxVramMB(Options.OllamaTargetOptions target);

    /// <summary>
    /// The full fleet hardware topology: one row per effective configured target (Cloud
    /// excluded, configuration order), always fail-soft with per-node reachability
    /// flags. Peer targets are probed in parallel (3 s per probe, 24 h per-target TTL);
    /// the local workstation target is served from the local provider.
    /// </summary>
    Task<IReadOnlyList<FleetHardwareNode>> GetFleetHardwareAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores a manually pushed profile for the named target (case-insensitive match
    /// against the effective targets). Returns false (no state change) for unknown targets.
    /// Manual entries are authoritative: peer refresh must never overwrite them.
    /// </summary>
    Task<bool> UpsertProfileAsync(string targetName, NodeHardwareProfile profile, CancellationToken ct = default);
}
