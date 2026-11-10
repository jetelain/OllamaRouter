using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Security;

namespace OllamaRouter.Options;

/// <summary>
/// Bind-time unwrap of the fleet targets' provider API keys (task #61, maintainer decision 3).
/// <para>
/// Wire format of <c>OllamaRouter:Targets[i]:ApiKey</c> in <c>config/config.json</c>:
/// <c>v1:</c> + Base64(<see cref="IKeyProtector.Protect"/>(<see cref="Encoding.UTF8"/> of the
/// plaintext key)). A value WITHOUT the <c>v1:</c> prefix is treated as LEGACY PLAINTEXT
/// (accepted for compatibility; the persistence layer re-wraps it on the next write).
/// </para>
/// <para>
/// Registered AFTER <see cref="OllamaRouterOptionsNormalizer"/> so the normalizer sees the raw
/// (still-wrapped) values — normalization never inspects key material. Every target whose
/// <c>ApiKey</c> carries the <c>v1:</c> prefix is unwrapped here to the plaintext in-memory
/// value. Any unwrap failure (bad Base64, a DPAPI blob sealed by another Windows user/machine)
/// degrades to <c>ApiKey = null</c> with a <c>LogWarning</c> — the target then dispatches in
/// unauthenticated mode (the provider's 401 surfaces at request time) and NEVER crashes the
/// startup/bind pipeline. The raw key is never logged.
/// </para>
/// </summary>
public sealed class TargetApiKeyUnwrapPostProcessor : IPostConfigureOptions<OllamaRouterOptions>
{
    /// <summary>The at-rest envelope prefix of the first (current) key-wrap format.</summary>
    public const string WrappedPrefix = "v1:";

    private readonly IKeyProtector? _keyProtector;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates the post-processor. <paramref name="keyProtector"/> may be null (bare test
    /// hosts without the DI registration): wrapped keys then degrade to null with a warning,
    /// legacy plaintext values pass through untouched.
    /// </summary>
    public TargetApiKeyUnwrapPostProcessor(
        IKeyProtector? keyProtector = null,
        ILogger? logger = null)
    {
        _keyProtector = keyProtector;
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
    /// Unwraps one stored key value: <c>v1:</c>-prefixed values are Base64-decoded and run
    /// through <see cref="IKeyProtector.Unprotect"/>; anything else (null, blank, legacy
    /// plaintext) is returned verbatim. Failures degrade to null + warning (never throw).
    /// </summary>
    private string? UnwrapKey(string? targetName, string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return storedValue;
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
}
