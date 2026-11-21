using System.Runtime.Versioning;
using Microsoft.Win32;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace OllamaRouter.Client.Protocol;

/// <summary>
/// The complete, deterministic Windows registry plan for the <c>ollamarouter://</c> protocol
/// handler (task #107): the four user-scoped values under <c>HKCU\Software\Classes\ollamarouter</c>.
/// Pure data — the <see cref="ProtocolRegistrationService.CreateWindowsPlan"/> builder produces it
/// without touching the registry, so unit tests can pin every byte.
/// </summary>
public sealed record WindowsProtocolRegistration
{
    /// <summary>The scheme key path, relative to <c>HKEY_CURRENT_USER</c>
    /// (<c>Software\Classes\ollamarouter</c>).</summary>
    public required string SchemeKeyPath { get; init; }

    /// <summary>The scheme key's default value (<c>URL:OllamaRouter Protocol</c>).</summary>
    public required string DisplayName { get; init; }

    /// <summary>The <c>URL Protocol</c> named value (empty string — marks the key as a protocol
    /// handler to Windows).</summary>
    public required string UrlProtocolValue { get; init; }

    /// <summary>The icon subkey path (relative to <c>HKEY_CURRENT_USER</c>).</summary>
    public required string IconKeyPath { get; init; }

    /// <summary>The icon subkey's default value: the quoted app executable plus <c>,0</c>.</summary>
    public required string IconValue { get; init; }

    /// <summary>The open-command subkey path (relative to <c>HKEY_CURRENT_USER</c>).</summary>
    public required string CommandKeyPath { get; init; }

    /// <summary>The open-command subkey's default value: the quoted app executable plus
    /// <c>" %1"</c>.</summary>
    public required string CommandValue { get; init; }
}

/// <summary>
/// The complete, deterministic Linux XDG plan for the <c>ollamarouter://</c> protocol handler
/// (task #107): the per-user desktop entry that declares
/// <c>MimeType=x-scheme-handler/ollamarouter;</c> and the best-effort <c>xdg-mime</c> command
/// (null when <c>xdg-mime</c> is not on <c>PATH</c>). Pure data — the
/// <see cref="ProtocolRegistrationService.CreateLinuxPlan"/> builder produces it without touching
/// the file system, so unit tests can pin every byte.
/// </summary>
public sealed record LinuxProtocolRegistration
{
    /// <summary>The desktop-entry path
    /// (<c>&lt;home&gt;/.local/share/applications/ollamarouter.desktop</c>).</summary>
    public required string DesktopEntryPath { get; init; }

    /// <summary>The full desktop-entry INI content.</summary>
    public required string DesktopEntryContent { get; init; }

    /// <summary>The XDG scheme-handler MIME type (<c>x-scheme-handler/ollamarouter</c>).</summary>
    public required string MimeType { get; init; }

    /// <summary>The best-effort <c>xdg-mime</c> invocation
    /// (<c>xdg-mime default ollamarouter.desktop x-scheme-handler/ollamarouter</c>), or null when
    /// <c>xdg-mime</c> cannot be resolved on <c>PATH</c>.</summary>
    public required string? XdgMimeCommand { get; init; }
}

/// <summary>
/// User-scoped (NON-elevated) registration of the <c>ollamarouter://</c> custom URI protocol
/// (task #107) so browser-based tools (e.g. the NightCodeForge Web UI) can launch
/// <c>ollamarouter-app</c> with <c>ollamarouter://open</c> (dashboard) or
/// <c>ollamarouter://wizard</c> (Setup Wizard) without administrator/root privileges.
/// <para>
/// Windows: <c>HKCU\Software\Classes\ollamarouter</c> (no HKLM, no elevation). Linux: an XDG
/// desktop entry in <c>~/.local/share/applications</c> plus a best-effort <c>xdg-mime</c>
/// default-handler update. Every other platform is a no-op (macOS requires <c>.app</c> bundle
/// metadata and is out of scope).
/// </para>
/// <para>
/// Registration is strictly BEST-EFFORT at every call site: <see cref="Register"/> and
/// <see cref="Unregister"/> swallow all exceptions and never change caller exit codes.
/// The pure <see cref="CreateWindowsPlan"/> / <see cref="CreateLinuxPlan"/> builders are the
/// testable seam — unit tests pin the payloads without ever invoking the executors.
/// </para>
/// </summary>
public static class ProtocolRegistrationService
{
    /// <summary>The URI scheme handled by the app.</summary>
    public const string Scheme = "ollamarouter";

    /// <summary>The Windows display name for the scheme.</summary>
    public const string DisplayName = "URL:OllamaRouter Protocol";

    /// <summary>The Linux XDG scheme-handler MIME type.</summary>
    public const string MimeType = "x-scheme-handler/ollamarouter";

    /// <summary>The Linux XDG desktop-entry file name.</summary>
    public const string DesktopEntryFileName = "ollamarouter.desktop";

    /// <summary>The Windows scheme-key path relative to <c>HKEY_CURRENT_USER</c>.</summary>
    internal const string WindowsSchemeKeyPath = @"Software\Classes\ollamarouter";

    /// <summary>
    /// Builds the deterministic Windows registration plan for <paramref name="appExecutablePath"/>
    /// (no registry access). The icon and command values carry the quoted executable path.
    /// </summary>
    public static WindowsProtocolRegistration CreateWindowsPlan(string appExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(appExecutablePath);
        return new WindowsProtocolRegistration
        {
            SchemeKeyPath = WindowsSchemeKeyPath,
            DisplayName = DisplayName,
            UrlProtocolValue = string.Empty,
            IconKeyPath = $@"{WindowsSchemeKeyPath}\DefaultIcon",
            IconValue = $"\"{appExecutablePath}\",0",
            CommandKeyPath = $@"{WindowsSchemeKeyPath}\shell\open\command",
            CommandValue = $"\"{appExecutablePath}\" \"%1\"",
        };
    }

    /// <summary>
    /// Builds the deterministic Linux XDG registration plan for
    /// <paramref name="appExecutablePath"/> under <paramref name="homeDirectory"/>.
    /// <paramref name="xdgMimeOnPath"/> is the PATH probe seam (<c>true</c> ⇒ the plan carries the
    /// <c>xdg-mime</c> command); the default probe checks each <c>PATH</c> entry for
    /// <c>xdg-mime</c>. No file system writes here.
    /// </summary>
    public static LinuxProtocolRegistration CreateLinuxPlan(
        string appExecutablePath, string homeDirectory, Func<string, bool>? xdgMimeOnPath = null)
    {
        ArgumentNullException.ThrowIfNull(appExecutablePath);
        ArgumentNullException.ThrowIfNull(homeDirectory);
        var probe = xdgMimeOnPath ?? DefaultXdgMimeOnPath;
        var present = probe("xdg-mime");
        return new LinuxProtocolRegistration
        {
            DesktopEntryPath = Path.Combine(homeDirectory, ".local", "share", "applications", DesktopEntryFileName).Replace('\\', '/'),
            DesktopEntryContent = BuildDesktopEntryContent(appExecutablePath),
            MimeType = MimeType,
            XdgMimeCommand = present ? $"xdg-mime default {DesktopEntryFileName} {MimeType}" : null,
        };
    }

    /// <summary>
    /// Registers the <c>ollamarouter://</c> handler for <paramref name="appExecutablePath"/>
    /// (default: <see cref="Environment.ProcessPath"/> — the running <c>ollamarouter-app</c>) for the
    /// CURRENT USER (no elevation). Windows: idempotently (re)writes the four
    /// <c>HKCU\Software\Classes\ollamarouter</c> values. Linux: idempotently overwrites the
    /// <c>~/.local/share/applications/ollamarouter.desktop</c> entry and, when <c>xdg-mime</c> is on
    /// <c>PATH</c>, sets it as the default handler (bounded wait). Other platforms: no-op.
    /// Best-effort: all exceptions are swallowed (registration must never crash the app/CLI).
    /// </summary>
    public static void Register(string? appExecutablePath = null)
    {
        try
        {
            var exe = appExecutablePath ?? Environment.ProcessPath ?? string.Empty;
            if (exe.Length == 0)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                RegisterWindows(exe);
            }
            else if (OperatingSystem.IsLinux())
            {
                RegisterLinux(exe);
            }
            // Other platforms (macOS ...) are deliberately a no-op (out of scope).
        }
        catch
        {
            // Best-effort by contract: a registration failure never changes the caller's behavior.
        }
    }

    /// <summary>
    /// Removes the user-scoped <c>ollamarouter://</c> handler registration for the current user:
    /// Windows deletes <c>HKCU\Software\Classes\ollamarouter</c> entirely; Linux deletes the
    /// desktop-entry file. Other platforms: no-op. Best-effort: all exceptions are swallowed.
    /// </summary>
    public static void Unregister()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Registry.CurrentUser.DeleteSubKeyTree(WindowsSchemeKeyPath, throwOnMissingSubKey: false);
            }
            else if (OperatingSystem.IsLinux())
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                {
                    File.Delete(Path.Combine(home, ".local", "share", "applications", DesktopEntryFileName));
                }
            }
        }
        catch
        {
            // Best-effort by contract.
        }
    }

    /// <summary>
    /// The default <c>xdg-mime</c> PATH probe: true when any <c>PATH</c> entry contains an
    /// <c>xdg-mime</c> file (Linux <c>:</c> / Windows <c>;</c> separators).
    /// </summary>
    private static bool DefaultXdgMimeOnPath(string _probeFileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(':', ';'))
        {
            if (dir.Length == 0)
            {
                continue;
            }

            if (File.Exists(Path.Combine(dir, "xdg-mime")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the desktop-entry INI text for the Linux XDG plan.</summary>
    private static string BuildDesktopEntryContent(string appExecutablePath) => $"""
        [Desktop Entry]
        Version=1.0
        Type=Application
        Name=OllamaRouter
        Comment=OllamaRouter dashboard and setup wizard
        Exec="{appExecutablePath}" %u
        Terminal=false
        MimeType={MimeType};
        StartupNotify=true
        """;

    [SupportedOSPlatform("windows")]
    private static void RegisterWindows(string exe)
    {
        var plan = CreateWindowsPlan(exe);
        using var schemeKey = Registry.CurrentUser.CreateSubKey(plan.SchemeKeyPath)
            ?? throw new InvalidOperationException("could not open the ollamarouter scheme key.");
        schemeKey.SetValue(string.Empty, plan.DisplayName, RegistryValueKind.String);
        schemeKey.SetValue("URL Protocol", plan.UrlProtocolValue, RegistryValueKind.String);

        using var iconKey = Registry.CurrentUser.CreateSubKey(plan.IconKeyPath)
            ?? throw new InvalidOperationException("could not open the ollamarouter DefaultIcon key.");
        iconKey.SetValue(string.Empty, plan.IconValue, RegistryValueKind.String);

        using var commandKey = Registry.CurrentUser.CreateSubKey(plan.CommandKeyPath)
            ?? throw new InvalidOperationException("could not open the ollamarouter open\\command key.");
        commandKey.SetValue(string.Empty, plan.CommandValue, RegistryValueKind.String);
    }

    private static void RegisterLinux(string exe)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return; // no resolvable user home: nothing safe to register against
        }

        var plan = CreateLinuxPlan(exe, home);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.DesktopEntryPath)!);
        File.WriteAllText(plan.DesktopEntryPath, plan.DesktopEntryContent);

        if (plan.XdgMimeCommand is not null)
        {
            var parts = plan.XdgMimeCommand.Split(' ', 2);
            var process = new ProcessStartInfo(parts[0], parts.Length > 1 ? parts[1] : string.Empty)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var runner = System.Diagnostics.Process.Start(process);
            runner?.WaitForExit(5_000); // bounded: a hung xdg-mime must never block startup
        }
    }
}
