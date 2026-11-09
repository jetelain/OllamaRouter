using System.Net;
using System.Text.Json.Nodes;
using OllamaRouter.Options;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// Deterministic in-process scenario (task #85): chats carrying the public
/// <c>X-OllamaRouter-Session-Id</c> header flow end-to-end through the full production pipeline
/// into the NEW per-session usage &amp; savings API: the bounded in-memory store accumulates
/// per-target token counters + GPU time for the session, the list endpoint reports the
/// most-recently-active session (the headerless request created NOTHING), the detail endpoint
/// reports the per-target breakdown whose cost figures equal the monitor's exact cost math
/// (cached-token pricing per #83 + electricity subtraction per #84) computed from the returned
/// elapsedMs, an unknown session 404s, and a shrunk <c>SessionUsageMaxSessions</c> cap evicts
/// the least-recently-active session.
/// </summary>
public class MeshSessionUsageScenario
{
    private const string Model = "session-usage:latest";

    private static DaemonModel ModelFixture() => new()
    {
        Name = Model,
        SizeBytes = 5L * 1024 * 1024 * 1024,
        ParameterCount = 8_000_000_000L, // small dense: routable on both fixture daemons.
        ExpertCount = 0,
        Family = "llama",
        ContextLength = 4096
    };

    private static PricingOptions ScenarioPricing() => new()
    {
        PromptPricePerMillion = 0.20,
        CompletionPricePerMillion = 0.80,
        CachedPromptPricePerMillion = 0.05
    };

    [Fact]
    public async Task SessionedChats_AccumulatePerSession_WithMonitorParityCosts_HeaderlessCreatesNoEntry()
    {
        var desktop = new FakeOllamaDaemon("desktop", [ModelFixture()], capacity: 1)
        {
            MaxVramMB = 16_384,
            PromptEvalCachedCount = 10
        };
        var server = new FakeOllamaDaemon("server", [ModelFixture()], capacity: 1)
        {
            MaxVramMB = 16_384
        };
        await using var desktopD = desktop;
        await using var serverD = server;
        await desktop.StartAsync();
        await server.StartAsync();
        await using var router = await MeshRouter.StartAsync(
            "R-session-usage",
            [desktop, server],
            cloudEnabled: false,
            configureOptions: options => options.Pricing = ScenarioPricing());

        Assert.True(await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(10)),
            "both daemons must come Online before the requests are sent");
        await MeshTestHelpers.WarmupAsync(router, Model); // headerless warm-up: must never create a session entry

        // Two pinned chats to "desktop" + one pinned chat to "server", ALL with the same public
        // session ID; then one chat with NO session header (it must not appear in the store).
        for (var i = 0; i < 2; i++)
        {
            var response = await MeshTestHelpers.SendChatAsync(router, Model, 8, pin: "desktop", sessionId: "sess-85");
            await MeshTestHelpers.DrainAsync(response);
        }
        var chat = await MeshTestHelpers.SendChatAsync(router, Model, 8, pin: "server", sessionId: "sess-85");
        await MeshTestHelpers.DrainAsync(chat);
        var headerless = await MeshTestHelpers.SendChatAsync(router, Model, 8);
        await MeshTestHelpers.DrainAsync(headerless);

        // Bounded poll (standard mesh settle pattern): the completion bookkeeping runs in the
        // middleware's finally block, which can lag the client-side drain.
        var settled = await PollSessionsAsync(router, doc =>
        {
            if (doc["count"]?.GetValue<int>() != 1 || doc["sessions"]?.AsArray().Count != 1)
            {
                return false;
            }

            var session = doc["sessions"]!.AsArray()[0];
            return session!["sessionId"]?.ToString() == "sess-85"
                && session["requests"]?.GetValue<int>() == 3 // 2 desktop + 1 server; the list rows carry the session totals
                && session["cachedInputTokens"]?.GetValue<int>() == 20;
        });
        Assert.True(settled, "the session list must contain EXACTLY the one session (the headerless " +
            "request created nothing) with desktop = 2 requests and server = 1 request");

        // Detail endpoint: per-target rows + cost parity with the monitor formulas, computed
        // from the SAME document's returned values (deterministic, the #83 pattern).
        using var detailResponse = await router.Client.GetAsync("/api/router/monitor/sessions/sess-85");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = JsonNode.Parse(await detailResponse.Content.ReadAsStringAsync())!;
        Assert.Equal("sess-85", detail["sessionId"]!.ToString());
        Assert.NotNull(detail["currency"]);

        var perTarget = detail["perTarget"]!.AsArray();
        var desktopRow = perTarget.FirstOrDefault(n => n!["target"]?.ToString() == "desktop")!;
        var serverRow = perTarget.FirstOrDefault(n => n!["target"]?.ToString() == "server")!;
        Assert.Equal(2, desktopRow["requests"]!.GetValue<int>());
        Assert.Equal(10 * 2, desktopRow["cachedInputTokens"]!.GetValue<int>()); // 10 cached per chat (opt-in)
        Assert.Equal(1, serverRow["requests"]!.GetValue<int>());
        Assert.Equal(0, serverRow["cachedInputTokens"]!.GetValue<int>());

        foreach (var row in new[] { desktopRow, serverRow })
        {
            Assert.True(CostRowMatchesMonitorFormulas(row));
        }

        // Top-level totals = sums of the per-target rows; the Q1 = A identities hold for this
        // Cloud-free session (savings == cloudValue − electricity exactly, both 4-decimal).
        Assert.Equal(desktopRow["requests"]!.GetValue<int>() + serverRow["requests"]!.GetValue<int>(), detail["requests"]!.GetValue<int>());
        Assert.Equal(desktopRow["inputTokens"]!.GetValue<double>() + serverRow["inputTokens"]!.GetValue<double>(), detail["inputTokens"]!.GetValue<double>(), 4);
        Assert.Equal(desktopRow["cachedInputTokens"]!.GetValue<double>() + serverRow["cachedInputTokens"]!.GetValue<double>(), detail["cachedInputTokens"]!.GetValue<double>(), 4);
        Assert.Equal(desktopRow["outputTokens"]!.GetValue<double>() + serverRow["outputTokens"]!.GetValue<double>(), detail["outputTokens"]!.GetValue<double>(), 4);
        Assert.Equal(desktopRow["elapsedMs"]!.GetValue<double>() + serverRow["elapsedMs"]!.GetValue<double>(), detail["elapsedMs"]!.GetValue<double>(), 4);
        Assert.Equal(Math.Round(desktopRow["cloudValue"]!.GetValue<double>() + serverRow["cloudValue"]!.GetValue<double>(), 4), detail["cloudValue"]!.GetValue<double>(), 4);
        Assert.Equal(Math.Round(desktopRow["electricityCost"]!.GetValue<double>() + serverRow["electricityCost"]!.GetValue<double>(), 4), detail["electricityCost"]!.GetValue<double>(), 4);
        Assert.Equal(Math.Round(desktopRow["savings"]!.GetValue<double>() + serverRow["savings"]!.GetValue<double>(), 4), detail["savings"]!.GetValue<double>(), 4);

        // Unknown session ⇒ 404.
        using var unknown = await router.Client.GetAsync("/api/router/monitor/sessions/unknown-85");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ShrunkenSessionUsageCap_EvictsLeastRecentlyActive()
    {
        var node1 = new FakeOllamaDaemon("node1", [ModelFixture()], capacity: 2)
        {
            MaxVramMB = 16_384
        };
        await using var node1D = node1;
        await node1.StartAsync();
        await using var router = await MeshRouter.StartAsync(
            "R-session-usage-cap",
            [node1],
            cloudEnabled: false,
            configureOptions: options =>
            {
                options.Pricing = ScenarioPricing();
                options.SessionUsageMaxSessions = 2;
            });

        Assert.True(await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(10)),
            "the daemon must come Online before the requests are sent");

        foreach (var id in new[] { "s-a", "s-b", "s-c" })
        {
            var response = await MeshTestHelpers.SendChatAsync(router, Model, 8, pin: "node1", sessionId: id);
            await MeshTestHelpers.DrainAsync(response);
        }

        var settled = await PollSessionsAsync(router, doc =>
        {
            var sessions = doc["sessions"]?.AsArray();
            return sessions is { Count: 2 }
                && sessions[0]!["sessionId"]?.ToString() == "s-c"
                && sessions[1]!["sessionId"]?.ToString() == "s-b";
        });
        Assert.True(settled, "with SessionUsageMaxSessions = 2, the first session (s-a) must be " +
            "evicted and the list must read [s-c, s-b] (most-recently-active first)");

        using var evicted = await router.Client.GetAsync("/api/router/monitor/sessions/s-a");
        Assert.Equal(HttpStatusCode.NotFound, evicted.StatusCode);
    }

    // Verifies one per-target row's cost figures against the SAME monitor formulas the
    // production endpoint uses (the fixture configures neither PowerWatts nor the electricity
    // knobs, so the 150 W / 0.20 effective defaults apply), computed from the row's own
    // returned token counts + elapsedMs so the assertion stays deterministic.
    private static bool CostRowMatchesMonitorFormulas(JsonNode row)
    {
        var input = row["inputTokens"]!.GetValue<double>();
        var cached = row["cachedInputTokens"]!.GetValue<double>();
        var output = row["outputTokens"]!.GetValue<double>();
        var elapsedMs = row["elapsedMs"]!.GetValue<double>();

        var cloudValue = Math.Round((input - cached) / 1_000_000.0 * 0.20
            + cached / 1_000_000.0 * 0.05
            + output / 1_000_000.0 * 0.80, 4);
        var electricity = Math.Round(
            elapsedMs / 3_600_000.0 * PricingOptions.DefaultPowerWatts / 1000.0 * PricingOptions.DefaultElectricityPricePerKwh,
            4);
        var savings = Math.Round(cloudValue - electricity, 4);

        return row["cloudValue"]?.GetValue<double>() == cloudValue
            && row["electricityCost"]?.GetValue<double>() == electricity
            && row["savings"]?.GetValue<double>() == savings;
    }

    private static async Task<bool> PollSessionsAsync(MeshRouter router, Func<JsonNode, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var response = await router.Client.GetAsync("/api/router/monitor/sessions");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var document = JsonNode.Parse(await response.Content.ReadAsStringAsync());
                if (document is not null && predicate(document))
                {
                    return true;
                }
            }

            await Task.Delay(250);
        }

        return false;
    }
}
