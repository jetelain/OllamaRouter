using System.Net;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Location classification of a fleet target, used to drive the monitor UI icon/color
/// (🖥️ local / 📡 remote / ☁️ cloud) and the lowercase wire value in the monitor and
/// router fleet APIs.
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

/// <summary>
/// Pure, synchronous, DNS-free classifier that derives a fleet target's
/// <see cref="TargetLocation"/> from its configured <c>Url</c> (or an explicit
/// <see cref="OllamaTargetOptions.Location"/> override). Unit-testable without DI.
/// </summary>
public static class TargetLocationClassifier
{
    /// <summary>
    /// Classifies <paramref name="url"/> into a <see cref="TargetLocation"/>:
    /// loopback hosts → <see cref="TargetLocation.Local"/>; RFC 1918 IPv4 literals,
    /// IPv6 ULA (<c>fc00::/7</c>), link-local IPv6 (<c>fe80::/10</c>), <c>.local</c>
    /// hostnames, Tailscale <c>.ts.net</c> addresses and single-label hostnames →
    /// <see cref="TargetLocation.Remote"/>; everything else (public literals, multi-label
    /// public hostnames, and null/whitespace/unparseable URLs) → the fail-safe
    /// <see cref="TargetLocation.Cloud"/>.
    /// </summary>
    public static TargetLocation ClassifyUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return TargetLocation.Cloud;
        }

        var host = NormalizeHost(uri.Host);

        if (IsLoopbackUrl(url))
        {
            return TargetLocation.Local;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return ClassifyIpAddress(address);
        }

        // Hostname rules (the host is not an IP literal).
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ||
            host.IndexOf('.') < 0)
        {
            return TargetLocation.Remote;
        }

        return TargetLocation.Cloud;
    }

    /// <summary>
    /// Override-aware classification: an explicit <see cref="OllamaTargetOptions.Location"/>
    /// wins over the URL-based classification; a null <paramref name="options"/> classifies
    /// as <see cref="TargetLocation.Cloud"/>.
    /// </summary>
    public static TargetLocation Classify(OllamaTargetOptions? options)
    {
        if (options is null)
        {
            return TargetLocation.Cloud;
        }

        return options.Location ?? ClassifyUrl(options.Url);
    }

    /// <summary>
    /// Returns true when <paramref name="url"/> points at a loopback / same-host address:
    /// <c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>, <c>0.0.0.0</c>, or <c>::</c>
    /// (unspecified hosts are only reachable from the same host).
    /// </summary>
    public static bool IsLoopbackUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = NormalizeHost(uri.Host);
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               host == "127.0.0.1" ||
               host == "0.0.0.0" ||
               host == "::1" ||
               host == "::";
    }

    /// <summary>
    /// Normalizes the <see cref="Uri.Host"/> for comparison: strips the surrounding brackets
    /// that .NET keeps for IPv6 literals (e.g. "[::1]" -> "::1") so IP-literal parsing and
    /// the loopback set match on every platform.
    /// </summary>
    private static string NormalizeHost(string host)
    {
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            return host[1..^1];
        }

        return host;
    }

    /// <summary>
    /// Returns the lowercase wire value of <paramref name="location"/>:
    /// "local" / "remote" / "cloud".
    /// </summary>
    public static string ToWireValue(TargetLocation location) => location switch
    {
        TargetLocation.Local => "local",
        TargetLocation.Remote => "remote",
        _ => "cloud"
    };

    // IP-literal classification: RFC 1918 IPv4 and IPv6 ULA / link-local are LAN;
    // everything else (public, CGNAT 100.64/10, 169.254/16, 0.x/8) is Cloud
    // (loopback and 0.0.0.0/:: are already handled as Local by IsLoopbackUrl).
    private static TargetLocation ClassifyIpAddress(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();
            if (octets[0] == 10 ||
                (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) ||
                (octets[0] == 192 && octets[1] == 168))
            {
                return TargetLocation.Remote;
            }

            return TargetLocation.Cloud;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();

            // fc00::/7 -> first byte 0xfc..0xfd; fe80::/10 -> first byte 0xfe,
            // second byte 0x80..0xbf (first 10 bits = 1111 1110 10).
            if ((bytes[0] & 0xFE) == 0xFC ||
                (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80))
            {
                return TargetLocation.Remote;
            }

            return TargetLocation.Cloud;
        }

        return TargetLocation.Cloud;
    }
}
