using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OllamaRouter.Options;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Services.Process;

/// <summary>
/// On Windows and macOS, ensures that the local Ollama daemon is running, launching it from its
/// default installation location (<c>ollama app.exe</c> on Windows, <c>Ollama.app</c> on macOS)
/// if it is not already active. On other platforms this is a no-op, as Ollama is expected to be
/// managed by the OS (e.g. systemd) or run manually.
/// <para>
/// <b>Port-availability detection (plan §2.5):</b> before launching, the expected local daemon
/// port (parsed from the first loopback <see cref="OllamaRouterOptions.Targets"/> entry or
/// <see cref="OllamaRouterOptions.LocalUrl"/>, defaulting to 11435 then 11434) is probed. When
/// that port is already occupied — e.g. a manually managed daemon started with
/// <c>OLLAMA_HOST=0.0.0.0:11435</c> for a dual-router mesh — the launcher logs a warning and
/// does NOT start a second <c>ollama</c>, avoiding the classic "port in use" crash loop.
/// </para>
/// </summary>
public sealed class OllamaProcessLauncher : IOllamaProcessLauncher
{
    // Launch process based on the Ollama source code for Windows, which is located here:
    // https://github.com/ollama/ollama/blob/main/cmd/start_windows.go

    private const string ProcessName = "ollama";

    /// <summary>Default daemon port probed when no explicit port can be parsed from the options.</summary>
    public const int PrimaryDefaultPort = 11435;

    /// <summary>Legacy daemon port probed after <see cref="PrimaryDefaultPort"/>.</summary>
    public const int LegacyDefaultPort = 11434;

    private static readonly string DefaultWindowsExecutablePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Ollama", "ollama app.exe");

    private static readonly string[] DefaultMacAppPaths =
    [
        "/Applications/Ollama.app",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Ollama.app")
    ];

    private readonly ILogger<OllamaProcessLauncher> _logger;

    public OllamaProcessLauncher(ILogger<OllamaProcessLauncher> logger)
    {
        _logger = logger;
    }

    public void EnsureRunning()
    {
        EnsureRunning(options: null);
    }

    public void EnsureRunning(OllamaRouterOptions? options)
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

            // Port-availability detection (plan §2.5): when the expected daemon port is already
            // occupied (a manually managed daemon, e.g. started with OLLAMA_HOST=0.0.0.0:11435),
            // do not start a second ollama that would immediately fail to bind.
            var probedPort = ResolveExpectedDaemonPort(options);
            if (probedPort is { } port && !IsPortAvailable(port))
            {
                _logger.LogWarning(
                    "Port {Port} is in use by another process; not starting Ollama.",
                    port);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                StartOnWindows();
            }
            else
            {
                StartOnMacOS(options);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure that Ollama is running.");
        }
    }

    /// <summary>
    /// Detects a running Ollama process case-insensitively: the server process is named
    /// <c>ollama</c> on both platforms, while the macOS menu bar app is named <c>Ollama</c>, so a
    /// case-sensitive name comparison would miss it and a second daemon would be launched.
    /// </summary>
    private static bool IsOllamaRunning()
    {
        foreach (var process in System.Diagnostics.Process.GetProcesses())
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
                    // The process exited while we were reading its name; skip it.
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

        System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = DefaultWindowsExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(DefaultWindowsExecutablePath),
            UseShellExecute = true,
            Arguments = "--fast-startup hidden"
        });
    }

    private void StartOnMacOS(OllamaRouterOptions? options)
    {
        var appPath = DefaultMacAppPaths.FirstOrDefault(Directory.Exists);
        if (appPath is null)
        {
            _logger.LogWarning("Ollama.app not found in {Paths}. It will not be started automatically.", string.Join(", ", DefaultMacAppPaths));
            return;
        }

        var host = ResolveExpectedDaemonHost(options);
        var startInfo = new ProcessStartInfo
        {
            FileName = "open",
            ArgumentList = { "-g" },
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (host is not null)
        {
            // LaunchServices does not inherit our environment, so pass OLLAMA_HOST explicitly.
            startInfo.ArgumentList.Add("--env");
            startInfo.ArgumentList.Add($"OLLAMA_HOST={host}");
        }
        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add(appPath);

        _logger.LogInformation("Ollama is not running, starting it from {Path} (OLLAMA_HOST={Host}).", appPath, host ?? "default");

        using var process = System.Diagnostics.Process.Start(startInfo);
    }

    /// <summary>
    /// Probes <paramref name="port"/> on the loopback address by attempting to bind a
    /// <see cref="TcpListener"/>. Returns true when the port is free to bind, false when it is
    /// already occupied (or the probe fails). Platform-neutral: the bind probe works on any OS.
    /// </summary>
    internal static bool IsPortAvailable(int port, string host = "127.0.0.1")
    {
        try
        {
            var address = IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Loopback;
            using var listener = new TcpListener(address, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the expected local daemon port to probe from <paramref name="options"/>: the port
    /// of the first loopback target (or <see cref="OllamaRouterOptions.LocalUrl"/> when no targets
    /// are configured), falling back to <see cref="PrimaryDefaultPort"/> (11435) when no explicit
    /// port can be parsed (including a null options object or a URL without a port). Always returns
    /// a usable port for the bind probe.
    /// </summary>
    internal static int? ResolveExpectedDaemonPort(OllamaRouterOptions? options)
    {
        var url = options?.Targets?.FirstOrDefault(t => IsLoopback(t.Url))?.Url;
#pragma warning disable CS0618
        if (string.IsNullOrWhiteSpace(url))
        {
            url = options?.LocalUrl;
        }
#pragma warning restore CS0618

        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Port > 0)
        {
            return uri.Port;
        }

        return PrimaryDefaultPort;
    }

    internal static bool IsLoopback(string? url) => TargetLocationClassifier.IsLoopbackUrl(url);

    /// <summary>
    /// Resolves the expected local daemon host:port to pass as <c>OLLAMA_HOST</c> when launching
    /// Ollama on macOS: the host:port of the first loopback target (or
    /// <see cref="OllamaRouterOptions.LocalUrl"/> when no targets are configured), or null when no
    /// absolute URL can be parsed (in which case Ollama's own default port applies).
    /// </summary>
    internal static string? ResolveExpectedDaemonHost(OllamaRouterOptions? options)
    {
        var url = options?.Targets?.FirstOrDefault(t => IsLoopback(t.Url))?.Url;
#pragma warning disable CS0618
        if (string.IsNullOrWhiteSpace(url))
        {
            url = options?.LocalUrl;
        }
#pragma warning restore CS0618

        return GetOllamaHost(url);
    }

    /// <summary>
    /// Extracts the <c>host:port</c> pair from an absolute URL (e.g.
    /// <c>http://127.0.0.1:11435/</c> → <c>127.0.0.1:11435</c>; scheme default ports apply), or
    /// null when the URL is missing or not absolute.
    /// </summary>
    internal static string? GetOllamaHost(string? localUrl)
    {
        if (string.IsNullOrWhiteSpace(localUrl) || !Uri.TryCreate(localUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return $"{uri.Host}:{uri.Port}";
    }
}
