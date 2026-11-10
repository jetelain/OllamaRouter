using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.Sessions;
using OllamaRouter.Options;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Endpoints;

public class RouterSessionUsageEndpointsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(WebApplication app, HttpClient client, TestOptionsMonitor<OllamaRouterOptions> options, SessionUsageStore store)
        {
            App = app;
            Client = client;
            Options = options;
            Store = store;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public TestOptionsMonitor<OllamaRouterOptions> Options { get; }
        public SessionUsageStore Store { get; }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static async Task<Harness> CreateAppAsync(OllamaRouterOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var optionsMonitor = new TestOptionsMonitor<OllamaRouterOptions>(options);
        var store = new SessionUsageStore(optionsMonitor, NullLogger<SessionUsageStore>.Instance);

        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(optionsMonitor);
        builder.Services.AddSingleton<ISessionUsageStore>(store);

        var app = builder.Build();
        app.MapRouterSessionUsageEndpoints();
        await app.StartAsync();

        return new Harness(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, optionsMonitor, store);
    }

    private static OllamaRouterOptions OptionsWithPricing() => new()
    {
        Targets =
        [
            new OllamaTargetOptions { Name = "Desktop", Url = "http://127.0.0.1:11435" },
            new OllamaTargetOptions { Name = "Server", Url = "http://192.168.1.10:11434" }
        ],
        Pricing = new PricingOptions
        {
            PromptPricePerMillion = 0.20,
            CompletionPricePerMillion = 0.80,
            CachedPromptPricePerMillion = 0.05,
            Currency = "$"
        }
    };

    private static ActivityLogEntry Entry(
        string sessionId,
        RoutingTarget target,
        int estimated = 10,
        int? actualPrompt = null,
        int? actualResponse = null,
        long elapsedMs = 100,
        int? cached = null,
        DateTimeOffset? timestamp = null) => new(
        timestamp ?? T0,
        target,
        "model",
        estimated,
        actualPrompt,
        actualResponse,
        elapsedMs,
        200,
        true,
        SessionId: sessionId,
        CachedInputTokens: cached);

    // ------------------------------------------------------------------
    //  GET /api/router/monitor/sessions (list)
    // ------------------------------------------------------------------

    [Fact]
    public async Task List_EmptyStore_Returns200_WithEmptyShape_AndOmittedCostFields()
    {
        await using var harness = await CreateAppAsync(new OllamaRouterOptions()); // no pricing

        using var response = await harness.Client.GetAsync("/api/router/monitor/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(0, node["count"]!.GetValue<int>());
        Assert.Empty(node["sessions"]!.AsArray());
        Assert.Null(node["currency"]); // omitted (no token price rate configured)
    }

    [Fact]
    public async Task List_SeededStore_ReturnsMostRecentlyActiveFirst_WithSumsAndCostFields()
    {
        await using var harness = await CreateAppAsync(OptionsWithPricing());
        harness.Store.Record(Entry("s1", (RoutingTarget)"Desktop", actualPrompt: 10, actualResponse: 5, cached: 2, elapsedMs: 100));
        harness.Store.Record(Entry("s2", (RoutingTarget)"Server", actualPrompt: 20, actualResponse: 7, elapsedMs: 200));
        harness.Store.Record(Entry("s1", (RoutingTarget)"Server", actualPrompt: 30, actualResponse: 11, cached: 5, elapsedMs: 300));

        using var response = await harness.Client.GetAsync("/api/router/monitor/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("$", node["currency"]!.ToString());
        Assert.Equal(2, node["count"]!.GetValue<int>());
        var sessions = node["sessions"]!.AsArray();
        Assert.Equal("s1", sessions[0]!["sessionId"]!.ToString()); // s1 recorded LAST ⇒ MRU first
        Assert.Equal("s2", sessions[1]!["sessionId"]!.ToString());

        var s1 = sessions[0]!.AsObject();
        Assert.Equal(2, s1["requests"]!.GetValue<int>());
        Assert.Equal(40, s1["inputTokens"]!.GetValue<int>());
        Assert.Equal(7, s1["cachedInputTokens"]!.GetValue<int>());
        Assert.Equal(16, s1["outputTokens"]!.GetValue<int>());
        Assert.Equal(400.0, s1["elapsedMs"]!.GetValue<double>());
        Assert.NotNull(s1["lastSeenAt"]);
        Assert.NotNull(s1["cloudValue"]);
        Assert.NotNull(s1["electricityCost"]);
        Assert.NotNull(s1["savings"]);
        Assert.NotNull(s1["actualCloudCost"]);
        Assert.NotNull(s1["cost"]);

        // Cloud-free identity: neither session used the Cloud target ⇒ actualCloudCost 0.0
        // and cost == electricityCost (round(0.0 + electricity, 4) == the 4-dec electricity).
        Assert.Equal(0.0, s1["actualCloudCost"]!.GetValue<double>());
        Assert.Equal(s1["electricityCost"]!.GetValue<double>(), s1["cost"]!.GetValue<double>());
        var s2Row = sessions[1]!.AsObject();
        Assert.Equal(0.0, s2Row["actualCloudCost"]!.GetValue<double>());
        Assert.Equal(s2Row["electricityCost"]!.GetValue<double>(), s2Row["cost"]!.GetValue<double>());
    }

    // ------------------------------------------------------------------
    //  GET /api/router/monitor/sessions/{sessionId} (detail)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Detail_UnknownSession_Returns404_WithSessionNotFoundBody()
    {
        await using var harness = await CreateAppAsync(OptionsWithPricing());

        using var response = await harness.Client.GetAsync("/api/router/monitor/sessions/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("session not found: does-not-exist", node["error"]!.ToString());
    }

    [Fact]
    public async Task Detail_ReturnsPerTargetBreakdown_FleetOrder_CloudLast_CaseInsensitive()
    {
        await using var harness = await CreateAppAsync(OptionsWithPricing());
        harness.Store.Record(Entry("sess-85", (RoutingTarget)"Server", actualPrompt: 20, actualResponse: 7, elapsedMs: 200));
        harness.Store.Record(Entry("sess-85", RoutingTarget.Cloud, actualPrompt: 10, actualResponse: 2, elapsedMs: 50));
        harness.Store.Record(Entry("sess-85", (RoutingTarget)"Desktop", actualPrompt: 10, actualResponse: 5, cached: 2, elapsedMs: 100));
        harness.Store.Record(Entry("sess-85", (RoutingTarget)"Removed", actualPrompt: 4, actualResponse: 1, elapsedMs: 25)); // historical

        using var response = await harness.Client.GetAsync("/api/router/monitor/sessions/SESS-85");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("sess-85", node["sessionId"]!.ToString()); // first-seen casing

        var perTarget = node["perTarget"]!.AsArray();
        Assert.Equal(4, perTarget.Count);
        Assert.Equal("Desktop", perTarget[0]!["target"]!.ToString()); // fleet order
        Assert.Equal("Server", perTarget[1]!["target"]!.ToString());
        Assert.Equal("Cloud", perTarget[2]!["target"]!.ToString()); // Cloud last
        Assert.Equal("Removed", perTarget[3]!["target"]!.ToString()); // historical appended after

        var cloud = perTarget[2]!.AsObject();
        Assert.Equal(0.0, cloud["electricityCost"]!.GetValue<double>()); // Q1 = A
        Assert.Equal(0.0, cloud["savings"]!.GetValue<double>());
        Assert.NotNull(cloud["cloudValue"]);

        // actualCloudCost/cost are TOP-LEVEL session fields only — never on the per-target rows.
        Assert.Null(perTarget[0]!["actualCloudCost"]);
        Assert.Null(perTarget[1]!["cost"]);
        Assert.NotNull(node["actualCloudCost"]);
        Assert.NotNull(node["cost"]);

        // Top-level totals = sums over the per-target rows.
        Assert.Equal(4, node["requests"]!.GetValue<int>());
        Assert.Equal(44, node["inputTokens"]!.GetValue<int>());
        Assert.Equal(2, node["cachedInputTokens"]!.GetValue<int>());
        Assert.Equal(15, node["outputTokens"]!.GetValue<int>());
        Assert.Equal(375.0, node["elapsedMs"]!.GetValue<double>());
    }

    // ------------------------------------------------------------------
    //  Cost math (read-time pricing, Q1/Q3 contract)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Detail_CostFields_MatchMonitorFormulas_AndCloudRowIsZeroCostZeroSavings()
    {
        var options = OptionsWithPricing();
        await using var harness = await CreateAppAsync(options);
        harness.Store.Record(Entry("s1", (RoutingTarget)"Desktop", actualPrompt: 10_000, actualResponse: 5_000, cached: 2_000, elapsedMs: 60_000));
        harness.Store.Record(Entry("s1", RoutingTarget.Cloud, actualPrompt: 1_000, actualResponse: 100, elapsedMs: 4_000));

        using var response = await harness.Client.GetAsync("/api/router/monitor/sessions/s1");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        var rates = MonitorEndpointsExtensions.EffectiveRates(options.Pricing!);
        double CloudValueOf(SessionUsageTotals t) => Math.Round(
            MonitorEndpointsExtensions.TokenCost(
                new ActivityStatisticsTotals(t.Requests, t.InputTokens, t.OutputTokens, t.CachedInputTokens, t.ElapsedMilliseconds),
                rates.Input, rates.Cached, rates.Output), 4);

        var desktopTotals = harness.Store.TryGetSession("s1")!.ByTarget[(RoutingTarget)"Desktop"];
        var cloudTotals = harness.Store.TryGetSession("s1")!.ByTarget[RoutingTarget.Cloud];

        var expectedDesktopElectricity = MonitorEndpointsExtensions.ElectricityCost(
            desktopTotals.ElapsedMilliseconds,
            MonitorEndpointsExtensions.ResolvePowerWatts((RoutingTarget)"Desktop", options.GetEffectiveTargets(), options.Pricing!),
            rates.ElectricityKwh);
        var expectedDesktopSavings = Math.Round(CloudValueOf(desktopTotals) - expectedDesktopElectricity, 4);

        var perTarget = node["perTarget"]!.AsArray();
        var desktop = perTarget[0]!.AsObject();
        Assert.Equal(CloudValueOf(desktopTotals), desktop["cloudValue"]!.GetValue<double>(), 4);
        Assert.Equal(expectedDesktopElectricity, desktop["electricityCost"]!.GetValue<double>(), 4);
        Assert.Equal(expectedDesktopSavings, desktop["savings"]!.GetValue<double>(), 4);

        var cloud = perTarget[1]!.AsObject();
        Assert.Equal(CloudValueOf(cloudTotals), cloud["cloudValue"]!.GetValue<double>(), 4);
        Assert.Equal(0.0, cloud["electricityCost"]!.GetValue<double>());
        Assert.Equal(0.0, cloud["savings"]!.GetValue<double>());

        // Session totals (Q1 = A identities): cloudValue includes Cloud; savings = Σ non-Cloud.
        var expectedTotalCloudValue = Math.Round(CloudValueOf(desktopTotals) + CloudValueOf(cloudTotals), 4);
        var expectedTotalElectricity = Math.Round(expectedDesktopElectricity, 4);
        var expectedTotalSavings = Math.Round(expectedDesktopSavings, 4);
        Assert.Equal(expectedTotalCloudValue, node["cloudValue"]!.GetValue<double>(), 4);
        Assert.Equal(expectedTotalElectricity, node["electricityCost"]!.GetValue<double>(), 4);
        Assert.Equal(expectedTotalSavings, node["savings"]!.GetValue<double>(), 4);

        // actualCloudCost = the Cloud row's OWN token value (1k in @0.20 + 100 out @0.80 =
        // 0.00028) — the actual money paid to cloud; DISTINCT from the all-cloud cloudValue
        // baseline (0.0057 Desktop + 0.00028 Cloud = 0.00598), which values the local row too.
        Assert.Equal(0.00028, node["actualCloudCost"]!.GetValue<double>(), 4);
        Assert.Equal(0.00598, node["cloudValue"]!.GetValue<double>(), 4);
        Assert.Equal(cloud["cloudValue"]!.GetValue<double>(), node["actualCloudCost"]!.GetValue<double>(), 4);
        Assert.NotEqual(node["cloudValue"]!.GetValue<double>(), node["actualCloudCost"]!.GetValue<double>());

        // cost = round(actualCloudCost + electricityCost, 4) = the ACTUAL spend (0.00028 +
        // 0.0005 electricity) — NOT the savings-vs-baseline figure (0.0052).
        Assert.Equal(0.00078, node["cost"]!.GetValue<double>(), 4);
        Assert.Equal(0.0052, node["savings"]!.GetValue<double>(), 4);
        Assert.NotEqual(node["cost"]!.GetValue<double>(), node["savings"]!.GetValue<double>());
    }

    [Fact]
    public async Task Detail_CloudOnlySession_ActualCloudCostEqualsCloudValue_CostEqualsActualCloudCost()
    {
        await using var harness = await CreateAppAsync(OptionsWithPricing());
        harness.Store.Record(Entry("s1", RoutingTarget.Cloud, actualPrompt: 1_000, actualResponse: 100, elapsedMs: 4_000));

        using var response = await harness.Client.GetAsync("/api/router/monitor/sessions/s1");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        var actualCloudCost = node["actualCloudCost"]!.GetValue<double>();
        Assert.Equal(0.00028, actualCloudCost, 4); // Cloud's own token value (1k in @0.20 + 100 out @0.80)
        Assert.Equal(0.0, node["electricityCost"]!.GetValue<double>()); // no local targets ⇒ no power
        Assert.Equal(0.00028, node["cost"]!.GetValue<double>(), 4); // cloud-only: cost == actualCloudCost

        // Cloud-only contrast case: the all-cloud baseline IS the Cloud row's own value.
        Assert.Equal(actualCloudCost, node["cloudValue"]!.GetValue<double>(), 4);
    }

    [Fact]
    public async Task Detail_Rounding_CostRoundedLast_FromRoundedParts_NeverClamped()
    {
        var options = new OllamaRouterOptions
        {
            Targets =
            [
                new OllamaTargetOptions { Name = "Desktop", Url = "http://127.0.0.1:11435" },
                new OllamaTargetOptions { Name = "Server", Url = "http://192.168.1.10:11434" }
            ],
            // 0.3333/M produces raw values beyond 4 decimals (no cached rate ⇒ cached falls
            // back to the input rate; irrelevant here — no cached tokens).
            Pricing = new PricingOptions
            {
                PromptPricePerMillion = 0.3333,
                CompletionPricePerMillion = 0.3333,
                Currency = "$"
            }
        };
        await using var harness = await CreateAppAsync(options);
        harness.Store.Record(Entry("s1", (RoutingTarget)"Desktop", actualPrompt: 1_111_111, actualResponse: 0, elapsedMs: 1_000_000));
        harness.Store.Record(Entry("s1", RoutingTarget.Cloud, actualPrompt: 1_111_111, actualResponse: 0, elapsedMs: 4_000));

        using var response = await harness.Client.GetAsync("/api/router/monitor/sessions/s1");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        // Raw 0.370333… ⇒ actualCloudCost 0.3703; raw electricity (1000 s @ 150 W @ 0.20/kWh =
        // 0.008333…) ⇒ 0.0083 — per-target 4-decimal rounding first.
        Assert.Equal(0.3703, node["actualCloudCost"]!.GetValue<double>(), 4);
        Assert.Equal(0.0083, node["electricityCost"]!.GetValue<double>(), 4);

        // cost = round(0.3703 + 0.0083, 4) = 0.3786 from the ROUNDED parts, rounded LAST;
        // the unrounded 0.378666… (→ 0.3787) is NOT emitted, and no clamping applies.
        Assert.Equal(0.3786, node["cost"]!.GetValue<double>(), 4);
        Assert.NotEqual(0.3787, node["cost"]!.GetValue<double>(), 4);
    }

    [Fact]
    public async Task Detail_CostMath_ParitiesWithMonitorCalculateCostStatus_ForIdenticalInputs()
    {
        var options = OptionsWithPricing();
        await using var harness = await CreateAppAsync(options);
        var byTarget = new Dictionary<RoutingTarget, ActivityStatisticsTotals>
        {
            [(RoutingTarget)"Desktop"] = new(3, 12_000, 6_000, 2_500, 90_000),
            [RoutingTarget.Cloud] = new(1, 1_000, 100, 0, 4_000)
        };

        // Seed the store with counters that reproduce the SAME per-target totals.
        harness.Store.Record(Entry("s1", (RoutingTarget)"Desktop", actualPrompt: 7_000, actualResponse: 3_000, cached: 1_500, elapsedMs: 60_000));
        harness.Store.Record(Entry("s1", (RoutingTarget)"Desktop", actualPrompt: 5_000, actualResponse: 3_000, cached: 1_000, elapsedMs: 30_000));
        harness.Store.Record(Entry("s1", RoutingTarget.Cloud, actualPrompt: 1_000, actualResponse: 100, elapsedMs: 4_000));

        var monitorStatus = MonitorEndpointsExtensions.CalculateCostStatus(byTarget, options.Pricing, options.GetEffectiveTargets());
        Assert.NotNull(monitorStatus);

        using var response = await harness.Client.GetAsync("/api/router/monitor/sessions/s1");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var desktop = node["perTarget"]!.AsArray().First(n => n!["target"]!.ToString() == "Desktop")!.AsObject();

        // Per-target parity with the monitor's cost status.
        Assert.Equal(monitorStatus!.PerTargetSavings["Desktop"], desktop["savings"]!.GetValue<double>(), 4);
        Assert.Equal(monitorStatus.PerTargetElectricityCost["Desktop"], desktop["electricityCost"]!.GetValue<double>(), 4);
        Assert.Equal(Math.Round(
            MonitorEndpointsExtensions.TokenCost(
                byTarget[(RoutingTarget)"Desktop"],
                0.20,
                0.05,
                0.80), 4), desktop["cloudValue"]!.GetValue<double>(), 4);

        // Totals: the session electricity/savings equal the monitor's 7-day aggregates (same
        // inputs); the session cloudValue ALSO includes the Cloud row (monitor excludes it).
        Assert.Equal(monitorStatus.ElectricityCost, node["electricityCost"]!.GetValue<double>(), 4);
        Assert.Equal(monitorStatus.EstimatedSavings, node["savings"]!.GetValue<double>(), 4);
        Assert.Equal(
            Math.Round(monitorStatus.CloudOverflowCost +
                       Math.Round(MonitorEndpointsExtensions.TokenCost(byTarget[(RoutingTarget)"Desktop"], 0.20, 0.05, 0.80), 4), 4),
            node["cloudValue"]!.GetValue<double>(), 4);

        // Session-level actualCloudCost/cost parity with the monitor's cost status (same
        // formula: the Cloud row's own token value; cost rounds the rounded parts last).
        Assert.Equal(monitorStatus.CloudOverflowCost, node["actualCloudCost"]!.GetValue<double>(), 4);
        Assert.Equal(
            Math.Round(monitorStatus.CloudOverflowCost + monitorStatus.ElectricityCost, 4),
            node["cost"]!.GetValue<double>(), 4);
    }

    [Fact]
    public async Task Detail_PricingUnconfigured_OmitsCostFieldsAndCurrency_KeepsTokenTotals()
    {
        var options = new OllamaRouterOptions
        {
            Targets = [new OllamaTargetOptions { Name = "Desktop", Url = "http://127.0.0.1:11435" }]
            // Pricing left null — but also exercise an EMPTY pricing object (IsConfigured false).
        };
        await using var harness = await CreateAppAsync(options);
        harness.Store.Record(Entry("s1", (RoutingTarget)"Desktop", actualPrompt: 10, actualResponse: 5, cached: 2, elapsedMs: 100));

        using var response = await harness.Client.GetAsync("/api/router/monitor/sessions/s1");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(1, node["requests"]!.GetValue<int>());
        Assert.Equal(10, node["inputTokens"]!.GetValue<int>());
        Assert.Equal(2, node["cachedInputTokens"]!.GetValue<int>());
        Assert.Equal(5, node["outputTokens"]!.GetValue<int>());
        Assert.Equal(100.0, node["elapsedMs"]!.GetValue<double>());
        Assert.NotNull(node["lastSeenAt"]);
        Assert.Null(node["currency"]);
        Assert.Null(node["cloudValue"]);
        Assert.Null(node["electricityCost"]);
        Assert.Null(node["savings"]);
        Assert.Null(node["actualCloudCost"]);
        Assert.Null(node["cost"]);
        Assert.Null(node["perTarget"]!.AsArray()[0]!["cloudValue"]);
        Assert.Null(node["perTarget"].AsArray()[0]!["electricityCost"]);
        Assert.Null(node["perTarget"].AsArray()[0]!["savings"]);

        // The list rows omit the new fields too (token totals always present).
        using var listResponse = await harness.Client.GetAsync("/api/router/monitor/sessions");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listNode = JsonNode.Parse(await listResponse.Content.ReadAsStringAsync())!;
        Assert.Null(listNode["currency"]);
        var listRow = listNode["sessions"]!.AsArray()[0]!.AsObject();
        Assert.Equal(10, listRow["inputTokens"]!.GetValue<int>());
        Assert.Equal(5, listRow["outputTokens"]!.GetValue<int>());
        Assert.Null(listRow["cloudValue"]);
        Assert.Null(listRow["actualCloudCost"]);
        Assert.Null(listRow["cost"]);
    }

    [Fact]
    public async Task Detail_LivePricingChange_RepricesStoredSession_AtReadTime()
    {
        var options = OptionsWithPricing();
        await using var harness = await CreateAppAsync(options);
        harness.Store.Record(Entry("s1", (RoutingTarget)"Desktop", actualPrompt: 10_000, actualResponse: 5_000, cached: 2_000, elapsedMs: 60_000));

        using var first = await harness.Client.GetAsync("/api/router/monitor/sessions/s1");
        var firstNode = JsonNode.Parse(await first.Content.ReadAsStringAsync())!;
        var firstValue = firstNode["cloudValue"]!.GetValue<double>();

        // Flip the pricing live (no store change): the figures must re-price on the next read.
        harness.Options.SetValue(new OllamaRouterOptions
        {
            Targets = options.Targets,
            Pricing = new PricingOptions { PricePerMillion = 10.0, Currency = "€" }
        });

        using var second = await harness.Client.GetAsync("/api/router/monitor/sessions/s1");
        var secondNode = JsonNode.Parse(await second.Content.ReadAsStringAsync())!;

        Assert.Equal("€", secondNode["currency"]!.ToString());
        var expected = Math.Round((10_000 - 2_000) / 1_000_000.0 * 10.0 + 2_000 / 1_000_000.0 * 10.0 + 5_000 / 1_000_000.0 * 10.0, 4);
        Assert.Equal(expected, secondNode["cloudValue"]!.GetValue<double>(), 4);
        Assert.NotEqual(firstValue, secondNode["cloudValue"]!.GetValue<double>());

        // The new fields re-price on the read too: no Cloud usage ⇒ actualCloudCost stays
        // 0.0 and cost stays the (unchanged) electricity cost of the local row.
        Assert.Equal(0.0, secondNode["actualCloudCost"]!.GetValue<double>());
        Assert.Equal(secondNode["electricityCost"]!.GetValue<double>(), secondNode["cost"]!.GetValue<double>());
        Assert.Equal(firstNode["cost"]!.GetValue<double>(), secondNode["cost"]!.GetValue<double>(), 4);
    }

    [Fact]
    public async Task Detail_UnknownSession_IdWithSlash_IsUnescaped()
    {
        await using var harness = await CreateAppAsync(OptionsWithPricing());
        harness.Store.Record(Entry("a/b", (RoutingTarget)"Desktop", actualPrompt: 1, actualResponse: 1, elapsedMs: 1));

        using var response = await harness.Client.GetAsync("/api/router/monitor/sessions/a%2Fb");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("a/b", node["sessionId"]!.ToString());
    }
}
