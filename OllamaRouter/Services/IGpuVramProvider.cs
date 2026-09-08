namespace OllamaRouter.Services;

/// <summary>
/// Provides the amount of video memory (VRAM) currently free on the local GPU.
/// </summary>
public interface IGpuVramProvider
{
    int GetFreeVramMB();
}
