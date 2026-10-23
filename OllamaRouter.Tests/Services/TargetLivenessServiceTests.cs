using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class TargetLivenessServiceTests : IDisposable
{
    private const string LocalUrl = "http://127.0.0.1:11991";
    private const string RemoteUrl = "http://127.0.0.1:11992";

    private readonly List<HttpClient> _clients = [];

    // Records every probe request and delegates to the responder (which may throw,
    // to simulate timeout / hard-error transport failures).
    private sealed class CountingFakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            return Task.Run(() => respond(request));
        }
    }

    // Tracks RefreshAsync calls so tests can assert the catalog sync on rejoin.
    private sealed class CountingCatalogCache : IModelCatalogCacheService
    {
        public int RefreshCount { get; private set; }
        public List<string?> RefreshedUrls { get; } = [];

        public Task<bool> ModelExistsAsync(string? baseUrl, string modelName, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasAnyModelsAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<IReadOnlyList<string>> RefreshAsync(string? baseUrl, CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            RefreshedUrls.Add(baseUrl);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<(IReadOnlyList<string> Names, string Digest)> RefreshWithDigestAsync(string? baseUrl, CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            RefreshedUrls.Add(baseUrl);
            return Task.FromResult((new List<string>() as IReadOnlyList<string>, string.Empty));
        }

        public Task<string> GetCurrentDigestAsync(string? baseUrl, CancellationToken cancellationToken = default) => Task.FromResult("");

        public string CurrentDigest(string? baseUrl) => "";

        public void RegisterCapabilityCacheKeys(string baseUrl, IEnumerable<string> keys, string digest)
        {
        }

        public Task<JsonArray?> GetStaleTagsAsync(string url) => Task.FromResult<JsonArray?>(null);
        public void SetStaleTags(string url, JsonArray tags) { }
        public Task<JsonArray?> GetStaleV1ModelsAsync(string url) => Task.FromResult<JsonArray?>(null);
        public void SetStaleV1Models(string url, JsonArray data) { }
        public Task<JsonArray?> GetStalePsAsync(string url) => Task.FromResult<JsonArray?>(null);
        public void SetStalePs(string url, JsonArray models) { }
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("ok", Encoding.UTF8, "text/plain")
    };

    private static HttpRequestException Refused() =>
        new("Connection refused", new SocketException((int)SocketError.ConnectionRefused));

    private static TaskCanceledException Timeout() => new("The request was canceled due to the configured HttpClient.Timeout");

    private static (TargetLivenessService Sut, CountingFakeHttpMessageHandler Handler, CountingCatalogCache Catalog) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        TimeSpan? reprobeInterval = null,
        TimeSpan? probeTimeout = null,
        TimeSpan? onlineCacheTtl = null)
    {
        var handler = new CountingFakeHttpMessageHandler(respond);
        var client = new HttpClient(handler) { Timeout = probeTimeout ?? TimeSpan.FromSeconds(2) };
        var options = Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions
        {
            LocalUrl = LocalUrl,
            RemoteUrl = RemoteUrl
        });
        var catalog = new CountingCatalogCache();
        var sut = new TargetLivenessService(options, catalog, client, NullLogger<TargetLivenessService>.Instance, reprobeInterval, onlineCacheTtl);
        return (sut, handler, catalog);
    }

    private async Task<(TargetLivenessService Sut, CountingFakeHttpMessageHandler Handler, CountingCatalogCache Catalog)> CreateStartedSutAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        TimeSpan reprobeInterval,
        TimeSpan? probeTimeout = null)
    {
        var (sut, handler, catalog) = CreateSut(respond, reprobeInterval, probeTimeout);
        await sut.StartAsync(CancellationToken.None);
        return (sut, handler, catalog);
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task BootProbe_ReachableTarget_MarksOnline()
    {
        var (sut, _, catalog) = await CreateStartedSutAsync(_ => Ok(), reprobeInterval: TimeSpan.FromSeconds(30));
        try
        {
            await AssertEventuallyAsync(() => sut.IsOnline(RoutingTarget.Local));
            Assert.True(sut.ServiceStartTimeUtc > DateTimeOffset.MinValue);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task BootProbe_ConnectionRefused_MarksOfflineImmediately()
    {
        var (sut, _, catalog) = CreateSut(_ => throw Refused());
        try
        {
            await sut.StartAsync(CancellationToken.None);
            await AssertEventuallyAsync(() =>
            {
                var entries = sut.GetEntries();
                return entries.TryGetValue(RoutingTarget.Local, out var local) && local.State == TargetLivenessState.Offline;
            });

            var entry = sut.GetEntries()[RoutingTarget.Local];
            Assert.Equal(TargetLivenessState.Offline, entry.State);
            Assert.Equal("connection error", entry.LastError);
            Assert.False(sut.IsOnline(RoutingTarget.Local));
            // A hard error never triggers a catalog refresh.
            Assert.Equal(0, catalog.RefreshCount);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EnsureKnown_Success_MarksOnline()
    {
        var (sut, _, _) = CreateSut(_ => Ok());

        var state = await sut.EnsureKnownAsync(RoutingTarget.Local);

        Assert.Equal(TargetLivenessState.Online, state);
        Assert.True(sut.IsOnline(RoutingTarget.Local));
    }

    [Fact]
    public async Task EnsureKnown_ConnectionRefused_TripsOfflineImmediatelyOnSingleProbe()
    {
        var (sut, handler, _) = CreateSut(_ => throw Refused());

        var state = await sut.EnsureKnownAsync(RoutingTarget.Local);

        Assert.Equal(TargetLivenessState.Offline, state);
        Assert.False(sut.IsOnline(RoutingTarget.Local));
        Assert.Equal(1, handler.RequestCount); // No hysteresis for hard errors: one probe trips it.
    }

    [Fact]
    public async Task EnsureKnown_SingleTimeout_DoesNotTripOffline_AndIsNotRoutable()
    {
        var (sut, handler, _) = CreateSut(_ => throw Timeout());

        var state = await sut.EnsureKnownAsync(RoutingTarget.Local);

        // Unknown + 1 timeout strike: the 2-strike hysteresis keeps the state from tripping
        // to Offline, but the target is not routable (it never had a successful probe).
        Assert.NotEqual(TargetLivenessState.Offline, state);
        Assert.False(sut.IsOnline(RoutingTarget.Local));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task EnsureKnown_TwoConsecutiveTimeouts_TripOffline()
    {
        var (sut, handler, _) = CreateSut(_ => throw Timeout());

        await sut.EnsureKnownAsync(RoutingTarget.Local);
        var state = await sut.EnsureKnownAsync(RoutingTarget.Local);

        Assert.Equal(TargetLivenessState.Offline, state);
        Assert.False(sut.IsOnline(RoutingTarget.Local));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task EnsureKnown_TimeoutThenSuccess_ResetsCounter_StaysOnline()
    {
        var flips = 0;
        var (sut, _, _) = CreateSut(_ =>
        {
            flips++;
            return flips == 1 ? throw Timeout() : Ok();
        });

        await sut.EnsureKnownAsync(RoutingTarget.Local);
        var state = await sut.EnsureKnownAsync(RoutingTarget.Local);

        Assert.Equal(TargetLivenessState.Online, state);
        Assert.True(sut.IsOnline(RoutingTarget.Local));
    }

    [Fact]
    public async Task EnsureKnown_HardErrorWhileOnline_TripsOfflineImmediately()
    {
        var flips = 0;
        // A zero TTL forces the second call to re-probe instead of short-circuiting on cache.
        var (sut, _, _) = CreateSut(_ =>
        {
            flips++;
            return flips == 1 ? Ok() : throw Refused();
        }, onlineCacheTtl: TimeSpan.Zero);

        await sut.EnsureKnownAsync(RoutingTarget.Local); // Online
        var state = await sut.EnsureKnownAsync(RoutingTarget.Local);

        Assert.Equal(TargetLivenessState.Offline, state);
        Assert.False(sut.IsOnline(RoutingTarget.Local));
    }

    [Fact]
    public async Task EnsureKnown_HealthyTargetIsNotReProbedWithinTtl()
    {
        var (sut, handler, _) = CreateSut(_ => Ok());

        await sut.EnsureKnownAsync(RoutingTarget.Local);
        await sut.EnsureKnownAsync(RoutingTarget.Local);
        await sut.EnsureKnownAsync(RoutingTarget.Local);

        Assert.Equal(1, handler.RequestCount); // TTL (3 s) suppresses the follow-up probes.
    }

    [Fact]
    public async Task EnsureKnown_ConcurrentCallers_CoalesceIntoSingleProbe()
    {
        var flips = 0;
        var (sut, handler, _) = CreateSut(request =>
        {
            flips++;
            // The first probe lingers so the 7 sibling calls coalesce onto it;
            // all requests then complete successfully.
            if (flips == 1)
            {
                Thread.Sleep(400);
            }

            return Ok();
        });

        var tasks = Enumerable.Range(0, 8).Select(_ => sut.EnsureKnownAsync(RoutingTarget.Local)).ToList();
        // Let all callers start (they pile onto the in-flight probe).
        await Task.Delay(50);

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(TargetLivenessState.Online, r));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ReProber_OfflineTargetRecovers_RejoinsWithinOneTickAndTriggersCatalogRefreshExactlyOnce()
    {
        var recovered = false;
        var stopped = false;
        var (sut, handler, catalog) = await CreateStartedSutAsync(request =>
        {
            // Only Local recovers; Remote stays refused so the catalog refresh count
            // is attributable solely to Local's rejoin.
            if (request.RequestUri?.ToString().StartsWith(LocalUrl, StringComparison.OrdinalIgnoreCase) == true)
            {
                return recovered ? Ok() : throw Refused();
            }

            throw Refused();
        }, reprobeInterval: TimeSpan.FromMilliseconds(100));
        try
        {
            // Let the boot probe (and first re-probe ticks) see the refused connection.
            await AssertEventuallyAsync(() =>
                sut.GetEntries().TryGetValue(RoutingTarget.Local, out var e) && e.State == TargetLivenessState.Offline);

            recovered = true;

            // Rejoin must be observed within one tick + generous margin, and the
            // rejoin transition must trigger the asynchronous catalog refresh.
            await AssertEventuallyAsync(
                () => sut.IsOnline(RoutingTarget.Local) && catalog.RefreshCount >= 1,
                timeoutMs: 3000);

            // Stop first so no further (non-existent) rejoin can add refreshes, then
            // assert the refresh happened exactly once, for Local's URL only.
            await sut.StopAsync(CancellationToken.None);
            stopped = true;

            Assert.Equal(1, catalog.RefreshCount);
            Assert.Single(catalog.RefreshedUrls);
            Assert.Equal(LocalUrl, catalog.RefreshedUrls[0]);
        }
        finally
        {
            if (!stopped)
            {
                await sut.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task GetEntries_NeverProbedFleetTargets_AppearAsUnknown()
    {
        var (sut, _, _) = CreateSut(_ => Ok());

        var entries = sut.GetEntries();

        Assert.Equal(TargetLivenessState.Unknown, entries[RoutingTarget.Local].State);
        Assert.Equal(TargetLivenessState.Unknown, entries[RoutingTarget.Remote].State);
        Assert.Null(entries[RoutingTarget.Local].LastError);
    }

    [Fact]
    public void GetFleetTargets_ExcludesCloud_AndDeduplicates()
    {
        var (sut, _, _) = CreateSut(_ => Ok());

        var fleet = sut.GetFleetTargets();

        Assert.DoesNotContain(fleet, t => t == RoutingTarget.Cloud);
        Assert.Contains(RoutingTarget.Local, fleet);
        Assert.Contains(RoutingTarget.Remote, fleet);
        Assert.Equal(fleet.Count, fleet.DistinctBy(t => t.Value).Count());
    }

    [Fact]
    public async Task RealSocket_ListeningButSilentDaemon_TwoProbeTimeoutsTripOffline_SingleTimeoutDoesNot()
    {
        // A real loopback listener that is never accepted from: the kernel accept
        // backlog completes the TCP handshake (connection establishment SUCCEEDS),
        // but no response ever comes, so each probe is a pure timeout — the
        // "slow (but connected) daemon" case.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var url = $"http://127.0.0.1:{port}";

        var options = Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions { LocalUrl = url });
        var catalog = new CountingCatalogCache();
        var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) };
        _clients.Add(client);
        var sut = new TargetLivenessService(options, catalog, client, NullLogger<TargetLivenessService>.Instance);

        var first = await sut.EnsureKnownAsync(RoutingTarget.Local);
        Assert.NotEqual(TargetLivenessState.Offline, first); // 1 strike: hysteresis holds.

        var second = await sut.EnsureKnownAsync(RoutingTarget.Local);
        Assert.Equal(TargetLivenessState.Offline, second);   // 2nd consecutive strike: trips.

        var entry = sut.GetEntries()[RoutingTarget.Local];
        Assert.Equal(TargetLivenessState.Offline, entry.State);
        Assert.Equal("timeout", entry.LastError);

        // One successful probe restores Online (via a real responding socket).
        using var okListener = new TcpListener(IPAddress.Loopback, 0);
        okListener.Start();
        var okPort = ((IPEndPoint)okListener.LocalEndpoint).Port;
        var stopOkDraining = false;
        var okDrain = Task.Run(async () =>
        {
            while (!stopOkDraining)
            {
                try
                {
                    using var socket = await okListener.AcceptSocketAsync();
                    using var stream = new NetworkStream(socket);
                    var body = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    await stream.WriteAsync(body);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception)
                {
                    // Socket reset while writing; keep draining.
                }
            }
        });

        var onlineOptions = Microsoft.Extensions.Options.Options.Create(new OllamaRouterOptions { LocalUrl = $"http://127.0.0.1:{okPort}" });
        var onlineSut = new TargetLivenessService(onlineOptions, catalog, client, NullLogger<TargetLivenessService>.Instance);
        var state = await onlineSut.EnsureKnownAsync(RoutingTarget.Local);

        Assert.Equal(TargetLivenessState.Online, state);
        Assert.True(onlineSut.IsOnline(RoutingTarget.Local));

        stopOkDraining = true;
        listener.Stop();
        okListener.Stop();
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition, int timeoutMs = 3000, int pollMs = 20)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                Assert.Fail("Condition was not met within the timeout.");
            }

            await Task.Delay(pollMs);
        }
    }
}
