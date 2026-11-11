using System.Net;

namespace OllamaRouter.Services.Monitoring;

/// <summary>
/// Best-effort attribution of loopback TCP callers to their owning local process (task #106):
/// local LLM clients (IDE extensions, CLI tools, scripts) that reach the router without
/// <c>X-OllamaRouter-Session-Id</c>/<c>X-OllamaRouter-Agent-Id</c> headers become visible in the
/// activity monitor and per-session usage aggregates under a <c>process:&lt;Name&gt;</c> identity.
/// </summary>
public interface ILocalClientIdentityResolver
{
    /// <summary>
    /// Best-effort resolution of the local process that owns the loopback TCP connection whose
    /// source endpoint is <c>(remoteIp, remotePort)</c> and which targets the router port
    /// <c>localPort</c>.
    /// Returns the display identity <c>"process:&lt;ProcessName&gt;"</c> (e.g.
    /// <c>"process:Code"</c>) or <c>null</c>.
    /// NEVER throws: null <c>remoteIp</c>, non-loopback addresses, ports &lt;= 0, missing table
    /// rows, exited processes and insufficient permissions all return <c>null</c> (logging
    /// &lt;= Trace).
    /// </summary>
    string? TryResolveClientIdentity(IPAddress? remoteIp, int remotePort, int localPort);
}
