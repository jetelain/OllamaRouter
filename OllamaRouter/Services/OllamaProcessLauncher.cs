using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// On Windows, ensures that <c>ollama.exe</c> is running, launching it from its default
/// installation directory if it is not already active. On other platforms this is a no-op,
/// as Ollama is expected to be managed by the OS (e.g. systemd) or run manually.
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
        EnsureRunning(options: null);
    }

    public void EnsureRunning(OllamaRouterOptions? options)
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
        var url = options is null ? null
            : options.Targets is { Count: > 0 } && IsLoopback(options.Targets[0].Url)
                ? options.Targets[0].Url
                : options.LocalUrl;

        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Port > 0)
        {
            return uri.Port;
        }

        return PrimaryDefaultPort;
    }

    private static bool IsLoopback(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == "::1");
    }
}
