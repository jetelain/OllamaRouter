using System.Diagnostics;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Ensures that Ollama is running, launching it from its default installation location if it is
/// not already active: <c>ollama app.exe</c> on Windows, <c>Ollama.app</c> on macOS. On other
/// platforms this is a no-op, as Ollama is expected to be managed by the OS (e.g. systemd) or run manually.
/// </summary>
public sealed class OllamaProcessLauncher : IOllamaProcessLauncher
{
    // Launch process based on the Ollama source code for Windows, which is located here:
    // https://github.com/ollama/ollama/blob/main/cmd/start_windows.go

    private const string ProcessName = "ollama";

    private static readonly string DefaultWindowsExecutablePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Ollama", "ollama app.exe");

    // The macOS app may be installed system-wide or for the current user only.
    private static readonly string[] DefaultMacAppPaths =
    [
        "/Applications/Ollama.app",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Ollama.app")
    ];

    private readonly ILogger<OllamaProcessLauncher> _logger;
    private readonly OllamaRouterOptions _options;

    public OllamaProcessLauncher(ILogger<OllamaProcessLauncher> logger, IOptions<OllamaRouterOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public void EnsureRunning()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            if (IsOllamaRunning())
            {
                _logger.LogInformation("Ollama is already running.");
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                StartOnWindows();
            }
            else
            {
                StartOnMacOS();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure that Ollama is running.");
        }
    }

    // Matches the server ("ollama") and, on macOS, the menu bar app ("Ollama"): the comparison is
    // case-insensitive as process names are case-sensitive on macOS.
    private static bool IsOllamaRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited while enumerating.
                }
            }
        }

        return false;
    }

    private void StartOnWindows()
    {
        if (!File.Exists(DefaultWindowsExecutablePath))
        {
            _logger.LogWarning("Ollama executable not found at {Path}. It will not be started automatically.", DefaultWindowsExecutablePath);
            return;
        }

        _logger.LogInformation("Ollama is not running, starting it from {Path}.", DefaultWindowsExecutablePath);

        Process.Start(new ProcessStartInfo
        {
            FileName = DefaultWindowsExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(DefaultWindowsExecutablePath),
            UseShellExecute = true,
            Arguments = "--fast-startup hidden"
        });
    }

    private void StartOnMacOS()
    {
        var appPath = DefaultMacAppPaths.FirstOrDefault(Directory.Exists);
        if (appPath is null)
        {
            _logger.LogWarning("Ollama.app not found in {Paths}. It will not be started automatically.", string.Join(", ", DefaultMacAppPaths));
            return;
        }

        // Launched through LaunchServices ("open"), the app does not inherit this process environment,
        // so the listen address expected by LocalUrl is passed explicitly. "-g" keeps it in the background.
        var startInfo = new ProcessStartInfo
        {
            FileName = "open",
            ArgumentList = { "-g" },
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var ollamaHost = GetOllamaHost(_options.LocalUrl);
        if (ollamaHost is not null)
        {
            startInfo.ArgumentList.Add("--env");
            startInfo.ArgumentList.Add($"OLLAMA_HOST={ollamaHost}");
        }

        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add(appPath);

        _logger.LogInformation("Ollama is not running, starting it from {Path} (OLLAMA_HOST={Host}).", appPath, ollamaHost ?? "default");

        using var process = Process.Start(startInfo);
    }

    /// <summary>
    /// Returns the <c>OLLAMA_HOST</c> value ("host:port") matching <paramref name="localUrl"/>, or
    /// <see langword="null"/> if it is not a valid absolute URL.
    /// </summary>
    internal static string? GetOllamaHost(string localUrl)
    {
        if (!Uri.TryCreate(localUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return $"{uri.Host}:{uri.Port}";
    }
}
