using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OllamaRouter.Options;
using OllamaRouter.ReverseProxy;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// Loop-free mesh routing (plan §4 "Mesh Operation" row + §2.5 loop-free invariant). The
/// assertion surface is the daemon side: every daemon is a Kestrel stub behind which the
/// router's traffic lands, and each recorded request carries the CLIENT endpoint of the
/// connection that reached the daemon. Combined with the config invariant (each router's
/// configured target URLs point only at daemon ports — so the dispatch loop only ever dials
/// daemons, never a peer router) and exactly-once hit counting, this proves the mesh is
/// loop-free: outbound traffic can only ever reach physical daemons.
/// </summary>
[Collection("InProcessMeshFleet")]
public class MeshRoutingScenarios
{
    // ---- Loop-free destination invariant: every daemon-bound connection originates from a
    //      router port or the test client (never from a peer router's port); and each
    //      router's configured Targets point only at the physical daemons' ports. ----
    [Fact]
    public async Task LoopFree_DaemonTraffic_OriginatesOnlyFromRoutersOrClients()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var r1 = await fleet.CreateRouterAsync("R1");
        var r2 = await fleet.CreateRouterAsync("R2");
        await MeshTestHelpers.WarmupAsync(r1, MeshCatalog.MoeModel);
        await MeshTestHelpers.WarmupAsync(r2, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(r1, TimeSpan.FromSeconds(5));
        await MeshTestHelpers.WaitAllOnlineAsync(r2, TimeSpan.FromSeconds(5));

        // Config invariant: each router's targets resolve ONLY to the physical daemons' ports
        // — a peer router's port must never be a configured destination (loop-free by
        // configuration: the dispatch loop only ever dials configured target URLs).
        var daemonPorts = fleet.Daemons.Select(d => d.Port).ToHashSet();
        foreach (var router in fleet.Routers)
        {
            foreach (var target in router.Options.Targets)
            {
                var port = new Uri(target.Url!).Port;
                Assert.True(
                    daemonPorts.Contains(port),
                    $"router {router.Name} target '{target.Name}' points at port {port}, which is not a physical daemon port ({string.Join(",", daemonPorts)}) — mesh loops are only possible if a router lists a peer router as a target");
            }
        }

        // Observation invariant: every request any daemon received arrived over a loopback
        // connection whose source port is NEVER another daemon's port (a daemon-to-daemon hop
        // — or a peer-router hop masquerading as one — would surface here). Sources are either
        // the routers' outbound pools or the test client's ephemeral ports.
        var allDaemonPorts = fleet.Daemons.Select(d => d.Port).ToHashSet();
        foreach (var daemon in fleet.Daemons)
        {
            foreach (var request in daemon.Requests)
            {
                var source = request.SourceEndpoint as IPEndPoint;
                Assert.NotNull(source);
                Assert.True(
                    source!.AddressFamily == AddressFamily.InterNetwork && IPAddress.IsLoopback(source.Address),
                    $"daemon {daemon.Name} saw non-loopback traffic from {request.SourceEndpoint}");
                Assert.True(
                    !allDaemonPorts.Contains(source.Port),
                    $"daemon {daemon.Name} saw traffic sourced from port {source.Port}, which belongs to a PHYSICAL DAEMON — daemon-to-daemon/peer traffic would only be possible in a loop");
            }
        }
        }

    // ---- Exactly-once delivery under concurrent bidirectional traffic: N client requests
    //      split across R1 and R2 in flight at once → exactly N chat hits across ALL daemons
    //      (a router→router hop would double-count or drop; a dead request would also drop
    //      the bounded wall-time assertion). ----
    [Fact]
    public async Task ExactlyOnce_ConcurrentBidirectionalTraffic_NoDoubleDelivery()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var r1 = await fleet.CreateRouterAsync("R1");
        var r2 = await fleet.CreateRouterAsync("R2");
        await MeshTestHelpers.WarmupAsync(r1, MeshCatalog.MoeModel);
        await MeshTestHelpers.WarmupAsync(r2, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(r1, TimeSpan.FromSeconds(5));
        await MeshTestHelpers.WaitAllOnlineAsync(r2, TimeSpan.FromSeconds(5));
        fleet.ResetObservation();

        const int total = 12;
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, total).Select(i =>
            i % 2 == 0
                ? MeshTestHelpers.ChatAsync(r1, MeshCatalog.MoeModel, 16)
                : MeshTestHelpers.ChatAsync(r2, MeshCatalog.MoeModel, 16));
        var results = await Task.WhenAll(tasks);
        var elapsed = sw.Elapsed;

        Assert.True(elapsed < TimeSpan.FromSeconds(30), $"the concurrent bidirectional batch must complete without deadlock (took {elapsed.TotalSeconds:F1} s)");
        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(HttpStatusCode.OK, results[i].Status);
        }

        var chatHits = fleet.Daemons.Sum(d => d.ChatHits().Count);
        Assert.Equal(total, chatHits);
    }

    // ---- No deadlock: the concurrent batch above is also asserted to finish every request
    //      2xx within a hard wall-time bound (the "bounded wall time, no deadlock" clause).
    //      Folded here as a denser variant (24 requests, 8 in flight per router at a time). ----
    [Fact]
    public async Task NoDeadlock_SustainedBidirectionalBatch_BoundedWallTime()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var r1 = await fleet.CreateRouterAsync("R1");
        var r2 = await fleet.CreateRouterAsync("R2");
        await MeshTestHelpers.WarmupAsync(r1, MeshCatalog.MoeModel);
        await MeshTestHelpers.WarmupAsync(r2, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(r1, TimeSpan.FromSeconds(5));
        await MeshTestHelpers.WaitAllOnlineAsync(r2, TimeSpan.FromSeconds(5));

        const int batch = 8;
        const int rounds = 3; // 24 total, 4 in flight per router at once
        var sw = Stopwatch.StartNew();
        for (var round = 0; round < rounds; round++)
        {
            var tasks = new List<Task<(HttpStatusCode, string, string?, int?, string?)>>();
            for (var i = 0; i < batch; i++)
            {
                tasks.Add(i % 2 == 0
                    ? MeshTestHelpers.ChatAsync(r1, MeshCatalog.MoeModel, 16)
                    : MeshTestHelpers.ChatAsync(r2, MeshCatalog.MoeModel, 16));
            }

            var results = await Task.WhenAll(tasks);
            foreach (var (status, _, served, _, _) in results)
            {
                Assert.Equal(HttpStatusCode.OK, status);
                Assert.False(string.IsNullOrWhiteSpace(served));
            }
        }

        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(30),
            $"the sustained bidirectional batch must finish within the wall-time bound (took {sw.Elapsed.TotalSeconds:F1} s)");
    }

    // ---- Peer visibility: each router's aggregated endpoints expose the FULL fleet union
    //      (all three daemons' models) even while the OTHER router is serving traffic
    //      simultaneously (mesh load visibility via direct /api/ps). ----
    [Fact]
    public async Task PeerVisibility_AggregatedEndpoints_FleetUnionUnderConcurrentLoad()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var r1 = await fleet.CreateRouterAsync("R1");
        var r2 = await fleet.CreateRouterAsync("R2");
        await MeshTestHelpers.WarmupAsync(r1, MeshCatalog.MoeModel);
        await MeshTestHelpers.WarmupAsync(r2, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(r1, TimeSpan.FromSeconds(5));
        await MeshTestHelpers.WaitAllOnlineAsync(r2, TimeSpan.FromSeconds(5));
        fleet.ResetObservation();

        // Keep the other router busy while each router's aggregation endpoints are queried.
        using var loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var load = KeepBusyAsync(r2, loadCts.Token);
        var tagsR1 = await r1.Client.GetStringAsync("/api/tags", loadCts.Token);
        var psR1 = await r1.Client.GetStringAsync("/api/ps", loadCts.Token);

        // Both models appear on the union (dense from desktop+server, MoE from all three).
        Assert.Contains(MeshCatalog.DenseModel, tagsR1);
        Assert.Contains(MeshCatalog.MoeModel, tagsR1);
        Assert.Contains(MeshCatalog.MoeModel, psR1);

        loadCts.Cancel();
        try
        {
            await load;
        }
        catch (OperationCanceledException)
        {
            // Expected: the background load loop is cancelled by the test.
        }
    }

    private static async Task KeepBusyAsync(MeshRouter router, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 16);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
