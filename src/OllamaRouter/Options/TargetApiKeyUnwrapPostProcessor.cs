using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Security;

namespace OllamaRouter.Options;

/// <summary>
/// Bind-time unwrap of the fleet targets' provider API keys (task #61, maintainer decision 3;
/// extended by task #110 for the OS keyring).
/// <para>
/// Wire format of <c>OllamaRouter:Targets[i]:ApiKey</c> in <c>config/config.json</c> (priority
/// order in <see cref="UnwrapKey"/>):
/// <list type="number">
/// <item><c>keyring:&lt;targetName&gt;</c> (task #110, <see cref="KeyringPrefix"/>): the secret
/// lives in the OS credential store (package <c>Pmad.OllamaRouter</c>, service <c>openai</c>,
/// username <c>&lt;targetName&gt;</c>) and is resolved through the <see cref="ICredentialStore"/>;
/// any degradation (store missing/unavailable, entry missing) → <c>null</c> + warning, never a
/// startup crash.</item>
/// <item><c>v1:</c> + Base64(<see cref="IKeyProtector.Protect"/>(<see cref="Encoding.UTF8"/> of
/// the plaintext key)) — the legacy DPAPI / <c>0600</c>-file envelope (keyring-unavailable
/// fallback).</item>
/// <item>Anything else (null, blank, legacy plaintext): returned verbatim (accepted for
/// compatibility; the persistence layer re-wraps / migrates it on the next write).</item>
/// </list>
/// </para>
/// <para>
/// Registered AFTER <see cref="OllamaRouterOptionsNormalizer"/> so the normalizer sees the raw
/// (still-wrapped) values — normalization never inspects key material. Any unwrap failure (bad
/// Base64, a DPAPI blob sealed by another Windows user/machine, a missing keyring entry)
/// degrades to <c>ApiKey = null</c> with a <c>LogWarning</c> — the target then dispatches in
/// unauthenticated mode (the provider's 401 surfaces at request time) and NEVER crashes the
/// startup/bind pipeline. The raw key is never logged.
/// </para>
/// </summary>
public sealed class TargetApiKeyUnwrapPostProcessor : IPostConfigureOptions<OllamaRouterOptions>
{
    /// <summary>The at-rest envelope prefix of the first (current) key-wrap format.</summary>
    public const string WrappedPrefix = "v1:";

    /// <summary>
    /// At-rest marker prefix of the OS-keyring format (task #110): the full stored value is
    /// <c>keyring:&lt;targetName&gt;</c> and the payload is the target name (= keyring username).
    /// Marker parsability is guaranteed by the target-name validation (no <c>/</c>, <c>\</c>,
    /// <c>:</c>; max 64 chars; never <c>Cloud</c>).
    /// </summary>
    public const string KeyringPrefix = "keyring:";

    private readonly IKeyProtector? _keyProtector;
    private readonly ICredentialStore? _credentialStore;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates the post-processor. <paramref name="keyProtector"/> may be null (bare test
    /// hosts without the DI registration): wrapped keys then degrade to null with a warning,
    /// legacy plaintext values pass through untouched. <paramref name="credentialStore"/>
    /// (task #110) may be null as well: <c>keyring:</c> markers then degrade to null with a
    /// warning (no keyring in this host — fallback hosts only ever persist <c>v1:</c> values,
    /// so a marker here can only mean a config copied from a keyring-equipped machine).
    /// </summary>
    public TargetApiKeyUnwrapPostProcessor(
        IKeyProtector? keyProtector = null,
        ICredentialStore? credentialStore = null,
        ILogger? logger = null)
    {
        _keyProtector = keyProtector;
        _credentialStore = credentialStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public void PostConfigure(string? name, OllamaRouterOptions options)
    {
        foreach (var target in options.Targets)
        {
            target.ApiKey = UnwrapKey(target.Name, target.ApiKey);
        }
    }

    /// <summary>
    /// Unwraps one stored key value in the documented priority order:
    /// <c>keyring:&lt;targetName&gt;</c> → <see cref="ICredentialStore"/> (fail-soft to null);
    /// <c>v1:</c>-prefixed → Base64-decoded + <see cref="IKeyProtector.Unprotect"/> (fail-soft
    /// to null); anything else (null, blank, legacy plaintext) → verbatim. Never throws.
    /// </summary>
    private string? UnwrapKey(string? targetName, string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return storedValue;
        }

        if (storedValue.StartsWith(KeyringPrefix, StringComparison.Ordinal))
        {
            return UnwrapKeyringMarker(targetName, storedValue[KeyringPrefix.Length..]);
        }

        if (!storedValue.StartsWith(WrappedPrefix, StringComparison.Ordinal))
        {
            // Legacy plaintext (or an unencrypted in-memory value): pass through. The
            // persistence layer re-wraps on the next write.
            return storedValue;
        }

        if (_keyProtector is null)
        {
            _logger?.LogWarning(
                "Target '{TargetName}' carries a wrapped ('{Prefix}') API key but no IKeyProtector is available; " +
                "the key degrades to unauthenticated mode.",
                targetName, WrappedPrefix);
            return null;
        }

        try
        {
            var payload = storedValue[WrappedPrefix.Length..];
            var protectedBytes = Convert.FromBase64String(payload);
            var plainBytes = _keyProtector.Unprotect(protectedBytes);
            var key = Encoding.UTF8.GetString(plainBytes);
            return key.Length == 0 ? null : key;
        }
        catch (Exception)
        {
            // Bad Base64, a DPAPI blob sealed by another account/machine, or a protector
            // failure: degrade to null (unauthenticated mode; the provider's 401 surfaces at
            // dispatch). The raw value is NEVER logged.
            _logger?.LogWarning(
                "Failed to unwrap the stored API key of target '{TargetName}' (corrupt envelope or foreign key-protector blob); " +
                "the target degrades to unauthenticated mode. Re-enter the key via the Setup API to restore it.",
                targetName);
            return null;
        }
    }

    /// <summary>
    /// Resolves a <c>keyring:&lt;username&gt;</c> marker (task #110): the payload is the target
    /// name / keyring username, looked up with the fixed addressing triple
    /// (<see cref="ICredentialStore.PackageId"/>, <see cref="ICredentialStore.OpenAiService"/>,
    /// username). Every degradation path (no store registered, store unavailable, blank
    /// username, missing entry, backend error — the store itself is fail-soft) returns
    /// <c>null</c> with a warning; this method never throws (a keyring hiccup must not crash
    /// startup).
    /// </summary>
    private string? UnwrapKeyringMarker(string? targetName, string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            _logger?.LogWarning(
                "Target '{TargetName}' carries a malformed keyring API key marker (blank username after '{Prefix}'); " +
                "the key degrades to unauthenticated mode.",
                targetName, KeyringPrefix);
            return null;
        }

        if (_credentialStore is null)
        {
            _logger?.LogWarning(
                "Target '{TargetName}' carries a keyring API key marker ('{Marker}') but no ICredentialStore is available; " +
                "the key degrades to unauthenticated mode.",
                targetName, KeyringPrefix);
            return null;
        }

        if (!_credentialStore.IsAvailable)
        {
            _logger?.LogWarning(
                "Target '{TargetName}' carries a keyring API key marker ('{Marker}') but the OS credential store is " +
                "unavailable (headless/CI environment); the key degrades to unauthenticated mode.",
                targetName, KeyringPrefix);
            return null;
        }

        string? key = null;
        try
        {
            key = _credentialStore.GetPassword(ICredentialStore.PackageId, ICredentialStore.OpenAiService, username);
        }
        catch (Exception)
        {
            // Defensive: the production store is fail-soft by contract, but a custom/defective
            // implementation must never crash the startup/bind pipeline. (Never logs the
            // password — it is not in scope here.)
            _logger?.LogWarning(
                "Keyring read for target '{TargetName}' (username '{Username}', service '{Service}') threw unexpectedly; " +
                "the target degrades to unauthenticated mode.",
                targetName, username, ICredentialStore.OpenAiService);
            return null;
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger?.LogWarning(
                "Keyring credential for target '{TargetName}' (username '{Username}', service '{Service}') is missing or empty; " +
                "the target degrades to unauthenticated mode. Re-enter the key via the Setup API to restore it.",
                targetName, username, ICredentialStore.OpenAiService);
            return null;
        }

        return key;
    }
}
