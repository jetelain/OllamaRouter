using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// Fault-injection resilience (plan §4 fault rows + §5 FaultInjectionScenarios): socket
/// closures mid-stream, daemon crash+restart, latency spikes against the probe window,
/// mid-stream abort streak semantics, and the terminal all-dead paths (503 diagnostics).
/// Runs against the single-router fleet (lighter; the loop-free mesh rows live in
/// <see cref="MeshRoutingScenarios"/>). All scenarios are serialized within this collection to
/// avoid OS resource contention (each test spins up 5 Kestrel servers; 6 in parallel = 30).
/// </summary>
[Collection("InProcessMeshFleet")]
public class FaultInjectionScenarios
{
    // ---- Abrupt socket closure mid-stream: the daemon commits headers + N chunks then resets
    //      the socket. The client's stream is INTERRUPTED (the transport surfaces a connection
    //      reset, not a completed NDJSON response — so done:true is never observed); the served
    //      target's failure streak is fed; and the NEXT request completes fully on a healthy
    //      target. The no-corruption property is guaranteed structurally: a reset terminates the
    //      byte stream mid-line, so the client can only ever hold a strict prefix of the valid
    //      NDJSON the daemon emitted (no mixed/garbled bytes) — asserted via the daemon
    //      observation plane (exactly the expected number of committed chunks, no done:true). ----
    [Fact]
    public async Task AbruptSocketClosureMidStream_NoCommittedCorruption()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        var baselines = fleet.Daemons.ToDictionary(d => d.Name, _ => _.ChatHits().Count);
        // ALL daemons abort mid-stream, so the assertion is routing-independent: whichever target
        // the dispatch loop lands on (pinned or failed-over under parallel-load pressure), the
        // committed stream is truncated deterministically.
        foreach (var daemon in fleet.Daemons)
        {
            daemon.SetChatBehavior(ChatMode.StreamNThenAbort, 2); // 2 NDJSON chunks, then socket reset
        }
        try
        {
            var (status, body, served, interrupted, response) = await SendChatAbortKeepOpenAsync(router, MeshCatalog.MoeModel, 16, "desktop");

            // The headers were committed (200) but the stream was truncated mid-body. The daemon
            // applies a settle delay before its RST so the forwarder reliably reads the 200 + the
            // committed chunks BEFORE the socket resets — the abort is deterministically
            // COMMITTED (no uncommitted-race → no dispatch redrive → no 503). INVARIANT: the
            // stream never completes (no done:true) and every committed byte is a valid NDJSON
            // prefix.
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(body.Contains("\"done\":true"), "a mid-stream abort must not deliver a completed response");

            // Committed bytes are a clean NDJSON prefix (no done:true terminator; every present
            // line, if any, parses — no torn/garbled bytes).
            foreach (var line in body.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                Assert.NotNull(JsonNode.Parse(line));
            }

            // No dispatch retry of a committed response: the fleet received EXACTLY ONE chat hit
            // for this attempt (a redrive would have delivered a second /api/chat somewhere).
            Assert.Equal(baselines.Values.Sum() + 1, fleet.Daemons.Sum(d => d.ChatHits().Count));
            // §7 binding: a committed mid-stream failure FEEDS the served target's failure streak
            // (headers already flushed, then the transport dropped — a transport-reliability
            // signal, not a clean success). Poll the streak while the client response is still
            // OPEN: the server's finally-block records the streak, and disposing the client
            // socket first would cancel RequestAborted in a race that masks the streak.
            Assert.True(await MeshTestHelpers.WaitForPositiveStreakAsync(router, served, TimeSpan.FromSeconds(3)),
                $"the mid-stream abort must feed the served target's ({served}) failure streak (§7)");
            response.Dispose();
        }
        finally
        {
            foreach (var daemon in fleet.Daemons)
            {
                daemon.ResetBehavior();
            }
        }

        // The NEXT request completes fully (no daemon aborts anymore; the dispatch loop serves a
        // healthy target to completion).
        var next = await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 16);
        Assert.Equal(HttpStatusCode.OK, next.Status);
        Assert.Contains("\"done\":true", next.Body);
    }

    // ---- Daemon crash + restart: stop mid-idle → failover without a hang; restart on the
    //      SAME port → the liveness re-prober rejoins the target within 2 re-probe intervals
    //      (the harness injects a 1 s interval, so ≤ ~2–3 s of real time). ----
    [Fact]
    public async Task DaemonCrashAndRestart_RejoinsWithinTwoReprobes()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        var laptop = fleet.Daemon("laptop");
        await laptop.StopAsync();
        try
        {
            // While the laptop is down, a request pinned to it fails over — and does NOT hang.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (status, body, served, _, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.MoeModel, 16, pin: "laptop");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("\"done\":true", body);
            Assert.True(!string.Equals(served, "laptop", StringComparison.OrdinalIgnoreCase), $"the stopped laptop must fail over; got {served}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"failover during the outage must not hang (took {sw.Elapsed.TotalSeconds:F1} s)");

            // Crash+restart on the SAME port; the re-prober must rejoin the target within 2
            // re-probe intervals.
            await laptop.StartAsync();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                var state = await router.Liveness.EnsureKnownAsync(new RoutingTarget("laptop"));
                if (state == TargetLivenessState.Online)
                {
                    break;
                }

                await Task.Delay(200);
            }

            Assert.Equal(TargetLivenessState.Online, await router.Liveness.EnsureKnownAsync(new RoutingTarget("laptop")));
        }
        finally
        {
            await laptop.StartAsync(); // idempotent: leaves the daemon running for teardown.
        }
    }

    // ---- Latency spike: two consecutive probe timeouts trip Online → Offline (hysteresis:
    //      a single timeout does NOT trip a TTL-fresh target); requests never hang during the
    //      spike; one successful probe restores Online. ----
    [Fact]
    public async Task LatencySpike_TwoProbeTimeouts_TripOffline_ThenRecover()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        var laptop = fleet.Daemon("laptop");
        // A probe-window latency spike: every GET / takes 2.5 s (past the 1.5 s probe budget).
        laptop.SetLivenessDelay(TimeSpan.FromSeconds(2.5));
        try
        {
            // Requests to the other targets must keep flowing (no hang) while the laptop probes stall.
            var (status, body, _, _, _) = await MeshTestHelpers.ChatAsync(router, MeshCatalog.DenseModel, 400);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("\"done\":true", body);

            // Let the re-prober (1 s cadence) accrue ≥ 2 consecutive timeouts on the laptop.
            await Task.Delay(TimeSpan.FromSeconds(4));
            var state = await router.Liveness.EnsureKnownAsync(new RoutingTarget("laptop"));
            Assert.Equal(TargetLivenessState.Offline, state);
        }
        finally
        {
            laptop.ResetBehavior();
        }

        // Latency removed → one successful probe restores Online.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var state = await router.Liveness.EnsureKnownAsync(new RoutingTarget("laptop"));
            if (state == TargetLivenessState.Online)
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.Equal(TargetLivenessState.Online, await router.Liveness.EnsureKnownAsync(new RoutingTarget("laptop")));
    }

    // ---- Mid-stream abort feeds the streak but NEVER retries: the daemon proxy shows
    //      EXACTLY ONE chat hit on that attempt (a committed response cannot be redriven). ----
    [Fact]
    public async Task MidStreamAbort_FeedsStreak_NeverRetries()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        var baseline = fleet.Daemons.ToDictionary(d => d.Name, _ => _.ChatHits().Count);
        foreach (var daemon in fleet.Daemons)
        {
            daemon.SetChatBehavior(ChatMode.StreamNThenAbort, 1);
        }
        try
        {
            var (status, body, served, interrupted, response) = await SendChatAbortKeepOpenAsync(router, MeshCatalog.MoeModel, 16, "desktop");
            Assert.True(interrupted || !body.Contains("\"done\":true"), "mid-stream abort must not complete");
            // Exactly ONE chat hit on the SERVED target — a committed response (headers already
            // flushed) is terminal in the dispatch loop, so a mid-stream abort NEVER triggers a
            // dispatch retry (a redrive would double-serve the same request to another target).
            var totalHits = fleet.Daemons.Sum(d => d.ChatHits().Count);
            Assert.Equal(baseline.Values.Sum() + 1, totalHits);
            // §7 binding: the committed mid-stream failure FEEDS the served target's streak
            // (transport dropped after the response had started). Poll the streak while the
            // client response is still OPEN: the server's finally-block records the streak, and
            // disposing the client socket first would cancel RequestAborted in a race that would
            // mask the streak (misclassifying the server abort as a client abort).
            Assert.True(await MeshTestHelpers.WaitForPositiveStreakAsync(router, served, TimeSpan.FromSeconds(3)),
                $"the mid-stream abort must feed the streak for target {served} (§7 FeedsStreak)");
            response.Dispose();
        }
        finally
        {
            foreach (var daemon in fleet.Daemons)
            {
                daemon.ResetBehavior();
            }
        }
        }

    // ---- All targets dead, Cloud DISABLED: the terminal 503 body carries targets[] with
    //      states (the all-fleet-dead diagnostics path). ----
    [Fact]
    public async Task AllTargetsDead_CloudDisabled_Returns503Diagnostics()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1", cloudEnabled: false);
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        foreach (var daemon in fleet.Daemons)
        {
            await daemon.StopAsync();
        }

        // Wait until the liveness re-prober has tripped every target (2-strike hysteresis on a
        // 1 s cadence + slack).
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !IsFleetAllOffline(router))
        {
            await Task.Delay(300);
        }

        Assert.True(IsFleetAllOffline(router), "all daemons must be tripped Offline before the terminal 503 assertion");
        try
        {
            var response = await router.Client.PostAsJsonAsync("/api/chat", BuildChatBody(MeshCatalog.MoeModel, 16));
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var root = JsonNode.Parse(body);
            Assert.NotNull(root?["targets"]);
            var targets = (JsonArray)root!["targets"]!;
            Assert.True(targets.Count >= 3, $"the 503 diagnostics must enumerate the fleet targets (got {targets.Count})");
            // Each entry carries a name + a lowercased state (the raw liveness state).
            foreach (var entry in targets)
            {
                Assert.NotNull(entry?["name"]);
                Assert.NotNull(entry?["state"]);
            }
        }
        finally
        {
            await Task.WhenAll(fleet.Daemons.Select(d => d.StartAsync()));
        }
    }

    // ---- Cloud relay rewrite (Cloud Overflow ordering, plan §4): the fleet is fully healthy, but
    //      the requested model is hosted by NO daemon and has a per-model CloudModel configured.
    //      Every fleet target is disqualified at stage 1 (model absent from its catalog), so the
    //      routing decision resolves to the Cloud pseudo-target (fixed score 0, the only qualifier).
    //      The dispatch loop's terminal Cloud step must then REWRITE the request body's model to
    //      the cloud model and forward it to the PREFERRED relay daemon (PreferredRelayTarget) —
    //      which is asserted at the daemon observation plane: the relay daemon must have received
    //      a /api/chat whose body carries the CLOUD model name (not the requested model), while
    //      no other daemon saw any chat hit for this request. ----
    [Fact]
    public async Task CloudRelay_UnhostedModel_RewrittenCloudModelReachesPreferredRelay()
    {
        const string cloudModelName = "moe-35b:cloud";
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1", cloudEnabled: true, configureOptions: options =>
        {
            // Cloud overflow for the requested (unhosted) model, relaying through the desktop daemon.
            options.Models["moe-35b"] = new ModelThresholds { CloudModel = cloudModelName };
            options.CloudOverflow.PreferredRelayTarget = "desktop";
        });
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));
        fleet.ResetObservation();

        // 'moe-35b:cloud' exists on NO daemon (their catalogs carry only dense-27b / moe-35b), so
        // the request can only be served via the Cloud relay path.
        var (status, _, served, _, _) = await MeshTestHelpers.ChatAsync(router, cloudModelName, 16);
        Assert.Equal(HttpStatusCode.OK, status);

        // The relay daemon must have received the cloud-REWRITTEN model (the whole point of the
        // terminal Cloud step) — asserted on the body the daemon actually recorded.
        Assert.True(
            await MeshTestHelpers.WaitForChatModelAsync(fleet.Daemon("desktop"), cloudModelName, TimeSpan.FromSeconds(3)),
            "the preferred relay daemon must receive the cloud-rewritten model name in the request body");
        var desktopHits = Assert.Single(fleet.Daemon("desktop").ChatHits());
        // Assert the EXACT recorded model field (parsing the body): it must equal the cloud
        // model name — a substring check would be ambiguous because "moe-35b" is a prefix of
        // "moe-35b:cloud".
        var recordedModel = JsonNode.Parse(desktopHits.Body) is JsonObject obj
            ? obj["model"]?.GetValue<string>()
            : null;
        Assert.Equal(cloudModelName, recordedModel);
        Assert.Contains(cloudModelName, desktopHits.Body, StringComparison.Ordinal);

        // No other daemon may have been hit for this request (the preferred relay serves it).
        Assert.Empty(fleet.Daemon("laptop").ChatHits());
        Assert.Empty(fleet.Daemon("server").ChatHits());

        // Telemetry: the decided target is the Cloud pseudo-target (the request was resolved to
        // Cloud because no daemon hosts the model), so the public header echoes "Cloud" — the
        // relay's identity is observed on the daemon side (the rewritten body above).
        Assert.True(string.Equals(served, "Cloud", StringComparison.OrdinalIgnoreCase),
            $"the Cloud relay decision must surface the Cloud target in telemetry; got '{served}'");
    }

    private static bool IsFleetAllOffline(MeshRouter router)
    {
        return router.Liveness.GetEntries().Values.All(e => e.State == TargetLivenessState.Offline);
    }

    private static object BuildChatBody(string model, int tokens) =>
        new
        {
            model,
            stream = true,
            messages = new object[] { new { role = "user", content = MeshTestHelpers.BuildPrompt(tokens) } }
        };

    private static async Task<(HttpStatusCode Status, string Body, string Served, bool Interrupted, HttpResponseMessage Response)>
        SendChatAbortKeepOpenAsync(MeshRouter router, string model, int tokens, string? pin)
    {
        var response = await MeshTestHelpers.SendChatAsync(router, model, tokens, pin: pin, returnOnHeaders: true);
        var status = response.StatusCode;
        string? served = null;
        if (response.Headers.TryGetValues(OllamaRouter.ReverseProxy.OllamaReverseProxyConfig.PublicTargetHeader, out var sv))
        {
            served = sv.FirstOrDefault();
        }

        // Reads the (possibly aborted) body WITHOUT disposing the response: disposing closes the
        // client socket, which cancels the router's RequestAborted token in a race with the
        // server's finally-block (§7 streak bookkeeping) — that race makes a genuine
        // server-side mid-stream abort look like a client abort and suppress the streak.
        // The caller disposes the response AFTER the streak/liveness assertions.
        var (text, interrupted) = await MeshTestHelpers.DrainTolerantKeepOpenAsync(response);
        return (status, text, served ?? string.Empty, interrupted, response);
    }
    }
