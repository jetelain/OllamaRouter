using System.Net;
using System.Text.Json.Nodes;
using OllamaRouter.Options;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// Deterministic in-process scenario (task #83): a native-NDJSON request served by an Ollama
/// target that reports a prompt-cache hit (<c>prompt_eval_cached_count</c> in the final frame,
/// opt-in via <see cref="FakeOllamaDaemon.PromptEvalCachedCount"/>) flows end-to-end through the
/// full production pipeline into the monitor surface: the target's per-period statistics carry
/// <c>cachedInputTokens</c> (a subset of <c>inputTokens</c>), the recent-request row carries it
/// too, and the <c>cost</c> status values the usage with the cached split when
/// <c>Pricing:CachedPromptPricePerMillion</c> is configured.
/// </summary>
public class MeshCachedTokenCostScenario
{
    private const string Model = "cached-cost:latest";

    private static DaemonModel ModelFixture() => new()
    {
        Name = Model,
        SizeBytes = 5L * 1024 * 1024 * 1024,
        ParameterCount = 8_000_000_000L, // small dense: routable on the single fixture daemon.
        ExpertCount = 0,
        Family = "llama",
        ContextLength = 4096
    };

    private static JsonNode? PeriodStat(JsonNode document, string name, string period) =>
        document["statistics"]![period]![name];

    [Fact]
    public async Task MonitorApi_CachedPromptHit_FlowsIntoStatsAndCost()
    {
        // The daemon reports a prompt-cache hit on its terminal NDJSON frame: prompt_eval_count
        // = 12, prompt_eval_cached_count = 10 (opt-in flag), eval_count = 7.
        var desktop = new FakeOllamaDaemon("desktop", [ModelFixture()], capacity: 1)
        {
            MaxVramMB = 16_384,
            PromptEvalCachedCount = 10
        };
        await using var desktopD = desktop;
        await desktop.StartAsync();
        await using var router = await MeshRouter.StartAsync(
            "R-cached-cost",
            [desktop],
            cloudEnabled: false,
            configureOptions: options => options.Pricing = new PricingOptions
            {
                PromptPricePerMillion = 0.20,
                CompletionPricePerMillion = 0.80,
                CachedPromptPricePerMillion = 0.05
            });

        Assert.True(await MeshTestHelpers.WaitAllOnlineAsync(router, TimeSpan.FromSeconds(10)),
            "the desktop daemon must come Online before the request is sent");

        // One pinned native chat request; the daemon's cached tail is captured by the router.
        var chat = await MeshTestHelpers.SendChatAsync(router, Model, 8, pin: "desktop");
        await MeshTestHelpers.DrainAsync(chat);

        // Bounded poll (standard mesh settle pattern): re-fetch /monitor/api until the target's
        // last-7-days statistics carry the cached count (input 12 / cached 10 / output 7), the
        // recent-request row carries it too, and the cost status values the cached usage
        // (minus the observed-elapsed electricity term, task #84).
        var settled = await PollMonitorAsync(router, doc =>
        {
            var stat = PeriodStat(doc, "desktop", "last7Days");
            if (stat is null)
            {
                return false;
            }

            return stat["inputTokens"]?.GetValue<int>() == 12
                && stat["cachedInputTokens"]?.GetValue<int>() == 10
                && stat["outputTokens"]?.GetValue<int>() == 7
                && stat["elapsedMs"] is not null
                && FirstRequestCached(doc) == 10
                && CostMatchesCachedSplit(doc, stat);
        });

        Assert.True(settled, "the /monitor/api target statistics must carry cachedInputTokens=10 " +
            "(a subset of inputTokens=12) and the cumulative elapsedMs, the recent-request row " +
            "must carry cachedInputTokens=10, and the cost status must value the cached usage " +
            "minus the electricity term");
    }

    // The cost object is present and values the "desktop" usage with the cached split
    // ((12 − 10)/1e6 × 0.20) + (10/1e6 × 0.05) + (7/1e6 × 0.80) = 0.0000065 → 0.0 at the
    // production 4-decimal rounding for this 12/10/7 scale (the exact-value formula rows are
    // asserted in the unit cost-math table; a larger-scale live check is the E2E scenario).
    // Task #84: the ACTUAL savings additionally subtract the observed-elapsed electricity term
    // (150 W / 0.20 per-kWh effective defaults of this fixture — the fixture configures neither
    // PowerWatts nor the electricity knobs), computed from the SAME document's elapsedMs so the
    // assertion stays deterministic.
    private static bool CostMatchesCachedSplit(JsonNode document, JsonNode desktopStat)
    {
        var cost = document["cost"];
        if (cost is null)
        {
            return false;
        }

        var elapsedMs = desktopStat["elapsedMs"]?.GetValue<double>() ?? 0.0;
        var electricity = Math.Round(
            elapsedMs / 3_600_000.0 * PricingOptions.DefaultPowerWatts / 1000.0 * PricingOptions.DefaultElectricityPricePerKwh,
            4);
        // Token value rounds to 0.0 at this scale: savings = round(0.0 − electricity, 4).
        var expectedSavings = Math.Round(-electricity, 4);

        return cost["estimatedSavings"]?.GetValue<double>() == expectedSavings
            && cost["perTargetSavings"]?["desktop"]?.GetValue<double>() == expectedSavings
            && cost["electricityCost"]?.GetValue<double>() == electricity
            && (cost["perTargetElectricityCost"]?["desktop"]?.GetValue<double>() ?? double.NaN) == electricity;
    }

    // The cached count on the FIRST (most recent) request row, or null when absent/unknown.
    private static int? FirstRequestCached(JsonNode document)
    {
        var requests = document["requests"] as JsonArray;
        if (requests is { Count: > 0 })
        {
            return requests[0]?["cachedInputTokens"]?.GetValue<int>();
        }

        return null;
    }

    private static async Task<bool> PollMonitorAsync(MeshRouter router, Func<JsonNode, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var response = await router.Client.GetAsync("/monitor/api");
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
