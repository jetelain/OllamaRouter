using System.Text.Json;

namespace OllamaRouter.Security;

/// <summary>
/// Read-only access to the router's locally persisted default API key (task #98): the
/// <c>default-key.dat</c> envelope (<c>{"key": "...", "generatedAt": "..."}</c>) written by
/// the server's <c>ApiKeyStore</c> and wrapped at rest by an <see cref="IKeyProtector"/>
/// (DPAPI on Windows, plain <c>0600</c> file on Unix). Consumed by CLIs, the desktop app and
/// external tooling that need to authenticate against a router installed on the same
/// machine without re-entering the key.
/// </summary>
public interface IDefaultKeyReader
{
    /// <summary>
    /// The resolved on-disk path of the key envelope (diagnostic; the file may not exist).
    /// </summary>
    string KeyFilePath { get; }

    /// <summary>
    /// Reads and unwraps the default key envelope, returning the raw base64url key.
    /// Fails safe: returns <c>null</c> — NEVER throws — when the file is absent, unreadable,
    /// the protector cannot unwrap it or the envelope is malformed. Re-reads the file on
    /// every call (no cache) so an auto-rotated default key is always current.
    /// </summary>
    string? ReadDefaultKey();
}

/// <summary>
/// Default <see cref="IDefaultKeyReader"/> implementation (task #98).
/// <para>
/// Resolution precedence for <see cref="KeyFilePath"/> (the first candidate whose FILE
/// exists wins; when none exists the first candidate in priority order is reported):
/// <list type="number">
/// <item>an explicitly configured config directory → <c>&lt;configDirectory&gt;/default-key.dat</c>;</item>
/// <item>the <c>OLLAMAROUTER_HOME</c> environment variable → <c>&lt;OLLAMAROUTER_HOME&gt;/config/default-key.dat</c>;</item>
/// <item>the platform default install dir — Windows <c>%LOCALAPPDATA%\OllamaRouter\config\default-key.dat</c>,
/// Unix <c>&lt;HOME&gt;/.ollamarouter/config/default-key.dat</c>.</item>
/// </list>
/// The chain mirrors the server's production layout exactly (<c>ApiKeyStore</c> writes
/// <c>&lt;contentRoot&gt;/config/default-key.dat</c>; the install dir is
/// <c>%LOCALAPPDATA%\OllamaRouter</c>).
/// </para>
/// </summary>
public sealed class DefaultKeyReader : IDefaultKeyReader
{
    private const string KeyFileName = "default-key.dat";
    private const string HomeEnvironmentVariable = "OLLAMAROUTER_HOME";

    private readonly IKeyProtector _keyProtector;
    private readonly Func<string, string?> _environmentVariableAccessor;

    /// <summary>
    /// Creates a reader with an explicit config directory (candidate 1 of the resolution
    /// chain), a custom <paramref name="protector"/> (tests inject a fake so real DPAPI is
    /// never touched) and an injectable environment-variable accessor (defaults to
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>).
    /// </summary>
    public DefaultKeyReader(
        string? configDirectory = null,
        IKeyProtector? protector = null,
        Func<string, string?>? environmentVariableAccessor = null)
    {
        _keyProtector = protector ?? CreatePlatformProtector();
        _environmentVariableAccessor = environmentVariableAccessor
            ?? (name => Environment.GetEnvironmentVariable(name));

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            candidates.Add(Path.Combine(configDirectory, KeyFileName));
        }

        var home = _environmentVariableAccessor(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, "config", KeyFileName));
        }

        candidates.Add(GetPlatformDefaultPath());

        // The first candidate whose FILE exists wins; when none exists, report the first
        // candidate in priority order (a useful diagnostic for "where WOULD it be read").
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                KeyFilePath = candidate;
                return;
            }
        }

        KeyFilePath = candidates[0];
    }

    /// <summary>
    /// Creates a reader with no explicit config directory and the platform-default protector
    /// (<see cref="DpapiKeyProtector"/> on Windows, <see cref="PlainKeyProtector"/> on Unix).
    /// </summary>
    public static DefaultKeyReader CreateDefault() => new();

    /// <inheritdoc />
    public string KeyFilePath { get; }

    /// <inheritdoc />
    public string? ReadDefaultKey()
    {
        try
        {
            if (!File.Exists(KeyFilePath))
            {
                return null;
            }

            var envelopeBytes = _keyProtector.Unprotect(File.ReadAllBytes(KeyFilePath));
            using var document = JsonDocument.Parse(envelopeBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("key", out var keyElement) ||
                keyElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var key = keyElement.GetString();
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch
        {
            // Fails safe by contract: missing/unreadable file (IOException,
            // UnauthorizedAccessException), an unwrappable blob (CryptographicException on
            // Windows) or a malformed envelope (JsonException) all → null, never a throw.
            return null;
        }
    }

    /// <summary>
    /// The platform-default key path: Windows <c>%LOCALAPPDATA%\OllamaRouter\config</c>,
    /// Unix <c>&lt;HOME&gt;/.ollamarouter/config</c> (the server's production install layout).
    /// </summary>
    private static string GetPlatformDefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "OllamaRouter", "config", KeyFileName);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".ollamarouter", "config", KeyFileName);
    }

    // CA1416-clean: the platform-specific DpapiKeyProtector is only instantiated behind the
    // OperatingSystem.IsWindows() early-return branch.
    private static IKeyProtector CreatePlatformProtector()
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiKeyProtector();
        }

        return new PlainKeyProtector();
    }
}
