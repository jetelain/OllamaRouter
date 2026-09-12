using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class ActivityStatisticsServiceTests : IDisposable
{
    private readonly string tempDirectory;
    private readonly string statsFilePath;
    private readonly Mock<IHostApplicationLifetime> lifetime = new();

    public ActivityStatisticsServiceTests()
    {
        tempDirectory = Directory.CreateTempSubdirectory("OllamaRouterTests").FullName;
        statsFilePath = Path.Combine(tempDirectory, "activity-statistics.json");
    }

    public void Dispose()
    {
        Directory.Delete(tempDirectory, recursive: true);
    }

    private ActivityStatisticsService CreateSut()
    {
        return new ActivityStatisticsService(statsFilePath, lifetime.Object, NullLogger<ActivityStatisticsService>.Instance);
    }

    private static ActivityLogEntry Entry(RoutingTarget target, int? promptTokens, int? responseTokens, bool success = true)
    {
        return new ActivityLogEntry(
            DateTimeOffset.UtcNow,
            target,
            "llama3",
            promptTokens ?? 0,
            promptTokens,
            responseTokens,
            100,
            200,
            success);
    }

    [Fact]
    public void GetSnapshot_NoData_ReturnsZeros()
    {
        var sut = CreateSut();

        var snapshot = sut.GetSnapshot();

        Assert.Equal(0, snapshot.TodayTotal.Requests);
        Assert.Equal(0, snapshot.TodayTotal.InputTokens);
        Assert.Equal(0, snapshot.TodayTotal.OutputTokens);
        Assert.Equal(0, snapshot.Last7DaysTotal.Requests);
        Assert.All(snapshot.Today.Values, t => Assert.Equal(0, t.Requests));
    }

    [Fact]
    public void Add_SuccessfulRequestsWithTokens_CountsRequestsAndTokensPerTarget()
    {
        var sut = CreateSut();

        sut.Add(Entry(RoutingTarget.Local, 100, 20));
        sut.Add(Entry(RoutingTarget.Local, 50, 10));
        sut.Add(Entry(RoutingTarget.Remote, 200, 30));

        var snapshot = sut.GetSnapshot();

        Assert.Equal(2, snapshot.Today[RoutingTarget.Local].Requests);
        Assert.Equal(150, snapshot.Today[RoutingTarget.Local].InputTokens);
        Assert.Equal(30, snapshot.Today[RoutingTarget.Local].OutputTokens);
        Assert.Equal(1, snapshot.Today[RoutingTarget.Remote].Requests);
        Assert.Equal(0, snapshot.Today[RoutingTarget.Cloud].Requests);
        Assert.Equal(3, snapshot.TodayTotal.Requests);
        Assert.Equal(350, snapshot.TodayTotal.InputTokens);
        Assert.Equal(60, snapshot.TodayTotal.OutputTokens);
    }

    [Fact]
    public void Add_MissingActualPromptTokens_FallsBackToEstimate()
    {
        var sut = CreateSut();

        sut.Add(new ActivityLogEntry(DateTimeOffset.UtcNow, RoutingTarget.Local, "llama3", 77, null, 15, 100, 200, true));

        var snapshot = sut.GetSnapshot();

        Assert.Equal(77, snapshot.Today[RoutingTarget.Local].InputTokens);
        Assert.Equal(1, snapshot.Today[RoutingTarget.Local].Requests);
    }

    [Fact]
    public void Add_IgnoresFailedRequestsAndRequestsWithoutOutputTokens()
    {
        var sut = CreateSut();

        sut.Add(Entry(RoutingTarget.Local, 10, 10, success: false));
        sut.Add(Entry(RoutingTarget.Local, 10, null));

        var snapshot = sut.GetSnapshot();

        Assert.Equal(0, snapshot.TodayTotal.Requests);
        Assert.Equal(0, snapshot.TodayTotal.InputTokens);
        Assert.Equal(0, snapshot.TodayTotal.OutputTokens);
    }

    [Fact]
    public void GetSnapshot_Last7Days_AggregatesWithToday()
    {
        var sut = CreateSut();

        sut.Add(Entry(RoutingTarget.Local, 10, 5));

        var snapshot = sut.GetSnapshot();

        Assert.Equal(1, snapshot.Last7Days[RoutingTarget.Local].Requests);
        Assert.Equal(1, snapshot.Last7DaysTotal.Requests);
        Assert.Equal(snapshot.Today[RoutingTarget.Local].Requests, snapshot.Last7Days[RoutingTarget.Local].Requests);
    }

    [Fact]
    public void GetSnapshot_Last7Days_ExcludesOlderDays()
    {
        var today = DateTime.Now.Date;
        var days = new List<ActivityStatisticsDate>
        {
            new()
            {
                Date = today.AddDays(-8),
                Statistics =
                {
                    [RoutingTarget.Local] = new ActivityStatisticsEntry { TotalRequests = 5, TotalInputTokens = 500, TotalOutputTokens = 50 }
                }
            },
            new()
            {
                Date = today.AddDays(-2),
                Statistics =
                {
                    [RoutingTarget.Local] = new ActivityStatisticsEntry { TotalRequests = 2, TotalInputTokens = 200, TotalOutputTokens = 20 }
                }
            }
        };
        File.WriteAllText(statsFilePath, JsonSerializer.Serialize(days, new JsonSerializerOptions { IncludeFields = true }));

        var sut = CreateSut();

        var snapshot = sut.GetSnapshot();

        Assert.Equal(2, snapshot.Last7Days[RoutingTarget.Local].Requests);
        Assert.Equal(0, snapshot.Today[RoutingTarget.Local].Requests);
        Assert.Equal(200, snapshot.Last7DaysTotal.InputTokens);
        Assert.Equal(20, snapshot.Last7DaysTotal.OutputTokens);
    }

    [Fact]
    public void Save_WritesTokenCountersToJsonFile()
    {
        var sut = CreateSut();

        sut.Add(Entry(RoutingTarget.Local, 100, 20));
        sut.Save();

        var persisted = File.ReadAllText(statsFilePath);
        Assert.Contains("\"TotalInputTokens\": 100", persisted);
        Assert.Contains("\"TotalOutputTokens\": 20", persisted);
        Assert.Contains("\"TotalRequests\": 1", persisted);
    }

    [Fact]
    public void Statistics_AreRestoredAcrossInstances()
    {
        var first = CreateSut();
        first.Add(Entry(RoutingTarget.Remote, 123, 45));
        first.Save();

        var second = CreateSut();
        second.Add(Entry(RoutingTarget.Remote, 7, 2));

        var snapshot = second.GetSnapshot();

        Assert.Equal(2, snapshot.Today[RoutingTarget.Remote].Requests);
        Assert.Equal(130, snapshot.Today[RoutingTarget.Remote].InputTokens);
        Assert.Equal(47, snapshot.Today[RoutingTarget.Remote].OutputTokens);
    }

    [Fact]
    public void Shutdown_SavesPendingStatistics()
    {
        var shutdownActions = new List<Action>();
        lifetime
            .Setup(l => l.ApplicationStopped.Register(It.IsAny<Action>()))
            .Callback((Action action) => shutdownActions.Add(action));

        var sut = CreateSut();
        sut.Add(Entry(RoutingTarget.Cloud, 11, 4));

        shutdownActions.ForEach(action => action());

        var second = CreateSut();

        Assert.Equal(1, second.GetSnapshot().Today[RoutingTarget.Cloud].Requests);
    }
}