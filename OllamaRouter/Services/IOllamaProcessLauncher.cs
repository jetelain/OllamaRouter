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
}
