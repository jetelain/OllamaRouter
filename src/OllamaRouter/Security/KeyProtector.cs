using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace OllamaRouter.Security;

/// <summary>
/// Platform abstraction over at-rest key protection (task #50). The store wraps the default
/// key envelope bytes through this interface before writing <c>config/default-key.dat</c>, so
/// the platform-specific mechanism (DPAPI on Windows, plain file + <c>0600</c> permissions on
/// Unix) is unit-testable: tests inject a fake protector and real DPAPI is never touched.
/// </summary>
public interface IKeyProtector
{
    /// <summary>Wraps <paramref name="plaintext"/> for at-rest storage (may return the input as-is).</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>Unwraps <paramref name="protectedData"/> produced by <see cref="Protect"/>.</summary>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// Identity (pass-through) protector: the protection of the key file comes entirely from the
/// restrictive Unix <c>0600</c> permissions applied by <see cref="SecretFilePermissions"/>.
/// Used on non-Windows platforms, and in unit tests.
/// </summary>
public sealed class PlainKeyProtector : IKeyProtector
{
    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext) => plaintext;

    /// <inheritdoc />
    public byte[] Unprotect(byte[] protectedData) => protectedData;
}

/// <summary>
/// Windows protector: wraps the key envelope with <see cref="ProtectedData"/> (DPAPI,
/// <see cref="DataProtectionScope.CurrentUser"/>) so the blob can only be unwrapped by the
/// SAME Windows user. P/Invoke-based — no new NuGet package, NativeAOT-safe. Throwing
/// <c>PlatformNotSupportedException</c> on non-Windows is impossible in the production DI
/// (the protector is selected by <c>OperatingSystem.IsWindows()</c> + the CA1416 guard in
/// <c>AddOllamaRouterCore</c>); the DI factory is the single selection site.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);

    /// <inheritdoc />
    public byte[] Unprotect(byte[] protectedData) =>
        ProtectedData.Unprotect(protectedData, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
}
