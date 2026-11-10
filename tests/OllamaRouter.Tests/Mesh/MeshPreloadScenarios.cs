using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// In-process mesh scenarios for the dedicated model pre-load API (task #44): a large
/// context intent lands on the capable server daemon (never a small dense desktop), and a
/// BUSY server (in-flight &gt; 0 through the real middleware dispatch) is bypassed in favor
/// of the idle capable desktop — the user's core requirement, end to end against real
/// in-process daemons (the <c>POST /api/generate</c> preload primitive, the <c>/api/ps</c>
/// residency reflection, and the real execution tracker).
/// </summary>
public class MeshPreloadScenarios
{
    private const long SmallDenseParams = 8_000_000_000L;

    private static DaemonModel SmallDense(string name, int? modelfileNumCtx) => new()
    {
        Name = name,
        SizeBytes = 5L * 1024 * 1024 * 1024,
        ParameterCount = SmallDenseParams,
        ExpertCount = 0,
        ModelfileNumCtx = modelfileNumCtx,
        Family = "llama"
    };

    private static JsonNode ReadJson(HttpResponseMessage response) =>
        JsonNode.Parse(response.Content.ReadAsStringAsync().Result)!;

    /// <summary>Bounded poll on a predicate (the mesh tests' standard settle pattern).</summary>
    private static async Task<bool> PollUntilAsync(Func<bool> predicate, TimeSpan timeout, TimeSpan? tick = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var interval = tick ?? TimeSpan.FromMilliseconds(50);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(interval);
        }

        return predicate();
    }

    [Fact]
    public async Task Preload_LargeContextIntent_LandsOnCapableServerDaemon()
    {
        // Desktop dense ceiling = 49,152 (Modelfile pin) < 260k + margin -> ineligible;
        // server dense ceiling = 270,000 -> the only eligible hoster.
        var desktop = new FakeOllamaDaemon("desktop", [SmallDense("preload-big:latest", 49_152)], capacity: 1) { MaxVramMB = 16_384 };
        var server = new FakeOllamaDaemon("server", [SmallDense("preload-big:latest", 270_000)], capacity: 2) { MaxVramMB = 98_304 };
        await Task.WhenAll(desktop.StartAsync(), server.StartAsync());
        await using var desktopD = desktop;
        await using var serverD = server;
        await using var router = await MeshRouter.StartAsync("R-preload", [desktop, server], cloudEnabled: false);

        Assert.True(await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(10)),
            "all daemons must come Online before preload is exercised");

        using var response = await router.Client.PostAsync(
            "/api/router/models/preload-big:latest/preload?contextIntentTokens=260000", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = ReadJson(response).AsObject()!;
        Assert.Equal("server", body["selectedTarget"]!.GetValue<string>());
        Assert.Equal(-1, body["keepAliveSeconds"]!.GetValue<int>()); // server class default (indefinite)
        Assert.Equal(260_000, body["numCtx"]!.GetValue<int>()); // min(intent, selected ceiling)
        Assert.True((bool)body["loadAccepted"]!);
        Assert.True((bool)body["residencyConfirmed"]!);
        var candidates = body["candidates"]!.AsArray();
        Assert.False((bool)candidates.First(c => c!["target"]!.GetValue<string>() == "desktop")["eligible"]!);

        // The load call hit ONLY the server daemon, with the exact payload, and the daemon's
        // /api/ps now reflects the residency.
        var payload = server.LastGeneratePayload("preload-big:latest");
        Assert.NotNull(payload);
        Assert.Equal(-1, payload!.Value.KeepAliveSeconds);
        Assert.Equal(260_000, payload.Value.NumCtx);
        Assert.Empty(desktop.GenerateHits());
        Assert.True(await MeshTestHelpers.WaitForLoadedAsync(router, "server", "preload-big:latest", TimeSpan.FromSeconds(5)),
            "the server daemon /api/ps must list the preloaded model");
    }

    [Fact]
    public async Task Preload_BusyServer_IsBypassedInFavorOfIdleCapableDesktop()
    {
        // Both daemons host the small model with equal (fallback 128k) ceilings; the server
        // is made busy by a pinned background chat that hangs (ChatMode.Hang holds the
        // dispatch open so the REAL execution tracker keeps the server in-flight > 0).
        var desktop = new FakeOllamaDaemon("desktop", [SmallDense("preload-busy:latest", null)], capacity: 1) { MaxVramMB = 16_384 };
        var server = new FakeOllamaDaemon("server", [SmallDense("preload-busy:latest", null)], capacity: 2) { MaxVramMB = 98_304 };
        await Task.WhenAll(desktop.StartAsync(), server.StartAsync());
        await using var desktopD = desktop;
        await using var serverD = server;
        await using var router = await MeshRouter.StartAsync("R-preload-busy", [desktop, server], cloudEnabled: false);

        Assert.True(await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(10)),
            "all daemons must come Online before preload is exercised");

        // The background pinned chat hangs on the server for ~8 s: in-flight stays > 0 across
        // the preload request window.
        server.SetChatBehavior(ChatMode.Hang, 8000);
        var background = MeshTestHelpers.SendChatAsync(router, "preload-busy:latest", 16, pin: "server");
        Assert.True(await PollUntilAsync(
            () => router.ExecutionTracker.GetState(new RoutingTarget("server"))?.InFlightRequests is > 0,
            TimeSpan.FromSeconds(15)),
            "the pinned background chat must observe server in-flight > 0");

        try
        {
            using var response = await router.Client.PostAsync(
                "/api/router/models/preload-busy:latest/preload?contextIntentTokens=8192", content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = ReadJson(response).AsObject()!;

            // The core busy-bypass: the busy server (eligible) is skipped in favor of the idle
            // capable desktop.
            Assert.Equal("desktop", body["selectedTarget"]!.GetValue<string>());
            Assert.Equal(900, body["keepAliveSeconds"]!.GetValue<int>()); // desktop class default (15 min)
            var serverCandidate = body["candidates"]!.AsArray()
                .First(c => c!["target"]!.GetValue<string>() == "server");
            Assert.True((bool)serverCandidate["eligible"]!);
            Assert.True((bool)serverCandidate["busy"]!);

            Assert.Empty(server.GenerateHits()); // the load never hit the busy node
            var payload = desktop.LastGeneratePayload("preload-busy:latest");
            Assert.NotNull(payload);
            Assert.Equal(900, payload!.Value.KeepAliveSeconds);
        }
        finally
        {
            try
            {
                await background;
            }
            catch
            {
                // The hang completes on its own (8 s); a cancelled read must not mask the outcome.
            }
        }
    }

    [Fact]
    public async Task Preload_SlowGeneratePastOldFourSecondBound_IsAcceptedUnderDefaultTwentySecondLoadBound()
    {
        // AC2 E2E (task #75): the fake daemon answers POST /api/generate after ~5 s (a
        // large-model cold-load simulation) — comfortably past the OLD 4 s catalog client bound,
        // well inside the default PreloadLoadTimeoutSeconds (20 s). With default options the
        // preload must 200 with loadAccepted=true (under the old 4 s bound it would have been
        // accepted=false + the HttpClient.Timeout cancellation message) and the residency check
        // (still on the 4 s catalog client) must confirm the model.
        var server = new FakeOllamaDaemon("server", [SmallDense("preload-slow:latest", 270_000)], capacity: 2) { MaxVramMB = 98_304 };
        await server.StartAsync();
        await using var serverD = server;
        await using var router = await MeshRouter.StartAsync("R-preload-slow", [server], cloudEnabled: false);

        Assert.True(await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(10)),
            "all daemons must come Online before preload is exercised");

        server.SetGenerateDelay(TimeSpan.FromSeconds(5));

        var started = Environment.TickCount64;
        using var response = await router.Client.PostAsync(
            "/api/router/models/preload-slow:latest/preload?contextIntentTokens=260000", content: null);
        var elapsedMs = Environment.TickCount64 - started;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = ReadJson(response).AsObject()!;
        Assert.Equal("server", body["selectedTarget"]!.GetValue<string>());
        Assert.True((bool)body["loadAccepted"]!); // would be false under the old 4 s bound
        Assert.True((bool)body["residencyConfirmed"]!); // the /api/ps check keeps the 4 s client bound
        Assert.InRange(elapsedMs, 4500, 30_000); // the ~5 s slow load completed (not canceled at 4 s)

        server.ResetBehavior();
    }
}
