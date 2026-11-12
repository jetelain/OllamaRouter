using Avalonia;
using Avalonia.Win32;
using Avalonia.X11;
using OllamaRouter.Client.Protocol;

namespace OllamaRouter.App;

/// <summary>
/// Desktop entry point of the OllamaRouter configuration &amp; dashboard application
/// (task #102). Software-only rendering on BOTH platforms (Win32 + X11) so the
/// configuration app never allocates a GPU swapchain (0 MB of GPU VRAM — the VRAM is
/// left for the local LLMs the router manages).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        LaunchOptions = ProtocolUriParser.Parse(args);
        try
        {
            // Self-register the ollamarouter:// handler for the RUNNING executable (user-scoped,
            // no elevation, idempotent) so standalone installs always resolve the protocol.
            ProtocolRegistrationService.Register();
        }
        catch
        {
            // Best-effort: registration is never fatal to the dashboard/wizard startup.
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// The parsed startup launch options (task #107): wizard vs. dashboard mode and the optional
    /// initial dashboard tab from <c>ollamarouter://…</c> URIs / <c>--wizard</c>. Consumed by
    /// <see cref="App"/> at framework initialization.
    /// </summary>
    internal static AppLaunchOptions LaunchOptions { get; private set; } = new() { Wizard = false };

    /// <summary>
    /// The verified Avalonia 11.3 software-rendering pattern: in 11.x the
    /// <c>UseWin32</c>/<c>UseX11</c> extensions take no options argument; the platform
    /// options are registered through <see cref="AppBuilder.With(object)"/> into the
    /// <c>AvaloniaLocator</c> and consumed by the platform at initialization.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] });
}
