namespace OllamaRouter.Services;

/// <summary>
/// Provides the amount of video memory (VRAM) currently free on the local GPU.
/// </summary>
public interface IGpuVramProvider
{
    int GetFreeVramMB();

    /// <summary>
    /// Best-effort snapshot of the LOCAL machine's GPU hardware (vendor + per-device display
    /// names + total VRAM in MB). Default implementation returns the "no GPU detected" snapshot
    /// (<see cref="LocalGpuSnapshot.Unknown"/>); vendor providers override when their CLI tool
    /// is available. Never throws; failures map to the unknown snapshot.
    /// </summary>
    Task<LocalGpuSnapshot> GetLocalGpuSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(LocalGpuSnapshot.Unknown);
}

/// <summary>
/// Immutable snapshot of the local workstation's GPU hardware: the vendor display name
/// (e.g. "Nvidia"), one human-readable device entry per physical GPU
/// (e.g. "NVIDIA GeForce RTX 4080 (16384 MB)"), and the summed total VRAM in MB.
/// </summary>
public sealed record LocalGpuSnapshot(string Vendor, IReadOnlyList<string> Devices, int TotalVramMB)
{
    /// <summary>The "no GPU detected" snapshot returned by the default implementation and on any probe failure.</summary>
    public static LocalGpuSnapshot Unknown { get; } = new("Unknown", Array.Empty<string>(), 0);
}
