using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OllamaRouter.Services;

/// <summary>
/// On Windows, ensures that <c>ollama.exe</c> is running, launching it from its default
/// installation directory if it is not already active. On other platforms this is a no-op,
/// as Ollama is expected to be managed by the OS (e.g. systemd) or run manually.
/// </summary>
public sealed class OllamaProcessLauncher : IOllamaProcessLauncher
{
    // Launch process based on the Ollama source code for Windows, which is located here:
    // https://github.com/ollama/ollama/blob/main/cmd/start_windows.go

    private const string ProcessName = "ollama";

    private static readonly string DefaultOllamaExecutablePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Ollama", "ollama app.exe");

    private readonly ILogger<OllamaProcessLauncher> _logger;

    public OllamaProcessLauncher(ILogger<OllamaProcessLauncher> logger)
    {
        _logger = logger;
    }

    public void EnsureRunning()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            if (Process.GetProcessesByName(ProcessName).Length > 0)
            {
                _logger.LogInformation("Ollama is already running.");
                return;
            }

            if (!File.Exists(DefaultOllamaExecutablePath))
            {
                _logger.LogWarning("Ollama executable not found at {Path}. It will not be started automatically.", DefaultOllamaExecutablePath);
                return;
            }

            _logger.LogInformation("Ollama is not running, starting it from {Path}.", DefaultOllamaExecutablePath);

            Process.Start(new ProcessStartInfo
            {
                FileName = DefaultOllamaExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(DefaultOllamaExecutablePath),
                UseShellExecute = true,
                Arguments = "--fast-startup hidden"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure that Ollama is running.");
        }
    }
}
