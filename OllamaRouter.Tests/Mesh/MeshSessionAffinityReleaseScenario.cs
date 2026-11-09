using System.Net;
using System.Text.Json.Nodes;
using OllamaRouter.ReverseProxy;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// Deterministic in-process scenario (task #86): a sticky session is pinned to a target via
/// the public <c>X-OllamaRouter-Session-Id</c> header (the honored pin creates the lease; the
/// next headerless-pin request rides the lease), the NEW
/// <c>POST /api/router/sessions/affinity/release</c> endpoint releases the lease
/// (<c>released: true</c>), the session's next request routes FRESH (the faster-configured
/// target — SpeedFactor desktop 1.5 / server 2.0 — wins once the SessionAffinityBonus is
/// gone, flipping the served target), re-pins to the new lease (the following request is
/// sticky to the NEW target), the idempotent repeat returns <c>released: false</c>, and a
/// headerless release 400s with the exact documented error body. All chats flow through the
/// full production pipeline against two fake daemons; the served target is read from the
/// response <c>X-OllamaRouter-Target</c> telemetry header.
/// </summary>
public class MeshSessionAffinityReleaseScenario
{
    private const string Model = "affinity-release:latest";
    private const string SessionId = "sess-86";

    private static DaemonModel ModelFixture() => new()
    {
        Name = Model,
        SizeBytes = 5L * 1024 * 1024 * 1024,
        ParameterCount = 8_000_000_000L, // small dense: routable on both fixture daemons.
        ExpertCount = 0,
        Family = "llama",
        ContextLength = 4096
    };

    [Fact]
    public async Task StickySession_Release_RoutesFresh_Repins_RepeatIsIdempotent()
    {
        var desktop = new FakeOllamaDaemon("desktop", [ModelFixture()], capacity: 2)
        {
            MaxVramMB = 16_384
        };
        var server = new FakeOllamaDaemon("server", [ModelFixture()], capacity: 2)
        {
            MaxVramMB = 16_384
        };
        await using var desktopD = desktop;
        await using var serverD = server;
        await desktop.StartAsync();
        await server.StartAsync();
        await using var router = await MeshRouter.StartAsync(
            "R-affinity-release",
            [desktop, server],
            cloudEnabled: false,
            configureOptions: options =>
            {
                // Deterministic per-decision SpeedBonus post-pass (maxHint = 2.0):
                // desktop = round(150 × 0.5 / 1.0) = +75, server = round(150 × 1.0 / 1.0) = +150.
                foreach (var target in options.Targets)
                {
                    target.SpeedFactor = target.Name == "desktop" ? 1.5 : 2.0;
                }
            });

        Assert.True(await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(10)),
            "both daemons must come Online before the requests are sent");
        await MeshTestHelpers.WarmupAsync(router, Model);

        // One PINNED warm-up chat per target so BOTH targets host the model (the hot
        // AffinityBonus +100 is symmetric below); force the tracker snapshot so the scoring
        // sees both models as loaded (the 2 s background refresh must not gate the scenario).
        foreach (var pin in new[] { "desktop", "server" })
        {
            var response = await MeshTestHelpers.SendChatAsync(router, Model, 8, pin: pin);
            await MeshTestHelpers.DrainAsync(response);
            await router.ExecutionTracker.RefreshAsync(CancellationToken.None);
        }

        // Chat A: the honored pin to "desktop" serves desktop AND creates sess-86's lease.
        var (statusA, servedA) = await ChatAsync(router, pin: "desktop", session: SessionId);
        Assert.Equal(HttpStatusCode.OK, statusA);
        Assert.Equal("desktop", servedA);

        // Chat B: NO pin, same session → sticky to the lease: desktop
        // 75 (speed) + 150 (session) + 100 (hot) [ +20 hardware bonus, if qualified ] vs
        // server 150 + 0 + 100 — margin ≥ 75 either way.
        await router.ExecutionTracker.RefreshAsync(CancellationToken.None);
        var (statusB, servedB) = await ChatAsync(router, pin: null, session: SessionId);
        Assert.Equal(HttpStatusCode.OK, statusB);
        Assert.True(servedB == "desktop",
            $"the session must be sticky to its leased target, but was served '{servedB}'");

        // Release #1: the live lease is found and removed.
        using var release1 = await ReleaseAsync(router.Client, SessionId);
        Assert.Equal(HttpStatusCode.OK, release1.StatusCode);
        var release1Body = JsonNode.Parse(await release1.Content.ReadAsStringAsync())!;
        Assert.Equal(SessionId, release1Body["sessionId"]!.ToString());
        Assert.True(release1Body["released"]!.GetValue<bool>(), "a live lease must report released:true");

        // Release #2 (immediate repeat): idempotent → released:false.
        using var release2 = await ReleaseAsync(router.Client, SessionId);
        Assert.Equal(HttpStatusCode.OK, release2.StatusCode);
        var release2Body = JsonNode.Parse(await release2.Content.ReadAsStringAsync())!;
        Assert.Equal(SessionId, release2Body["sessionId"]!.ToString());
        Assert.False(release2Body["released"]!.GetValue<bool>(), "the repeat must be idempotent (released:false)");

        // Chat C: NO pin, same session → FRESH decision (lease gone): desktop 75 + 100 [ +20 ]
        // vs server 150 + 100 — server wins by ≥ 55; the decision service re-leases
        // sess-86 → server.
        await router.ExecutionTracker.RefreshAsync(CancellationToken.None);
        var (statusC, servedC) = await ChatAsync(router, pin: null, session: SessionId);
        Assert.Equal(HttpStatusCode.OK, statusC);
        Assert.True(servedC == "server",
            $"with the lease released the faster-configured target must win the fresh decision, but was served '{servedC}'");

        // Chat D: NO pin, same session → sticky to the NEW lease (re-pinning after release is
        // un-suppressed): server 150 (speed) + 150 (session) + 100 (hot) vs desktop 75 + 100 [ +20 ].
        await router.ExecutionTracker.RefreshAsync(CancellationToken.None);
        var (statusD, servedD) = await ChatAsync(router, pin: null, session: SessionId);
        Assert.Equal(HttpStatusCode.OK, statusD);
        Assert.True(servedD == "server",
            $"the session must be sticky to its NEW lease, but was served '{servedD}'");

        // Release WITHOUT the header → 400 with the exact documented error body.
        using var releaseMissing = await ReleaseAsync(router.Client, sessionId: null);
        Assert.Equal(HttpStatusCode.BadRequest, releaseMissing.StatusCode);
        var missingBody = JsonNode.Parse(await releaseMissing.Content.ReadAsStringAsync())!;
        Assert.Equal("X-OllamaRouter-Session-Id header is required", missingBody["error"]!.ToString());
    }

    /// <summary>Sends a chat (optional pin / session header) and returns (status, served target header).</summary>
    private static async Task<(HttpStatusCode Status, string? Served)> ChatAsync(
        MeshRouter router,
        string? pin = null,
        string? session = null)
    {
        var response = await MeshTestHelpers.SendChatAsync(router, Model, 8, pin: pin, sessionId: session);
        var status = response.StatusCode;
        string? served = null;
        if (response.Headers.TryGetValues(OllamaReverseProxyConfig.PublicTargetHeader, out var values))
        {
            served = values.FirstOrDefault();
        }

        await MeshTestHelpers.DrainAsync(response);
        return (status, served);
    }

    /// <summary>Posts the release endpoint; a null <paramref name="sessionId"/> omits the header.</summary>
    private static async Task<HttpResponseMessage> ReleaseAsync(HttpClient client, string? sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/router/sessions/affinity/release");
        if (sessionId is not null)
        {
            request.Headers.Add(OllamaReverseProxyConfig.PublicSessionIdHeader, sessionId);
        }

        return await client.SendAsync(request);
    }
}
