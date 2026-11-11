namespace OllamaRouter.App.Connection;

/// <summary>
/// The connection mode of the desktop application (task #102): a LOCAL connection to the
/// user-machine daemon on the conventional loopback port (auto-start + seamless
/// <c>DefaultKeyReader</c> authentication) or a REMOTE LAN router (explicit URL + API key).
/// </summary>
public enum RouterConnectionMode
{
    /// <summary>Local daemon on <c>http://127.0.0.1:11434/</c> (auto-start, key reader auth).</summary>
    Local,

    /// <summary>Remote LAN router with an explicit URL and API key (session-only, in memory).</summary>
    Remote,
}

/// <summary>
/// One connection profile (task #102): the mode, the base URL and the (explicit) API key.
/// <para>
/// Invariant: LOCAL profiles carry <c>BaseUrl = OllamaRouterDaemonLauncher.DefaultBaseUrl</c>
/// and <c>ApiKey = null</c> (the client falls back to the <see cref="OllamaRouter.Security.IDefaultKeyReader"/>
/// re-read per request — transparent default-key rotation). REMOTE profiles carry the user
/// URL + explicit key; the key lives in PROCESS MEMORY FOR THE SESSION ONLY (never persisted
/// to disk).
/// </para>
/// </summary>
public sealed record RouterConnectionProfile(RouterConnectionMode Mode, Uri BaseUrl, string? ApiKey);
