using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OllamaRouter.Services.Monitoring;

/// <summary>
/// Best-effort, NativeAOT-safe local TCP client process resolver (task #106). Maps a loopback
/// caller's source TCP endpoint (<c>(remoteIp, remotePort)</c> targeting the router port
/// <c>localPort</c>) to the owning local process name so untagged local applications are
/// attributed in the activity monitor and per-session usage aggregates.
/// <para>
/// Platform strategy — zero process spawning, zero reflection:
/// <list type="bullet">
/// <item>Windows: <c>GetExtendedTcpTable</c> (<c>TCP_TABLE_OWNER_PID_CONNECTIONS</c>) from
/// <c>iphlpapi.dll</c>; the CLIENT-side <c>ESTABLISHED</c> row is matched (row local port =
/// client source port, row remote port = router port — the server-side row's owning PID would
/// be the router itself).</item>
/// <item>Linux: <c>/proc/net/tcp</c> / <c>/proc/net/tcp6</c> → socket inode → time-budgeted
/// <c>/proc/&lt;pid&gt;/fd</c> inode→PID scan → process name.</item>
/// </list>
/// </para>
/// <para>
/// Fail-safe contract: <see cref="TryResolveClientIdentity"/> never throws and never blocks
/// routing — every lookup failure (exited process, permission denial, missing/unreadable
/// tables, non-supported OS) returns <c>null</c> with at most Trace-level logging. Results
/// (positive AND null) are cached for a 30 s TTL in a bounded (≤ 1024 entries) thread-safe
/// cache: steady-state HTTP keep-alive traffic costs one dictionary lookup per request and a
/// failed lookup never re-scans the OS within the TTL. Ephemeral-port reuse across the 30 s
/// window is an accepted best-effort ambiguity (attribution only — the identity never feeds
/// sticky KV-cache routing or the session response echo).
/// </para>
/// </summary>
public sealed class LocalClientIdentityResolver : ILocalClientIdentityResolver
{
    /// <summary>Prefix of the display identity returned by this resolver (<c>process:&lt;Name&gt;</c>).</summary>
    public const string IdentityPrefix = "process:";

    /// <summary>Cache TTL for resolved (and failed) lookups: 30 s per (ip, source-port) key.</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>Bounded cache capacity (one entry per loopback source port).</summary>
    internal const int MaxCachedEntries = 1024;

    /// <summary>Linux inode→PID fd-scan time budget; exceeding it degrades to <c>null</c> (bounded worst case).</summary>
    internal static readonly TimeSpan LinuxScanBudget = TimeSpan.FromMilliseconds(200);

    // --- Windows GetExtendedTcpTable (iphlpapi.dll) constants ---
    private const uint AF_INET = 2;
    private const uint AF_INET6 = 23;
    private const uint TCP_TABLE_OWNER_PID_CONNECTIONS = 3;
    private const uint NO_ERROR = 0;

    // The two-call sizing pattern: the first (empty-buffer) call returns this code and the
    // exact buffer size required in the out-parameter (MSDN: GetExtendedTcpTable).
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    // MIB_TCP_STATE (tcpmib.h): CLOSED=1, LISTEN=2, SYN_SENT=3, SYN_RCVD=4, ESTAB=5,
    // FIN_WAIT1=6, FIN_WAIT2=7, CLOSE_WAIT=8, CLOSING=9, LAST_ACK=10, TIME_WAIT=11.
    private const int MIB_TCP_STATE_ESTAB = 5;

    // Row sizes from the tcpmib.h layouts (all-DWORD v4 row; v6 row carries the two
    // 4-byte scope-ID members): sizeof(MIB_TCPROW_OWNER_PID)=24, sizeof(MIB_TCP6ROW_OWNER_PID)=56.
    private const int IPv4RowSize = 24;
    private const int IPv6RowSize = 56;

    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<IPAddress, int, int, string?> _processLookup;
    private readonly ILogger<LocalClientIdentityResolver> _logger;

    // Keyed by the loopback caller's source endpoint. Values: (display identity or null,
    // absolute expiry in ticks). Null identities are cached too (negative caching).
    private readonly ConcurrentDictionary<(IPAddress RemoteIp, int RemotePort), (string? Identity, long ExpiresAtTicks)> _cache
        = new();

    /// <summary>
    /// Parameterless constructor (DI-resolvable): wires the platform implementation
    /// (<c>GetExtendedTcpTable</c> on Windows, <c>/proc</c> on Linux) with real-UTC clock.
    /// </summary>
    public LocalClientIdentityResolver()
        : this(clock: null, processLookup: LookupProcessName, logger: NullLogger<LocalClientIdentityResolver>.Instance)
    {
    }

    /// <summary>
    /// Test/host seam (internal): injectable clock (codebase convention, cf.
    /// <c>SessionAffinityService</c>) and process-lookup delegate
    /// (<c>(remoteIp, remotePort, localPort) → raw process name or null</c>); when
    /// <paramref name="processLookup"/> is null the platform implementation is used.
    /// </summary>
    internal LocalClientIdentityResolver(
        Func<DateTimeOffset>? clock,
        Func<IPAddress, int, int, string?>? processLookup,
        ILogger<LocalClientIdentityResolver>? logger)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _processLookup = processLookup ?? LookupProcessName;
        _logger = logger ?? NullLogger<LocalClientIdentityResolver>.Instance;
    }

    /// <inheritdoc cref="ILocalClientIdentityResolver.TryResolveClientIdentity"/>
    public string? TryResolveClientIdentity(IPAddress? remoteIp, int remotePort, int localPort)
    {
        try
        {
            // Short-circuit BEFORE any cache or OS interaction (allocation-free of syscalls):
            // invalid endpoint / non-loopback callers are never attributed (LAN traffic has
            // no meaningful local process to point at).
            if (remoteIp is null || remotePort <= 0 || localPort <= 0)
            {
                return null;
            }

            if (!IPAddress.IsLoopback(remoteIp))
            {
                return null;
            }

            var key = (RemoteIp: remoteIp, RemotePort: remotePort);
            var now = _clock();

            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached.ExpiresAtTicks >= now.Ticks)
                {
                    return cached.Identity;
                }

                _cache.TryRemove(key, out _);
            }

            string? identity;
            try
            {
                var name = _processLookup(remoteIp, remotePort, localPort);
                identity = string.IsNullOrEmpty(name) ? null : IdentityPrefix + name;
            }
            catch (Exception ex)
            {
                // Fail-safe: an OS seam failure (table copy error, /proc unreadable, ...) is
                // logged at Trace and degraded to null — routing must never be impacted.
                _logger.LogTrace(ex, "Local client identity lookup failed for {Ip}:{Port} → router port {LocalPort}.",
                    remoteIp, remotePort, localPort);
                identity = null;
            }

            StoreInCache(key, identity, now);
            return identity;
        }
        catch (Exception ex)
        {
            // Outermost fail-safe (contract: this method never throws).
            _logger.LogTrace(ex, "Local client identity resolution aborted for {Port}.", remotePort);
            return null;
        }
    }

    // Bounded cache maintenance: on capacity overrun, first sweep expired entries (lazy),
    // then evict the entry with the earliest expiry.
    private void StoreInCache((IPAddress RemoteIp, int RemotePort) key, string? identity, DateTimeOffset now)
    {
        _cache[key] = (Identity: identity, ExpiresAtTicks: now.Ticks + CacheTtl.Ticks);
        if (_cache.Count <= MaxCachedEntries)
        {
            return;
        }

        foreach (var kv in _cache)
        {
            if (kv.Value.ExpiresAtTicks <= now.Ticks)
            {
                _cache.TryRemove(kv.Key, out _);
            }
        }

        if (_cache.Count > MaxCachedEntries)
        {
            IPAddress ip = default!;
            int port = 0;
            long bestTicks = long.MaxValue;
            foreach (var kv in _cache)
            {
                if (kv.Value.ExpiresAtTicks < bestTicks)
                {
                    bestTicks = kv.Value.ExpiresAtTicks;
                    ip = kv.Key.RemoteIp;
                    port = kv.Key.RemotePort;
                }
            }

            _cache.TryRemove((ip, port), out _);
        }
    }

    // ------------------------------------------------------------------
    // Platform implementation: source endpoint → owning process NAME (raw, no prefix).
    // Returns null on ANY failure; the caller wraps formatting + caching.
    // ------------------------------------------------------------------

    private static string? LookupProcessName(IPAddress remoteIp, int remotePort, int localPort)
    {
        try
        {
            // A caller endpoint reported as IPv4-mapped IPv6 (::ffff:a.b.c.d) is an IPv4
            // connection: normalize before family selection (the kernel may publish the row
            // in EITHER table — AF_INET sockets live in the v4 table, IPv4-mapped AF_INET6
            // sockets live in the v6 table in ::ffff:a.b.c.d form — so both are searched).
            bool wasV4Mapped = false;
            if (remoteIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                && IsIPv4MappedPrefix(remoteIp.GetAddressBytes().AsSpan()))
            {
                remoteIp = remoteIp.MapToIPv4();
                wasV4Mapped = true;
            }

            if (OperatingSystem.IsWindows())
            {
                var ipVersion = remoteIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? AF_INET6
                    : AF_INET;
                var pid = WindowsGetOwningPid(remoteIp, remotePort, localPort, ipVersion);
                // An IPv4 client on the v6 stack (IPv4-mapped) may be published in the
                // AF_INET6 table in ::ffff:a.b.c.d form — fall back to it when the v4
                // table has no matching row.
                if (pid is null && ipVersion == AF_INET && wasV4Mapped)
                {
                    pid = WindowsGetOwningPid(remoteIp, remotePort, localPort, AF_INET6);
                }

                return pid is null ? null : GetProcessName(pid.Value);
            }

            if (OperatingSystem.IsLinux())
            {
                var linuxPid = LinuxGetOwningPid(remoteIp, remotePort, localPort);
                return linuxPid is null ? null : GetProcessName(linuxPid.Value);
            }

            // Unsupported OS (macOS, BSD, ...): best-effort degrades to anonymous.
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? GetProcessName(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            // Process exited between the table scan and the lookup, access denied, etc.
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Windows: GetExtendedTcpTable (P/Invoke, manual byte parsing — AOT/trim-safe).
    // ------------------------------------------------------------------

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        byte[]? tcpTable, ref uint tableSize, bool ofOrder, uint ipVersion, uint tableClass, uint reserved);

    private static int? WindowsGetOwningPid(IPAddress remoteIp, int remotePort, int localPort, uint ipVersion)
    {
        // Classic two-call pattern: the first call with an EMPTY buffer must come back with
        // ERROR_INSUFFICIENT_BUFFER (122), which reports the exact buffer size required in
        // tableSize; only the second, sized call may return NO_ERROR. (NO_ERROR on the first
        // call would mean the whole table fit in zero bytes — impossible — so any other
        // return degrades to null.)
        var tableSize = (uint)0;
        var result = GetExtendedTcpTable(Array.Empty<byte>(), ref tableSize, true, ipVersion, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
        if (result != ERROR_INSUFFICIENT_BUFFER || tableSize < 4)
        {
            return null;
        }

        var buffer = new byte[tableSize];
        result = GetExtendedTcpTable(buffer, ref tableSize, true, ipVersion, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
        if (result != NO_ERROR)
        {
            return null;
        }

        return MatchOwningPidInTable(buffer, remoteIp, remotePort, localPort, ipVersion);
    }

    /// <summary>
    /// Pure row matcher over a raw <c>GetExtendedTcpTable</c> buffer (4-byte
    /// <c>dwNumEntries</c> header followed by the rows). Internal so the tcpmib.h row layout
    /// it relies on is pinned by deterministic unit tests on every platform (CI runs on Linux,
    /// where the P/Invoke fetch itself never executes). Returns the <c>dwOwningPid</c> of the
    /// CLIENT-side ESTABLISHED row (row local port = client source port, row remote port =
    /// router port) or null — the server-side row's owning PID would be the router itself.
    /// Never throws: truncated/malformed tables degrade to null.
    /// </summary>
    internal static int? MatchOwningPidInTable(byte[] table, IPAddress remoteIp, int remotePort, int localPort, uint ipVersion)
    {
        try
        {
            var rowSize = ipVersion == AF_INET6 ? IPv6RowSize : IPv4RowSize;
            var numEntries = BitConverter.ToInt32(table, 0);
            var offset = 4;
            var targetAddress = remoteIp.GetAddressBytes();
            for (var i = 0; i < numEntries; i++, offset += rowSize)
            {
                if (offset + rowSize > table.Length)
                {
                    break;
                }

                // Row layouts (tcpmib.h, Windows SDK):
                //   MIB_TCPROW_OWNER_PID  (IPv4, 24 B): dwState@0, dwLocalAddr@4, dwLocalPort@8,
                //                                       dwRemoteAddr@12, dwRemotePort@16, dwOwningPid@20
                //   MIB_TCP6ROW_OWNER_PID (IPv6, 56 B): ucLocalAddr[16]@0, dwLocalScopeId@16, dwLocalPort@20,
                //                                       ucRemoteAddr[16]@24, dwRemoteScopeId@40,
                //                                       dwRemotePort@44, dwState@48, dwOwningPid@52
                // dwLocalPort/dwRemotePort are DWORDs in NETWORK byte order: the 16-bit port
                // is the first two bytes of the field read big-endian (cf. ntohs(dwLocalPort)).
                int state, localPortField, remotePortField, owningPid;
                if (ipVersion == AF_INET6)
                {
                    localPortField = ReadNetworkOrderPort(table, offset + 20);
                    remotePortField = ReadNetworkOrderPort(table, offset + 44);
                    state = BitConverter.ToInt32(table, offset + 48);
                    owningPid = (int)ReadUInt32(table, offset + 52);
                }
                else
                {
                    localPortField = ReadNetworkOrderPort(table, offset + 8);
                    remotePortField = ReadNetworkOrderPort(table, offset + 16);
                    state = BitConverter.ToInt32(table, offset);
                    owningPid = (int)ReadUInt32(table, offset + 20);
                }

                // Only live ESTABLISHED connections (MIB_TCP_STATE_ESTAB = 5 — note 1 is
                // MIB_TCP_STATE_CLOSED): closed/TIME_WAIT rows must never match.
                if (state != MIB_TCP_STATE_ESTAB)
                {
                    continue;
                }

                if (localPortField != remotePort || remotePortField != localPort)
                {
                    continue;
                }

                var addressBytes = ipVersion == AF_INET6
                    ? table.AsSpan(offset, 16).ToArray()
                    : table.AsSpan(offset + 4, 4).ToArray();
                if (!AddressMatchesTarget(addressBytes, targetAddress))
                {
                    continue;
                }

                return owningPid;
            }
        }
        catch (Exception)
        {
            // Malformed/truncated table: fail-soft (the caller degrades to null).
            return null;
        }

        return null;
    }

    // 16-byte equality with the IPv4-mapped IPv6 form (::ffff:a.b.c.d) accepted for an IPv4
    // target address (Windows may surface v4 endpoints in the AF_INET6 table).
    private static bool AddressMatchesTarget(byte[] rowAddress, byte[] targetAddress)
    {
        if (rowAddress.Length == 4 && targetAddress.Length == 4)
        {
            return rowAddress.SequenceEqual(targetAddress);
        }

        if (rowAddress.Length == 16)
        {
            if (targetAddress.Length == 16)
            {
                return rowAddress.SequenceEqual(targetAddress);
            }

            if (targetAddress.Length == 4)
            {
                // ::ffff:a.b.c.d — ten zero bytes, then the 0xFF 0xFF prefix, then the IPv4 octets.
                var mapped = new byte[16];
                mapped[10] = 0xFF;
                mapped[11] = 0xFF;
                mapped[12] = targetAddress[0];
                mapped[13] = targetAddress[1];
                mapped[14] = targetAddress[2];
                mapped[15] = targetAddress[3];
                return rowAddress.SequenceEqual(mapped);
            }
        }

        return false;
    }

    // The 16-bit port stored in a DWORD field in network byte order (first two bytes of the
    // field, big-endian — cf. ntohs(dwLocalPort) in the MSDN remarks).
    private static int ReadNetworkOrderPort(byte[] buffer, int offset) => (buffer[offset] << 8) | buffer[offset + 1];

    private static uint ReadUInt32(byte[] buffer, int offset)
        => (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

    // ------------------------------------------------------------------
    // Linux: /proc/net/tcp{,6} → socket inode → budgeted /proc/<pid>/fd scan → PID.
    // ------------------------------------------------------------------

    private static int? LinuxGetOwningPid(IPAddress remoteIp, int remotePort, int localPort)
    {
        if (remoteIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // Pure IPv6: only the AF_INET6 table.
            var v6Inode = LinuxFindSocketInode("/proc/net/tcp6", remoteIp, remotePort, localPort);
            return v6Inode is null ? null : LinuxFindPidWithInode(v6Inode.Value);
        }

        if (remoteIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return null;
        }

        // IPv4: plain AF_INET sockets live in /proc/net/tcp, but IPv4-mapped AF_INET6
        // sockets (e.g. .NET clients dialing a v4 loopback listener with
        // [::ffff:127.0.0.1]) live in /proc/net/tcp6 in ::ffff:a.b.c.d form — search both.
        var inode = LinuxFindSocketInode("/proc/net/tcp", remoteIp, remotePort, localPort);
        if (inode is null)
        {
            inode = LinuxFindSocketInode("/proc/net/tcp6", remoteIp, remotePort, localPort);
        }

        return inode is null ? null : LinuxFindPidWithInode(inode.Value);
    }

    // /proc/net/tcp row format:
    //   sl local_address rem_address   st tx_queue:rx_queue tr:tm->when retrnsmt uid timeout inode ...
    // local/remote = "<IPHEX>:<PORTHEX>" (little-endian hex, e.g. "0100007F:1F90" = 127.0.0.1:8080),
    // st = "01" (ESTABLISHED), inode at whitespace field index 9.
    private static long? LinuxFindSocketInode(string path, IPAddress remoteIp, int remotePort, int localPort)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception)
        {
            return null; // missing/unreadable procfs (non-Linux procfs, hardened container)
        }

        var targetAddress = remoteIp.GetAddressBytes();
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 10 || parts[3] != "01")
            {
                continue;
            }

            var clientPortField = ParseProcPort(parts[1]);
            var routerPortField = ParseProcPort(parts[2]);
            if (clientPortField != remotePort || routerPortField != localPort)
            {
                continue;
            }

            if (!AddressMatchesProcHex(GetProcAddressHex(parts[1]), targetAddress))
            {
                continue;
            }

            if (long.TryParse(parts[9], System.Globalization.NumberStyles.HexNumber, null, out var inode))
            {
                return inode;
            }
        }

        return null;
    }

    private static string? GetProcAddressHex(string localField)
    {
        var colon = localField.IndexOf(':');
        return colon >= 0 ? localField[..colon] : null;
    }

    private static int? ParseProcPort(string field)
    {
        var colon = field.IndexOf(':');
        var hex = colon >= 0 ? field[(colon + 1)..] : field;
        return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var port) ? port : null;
    }

    private static bool AddressMatchesProcHex(string? hex, byte[] targetAddress)
    {
        if (hex is null)
        {
            return false;
        }

        if (targetAddress.Length == 4 && hex.Length == 8)
        {
            // 32-bit little-endian hex word: "0100007F" → bytes {0x7F, 0x00, 0x00, 0x01} = 127.0.0.1
            uint value;
            if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out value))
            {
                return false;
            }

            return BitConverter.GetBytes(value).SequenceEqual(targetAddress);
        }

        if (hex.Length == 32)
        {
            // Four consecutive 32-bit words, each BYTE-REVERSED vs the address bytes
            // (kernel %4X of s6_addr32 on little-endian hosts): "0100007F" decodes to
            // address bytes {0x7F, 0x00, 0x00, 0x01}.
            var bytes = new byte[16];
            for (var word = 0; word < 4; word++)
            {
                if (!uint.TryParse(hex.AsSpan(word * 8, 8), System.Globalization.NumberStyles.HexNumber, null, out var value))
                {
                    return false;
                }

                var wordBytes = BitConverter.GetBytes(value);
                Array.Copy(wordBytes, 0, bytes, word * 4, 4);
            }

            if (targetAddress.Length == 16)
            {
                return bytes.SequenceEqual(targetAddress);
            }

            // IPv4-mapped IPv6 form (::ffff:a.b.c.d) accepted for an IPv4 target address.
            return targetAddress.Length == 4
                && bytes[8] == 0
                && bytes[9] == 0
                && bytes[10] == 0xFF
                && bytes[11] == 0xFF
                && bytes[12] == targetAddress[0]
                && bytes[13] == targetAddress[1]
                && bytes[14] == targetAddress[2]
                && bytes[15] == targetAddress[3];
        }

        return false;
    }

    private static int? LinuxFindPidWithInode(long inode)
    {
        var deadline = DateTime.UtcNow + LinuxScanBudget;
        var fdTarget = $"socket:[{inode}]";
        var fdsChecked = 0;

        string[] pidDirs;
        try
        {
            pidDirs = Directory.EnumerateDirectories("/proc").ToArray();
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var pidDir in pidDirs)
        {
            var name = Path.GetFileName(pidDir);
            if (!IsAllDigits(name.AsSpan()))
            {
                continue;
            }

            var fdDir = Path.Combine(pidDir, "fd");
            string[] fds;
            try
            {
                fds = Directory.EnumerateFileSystemEntries(fdDir).ToArray();
            }
            catch (Exception)
            {
                continue; // pid vanished or permission denied for this directory
            }

            foreach (var fd in fds)
            {
                // Time budget: re-check every 256 fd reads; an overrun degrades to null
                // (bounded worst case; the 30 s cache makes this a once-per-connection cost).
                if ((++fdsChecked & 0xFF) == 0 && DateTime.UtcNow > deadline)
                {
                    return null;
                }

                string? linkTarget;
                try
                {
                    linkTarget = new FileInfo(fd).LinkTarget;
                }
                catch (Exception)
                {
                    continue; // fd vanished / cross-user permission denial
                }

                if (linkTarget == fdTarget)
                {
                    return int.Parse(name);
                }
            }
        }

        return null;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        foreach (var c in span)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    // IPv4-mapped IPv6 form: ten zero bytes followed by 0xFF 0xFF (::ffff:a.b.c.d).
    private static bool IsIPv4MappedPrefix(ReadOnlySpan<byte> sixteenBytes)
    {
        if (sixteenBytes.Length != 16)
        {
            return false;
        }

        foreach (var b in sixteenBytes.Slice(0, 10))
        {
            if (b != 0)
            {
                return false;
            }
        }

        return sixteenBytes[10] == 0xFF && sixteenBytes[11] == 0xFF;
    }
}
