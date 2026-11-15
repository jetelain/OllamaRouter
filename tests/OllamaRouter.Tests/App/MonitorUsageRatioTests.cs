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
}
