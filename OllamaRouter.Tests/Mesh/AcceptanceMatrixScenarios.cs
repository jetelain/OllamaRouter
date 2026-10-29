using System.Diagnostics;
using System.Net;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// The §4 verification matrix, one <see cref="Fact"/> per row, executed against the simulated
/// 3-node mesh (2 in-process routers + 3 fake daemons) — the CI-gated primary surface (plan §4).
/// </summary>
[Collection("InProcessMeshFleet")]
public class AcceptanceMatrixScenarios
{
    // ---- Row: Catalog Gate. the dense model is absent from the laptop catalog, so it is
    //      NEVER routed to the 8 GB laptop (catalog gate + LargeDense-incompatibility). The
    //      request is served by a host that carries the model (desktop or server). ----
    [Fact]
    public async Task CatalogGate_DenseAbsentFromLaptop_NeverRoutesToLaptop()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.DenseModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        var (status, _, served, _, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.DenseModel, 400);
        Assert.Equal(HttpStatusCode.OK, status);

        var laptop = fleet.Daemon("laptop");
        // The laptop never hosts the dense model (catalog gate) and the router must not route it there.
        Assert.Contains(MeshCatalog.MoeModel, laptop.Models.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(MeshCatalog.DenseModel, laptop.Models.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("laptop", served);
        Assert.True(fleet.Daemon("laptop").ChatHits().Count(r => r.Path.StartsWith("/api/chat") && r.Body.Contains(MeshCatalog.DenseModel)) == 0,
            $"the dense model must NEVER reach the laptop daemon; served {served}");
        Assert.True(string.Equals(served, "desktop", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(served, "server", StringComparison.OrdinalIgnoreCase),
            $"the dense request must be served by a host that carries it (desktop/server); got {served}");
    }

    // ---- Row: Liveness & Probing. daemon down → immediate failover, no hang; restart → rejoins within 2 re-probes. ----
    [Fact]
    public async Task Liveness_DaemonDown_NoHang_ImmediateFailover_ThenRejoin()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        var laptop = fleet.Daemon("laptop");
        await laptop.StopAsync();
        await router.Liveness.MarkHardFailureAsync(new RoutingTarget("laptop"));

        var sw = Stopwatch.StartNew();
        var (status, body, served, _, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 16);
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"done\":true", body);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"failover must not hang (took {sw.Elapsed.TotalMilliseconds:F0} ms)");
        Assert.NotEqual("laptop", served);

        // Rejoin: restart the daemon on the same port; it must be Online within 2 re-probe intervals.
        await laptop.StartAsync();
        var rejoined = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            if (await router.Liveness.EnsureKnownAsync(new RoutingTarget("laptop"), CancellationToken.None) == TargetLivenessState.Online)
            {
                rejoined = true;
                break;
            }
            await Task.Delay(100, CancellationToken.None);
        }
        Assert.True(rejoined, "the restarted laptop daemon must rejoin the fleet");
    }

    // ---- Row: Parallel Tag Aggregation. a slow daemon misses the 500 ms budget; the union still returns fast. ----
    [Fact]
    public async Task ParallelTagAggregation_SlowDaemon_ReturnsUnionWithinBudget()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        // Warm the laptop's stale slot while it is fast, then make it slow (past the 500 ms budget).
        var warm = await router.Client.GetAsync("/api/tags");
        await warm.Content.ReadAsStringAsync();
        warm.Dispose();

        var laptop = fleet.Daemon("laptop");
        laptop.SetLivenessDelay(TimeSpan.FromSeconds(2));

        var sw = Stopwatch.StartNew();
        var response = await router.Client.GetAsync("/api/tags");
        var body = await response.Content.ReadAsStringAsync();
        sw.Stop();
        response.Dispose();
        laptop.ResetBehavior();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"aggregation must not hang on the slow daemon (took {sw.Elapsed.TotalMilliseconds:F0} ms)");
        // Union across the online daemons: the dense model (desktop+server) and the MoE model.
        Assert.Contains(MeshCatalog.DenseModel, body);
        Assert.Contains(MeshCatalog.MoeModel, body);
    }

    // ---- Row: Zero-Config Context. the desktop (16 GB, 49,152 Modelfile pin) wins the
    //      Architect sweet spot for a small dense prompt; a prompt too large for the desktop's
    //      pre-configured capacity spills to the 96 GB server (no Modelfile pin → the 131,072
    //      assumed fallback CMax). Task #26 dense clamp-only: a dense payload is only rewritten
    //      when the client static num_ctx EXCEEDS the node's pre-configured context (down-clamp);
    //      with no client num_ctx the payload is untouched (PassThrough — no num_ctx header).
    [Fact]
    public async Task ZeroConfigContext_SmallRoutesDesktop_LargeRoutesServer()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.DenseModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        // 35k dense prompt (no client num_ctx): desktop (49,152 pre-configured) qualifies
        // (35,000+8,192=43,192 ≤ 49,152) and wins; dense clamp-only pass-through leaves the
        // payload untouched (no num_ctx injected), so ctx (the Context-Size header) is null.
        var (status35, _, served35, ctx35, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.DenseModel, 35_000);
        Assert.Equal(HttpStatusCode.OK, status35);
        Assert.True(string.Equals(served35, "desktop", StringComparison.OrdinalIgnoreCase), $"a 35k dense prompt fits the desktop 49,152 capacity (35,000+8,192=43,192); got {served35}");
        Assert.True(ctx35 is null, $"dense pass-through (no client num_ctx) injects no num_ctx; got {ctx35}");

        // 120k dense prompt (120,000+8,192=128,192 > 49,152): disqualified from the desktop,
        // qualifies on the server (131,072 assumed fallback); dense pass-through → no num_ctx.
        var (status120, _, served120, ctx120, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.DenseModel, 120_000);
        Assert.Equal(HttpStatusCode.OK, status120);
        Assert.True(string.Equals(served120, "server", StringComparison.OrdinalIgnoreCase), $"a 120k dense prompt (128,192 needed) exceeds the desktop 49,152 capacity → server; got {served120}");
        Assert.True(ctx120 is null, $"dense pass-through (no client num_ctx) injects no num_ctx; got {ctx120}");
    }

    // ---- Row: Client Context Clamping. client 131072 + 2k prompt clamps to the desktop ceiling. ----
    [Fact]
    public async Task ClientClamping_2kPrompt_131072NumCtx_RunsOnDesktopClamped()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.DenseModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        var desktop = fleet.Daemon("desktop");
        var (status, _, served, ctx, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.DenseModel, 2_000, numCtx: 131_072);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("desktop", served);
        Assert.NotNull(ctx);
        Assert.True(ctx! <= 49_152, $"the client's 131072 must clamp to the desktop ceiling (got {ctx})");
        var received = desktop.LastReceivedNumCtx(MeshCatalog.DenseModel);
        Assert.NotNull(received);
        Assert.True(received! <= 49_152, $"the daemon must receive a rewritten num_ctx ≤ ceiling (got {received})");
    }

    // ---- Row: Dynamic Context Expansion. a ~195k MoE prompt (195,000+8,192=203,192 > 184,320
    //      pre-configured baseline) expands to the single extensible bucket 262,144. ----
    [Fact]
    public async Task DynamicExpansion_195kMoePrompt_InjectsExtensible262144()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        var (status, _, served, ctx, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 195_000);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(ctx == 262_144, $"a 195k MoE prompt (above the 184,320 baseline) sizes at the 262,144 extensible bucket; got {ctx}");

        var servedTarget = fleet.Daemons.First(d => d.Name == served);
        var received = servedTarget.LastReceivedNumCtx(MeshCatalog.MoeModel);
        Assert.True(received == 262_144, $"the served daemon must receive options.num_ctx == 262144; got {received}");
    }

    // ---- Row: Auto-Shrink Hysteresis. 195k → 262144 (expanded); 175k → retained; 30k → shrunk to the 184,320 pre-configured baseline. ----
    [Fact]
    public async Task AutoShrinkHysteresis_195k_175k_30k_RetainsThenShrinks()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        // Pin every step to the laptop — the MoE model is pinned at 184,320 (baseline) with a
        // 262,144 native limit, so a prompt past the baseline expands to 262,144. A single
        // target isolates the lease mechanism the §4 row is verifying
        // (expand → retain → shrink-to-baseline).
        // 195k (195,000+8,192=203,192 > 184,320) → fresh expansion to 262,144.
        var (_, _, served1, ctx1, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 195_000, pin: "laptop");
        Assert.Equal(262_144, ctx1);
        Assert.True(string.Equals(served1, "laptop", StringComparison.OrdinalIgnoreCase), $"the 195k MoE prompt must be served on the laptop; got {served1}");

        // 175k (175,000+8,192=183,192 ≤ 184,320 baseline) → the engine would drop to the
        // baseline, but the lease RETAINS the expanded 262,144 bucket (RetainedExpanded).
        var (_, _, served2, ctx2, mode2) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 175_000, pin: "laptop");
        Assert.True(ctx2 == 262_144, $"a 175k prompt retains the expanded 262,144 bucket (retained); got {ctx2}");
        Assert.True(string.Equals(mode2, "RetainedExpanded", StringComparison.OrdinalIgnoreCase), $"mode expected RetainedExpanded; got {mode2}");
        Assert.True(string.Equals(served1, served2, StringComparison.OrdinalIgnoreCase), $"the lease must stay pinned to the same (hot) target; {served1} vs {served2}");

        // 30k → below 50% of the 184,320 baseline → immediate shrink back to the baseline.
        var (_, _, served3, ctx3, mode3) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 30_000, pin: "laptop");
        Assert.True(ctx3 == 184_320, $"a 30k prompt (< 50% baseline) shrinks back to the pre-configured 184,320 baseline; got {ctx3}");
        Assert.True(string.Equals(mode3, "ShrunkToBaseline", StringComparison.OrdinalIgnoreCase), $"mode expected ShrunkToBaseline; got {mode3}");
        Assert.True(string.Equals(served2, served3, StringComparison.OrdinalIgnoreCase), $"the lease must stay on the same target through the shrink; {served2} vs {served3}");
        // The daemon proxy must have received the shrunk 184,320 in the outgoing options.num_ctx.
        var laptopReceived = fleet.Daemon("laptop").LastReceivedNumCtx(MeshCatalog.MoeModel);
        Assert.True(laptopReceived == 184_320, $"the laptop daemon must receive options.num_ctx == 184320 after the shrink; got {laptopReceived}");
        }

    // ---- Row: Model Incompatibility. the 27B dense model never routes to the 8GB laptop. ----
    [Fact]
    public async Task ModelIncompatibility_27bNeverRoutesToLaptop()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.DenseModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        // Hold the desktop busy so the decision must spill, but NEVER to the laptop (8GB vetoes LargeDense).
        var desktop = fleet.Daemon("desktop");
        desktop.SetChatStreamDelay(TimeSpan.FromSeconds(2));
        try
        {
            // Concurrent: one long-held desktop chat + one new dense request. The new one must
            // land on the server (desktop busy) and the laptop must see zero 27B hits.
            var held = MeshTestHelpers.SendChatAsync(router, MeshCatalog.DenseModel, 400, pin: "desktop");
            await Task.Delay(200, CancellationToken.None);
            var (status, _, served, _, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.DenseModel, 400);
            await DrainOrIgnore(held);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.NotEqual("laptop", served);
            Assert.True(fleet.Daemon("laptop").ChatHits().Count(r => r.Path.StartsWith("/api/chat") && r.Body.Contains(MeshCatalog.DenseModel)) == 0,
                "the 27B dense model must NEVER reach the 8GB laptop");
        }
        finally
        {
            desktop.ResetBehavior();
        }
    }

    // ---- Row: Hot-Model Affinity. interleaved Architect(27B)/Developer(MoE) keeps each on its hot target. ----
    [Fact]
    public async Task HotModelAffinity_InterleavedArchDev_NoEvictions()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        // Preload: 27B hot on desktop, MoE hot on laptop.
        await MeshTestHelpers.ChatAsync(router, MeshCatalog.DenseModel, 400, pin: "desktop");
        await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 16, pin: "laptop");
        // The execution tracker refreshes out-of-band on a 2 s TTL (manual RefreshAsync is a
        // no-op within the TTL), so wait for the background refresh to surface BOTH preloads —
        // affinity scoring reads the tracker's /api/ps snapshot, not the daemons directly.
        Assert.True(await MeshTestHelpers.WaitForLoadedAsync(router, "desktop", MeshCatalog.DenseModel, TimeSpan.FromSeconds(8)),
            "the tracker must surface the dense model loaded on the desktop");
        Assert.True(await MeshTestHelpers.WaitForLoadedAsync(router, "laptop", MeshCatalog.MoeModel, TimeSpan.FromSeconds(8)),
            "the tracker must surface the MoE model loaded on the laptop");

        var laptop = fleet.Daemon("laptop");
        for (var i = 0; i < 4; i++)
        {
            var (_, _, servedArch, _, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.DenseModel, 400);
            Assert.True(string.Equals(servedArch, "desktop", StringComparison.OrdinalIgnoreCase), $"iter {i}: 27B must stay on its hot desktop; got {servedArch}");

            var (_, _, servedDev, _, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 16);
            Assert.True(string.Equals(servedDev, "laptop", StringComparison.OrdinalIgnoreCase), $"iter {i}: MoE must stay on its hot laptop; got {servedDev}");

            // The laptop must keep its MoE model loaded throughout (no cross-routing eviction).
            Assert.Contains(MeshCatalog.MoeModel, laptop.LoadedModels, StringComparer.OrdinalIgnoreCase);
        }
    }

    // ---- Row: Standalone MoE Speed. an idle desktop wins a standalone MoE request (no dense hot/busy). ----
    [Fact]
    public async Task StandaloneMoeSpeed_IdleDesktop_WinsMoE()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        // Fresh fleet: no dense model hot or busy on the desktop/server, so the MoE offload slot
        // is NOT justified and the idle desktop (first in declaration order) wins by tie-break.
        var (status, _, served, _, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 16);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(string.Equals(served, "desktop", StringComparison.OrdinalIgnoreCase), $"a standalone MoE request with no dense-hot target wins on the idle desktop; got {served}");
    }

    // ---- Row: Concurrency Spill. a held (busy) desktop causes the second 27B request to spill to the server. ----
    [Fact]
    public async Task ConcurrencySpill_Second27bRequest_SpillsToServer()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.DenseModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        var desktop = fleet.Daemon("desktop");
        desktop.SetChatStreamDelay(TimeSpan.FromSeconds(3));
        try
        {
            var held = MeshTestHelpers.SendChatAsync(router, MeshCatalog.DenseModel, 400, pin: "desktop");
            // Wait for the held request to actually be in flight on the desktop.
            await WaitUntilInFlightAsync(router, "desktop", TimeSpan.FromSeconds(3));

            var (status, _, served, _, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.DenseModel, 400);
            await DrainOrIgnore(held);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(string.Equals(served, "server", StringComparison.OrdinalIgnoreCase), $"the second 27B request must spill to the server (desktop busy penalty); got {served}");
        }
        finally
        {
            desktop.ResetBehavior();
        }
    }

    // ---- Row: Uncommitted Failover. a daemon that aborts the connection BEFORE committing any
    //      response byte (a definitive pre-commit transport failure) → the client still gets a
    //      complete 200 from a healthy fallback, and the telemetry header reports the SERVED
    //      target (not the decided primary).
    [Fact]
    public async Task UncommittedFailover_KilledDaemon_CompletesOnFallback()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        var desktop = fleet.Daemon("desktop");
        // AbortBeforeCommit: the daemon resets the socket before flushing any response byte —
        // the definitive UNCOMMITTED transport failure (ForwarderError before any response
        // byte). The dispatch loop must rewind the (uncommitted) response state, fail over to
        // a healthy target, and deliver a COMPLETE response. (A StopAsync() kill is masked here
        // because Kestrel's graceful shutdown still services the router's warm keep-alive
        // connection; AbortBeforeCommit fails deterministically even on that connection.)
        desktop.SetChatBehavior(ChatMode.AbortBeforeCommit);
        try
        {
            // Pin to the aborting desktop so the dispatch loop's decided primary is the dead
            // candidate and the loop must fail over to a healthy target.
            var (status, body, served, _, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 16, pin: "desktop");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("\"done\":true", body);
            Assert.True(!string.Equals(served, "desktop", StringComparison.OrdinalIgnoreCase), $"the aborted desktop must fail over to a healthy fallback; got {served}");
            // Exactly-once delivery: the desktop hit is aborted (uncommitted), and exactly ONE
            // healthy fallback actually served the completed response.
            var servedDaemon = fleet.Daemons.First(d => string.Equals(d.Name, served, StringComparison.OrdinalIgnoreCase));
            Assert.True(servedDaemon.ChatHits().Count == 1, $"the fallback ({served}) must serve exactly one chat hit; got {servedDaemon.ChatHits().Count}");
        }
        finally
        {
            desktop.ResetBehavior();
        }
    }

    // ---- Row: Client Pinning. X-OllamaRouter-Target: laptop → served + echoed on the laptop. ----
    [Fact]
    public async Task ClientPinning_LaptopPin_RoutedAndEchoed()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        var (status, _, served, _, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 16, pin: "laptop");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(string.Equals(served, "laptop", StringComparison.OrdinalIgnoreCase), $"a pin to the laptop must be served (and echoed) on the laptop; got {served}");
        Assert.True(fleet.Daemon("laptop").ChatHits().Count == 1, $"laptop should have exactly one chat hit; got {fleet.Daemon("laptop").ChatHits().Count}");
    }

    // ---- Row: Mesh Operation. both routers access all daemons; exactly-once; bounded wall time; no deadlock. ----
    [Fact]
    public async Task MeshOperation_BothRouters_AllDaemons_NoLoop_NoDeadlock()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var r1 = await fleet.CreateRouterAsync("R1");
        var r2 = await fleet.CreateRouterAsync("R2");
        await MeshTestHelpers.WarmupAsync(r1, MeshCatalog.MoeModel);
        await MeshTestHelpers.WarmupAsync(r2, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(r1, TimeSpan.FromSeconds(5));
        await MeshTestHelpers.WaitAllOnlineAsync(r2, TimeSpan.FromSeconds(5));

        var before = fleet.Daemons.Sum(d => d.ChatHits().Count);

        var sw = Stopwatch.StartNew();
        var tasks = new List<Task<(HttpStatusCode Status, string? Body, string? Served, int? ContextSize, string? ContextMode)>>
        {
            Task.Run(() => MeshTestHelpers.ChatAsync(r1, MeshCatalog.MoeModel, 16)),
            Task.Run(() => MeshTestHelpers.ChatAsync(r2, MeshCatalog.MoeModel, 16)),
            Task.Run(() => MeshTestHelpers.ChatAsync(r1, MeshCatalog.DenseModel, 400)),
            Task.Run(() => MeshTestHelpers.ChatAsync(r2, MeshCatalog.DenseModel, 400))
        };
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"the concurrent bidirectional batch must complete (no deadlock) (took {sw.Elapsed.TotalSeconds:F1} s)");
        foreach (var (status, _, served, _, _) in results)
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(string.IsNullOrWhiteSpace(served));
        }

        var after = fleet.Daemons.Sum(d => d.ChatHits().Count);
        Assert.True(after - before == results.Length, $"exactly-once delivery: expected {results.Length} chat hits, got {after - before}");

        // Loop-free invariant: every recorded daemon request originates from a router port (or the
        // client), never from a peer router; and each router's target URLs point only at daemon ports.
        Assert.All(fleet.Daemons, daemon => AssertAllSourcesAreRoutersOrClient(fleet, daemon));
        AssertEachRouterTargetsOnlyDaemons(fleet);
    }

    // ------------------------------------------------------------------
    //  Assertion helpers
    // ------------------------------------------------------------------

    private static void AssertAllSourcesAreRoutersOrClient(InProcessMeshFleet fleet, FakeOllamaDaemon daemon)
    {
        var allDaemonPorts = fleet.Daemons.Select(d => d.Port).ToHashSet();
        foreach (var request in daemon.Requests)
        {
            if (request.SourceEndpoint is not System.Net.IPEndPoint ip)
            {
                continue; // no resolvable source (should not happen for loopback)
            }

            // The client's outbound pool uses ephemeral ports, and the routers' outbound pools use
            // ephemeral ports too, so we cannot positively identify the source as a router here.
            // What we CAN prove is the loop-free invariant: the source is never a PHYSICAL DAEMON's
            // port (a daemon-to-daemon/peer hop would only be possible in a routing loop), and it
            // is always loopback. Combined with AssertEachRouterTargetsOnlyDaemons (routers only
            // dial daemon ports) and the exactly-once hit count in the calling test, this closes
            // the loop-free proof for the Mesh Operation row.
            Assert.True(
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && System.Net.IPAddress.IsLoopback(ip.Address),
                $"daemon {daemon.Name} saw non-loopback traffic from {ip}");
            Assert.True(
                !allDaemonPorts.Contains(ip.Port),
                $"daemon {daemon.Name} saw traffic sourced from port {ip.Port}, which is a physical daemon's port — a daemon-to-daemon hop is only possible in a loop");
        }
    }

    private static void AssertEachRouterTargetsOnlyDaemons(InProcessMeshFleet fleet)
    {
        var daemonPorts = fleet.Daemons.Select(d => d.Port).ToHashSet();
        foreach (var router in fleet.Routers)
        {
            foreach (var target in router.Options.GetEffectiveTargets())
            {
                Assert.True(Uri.TryCreate(target.Url, UriKind.Absolute, out var uri),
                    $"target {target.Name} has an absolute URL");
                Assert.True(daemonPorts.Contains(uri.Port),
                    $"router {router.Name} target {target.Name} points at daemon port {uri.Port} (loop-free)");
            }
        }
    }

    private static async Task DrainOrIgnore(Task<HttpResponseMessage> held)
    {
        try
        {
            var response = await held;
            await response.Content.ReadAsStringAsync();
            response.Dispose();
        }
        catch
        {
            // The held request may have been interrupted; ignore.
        }
    }

    private static async Task WaitUntilInFlightAsync(MeshRouter router, string target, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var routingTarget = new RoutingTarget(target);
        while (DateTime.UtcNow < deadline)
        {
            var state = router.ExecutionTracker.GetState(routingTarget);
            if (state is { InFlightRequests: > 0 })
            {
                return;
            }
            await Task.Delay(50, CancellationToken.None);
        }
    }
}
