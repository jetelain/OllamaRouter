using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Ensures that the local Ollama process is running.
/// </summary>
public interface IOllamaProcessLauncher
{
    /// <summary>
    /// Checks if the local Ollama process is running, and starts it if not.
    /// </summary>
    void EnsureRunning();

    /// <summary>
    /// Checks if the local Ollama process is running, and starts it if not. When
    /// <paramref name="options"/> is supplied, the expected local daemon port (parsed from the
    /// first loopback target / <see cref="OllamaRouterOptions.LocalUrl"/>, defaulting to 11435
    /// then 11434) is probed first: if that port is already occupied by another process, the
    /// launcher logs a warning and does NOT start a second <c>ollama</c> (plan §2.5).
    /// </summary>
    void EnsureRunning(OllamaRouterOptions? options);
}
