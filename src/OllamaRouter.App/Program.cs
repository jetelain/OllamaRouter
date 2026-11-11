using Avalonia;
using Avalonia.Win32;
using Avalonia.X11;

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
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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
