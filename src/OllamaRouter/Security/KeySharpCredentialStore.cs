using KeySharp;
using Microsoft.Extensions.Logging;

namespace OllamaRouter.Security;

/// <summary>
/// Production <see cref="ICredentialStore"/> wrapping <c>Pmad.KeySharp</c>'s
/// <see cref="Keyring"/> facade (task #110): the OS credential store of the platform (Windows
/// Credential Manager, macOS Keychain, Linux Secret Service / libsecret).
/// <para>
/// Failure semantics (see <see cref="ICredentialStore"/>): <see cref="IsAvailable"/> is a
/// read-only probe that never throws (any native/backend error → <c>false</c>);
/// <see cref="GetPassword"/> and <see cref="DeletePassword"/> are fail-soft (<c>NotFound</c> →
/// <c>null</c> / no-op; other <see cref="KeyringException"/> or unexpected errors → warning +
/// <c>null</c> / no-op); <see cref="SetPassword"/> is fail-hard (any error rethrown so
/// <see cref="Configuration.RouterConfigPersistence"/> can 500 + roll back). The raw password
/// is NEVER logged — only the target username and the backend error message.
/// </para>
/// </summary>
public sealed class KeySharpCredentialStore : ICredentialStore
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates the store. <paramref name="logger"/> is optional (bare test hosts).
    /// </summary>
    public KeySharpCredentialStore(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            try
            {
                return Keyring.IsAvailable();
            }
            catch (Exception)
            {
                // Native library load failure, D-Bus error, ... — the probe must NEVER throw;
                // the caller then takes the IKeyProtector v1: fallback path.
                return false;
            }
        }
    }

    /// <inheritdoc />
    public string? GetPassword(string package, string service, string username)
    {
        try
        {
            return Keyring.GetPassword(package, service, username);
        }
        catch (KeyringException ex) when (ex.Type == ErrorType.NotFound)
        {
            // A missing entry is the normal "no key configured" state — silent null.
            return null;
        }
        catch (Exception ex)
        {
            // Unavailable store, access denied, unexpected native failure: fail soft so the
            // bind-time unwrap degrades to unauthenticated mode instead of crashing startup.
            // (Never logs the password — GetPassword takes no password argument anyway.)
            _logger?.LogWarning(ex,
                "OS keyring read for target '{Username}' (package '{Package}', service '{Service}') failed; " +
                "degrading to null.",
                username, package, service);
            return null;
        }
    }

    /// <inheritdoc />
    public void SetPassword(string package, string service, string username, string password)
    {
        try
        {
            Keyring.SetPassword(package, service, username, password);
        }
        catch (Exception ex)
        {
            // FAIL-HARD: rethrow so the persistence layer returns 500 and rolls back the
            // keyring pass. A silent no-op would persist a keyring: marker for a secret that
            // was never stored. The password is NEVER part of the log line.
            _logger?.LogError(ex,
                "OS keyring write for target '{Username}' (package '{Package}', service '{Service}') failed.",
                username, package, service);
            throw;
        }
    }

    /// <inheritdoc />
    public void DeletePassword(string package, string service, string username)
    {
        try
        {
            Keyring.DeletePassword(package, service, username);
        }
        catch (KeyringException ex) when (ex.Type == ErrorType.NotFound)
        {
            // Idempotent delete: a missing entry is the desired end state.
        }
        catch (Exception ex)
        {
            // Other backend errors: log + swallow. The orphan entry is inert (the config no
            // longer references it) and the delete is retried on the next clear/remove of the
            // same name. The password is never involved.
            _logger?.LogWarning(ex,
                "OS keyring delete for target '{Username}' (package '{Package}', service '{Service}') failed; " +
                "leaving the (unreferenced) entry behind.",
                username, package, service);
        }
    }
}
