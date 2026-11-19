using OllamaRouter.App.ViewModels;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="MonitorUsageRatioItem.FormattedTooltip"/> tests (task #114): the segment hover
/// tooltip must match the web monitor format <c>{Name}: {pct:0.#}% ({tokens} tokens, {requests} req)</c>
/// with human-readable token counts.
/// </summary>
public sealed class MonitorUsageRatioTests
{
    [Fact]
    public void FormattedTooltip_PartialShare_MatchesTheWebMonitorFormat()
    {
        var item = new MonitorUsageRatioItem
        {
            Name = "Local",
            Location = "local",
            Percentage = 25.5,
            Tokens = 1_700_000,
            Requests = 123,
        };

        Assert.Equal("Local: 25.5% (1.7M tokens, 123 req)", item.FormattedTooltip);
    }

    [Fact]
    public void FormattedTooltip_FullShare_Formats100WithoutDecimals()
    {
        var item = new MonitorUsageRatioItem
        {
            Name = "Cloud",
            Location = "cloud",
            Percentage = 100.0,
            Tokens = 9_700_000,
            Requests = 42,
        };

        Assert.Equal("Cloud: 100% (9.7M tokens, 42 req)", item.FormattedTooltip);
    }

    [Fact]
    public void FormattedTooltip_ZeroTokens_ShowsZeroFormat()
    {
        var item = new MonitorUsageRatioItem
        {
            Name = "Remote",
            Location = "remote",
            Percentage = 0.0,
            Tokens = 0,
            Requests = 0,
        };

        Assert.Equal("Remote: 0% (0 tokens, 0 req)", item.FormattedTooltip);
    }

    [Fact]
    public void FormattedTooltip_SubPercentShare_RoundsToOneDecimal()
    {
        var item = new MonitorUsageRatioItem
        {
            Name = "B",
            Location = "local",
            Percentage = 1.234,
            Tokens = 1_250,
            Requests = 7,
        };

        Assert.Equal("B: 1.2% (1.3k tokens, 7 req)", item.FormattedTooltip);
    }

    // ── task #117 (task #145: ≤2 per row): BuildUsageRatioLegendRows (legend wraps to ≤2 items per row) ──

    private static MonitorUsageRatioItem Item(string name) => new() { Name = name, Location = "local" };

    [Fact]
    public void BuildUsageRatioLegendRows_UpToTwoItems_SingleRowPreservingOrder()
    {
        foreach (var count in new[] { 1, 2 })
        {
            var items = Enumerable.Range(0, count).Select(i => Item($"T{i}")).ToList();
            var rows = MonitorViewModel.BuildUsageRatioLegendRows(items);

            Assert.Single(rows);
            Assert.Equal(items, rows[0]);
        }
    }

    [Theory]
    [InlineData(4, 2, 2)]
    [InlineData(5, 2, 2, 1)]
    [InlineData(6, 2, 2, 2)]
    [InlineData(7, 2, 2, 2, 1)]
    public void BuildUsageRatioLegendRows_MoreThanTwoItems_ChunksIntoConsecutiveRows(
        int count, params int[] expectedSizes)
    {
        var items = Enumerable.Range(0, count).Select(i => Item($"T{i}")).ToList();
        var rows = MonitorViewModel.BuildUsageRatioLegendRows(items);

        Assert.Equal(expectedSizes, rows.Select(r => r.Count));
        Assert.Equal(items, rows.SelectMany(r => r));
    }

    [Fact]
    public void BuildUsageRatioLegendRows_EmptyInput_ReturnsNoRows()
    {
        Assert.Empty(MonitorViewModel.BuildUsageRatioLegendRows(Array.Empty<MonitorUsageRatioItem>()));
    }

    [Fact]
    public void BuildUsageRatioLegendRows_NullInput_ReturnsNoRows()
    {
        Assert.Empty(MonitorViewModel.BuildUsageRatioLegendRows(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildUsageRatioLegendRows_InvalidItemsPerRow_Throws(int itemsPerRow)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MonitorViewModel.BuildUsageRatioLegendRows([Item("A")], itemsPerRow));
    }
}
