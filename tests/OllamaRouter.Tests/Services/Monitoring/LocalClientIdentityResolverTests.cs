using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaRouter.Extensions;
using OllamaRouter.Options;
using OllamaRouter.Services.Monitoring;

namespace OllamaRouter.Tests.Services.Monitoring;

public class LocalClientIdentityResolverTests
{
    private const int RouterPort = 11434;

    // --- 1. Input validation short-circuits (no cache, no OS query) ---

    [Fact]
    public void ReturnsNull_When_RemoteIpIsNull()
    {
        var seam = CreateSeam(name: "Code");
        Assert.Null(seam.Resolver.TryResolveClientIdentity(null, 49152, RouterPort));
        Assert.Equal(0, seam.Lookups);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReturnsNull_When_RemotePortIsZeroOrNegative(int remotePort)
    {
        var seam = CreateSeam(name: "Code");
        Assert.Null(seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, remotePort, RouterPort));
        Assert.Equal(0, seam.Lookups);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReturnsNull_When_LocalPortIsZeroOrNegative(int localPort)
    {
        var seam = CreateSeam(name: "Code");
        Assert.Null(seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 49152, localPort));
        Assert.Equal(0, seam.Lookups);
    }

    // --- 2. Non-loopback short-circuit: never touches the OS, never cached ---

    [Theory]
    [InlineData("192.168.1.50")]
    [InlineData("::ffff:10.0.0.9")]
    public void ReturnsNull_ForNonLoopbackAddress_WithoutQueryingOs(string ip)
    {
        var seam = CreateSeam(name: "Code");
        Assert.Null(seam.Resolver.TryResolveClientIdentity(IPAddress.Parse(ip), 49152, RouterPort));
        Assert.Equal(0, seam.Lookups);

        // Repeated calls for the same non-loopback key stay OS-free (no cache write either).
        Assert.Null(seam.Resolver.TryResolveClientIdentity(IPAddress.Parse(ip), 49152, RouterPort));
        Assert.Equal(0, seam.Lookups);
    }

    // --- 3. Happy path through the seam ---

    [Fact]
    public void ReturnsPrefixedProcessName_ForLoopback()
    {
        var seam = CreateSeam(name: "Code");
        Assert.Equal("process:Code", seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 49152, RouterPort));
        Assert.Equal(1, seam.Lookups);

        // An empty name from the OS seam degrades to null (never a bare prefix).
        var emptyNameSeam = CreateSeam(name: "");
        Assert.Null(emptyNameSeam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 49152, RouterPort));
    }

    // --- 4./5. Caching: positive AND negative results, TTL-bounded ---

    [Fact]
    public void CachesPositiveResult_UntilTtlExpires()
    {
        var clock = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var seam = CreateSeam(name: "Code", clock: () => clock);

        Assert.Equal("process:Code", seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 49152, RouterPort));
        Assert.Equal(1, seam.Lookups);

        clock += TimeSpan.FromSeconds(10);
        Assert.Equal("process:Code", seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 49152, RouterPort));
        Assert.Equal(1, seam.Lookups); // cache hit, no re-query within the 30 s TTL

        clock += TimeSpan.FromSeconds(21); // 31 s total > TTL
        Assert.Equal("process:Code", seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 49152, RouterPort));
        Assert.Equal(2, seam.Lookups); // TTL expired → re-query
    }

    [Fact]
    public void CachesNullResults_UntilTtlExpires()
    {
        var clock = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var seam = CreateSeam(name: null, clock: () => clock);

        Assert.Null(seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 49152, RouterPort));
        Assert.Equal(1, seam.Lookups);

        clock += TimeSpan.FromSeconds(29);
        Assert.Null(seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 49152, RouterPort));
        Assert.Equal(1, seam.Lookups); // negative caching: a keep-alive whose lookup failed never re-scans

        clock += TimeSpan.FromSeconds(2);
        Assert.Null(seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 49152, RouterPort));
        Assert.Equal(2, seam.Lookups);
    }

    // --- 6. Key isolation ---

    [Fact]
    public void DistinctKeys_DonShareEntries()
    {
        var seam = CreateSeam(name: "Code");

        Assert.Equal("process:Code", seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 49152, RouterPort));
        Assert.Equal("process:Code", seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 49153, RouterPort));
        Assert.Equal("process:Code", seam.Resolver.TryResolveClientIdentity(IPAddress.IPv6Loopback, 49152, RouterPort));
        Assert.Equal(3, seam.Lookups); // three distinct (ip, port) keys → three OS queries
    }

    // --- 7. Bounded cache: beyond the cap the earliest-expiring entry is evicted ---

    [Fact]
    public void CacheEvictsBeyondBoundedCap()
    {
        // The clock advances per insertion so every entry gets a strictly increasing expiry —
        // the first-inserted key is then deterministically the earliest-expiring (evicted) one.
        var baseClock = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var insertions = 0;
        var seam = CreateSeam(name: "Code", clock: () => baseClock + TimeSpan.FromMilliseconds(++insertions));

        var firstKeyPort = 49152;
        for (var port = firstKeyPort; port < firstKeyPort + LocalClientIdentityResolver.MaxCachedEntries + 1; port++)
        {
            Assert.Equal("process:Code", seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, port, RouterPort));
        }

        Assert.Equal(LocalClientIdentityResolver.MaxCachedEntries + 1, seam.Lookups);

        // Re-query the FIRST key: its earliest expiry made it the deterministic eviction victim,
        // so a fresh OS query happens (a cached hit would have left the counter unchanged).
        seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, firstKeyPort, RouterPort);
        Assert.Equal(LocalClientIdentityResolver.MaxCachedEntries + 2, seam.Lookups);
    }

    // --- 8./9. Live loopback sockets: the REAL platform implementation ---

    [Fact]
    public void LiveLoopbackSocket_ResolvesCurrentProcessName_Ipv4()
    {
        var expected = "process:" + System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        IPAddress? clientIp = null;
        int clientPort = 0;
        int listenerPort = 0;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, listenerPort);
        using var accepted = listener.AcceptTcpClient(); // held open → ESTABLISHED
        var clientEndpoint = (IPEndPoint)client.Client.LocalEndPoint!;
        clientIp = clientEndpoint.Address;
        clientPort = clientEndpoint.Port;

        var resolver = new LocalClientIdentityResolver();
        AssertLiveResolution(resolver, clientIp!, clientPort, listenerPort, expected);
    }

    [Fact]
    public void LiveLoopbackSocket_ResolvesCurrentProcessName_Ipv6()
    {
        var expected = "process:" + System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        IPAddress? clientIp = null;
        int clientPort = 0;
        int listenerPort = 0;

        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        client.Connect(IPAddress.IPv6Loopback, listenerPort);
        using var accepted = listener.AcceptTcpClient();
        var clientEndpoint = (IPEndPoint)client.Client.LocalEndPoint!;
        clientIp = clientEndpoint.Address;
        clientPort = clientEndpoint.Port;

        var resolver = new LocalClientIdentityResolver();
        AssertLiveResolution(resolver, clientIp!, clientPort, listenerPort, expected);
    }

    /// <summary>
    /// Strict on a real procfs (CI/Windows): the CLIENT-side ESTABLISHED row is visible in the
    /// table, so the resolver MUST return the current process name. On virtualized/sandboxed
    /// procfs (where the client socket row is never published) the resolver must instead
    /// degrade to null — the same row-matching predicate is checked up front so both branches
    /// are verified deterministically.
    /// </summary>
    private static void AssertLiveResolution(
        ILocalClientIdentityResolver resolver, IPAddress clientIp, int clientPort, int listenerPort, string expected)
    {
        var resolvable = IsClientSocketResolvable(clientIp, clientPort);
        var actual = resolver.TryResolveClientIdentity(clientIp, clientPort, listenerPort);
        if (resolvable)
        {
            Assert.Equal(expected, actual);
        }
        else
        {
            Assert.Null(actual);
        }
    }

    // True when the client connection is FULLY resolvable in the kernel TCP tables: an
    // ESTABLISHED row with local port = client source port (in /proc/net/tcp for AF_INET
    // clients, /proc/net/tcp6 for IPv6 / IPv4-mapped AF_INET6 clients — .NET dials v4
    // loopback via [::ffff:127.0.0.1]) AND that row's socket inode appears in the current
    // process's fd links (the test process owns the client socket). On Windows the P/Invoke
    // table is always fully visible, so the strict assertion is always applied there.
    private static bool IsClientSocketResolvable(IPAddress clientIp, int clientPort)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        if (!OperatingSystem.IsLinux())
        {
            return false; // unsupported OS: the resolver contractually returns null
        }

        string[] paths;
        if (clientIp.AddressFamily == AddressFamily.InterNetwork)
        {
            paths = new[] { "/proc/net/tcp", "/proc/net/tcp6" };
        }
        else if (clientIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            paths = new[] { "/proc/net/tcp6" };
        }
        else
        {
            return false;
        }

        var ownFds = new HashSet<string>();
        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{Environment.ProcessId}/fd"))
            {
                var link = new FileInfo(fd).LinkTarget;
                if (link != null && link.StartsWith("socket:"))
                {
                    ownFds.Add(link);
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        foreach (var path in paths)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var line in lines)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 10 || parts[3] != "01")
                {
                    continue;
                }

                var colon = parts[1].IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                if (!int.TryParse(parts[1][(colon + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var port)
                    || port != clientPort)
                {
                    continue;
                }

                if (long.TryParse(parts[9], System.Globalization.NumberStyles.HexNumber, null, out var inode)
                    && ownFds.Contains($"socket:[{inode}]"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // --- 10. Closed socket: the ESTABLISHED row is gone (or the pid already exited) → null ---

    [Fact]
    public void ClosedSocket_ReturnsNull()
    {
        var clientIp = IPAddress.Loopback;
        var clientPort = 0;
        var listenerPort = 0;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, listenerPort);
            listener.AcceptTcpClient().Dispose();
            var endpoint = (IPEndPoint)client.Client.LocalEndPoint!;
            clientIp = endpoint.Address;
            clientPort = endpoint.Port;
        } // client + accepted socket disposed → connection closed

        listener.Stop();
        listener.Dispose();

        var resolver = new LocalClientIdentityResolver();
        Assert.Null(resolver.TryResolveClientIdentity(clientIp, clientPort, listenerPort));
    }

    // --- 11. Fail-safe + short-circuit performance contract ---

    [Fact]
    public void FailingLookup_DegradestoNull_NeverThrows()
    {
        var seam = CreateSeam(
            name: null,
            lookup: (ip, rp, lp) => throw new System.ComponentModel.Win32Exception(5, "access denied"));
        Assert.Null(seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 49152, RouterPort));
        Assert.Equal(1, seam.Lookups);
    }

    [Fact]
    public void ShortCircuitPaths_AreFast()
    {
        var seam = CreateSeam(name: "Code");
        var nonLoopback = IPAddress.Parse("192.168.1.50");

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            Assert.Null(seam.Resolver.TryResolveClientIdentity(null, 49152, RouterPort));
            Assert.Null(seam.Resolver.TryResolveClientIdentity(nonLoopback, 49152, RouterPort));
            Assert.Null(seam.Resolver.TryResolveClientIdentity(IPAddress.Loopback, 0, RouterPort));
        }

        stopwatch.Stop();
        Assert.Equal(0, seam.Lookups);
        // 3000 short-circuit calls in well under 1 s (asserts the < 1 ms-per-call contract
        // without flaky single-sample timing).
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Took {stopwatch.Elapsed}");
    }

    // --- 12. DI registration ---

    [Fact]
    public void Registration_IsSingletonInAddOllamaRouter()
    {
        var services = new ServiceCollection();
        var stateDirectory = Path.Combine(Path.GetTempPath(), "ollama-router-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            services.AddOllamaRouter(
                new OllamaRouterOptions { LocalUrl = "http://localhost:11434", RemoteUrl = "http://remote:11434" },
                stateDirectory: stateDirectory);

            var descriptors = services
                .Where(d => d.ServiceType == typeof(ILocalClientIdentityResolver))
                .ToList();
            Assert.Contains(descriptors, d =>
                d.Lifetime == ServiceLifetime.Singleton && d.ImplementationType == typeof(LocalClientIdentityResolver));
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    // --- 13. Windows table parsing: the tcpmib.h row layout is pinned deterministically ---
    //
    // The GetExtendedTcpTable P/Invoke fetch only executes on Windows, but the row LAYOUT it
    // parses (MIB_TCPROW_OWNER_PID / MIB_TCP6ROW_OWNER_PID) must be correct everywhere. These
    // tests feed hand-crafted tables through the internal pure matcher on EVERY platform, so
    // an ABI-layout regression (row size, field offset, state value, port endianness, mapped
    // address) fails CI on Linux.

    private const uint AfInet = 2;
    private const uint AfInet6 = 23;

    // The TRUE tcpmib.h row sizes (24 = 6 * DWORD; 56 = 16+4+4+16+4+4+4+4) — deliberately
    // hard-coded and NOT the resolver's constants, so a layout drift in the resolver fails here.
    private const int V4RowSize = 24;
    private const int V6RowSize = 56;

    private static readonly byte[] Ipv6LoopbackBytes = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 };

    private static byte[] NewTable(int rowSize, int rows)
    {
        var table = new byte[4 + rows * rowSize];
        table[0] = (byte)rows; // dwNumEntries (little-endian u32; the low byte suffices here)
        return table;
    }

    // Port DWORD in network byte order: high byte first (cf. ntohs(dwLocalPort)).
    private static void WritePort(byte[] table, int offset, int port)
    {
        table[offset] = (byte)(port >> 8);
        table[offset + 1] = (byte)(port & 0xFF);
    }

    private static void WriteV4Row(byte[] table, int row, int state, int localPort, int remotePort, int pid)
    {
        // MIB_TCPROW_OWNER_PID: dwState@0, dwLocalAddr@4, dwLocalPort@8, dwRemoteAddr@12,
        // dwRemotePort@16, dwOwningPid@20 (24 bytes).
        var o = 4 + row * V4RowSize;
        BitConverter.GetBytes(state).CopyTo(table, o);
        table[o + 4] = 0x7F; table[o + 5] = 0; table[o + 6] = 0; table[o + 7] = 1; // 127.0.0.1 (in_addr order)
        WritePort(table, o + 8, localPort);
        table[o + 12] = 0x7F; table[o + 13] = 0; table[o + 14] = 0; table[o + 15] = 1;
        WritePort(table, o + 16, remotePort);
        BitConverter.GetBytes(pid).CopyTo(table, o + 20);
    }

    private static void WriteV6Row(byte[] table, int row, int state, int localPort, int remotePort, int pid, byte[]? localAddr = null, byte[]? remoteAddr = null)
    {
        // MIB_TCP6ROW_OWNER_PID: ucLocalAddr[16]@0, dwLocalScopeId@16, dwLocalPort@20,
        // ucRemoteAddr[16]@24, dwRemoteScopeId@40, dwRemotePort@44, dwState@48, dwOwningPid@52
        // (56 bytes; the scope IDs stay zero for loopback).
        var o = 4 + row * V6RowSize;
        (localAddr ?? Ipv6LoopbackBytes).CopyTo(table, o);
        WritePort(table, o + 20, localPort);
        (remoteAddr ?? Ipv6LoopbackBytes).CopyTo(table, o + 24);
        WritePort(table, o + 44, remotePort);
        BitConverter.GetBytes(state).CopyTo(table, o + 48);
        BitConverter.GetBytes(pid).CopyTo(table, o + 52);
    }

    [Fact]
    public void WindowsTableParse_Ipv4ClientRow_ReturnsOwningPid()
    {
        // Row 0: the SERVER-side row (local port = router port) — its PID is the router's;
        // row 1: the CLIENT-side ESTABLISHED row (reaching it requires the 24-byte stride);
        // row 2: a TIME_WAIT decoy with the identical ports.
        var table = NewTable(V4RowSize, 3);
        WriteV4Row(table, 0, state: 5, localPort: RouterPort, remotePort: 49152, pid: 9999);
        WriteV4Row(table, 1, state: 5, localPort: 49152, remotePort: RouterPort, pid: 4321);
        WriteV4Row(table, 2, state: 11, localPort: 49152, remotePort: RouterPort, pid: 8765);

        Assert.Equal(4321, LocalClientIdentityResolver.MatchOwningPidInTable(table, IPAddress.Loopback, 49152, RouterPort, AfInet));
    }

    [Fact]
    public void WindowsTableParse_Ipv6ClientRow_ReturnsOwningPid()
    {
        // Two 56-byte rows: the server-side row first, the client-side row second — a wrong
        // row stride (e.g. the legacy 48 bytes) never reaches row 1 and returns null.
        var table = NewTable(V6RowSize, 2);
        WriteV6Row(table, 0, state: 5, localPort: RouterPort, remotePort: 49152, pid: 9999);
        WriteV6Row(table, 1, state: 5, localPort: 49152, remotePort: RouterPort, pid: 4321);

        Assert.Equal(4321, LocalClientIdentityResolver.MatchOwningPidInTable(table, IPAddress.IPv6Loopback, 49152, RouterPort, AfInet6));
    }

    [Fact]
    public void WindowsTableParse_Ipv4MappedV6Row_MatchesIpv4Target()
    {
        // .NET clients dialing a v4 loopback listener via [::ffff:127.0.0.1] appear in the
        // AF_INET6 table in mapped form — an IPv4 target must match it.
        var mapped = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0x7F, 0, 0, 1 };
        var table = NewTable(V6RowSize, 1);
        WriteV6Row(table, 0, state: 5, localPort: 49152, remotePort: RouterPort, pid: 4321, localAddr: mapped, remoteAddr: mapped);

        Assert.Equal(4321, LocalClientIdentityResolver.MatchOwningPidInTable(table, IPAddress.Loopback, 49152, RouterPort, AfInet6));
    }

    [Fact]
    public void WindowsTableParse_NonEstablishedRows_ReturnsNull()
    {
        // CLOSED(1), LISTEN(2), SYN_SENT(3), FIN_WAIT1(6) and TIME_WAIT(11) with exact ports
        // must never match — in particular 1 is MIB_TCP_STATE_CLOSED, not ESTABLISHED, on
        // Windows (ESTAB = 5).
        foreach (var state in new[] { 1, 2, 3, 6, 11 })
        {
            var table = NewTable(V4RowSize, 1);
            WriteV4Row(table, 0, state: state, localPort: 49152, remotePort: RouterPort, pid: 4321);
            Assert.Null(LocalClientIdentityResolver.MatchOwningPidInTable(table, IPAddress.Loopback, 49152, RouterPort, AfInet));
        }
    }

    [Fact]
    public void WindowsTableParse_TruncatedOrEmptyTables_ReturnsNull()
    {
        Assert.Null(LocalClientIdentityResolver.MatchOwningPidInTable(Array.Empty<byte>(), IPAddress.Loopback, 49152, RouterPort, AfInet));
        Assert.Null(LocalClientIdentityResolver.MatchOwningPidInTable(new byte[2], IPAddress.Loopback, 49152, RouterPort, AfInet));

        // The header claims one row but the buffer holds only half of it.
        var table = new byte[4 + V4RowSize / 2];
        table[0] = 1;
        Assert.Null(LocalClientIdentityResolver.MatchOwningPidInTable(table, IPAddress.Loopback, 49152, RouterPort, AfInet));
    }

    // ------------------------------------------------------------------
    // Seam: counting fake process-lookup + optional fixed/mutable clock.
    // ------------------------------------------------------------------

    private sealed class Seam
    {
        private readonly int[] _counter;

        public Seam(LocalClientIdentityResolver resolver, int[] counter)
        {
            Resolver = resolver;
            _counter = counter;
        }

        public LocalClientIdentityResolver Resolver { get; }

        public int Lookups => _counter[0];
    }

    private static Seam CreateSeam(
        string? name,
        Func<DateTimeOffset>? clock = null,
        Func<IPAddress, int, int, string?>? lookup = null)
    {
        var counter = new int[1];
        Func<IPAddress, int, int, string?> effectiveLookup;
        if (lookup is not null)
        {
            effectiveLookup = (ip, rp, lp) =>
            {
                counter[0]++;
                return lookup(ip, rp, lp);
            };
        }
        else
        {
            effectiveLookup = (ip, rp, lp) =>
            {
                counter[0]++;
                return name;
            };
        }

        return new Seam(
            new LocalClientIdentityResolver(clock, effectiveLookup, NullLogger<LocalClientIdentityResolver>.Instance),
            counter);
    }
}
