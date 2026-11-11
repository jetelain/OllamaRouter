namespace OllamaRouter.Services.Routing;

/// <summary>
/// Location classification of a fleet target, used to drive the monitor UI icon/color
/// (🖥️️ local / 📡 remote / ☁️ cloud) and the lowercase wire value in the monitor and
/// router fleet APIs.
/// <para>
/// Task #100: physically relocated from the server assembly (<c>OllamaRouter</c>) into the
/// shared client SDK so the detached CLI TUI (<c>OllamaRouter.Cli</c>) can consume it without
/// referencing the server. The namespace <c>OllamaRouter.Services.Routing</c> is kept
/// verbatim, so the server's <c>TargetLocationClassifier</c> and every consuming file
/// compile with zero using-changes (the enum now resolves from this referenced assembly).
/// </para>
/// </summary>
public enum TargetLocation
{
    /// <summary>Loopback address (localhost, 127.0.0.1, ::1, 0.0.0.0, ::).</summary>
    Local = 0,

    /// <summary>LAN / private network (RFC 1918 IPv4, IPv6 ULA fc00::/7, link-local fe80::/10,
    /// mDNS <c>.local</c> hostnames, Tailscale <c>.ts.net</c> addresses, single-label hostnames).</summary>
    Remote = 1,

    /// <summary>Everything else (public literals/hostnames and the fail-safe default for
    /// null, malformed, or unresolvable URLs).</summary>
    Cloud = 2
}
