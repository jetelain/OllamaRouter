namespace OllamaRouter.Security;

/// <summary>
/// Abstraction over the native OS credential store (Windows Credential Manager, macOS Keychain,
/// Linux Secret Service) for the OpenAI-compatible upstream targets' provider API keys (task
/// #110). The production implementation (<see cref="KeySharpCredentialStore"/>) wraps
/// <c>Pmad.KeySharp</c>; unit tests inject an in-memory fake so the host OS credential manager
/// is never touched by the test suite.
/// <para>
/// Contract asymmetry (intentional):
/// <list type="bullet">
/// <item><see cref="IsAvailable"/> and <see cref="GetPassword"/> are FAIL-SOFT: a missing
/// credential, an unavailable store or any backend error degrades to <c>null</c> (a
/// <c>LogWarning</c> accompanies non-trivial failures) — the bind-time unwrap path must never
/// crash startup, and an absent entry simply means "the target dispatches unauthenticated".</item>
/// <item><see cref="DeletePassword"/> is idempotent and fail-soft: a missing entry is a no-op;
/// other errors are logged and swallowed (an inert orphan entry is harmless — it is not
/// referenced by the configuration anymore).</item>
/// <item><see cref="SetPassword"/> is FAIL-HARD: any backend error is rethrown so the
/// persistence layer can surface a 500 and roll back. A silent no-op is forbidden — it would
/// persist a <c>keyring:</c> marker in <c>config/config.json</c> for a secret that was never
/// stored (silent key loss).</item>
/// </list>
/// </para>
/// <para>
/// SECURITY RULE: implementations must NEVER log the raw password — target name / service /
/// error message only.
/// </para>
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Keyring package component of every OllamaRouter credential (addressing triple:
    /// <c>(PackageId, OpenAiService, &lt;targetName&gt;)</c>).
    /// </summary>
    public const string PackageId = "Pmad.OllamaRouter";

    /// <summary>Keyring service component of every OllamaRouter target credential.</summary>
    public const string OpenAiService = "openai";

    /// <summary>
    /// Whether the OS credential store is currently reachable and usable (LIVE evaluation on
    /// every access — no caching; call sites are rare and the availability can change at
    /// runtime, e.g. when a keyring daemon appears on a headless machine). ANY backend error
    /// (native library load failure, D-Bus error) reports <c>false</c>; this property never
    /// throws.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Reads the credential stored under (package, service, username). Fail-soft: a missing
    /// entry (<c>NotFound</c>) returns <c>null</c> silently; any other backend error logs a
    /// warning (without the password) and returns <c>null</c>. Never throws.
    /// </summary>
    string? GetPassword(string package, string service, string username);

    /// <summary>
    /// Creates or updates the credential stored under (package, service, username).
    /// FAIL-HARD: any backend error (including an unavailable store) is rethrown — callers are
    /// responsible for surfacing the failure and rolling back. Never logs the password.
    /// </summary>
    void SetPassword(string package, string service, string username, string password);

    /// <summary>
    /// Deletes the credential stored under (package, service, username). Idempotent and
    /// fail-soft: a missing entry is a no-op; any other backend error logs a warning (without
    /// the password) and is swallowed. Never throws.
    /// </summary>
    void DeletePassword(string package, string service, string username);
}
