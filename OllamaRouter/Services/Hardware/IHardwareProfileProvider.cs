namespace OllamaRouter.Services.Hardware;

/// <summary>
/// Provides the hardware profile of the LOCAL workstation this router runs on.
/// Implementations are fail-soft: every sub-probe degrades to <see cref="SystemRamType.Unknown"/>
/// / zero on failure — the interface NEVER throws.
/// </summary>
public interface IHardwareProfileProvider
{
    /// <summary>
    /// Returns the local node's hardware profile. The profile is probed lazily on the
    /// first call and cached for the process lifetime (hardware is static), so the
    /// second call is a pure memory read.
    /// </summary>
    Task<NodeHardwareProfile> GetLocalProfileAsync(CancellationToken cancellationToken = default);
}
