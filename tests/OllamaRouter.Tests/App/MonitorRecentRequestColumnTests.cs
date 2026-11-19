using OllamaRouter.App.ViewModels;
using OllamaRouter.Endpoints;
using OllamaRouter.Services.Monitoring;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Recent Requests table contract tests (task #117): the column model pins the 8-column
/// order (CACHED immediately after PROMPT, no RESULT column), the numeric right-alignment
/// flags, the red failed-status mapping (<see cref="MonitorRecentRequestRow.IsFailure"/>,
/// wired in the view via <c>Classes.status-failed="{Binding IsFailure}"</c>) and the
/// view-model's legend row splitting on refresh.
/// </summary>
public sealed class MonitorRecentRequestColumnTests
{
    // ── Column contract (XAML grids mirror MonitorViewModel.RecentRequestColumns) ──

    [Fact]
    public void RecentRequestColumns_HasExactlyEightColumnsInTheRequiredOrder()
    {
        Assert.Equal(
            ["TIMESTAMP", "TARGET", "MODEL", "PROMPT", "CACHED", "RESPONSE", "ELAPSED", "STATUS"],
            MonitorViewModel.RecentRequestColumns.Select(c => c.Header));
    }

    [Fact]
    public void RecentRequestColumns_HasNoResultColumn()
    {
        Assert.DoesNotContain(MonitorViewModel.RecentRequestColumns, c => c.Header == "RESULT");
    }

    [Fact]
    public void RecentRequestColumns_CachedSitsImmediatelyAfterPrompt()
    {
        var headers = MonitorViewModel.RecentRequestColumns.Select(c => c.Header).ToArray();
        Assert.Equal(Array.IndexOf(headers, "PROMPT") + 1, Array.IndexOf(headers, "CACHED"));
    }

    [Theory]
    [InlineData("TIMESTAMP", false)]
    [InlineData("TARGET", false)]
    [InlineData("MODEL", false)]
    [InlineData("PROMPT", true)]
    [InlineData("CACHED", true)]
    [InlineData("RESPONSE", true)]
    [InlineData("ELAPSED", true)]
    [InlineData("STATUS", true)]
    public void RecentRequestColumns_NumericFlags_MatchTheQuantities(string header, bool expectedNumeric)
    {
        var column = MonitorViewModel.RecentRequestColumns.Single(c => c.Header == header);
        Assert.Equal(expectedNumeric, column.IsNumeric);
    }

    // ── Red failed status ──

    private static MonitorRecentRequestRow Row(bool success, int? statusCode = 200) =>
        new(DateTimeOffset.UtcNow, "Local", "llama3:8b", 1000, 1010, 250, 150, 100.0, statusCode, success);

    [Fact]
    public void MonitorRecentRequestRow_IsFailure_IsTheNegationOfSuccess()
    {
        Assert.False(Row(success: true).IsFailure);
        Assert.True(Row(success: false, statusCode: 429).IsFailure);
    }

    // ── VM integration: legend row splitting on refresh ──

    [Fact]
    public async Task RefreshAsync_WithFourTargets_SplitsTheLegendIntoTwoRowsOf2And2()
    {
        var client = new FakeRouterApiClient
        {
            MonitorState = new MonitorStateResponse(
                true,
                [],
                new MonitorStatisticsStatus(
                    new Dictionary<string, ActivityStatisticsTotals>(),
                    new Dictionary<string, ActivityStatisticsTotals>
                    {
                        ["Local"] = new(10, 1000, 500, 0, 1000),
                        ["Remote"] = new(10, 2000, 500, 0, 1000),
                        ["Remote2"] = new(10, 3000, 500, 0, 1000),
                        ["Cloud"] = new(10, 4000, 500, 0, 1000),
                        ["total"] = new(40, 10000, 2000, 0, 4000),
                    }),
                [],
                []),
        };
        var viewModel = new MonitorViewModel(() => client);

        await viewModel.RefreshAsync();

        Assert.Equal(4, viewModel.UsageRatios.Count);
        Assert.Equal(new[] { 2, 2 }, viewModel.UsageRatioLegendRows.Select(r => r.Count));
        Assert.Equal(viewModel.UsageRatios, viewModel.UsageRatioLegendRows.SelectMany(r => r));
    }

    [Fact]
    public async Task RefreshAsync_WithASingleTarget_KeepsTheLegendOnOneRow()
    {
        var client = new FakeRouterApiClient { MonitorState = MonitorViewModelTests.BuildMonitorState() };
        var viewModel = new MonitorViewModel(() => client);

        await viewModel.RefreshAsync();

        Assert.Single(viewModel.UsageRatios);
        Assert.Single(viewModel.UsageRatioLegendRows);
    }
}
