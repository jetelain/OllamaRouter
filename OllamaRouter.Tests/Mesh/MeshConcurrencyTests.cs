using System.Net;
using System.Text.Json.Nodes;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// High-concurrency load (plan §4 "Concurrency" row): mixed Architect (dense 27B) / Developer
/// (MoE) traffic at ≥ 2× Σ MaxParallel concurrent clients. A bounded smoke runs unconditionally
/// in the gate; the full 10 s sustained soak is opt-in via <c>OLLAMAROUTER_MESH_SOAK=1</c> so the
/// standard 180 s test budget is protected. Both assert that every request COMPLETES — either 2xx
/// (a served/failover response) or a well-formed 503 from the capacity-exhaustion path (a dense
/// request arriving when every dense-host target is at its MaxParallel in-flight cap is a
/// legitimate, well-formed refusal, not a routing defect) — and that the shared-state REST
/// invariants hold once the load settles (in-flight counters back to 0, no negative counters,
/// streaks ≥ 0, lease snapshots well-formed, catalog digests stable).
/// </summary>
[Collection("InProcessMeshFleet")]
public class MeshConcurrencyTests
{
    // ---- Bounded concurrency smoke (gate): ~3 s of mixed traffic at 2×+ concurrency; every
    //      request completes (2xx or a well-formed 503) and the shared-state REST invariants
    //      hold. ----
    [Fact]
    public async Task ConcurrencySmoke_MixedTraffic_AllComplete_RestStateConsistent()
    {
        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        // Σ MaxParallel = desktop(1) + laptop(1) + server(2) = 4; 2× that = 8 concurrent clients
        // (4 Architect/dense + 4 Developer/MoE). Dense capacity is only 3 (desktop 1 + server 2),
        // so a 4th concurrent dense request can legitimately hit the capacity-exhaustion 503 —
        // that is a well-formed refusal, counted as a completed request (not a failure).
        const int concurrency = 8;
        const int interRequestDelayMs = 200;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var tasks = new List<Task>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var completed = new System.Collections.Concurrent.ConcurrentBag<string>();

        for (var i = 0; i < concurrency; i++)
        {
            tasks.Add(RunClientAsync(router, i % 2 == 0, interRequestDelayMs, stop.Token, failures, completed));
        }

        await Task.WhenAll(tasks);
        var elapsed = sw.Elapsed;
        Assert.True(elapsed >= TimeSpan.FromSeconds(2), $"the smoke must run a sustained window (ran {elapsed.TotalSeconds:F1} s)");

        Assert.True(
            failures.Count == 0,
            $"every concurrent request must complete 2xx or with a well-formed 503; malformed failures: {string.Join("; ", failures.Distinct().Take(5))}");
        Assert.True(completed.Count > 0, "the load window must have served a non-trivial number of requests");

        // REST invariants (see AssertRestStateInvariants): the load has settled, so the shared
        // state must be consistent — no orphaned in-flight, no negative counters, streaks ≥ 0,
        // lease snapshots well-formed, catalog digests stable.
        await AssertRestStateInvariantsAsync(router);
    }

    // ---- Sustained soak (opt-in OLLAMAROUTER_MESH_SOAK=1): 10 s at higher concurrency with
    //      periodic (500 ms) MID-RUN invariant sampling (in-flight ≥ 0 everywhere, streaks ≥ 0,
    //      no deadlock) plus the same REST invariants at the end. Early-passes with a clear
    //      notice when the env var is unset. ----
    [Fact]
    public async Task ConcurrencySoak_SustainedLoad_MidRunAndRestInvariants()
    {
        var enabled =
            string.Equals(Environment.GetEnvironmentVariable("OLLAMAROUTER_MESH_SOAK"), "1", StringComparison.Ordinal);
        if (!enabled)
        {
            // Opt-in gate: the soak is a longer, resource-heavy run protected out of the
            // standard CI budget. A clear notice + early pass keeps the default run unaffected.
            Console.WriteLine("[SKIP] OLLAMAROUTER_MESH_SOAK is not set to 1 — the sustained 10 s concurrency soak is opt-in.");
            return;
        }

        await using var fleet = await InProcessMeshFleet.CreateAsync();
        var router = await fleet.CreateRouterAsync("R1");
        await MeshTestHelpers.WarmupAsync(router, MeshCatalog.MoeModel);
        await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(5));

        const int concurrency = 12;
        const int interRequestDelayMs = 500;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var completed = new System.Collections.Concurrent.ConcurrentBag<string>();
        var midRunViolations = new System.Collections.Concurrent.ConcurrentBag<string>();
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tasks = new List<Task>();

        for (var i = 0; i < concurrency; i++)
        {
            tasks.Add(RunClientAsync(router, i % 2 == 0, interRequestDelayMs, stop.Token, failures, completed));
        }

        // Periodic mid-run invariant sampling (every 500 ms): in-flight ≥ 0 everywhere, streaks ≥ 0.
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                SampleMidRunInvariants(router, midRunViolations);
                await Task.Delay(500, stop.Token);
            }
        }, stop.Token).ContinueWith(t => { });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(tasks);
        var elapsed = sw.Elapsed;
        stop.Cancel();
        try
        {
            await sampler;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        Assert.True(elapsed >= TimeSpan.FromSeconds(9), $"the soak must sustain ≥ 10 s (ran {elapsed.TotalSeconds:F1} s)");
        Assert.True(
            failures.Count == 0,
            $"the soak must not deadlock or drop requests; malformed failures: {string.Join("; ", failures.Distinct().Take(5))}");
        Assert.True(
            midRunViolations.Count == 0,
            $"mid-run invariants must hold throughout the soak; violations: {string.Join("; ", midRunViolations.Distinct().Take(5))}");

        await AssertRestStateInvariantsAsync(router);
    }

    private static async Task RunClientAsync(
        MeshRouter router,
        bool architect,
        int interRequestDelayMs,
        CancellationToken cancellationToken,
        System.Collections.Concurrent.ConcurrentBag<string> failures,
        System.Collections.Concurrent.ConcurrentBag<string> completed)
    {
        var model = architect ? MeshCatalog.DenseModel : MeshCatalog.MoeModel;
        var request = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            request++;
            string body;
            HttpStatusCode status;
            try
            {
                using var response = await router.Client
                    .PostAsync("/api/chat", new StringContent(
                        BuildChatBody(model), System.Text.Encoding.UTF8, "application/json"), cancellationToken);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures.Add($"{(architect ? "Architect" : "Developer")} req{request} exception {ex.GetType().Name}");
                continue;
            }

            if (status == HttpStatusCode.OK)
            {
                completed.Add($"2xx req{request}");
            }
            else if (status == HttpStatusCode.ServiceUnavailable && IsWellFormedErrorBody(body))
            {
                // A capacity-exhaustion refusal: the fleet genuinely has no eligible target
                // (every dense-host is at its MaxParallel cap) and the response is a well-formed
                // Ollama error payload. This is a COMPLETED request, not a defect.
                completed.Add($"503 req{request}");
            }
            else
            {
                failures.Add($"{(architect ? "Architect" : "Developer")} req{request} → {(int)status} body={Truncate(body)}");
            }

            try
            {
                await Task.Delay(interRequestDelayMs, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static bool IsWellFormedErrorBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(body) is JsonObject obj && obj["error"] is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildChatBody(string model)
    {
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = new System.Text.Json.Nodes.JsonArray()
            {
                new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = MeshTestHelpers.BuildPrompt(64) }
            }
        };
        return payload.ToJsonString();
    }

    private static string Truncate(string s) => s.Length <= 80 ? s : s[..80];

    private static void SampleMidRunInvariants(MeshRouter router, System.Collections.Concurrent.ConcurrentBag<string> violations)
    {
        // In-flight counters must never be negative (the tracker floors at 0, but a bug could
        // expose a bad value); streaks must never be negative either.
        foreach (var state in router.ExecutionTracker.GetAllStates())
        {
            if (state.InFlightRequests < 0)
            {
                violations.Add($"in-flight negative on {state.Target}: {state.InFlightRequests}");
            }
        }

        foreach (var (target, streak) in router.Streaks.GetSnapshot())
        {
            if (streak < 0)
            {
                violations.Add($"streak negative on {target}: {streak}");
            }
        }
    }

    private static async Task AssertRestStateInvariantsAsync(MeshRouter router)
    {
        // Let the in-flight decrements (issued in the middleware's finally) settle.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && HasInFlight(router))
        {
            await Task.Delay(100);
        }

        // 1. In-flight counters must rest at 0 (no orphaned requests; no negative counters).
        foreach (var state in router.ExecutionTracker.GetAllStates())
        {
            Assert.True(
                state.InFlightRequests == 0,
                $"in-flight must rest at 0 after the load settles; {state.Target} has {state.InFlightRequests}");
        }

        // 2. Streaks must be ≥ 0 (a bug that double-records or underflows would surface here).
        foreach (var (target, streak) in router.Streaks.GetSnapshot())
        {
            Assert.True(streak >= 0, $"failure streak must be ≥ 0; {target} = {streak}");
        }

        // 3. Lease snapshots must be well-formed: every view names a model and holds a
        //    positive bucket (no orphan/negative lease state).
        foreach (var (target, views) in router.ShrinkManager.GetLeaseSnapshot())
        {
            foreach (var view in views)
            {
                Assert.False(string.IsNullOrWhiteSpace(view.Model));
                Assert.True(view.ActiveBucket > 0, $"lease on {target}/{view.Model} must hold a positive bucket (got {view.ActiveBucket})");
            }
        }

        // 4. Catalog state is consistent: the per-target loaded-model view is stable across two
        //    consecutive reads (a torn update would flip this between back-to-back reads).
        var digestA = ReadCatalogDigest(router);
        var digestB = ReadCatalogDigest(router);
        Assert.Equal(digestA, digestB);
    }

    private static bool HasInFlight(MeshRouter router)
    {
        return router.ExecutionTracker.GetAllStates().Any(s => s.InFlightRequests > 0);
    }

    private static string ReadCatalogDigest(MeshRouter router)
    {
        return string.Join("|",
            router.ExecutionTracker.GetAllStates()
                .Select(s => $"{s.Target}:{s.LoadedModels.Count}"));
    }
}
