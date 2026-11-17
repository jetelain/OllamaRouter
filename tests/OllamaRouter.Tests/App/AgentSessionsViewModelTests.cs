using System.Net;
using OllamaRouter.App.ViewModels;
using OllamaRouter.Client;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.Sessions;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="AgentSessionsViewModel"/> tests (task #124): the session list population in
/// wire order, the pure <c>BuildSummary</c> / <c>FormatCost</c> aggregation helpers (token
/// sums, cost totals against the SERVER-computed figures — the VM never re-implements the
/// pricing math), the documented Pricing anchor (prompt $3/M, completion $15/M, cached
/// $0.30/M, currency "$"), the empty state, the no-pricing dash columns, last-good data on
/// API/network failures, and the selection/detail flow (population, eviction cleanup,
/// inline 404 error).
/// </summary>
public sealed class AgentSessionsViewModelTests : IAsyncLifetime
{
    private static readonly DateTimeOffset LastSeen = new(2026, 11, 15, 12, 30, 0, TimeSpan.Zero);

    private readonly FakeRouterApiClient _client = new();
    private readonly AgentSessionsViewModel _viewModel;

    public AgentSessionsViewModelTests()
    {
        _viewModel = new AgentSessionsViewModel(() => _client);
    }

    private static RouterSessionUsageItem Item(
        string id, int requests, int input, int cached, int output, double elapsedMs,
        double? cloudValue, double? electricity, double? savings, double? actualCloudCost,
        double? cost) =>
        new(id, requests, input, cached, output, elapsedMs, LastSeen,
            cloudValue, electricity, savings, actualCloudCost, cost);

    // ------------------------------------------------------------------
    //  A. Refresh: list population
    // ------------------------------------------------------------------

    [Fact]
    public async Task Refresh_PopulatesRowsInWireOrder_AndMapsEveryField()
    {
        _client.SessionsResponse = new RouterSessionUsageListResponse("$", 2,
        [
            Item("agent-a", 12, 20_000, 4_000, 3_000, 45_250.0, 71.1, 2.4, 68.7, 1.2, 3.6),
            Item("agent-b", 3, 500, 50, 120, 1_200.0, 4.55, 0.1, 4.45, 0.0, 0.1),
        ]);

        await _viewModel.RefreshAsync();

        Assert.False(_viewModel.IsErrorVisible);
        Assert.True(_viewModel.HasSessions);
        Assert.Equal(2, _viewModel.Sessions.Count);

        // Wire order (most-recently-active first) is preserved.
        Assert.Equal(new[] { "agent-a", "agent-b" }, _viewModel.Sessions.Select(r => r.AgentId));

        var a = _viewModel.Sessions[0];
        Assert.Equal(12, a.Requests);
        Assert.Equal(20_000, a.InputTokens); // prompt, cached-INCLUSIVE
        Assert.Equal(4_000, a.CachedInputTokens);
        Assert.Equal(3_000, a.OutputTokens);
        Assert.Equal(23_000, a.TotalTokens);
        Assert.Equal(45_250.0, a.ElapsedMs);
        Assert.Equal(LastSeen, a.LastSeenAt);
        Assert.Equal("$", a.Currency);
        // The cost figures are mapped VERBATIM (never re-derived from the token counters).
        Assert.Equal(71.1, a.CloudValue);
        Assert.Equal(2.4, a.ElectricityCost);
        Assert.Equal(68.7, a.Savings);
        Assert.Equal(1.2, a.ActualCloudCost);
        Assert.Equal(3.6, a.Cost);
        Assert.Equal("$ 3.60", a.FormattedCost);
        Assert.Equal("$ 71.10", a.FormattedCloudValue);
        Assert.Equal("$ 68.70", a.FormattedSavings); // session-level savings (detail panel)
    }

    // ------------------------------------------------------------------
    //  B. BuildSummary: the pure aggregation helper
    // ------------------------------------------------------------------

    [Fact]
    public void BuildSummary_SumsTokensRequestsAndCosts_AcrossRows()
    {
        var rows = new[]
        {
            Item("a", 10, 2_000, 500, 1_000, 1_000.0, 5.0, 0.5, 4.5, 0.0, 0.5),
            Item("b", 2, 1_000, 100, 200, 500.0, 2.25, 0.25, 2.0, 1.75, 2.0),
        };

        var summary = AgentSessionsViewModel.BuildSummary(rows, "$");

        Assert.Equal(2, summary.Sessions);
        Assert.Equal(12, summary.Requests);
        Assert.Equal(3_000, summary.InputTokens);
        Assert.Equal(600, summary.CachedInputTokens);
        Assert.Equal(1_200, summary.OutputTokens);
        Assert.Equal(4_200, summary.TotalTokens);
        Assert.Equal(1_500.0, summary.ElapsedMs);
        Assert.Equal("$", summary.Currency);
        Assert.Equal(2.5, summary.TotalCost!.Value);
        Assert.Equal(7.25, summary.TotalCloudValue!.Value);
    }

    [Fact]
    public void BuildSummary_MixedNullCostRows_SumsOnlyPresentValues_AllNullYieldsNullTotals()
    {
        var mixed = new[]
        {
            Item("a", 1, 100, 0, 10, 10.0, 4.0, 0.4, 3.6, 0.0, 0.4),
            Item("b", 2, 200, 0, 20, 20.0, 1.0, null, null, null, null),
        };
        var summary = AgentSessionsViewModel.BuildSummary(mixed, "$");
        Assert.Equal(0.4, summary.TotalCost!.Value);
        Assert.Equal(5.0, summary.TotalCloudValue!.Value);

        var allNull = new[] { Item("a", 1, 100, 0, 10, 10.0, null, null, null, null, null) };
        var nullSummary = AgentSessionsViewModel.BuildSummary(allNull, null);
        Assert.Null(nullSummary.TotalCost);
        Assert.Null(nullSummary.TotalCloudValue);
        Assert.Null(nullSummary.Currency);
        Assert.Equal(1, nullSummary.Sessions);
        Assert.Equal(1, nullSummary.Requests);
    }

    [Fact]
    public void BuildSummary_EmptyRows_YieldsZeroedSummary()
    {
        var summary = AgentSessionsViewModel.BuildSummary(Array.Empty<RouterSessionUsageItem>(), "$");

        Assert.Equal(0, summary.Sessions);
        Assert.Equal(0, summary.Requests);
        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.CachedInputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.Equal(0.0, summary.ElapsedMs);
        Assert.Null(summary.TotalCost);
        Assert.Null(summary.TotalCloudValue);
    }

    // ------------------------------------------------------------------
    //  C. FormatCost: the pure display helper
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(12.345, "$", "$ 12.35")]
    [InlineData(0.0, "$", "$ 0.00")]
    [InlineData(8.4, "€", "€ 8.40")]
    [InlineData(-1.5, "$", "$ -1.50")]
    [InlineData(2.0, null, "$ 2.00")]
    public void FormatCost_RendersCurrencyAndTwoDecimals(double value, string? currency, string expected)
    {
        Assert.Equal(expected, AgentSessionsViewModel.FormatCost(value, currency));
    }

    [Theory]
    [InlineData("$")]
    [InlineData(null)]
    public void FormatCost_DashWhenAbsent(string? currency)
    {
        Assert.Equal("-", AgentSessionsViewModel.FormatCost(null, currency));
    }

    // ------------------------------------------------------------------
    //  D. Empty state / no pricing
    // ------------------------------------------------------------------

    [Fact]
    public async Task Refresh_EmptyStore_ShowsEmptyStateWithoutError()
    {
        // The fake's default: count 0, no sessions, no currency.
        await _viewModel.RefreshAsync();

        Assert.False(_viewModel.IsErrorVisible);
        Assert.False(_viewModel.HasSessions);
        Assert.Empty(_viewModel.Sessions);
        Assert.NotNull(_viewModel.Summary);
        var summary = _viewModel.Summary!;
        Assert.Equal(0, summary.Sessions);
        Assert.Equal(0, summary.Requests);
        Assert.Equal("0", summary.FormattedTotalTokens);
        Assert.Equal("-", summary.FormattedTotalCost);
        Assert.Equal("-", summary.FormattedTotalCloudValue);
    }

    [Fact]
    public async Task Refresh_NoPricing_CostColumnsAreDash_TokensStillPopulated()
    {
        _client.SessionsResponse = new RouterSessionUsageListResponse(null, 1,
        [
            Item("no-price", 5, 1_000, 200, 300, 5_000.0, null, null, null, null, null),
        ]);

        await _viewModel.RefreshAsync();

        var row = Assert.Single(_viewModel.Sessions);
        Assert.Equal("-", row.FormattedCost);
        Assert.Equal("-", row.FormattedCloudValue);
        Assert.Equal("-", row.FormattedSavings);
        Assert.Equal(1_000, row.InputTokens); // the token columns are unaffected
        Assert.Equal("-", _viewModel.Summary!.FormattedTotalCost);
        Assert.Equal("1.3k", _viewModel.Summary.FormattedTotalTokens);
    }

    // ------------------------------------------------------------------
    //  E. Cost-vs-Pricing anchor (server-computed values displayed verbatim)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Refresh_PricingAnchor_SumsTheServerComputedCostsVerbatim()
    {
        // Documented Pricing anchor: prompt $3/M, completion $15/M, cached $0.30/M, currency "$".
        // Session "agent-anchor": 2.0M input (0.5M of it cached) + 0.25M output.
        //   cloudValue = 1.5M * $3/M + 0.5M * $0.30/M + 0.25M * $15/M
        //              = 4.50 + 0.15 + 3.75 = $8.40 (4-decimal server rounding)
        // The Cloud target was never used: actualCloudCost = $0.00;
        // local electricity = $0.40 ⇒ cost = round(0.00 + 0.40, 4) = $0.40.
        _client.SessionsResponse = new RouterSessionUsageListResponse("$", 1,
        [
            Item("agent-anchor", 40, 2_000_000, 500_000, 250_000, 540_000.0,
                8.4, 0.4, 8.0, 0.0, 0.4),
        ]);

        await _viewModel.RefreshAsync();

        var row = Assert.Single(_viewModel.Sessions);
        Assert.Equal(8.4, row.CloudValue); // verbatim — NOT re-derived from the token counters
        Assert.Equal(0.4, row.Cost);
        Assert.Equal("$ 8.40", row.FormattedCloudValue);
        Assert.Equal("$ 0.40", row.FormattedCost);
        Assert.Equal("$ 8.00", row.FormattedSavings);

        var summary = _viewModel.Summary!;
        Assert.Equal(8.4, summary.TotalCloudValue!.Value);
        Assert.Equal(0.4, summary.TotalCost!.Value);
        Assert.Equal("$ 8.40", summary.FormattedTotalCloudValue);
        Assert.Equal("$ 0.40", summary.FormattedTotalCost);
    }

    // ------------------------------------------------------------------
    //  F. Refresh failure: last-good data preserved
    // ------------------------------------------------------------------

    [Fact]
    public async Task Refresh_AfterSuccess_ApiFailureKeepsLastGoodData_AndShowsError()
    {
        _client.SessionsResponse = new RouterSessionUsageListResponse("$", 1,
        [
            Item("keep-me", 7, 700, 70, 70, 7_000.0, 1.0, 0.1, 0.9, 0.0, 0.1),
        ]);
        await _viewModel.RefreshAsync();
        Assert.True(_viewModel.HasSessions);

        _client.NextCallException = new OllamaRouterApiException(HttpStatusCode.InternalServerError, "boom");
        await _viewModel.RefreshAsync();

        Assert.True(_viewModel.IsErrorVisible);
        Assert.Contains("boom", _viewModel.LastError);
        var row = Assert.Single(_viewModel.Sessions); // last-good preserved
        Assert.Equal("keep-me", row.AgentId);
        Assert.Equal(7, row.Requests);
    }

    [Fact]
    public async Task Refresh_AfterSuccess_NetworkFailureKeepsLastGoodData_AndShowsError()
    {
        _client.SessionsResponse = new RouterSessionUsageListResponse("$", 1,
        [
            Item("keep-me", 1, 100, 0, 10, 1_000.0, 1.0, 0.1, 0.9, 0.0, 0.1),
        ]);
        await _viewModel.RefreshAsync();

        _client.NextCallException = new HttpRequestException("connection reset");
        await _viewModel.RefreshAsync();

        Assert.True(_viewModel.IsErrorVisible);
        Assert.Contains("connection reset", _viewModel.LastError);
        Assert.Single(_viewModel.Sessions);
    }

    // ------------------------------------------------------------------
    //  G. Selection & detail
    // ------------------------------------------------------------------

    private static RouterSessionUsageDetailResponse Detail(string id, params RouterSessionUsageTargetItem[] targets) =>
        new("$", id, 10, 2_000, 500, 1_000, 1_500.0, LastSeen,
            7.25, 0.75, 6.5, 0.0, 0.75, targets);

    private void SeedAndRefreshSession(string id, double cloudValue, double cost)
    {
        _client.SessionsResponse = new RouterSessionUsageListResponse("$", 1,
        [
            Item(id, 10, 2_000, 500, 1_000, 1_500.0, cloudValue, 0.75, 6.5, 0.0, cost),
        ]);
    }

    /// <summary>
    /// Bounded, no-sleep settle wait (task #130 addendum, defense in depth): assigning
    /// <c>SelectedRow</c> fires the detail fetch FIRE-AND-FORGET, so the detail state is
    /// asserted only after the observable result has landed (bounded cooperative yields —
    /// no real sleep). The bounded terminal assert turns a leaked/stalled dispatch into a
    /// self-describing failure instead of a misleading one.
    /// </summary>
    private static async Task WaitDetailSettledAsync(Func<bool> settled, string what)
    {
        for (var i = 0; i < 20_000 && !settled(); i++)
        {
            await Task.Yield();
        }

        Assert.True(settled(), $"timed out waiting for {what}");
    }

    [Fact]
    public async Task SelectRow_FetchesDetailAndPopulatesTargetRows()
    {
        SeedAndRefreshSession("sel-1", 7.25, 0.75);
        _client.SessionDetails["sel-1"] = Detail("sel-1",
            new RouterSessionUsageTargetItem("Local", 6, 1_500, 400, 800, 1_200.0, 5.4, 0.75, 4.65),
            new RouterSessionUsageTargetItem("Cloud", 4, 500, 100, 200, 300.0, 1.85, 0.0, 0.0));
        await _viewModel.RefreshAsync();

        _viewModel.SelectedRow = _viewModel.Sessions[0];

        await WaitDetailSettledAsync(
            () => _viewModel.SelectedTargetRows.Count == 2, "the detail fetch to populate 2 target rows");

        Assert.Equal("sel-1", _viewModel.SelectedRow!.AgentId);
        Assert.Null(_viewModel.SelectedDetailError);
        Assert.Equal(2, _viewModel.SelectedTargetRows.Count);
        var local = _viewModel.SelectedTargetRows[0];
        Assert.Equal("Local", local.Target);
        Assert.Equal(6, local.Requests);
        Assert.Equal(1_500, local.InputTokens);
        Assert.Equal(5.4, local.CloudValue);
        Assert.Equal(0.75, local.ElectricityCost);
        Assert.Equal("$ 4.65", local.FormattedSavings);
        Assert.Equal(1.85, _viewModel.SelectedTargetRows[1].CloudValue);
        Assert.Equal(1, _client.Calls.Count(c => c == nameof(IOllamaRouterApiClient.GetSessionDetailAsync)));
    }

    [Fact]
    public async Task Selection_SurvivesRefresh_WhenTheSessionIsStillPresent()
    {
        SeedAndRefreshSession("stick", 7.25, 0.75);
        _client.SessionDetails["stick"] = Detail("stick",
            new RouterSessionUsageTargetItem("Local", 10, 2_000, 500, 1_000, 1_500.0, 7.25, 0.75, 6.5));
        await _viewModel.RefreshAsync();
        _viewModel.SelectedRow = _viewModel.Sessions[0];
        Assert.NotNull(_viewModel.SelectedRow);
        Assert.Single(_viewModel.SelectedTargetRows);

        // The 5 s poll re-lists the SAME session (its counters grew) — the selection survives.
        // Capture the current detail row FIRST: the re-fetch (fired by the re-pointed
        // selection) may already be in flight by the time RefreshAsync returns.
        var oldRow = _viewModel.SelectedTargetRows[0];
        SeedAndRefreshSession("stick", 9.0, 0.9);
        await _viewModel.RefreshAsync();

        // The re-fetch repopulates the detail rows with NEW row instances (the old batch is
        // cleared and re-added) — settle on that replacement.
        await WaitDetailSettledAsync(
            () => _viewModel.SelectedTargetRows.Count == 1 && !ReferenceEquals(oldRow, _viewModel.SelectedTargetRows[0]),
            "the detail re-fetch to repopulate the target rows");

        var stillSelected = _viewModel.SelectedRow!;
        Assert.NotNull(stillSelected);
        Assert.Equal("stick", stillSelected.AgentId);
        Assert.Equal(9.0, stillSelected.CloudValue); // the refreshed (new) row is selected
        Assert.Null(_viewModel.SelectedDetailError);
        Assert.Single(_viewModel.SelectedTargetRows); // the detail re-fetched
    }

    [Fact]
    public async Task EvictedSelection_IsCleared_WithNoDetailError()
    {
        SeedAndRefreshSession("evicted", 7.25, 0.75);
        _client.SessionDetails["evicted"] = Detail("evicted",
            new RouterSessionUsageTargetItem("Local", 10, 2_000, 500, 1_000, 1_500.0, 7.25, 0.75, 6.5));
        await _viewModel.RefreshAsync();
        _viewModel.SelectedRow = _viewModel.Sessions[0];
        Assert.NotNull(_viewModel.SelectedRow);
        Assert.Single(_viewModel.SelectedTargetRows);

        // The LRU store evicted the selected session between polls.
        SeedAndRefreshSession("other", 1.0, 0.1);
        await _viewModel.RefreshAsync();

        Assert.Null(_viewModel.SelectedRow);
        Assert.Empty(_viewModel.SelectedTargetRows);
        Assert.Null(_viewModel.SelectedDetailError);
        Assert.Single(_viewModel.Sessions);
    }

    [Fact]
    public async Task Detail404_ShowsInlineError_WithoutBreakingTheList()
    {
        SeedAndRefreshSession("ghost", 7.25, 0.75);
        // No entry in SessionDetails → the fake returns the wire 404.
        await _viewModel.RefreshAsync();

        _viewModel.SelectedRow = _viewModel.Sessions[0];

        await WaitDetailSettledAsync(
            () => _viewModel.SelectedDetailError is not null, "the detail 404 error to surface");

        Assert.NotNull(_viewModel.SelectedRow);
        Assert.Contains("404", _viewModel.SelectedDetailError);
        Assert.Empty(_viewModel.SelectedTargetRows);
        // The list itself is untouched: no tab-level error, last-good rows intact.
        Assert.False(_viewModel.IsErrorVisible);
        Assert.True(_viewModel.HasSessions);
        Assert.Single(_viewModel.Sessions);
    }

    [Fact]
    public async Task SelectingNothing_ClearsTheDetailPanel()
    {
        SeedAndRefreshSession("c1", 1.0, 0.1);
        _client.SessionDetails["c1"] = Detail("c1",
            new RouterSessionUsageTargetItem("Local", 1, 100, 0, 10, 100.0, 1.0, 0.1, 0.9));
        await _viewModel.RefreshAsync();
        _viewModel.SelectedRow = _viewModel.Sessions[0];
        Assert.Single(_viewModel.SelectedTargetRows);

        _viewModel.SelectedRow = null;

        Assert.Null(_viewModel.SelectedRow);
        Assert.Empty(_viewModel.SelectedTargetRows);
        Assert.Null(_viewModel.SelectedDetailError);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync() => await _viewModel.DisposeAsync();
}
