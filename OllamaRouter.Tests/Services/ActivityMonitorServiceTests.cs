using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class ActivityMonitorServiceTests
{
    [Fact]
    public void GetSnapshot_NoActivity_ReturnsIdleAndEmptyCollections()
    {
        var sut = new ActivityMonitorService();

        var snapshot = sut.GetSnapshot();

        Assert.False(snapshot.Busy[RoutingTarget.Local]);
        Assert.False(snapshot.Busy[RoutingTarget.Remote]);
        Assert.Empty(snapshot.InProgressRequests);
        Assert.Empty(snapshot.RecentRequests);
    }

    [Fact]
    public void StartRequest_MarksTargetAsBusy_AndListsInProgressRequest()
    {
        var sut = new ActivityMonitorService();

        sut.StartRequest(RoutingTarget.Local, "llama3", 42);

        var snapshot = sut.GetSnapshot();

        Assert.True(snapshot.Busy[RoutingTarget.Local]);
        Assert.False(snapshot.Busy[RoutingTarget.Remote]);
        var inProgress = Assert.Single(snapshot.InProgressRequests);
        Assert.Equal(RoutingTarget.Local, inProgress.Target);
        Assert.Equal("llama3", inProgress.Model);
        Assert.Equal(42, inProgress.EstimatedPromptTokens);
    }

    [Fact]
    public void StartRequest_DoesNotAffectOtherTargetBusyState()
    {
        var sut = new ActivityMonitorService();

        sut.StartRequest(RoutingTarget.Remote, "llama3", 10);

        var snapshot = sut.GetSnapshot();

        Assert.False(snapshot.Busy[RoutingTarget.Local]);
        Assert.True(snapshot.Busy[RoutingTarget.Remote]);
    }

    [Fact]
    public void CompleteRequest_RemovesFromInProgress_AndAddsToRecentHistory()
    {
        var sut = new ActivityMonitorService();
        var requestId = sut.StartRequest(RoutingTarget.Local, "llama3", 42);

        var entry = new ActivityLogEntry(
            DateTimeOffset.UtcNow,
            RoutingTarget.Local,
            "llama3",
            42,
            40,
            128,
            1500,
            200,
            true);

        sut.CompleteRequest(requestId, entry);

        var snapshot = sut.GetSnapshot();

        Assert.False(snapshot.Busy[RoutingTarget.Local]);
        Assert.Empty(snapshot.InProgressRequests);
        var recorded = Assert.Single(snapshot.RecentRequests);
        Assert.Equal(entry, recorded);
    }

    [Fact]
    public void CompleteRequest_TargetStaysBusy_WhileAnotherRequestIsStillInProgress()
    {
        var sut = new ActivityMonitorService();
        var firstRequestId = sut.StartRequest(RoutingTarget.Local, "llama3", 42);
        sut.StartRequest(RoutingTarget.Local, "llama3", 10);

        sut.CompleteRequest(firstRequestId, new ActivityLogEntry(
            DateTimeOffset.UtcNow, RoutingTarget.Local, "llama3", 42, 40, 128, 1500, 200, true));

        var snapshot = sut.GetSnapshot();

        Assert.True(snapshot.Busy[RoutingTarget.Local]);
        Assert.Single(snapshot.InProgressRequests);
    }

    [Fact]
    public void GetSnapshot_RecentRequests_AreOrderedMostRecentFirst()
    {
        var sut = new ActivityMonitorService();
        var older = new ActivityLogEntry(DateTimeOffset.UtcNow.AddMinutes(-5), RoutingTarget.Local, "modelA", 1, 1, 1, 100, 200, true);
        var newer = new ActivityLogEntry(DateTimeOffset.UtcNow, RoutingTarget.Remote, "modelB", 2, 2, 2, 200, 200, true);

        sut.CompleteRequest(sut.StartRequest(RoutingTarget.Local, "modelA", 1), older);
        sut.CompleteRequest(sut.StartRequest(RoutingTarget.Remote, "modelB", 2), newer);

        var recent = sut.GetSnapshot().RecentRequests;

        Assert.Equal(2, recent.Count);
        Assert.Equal(newer, recent[0]);
        Assert.Equal(older, recent[1]);
    }

    [Fact]
    public void RecordCompletion_KeepsOnlyBoundedHistory()
    {
        var sut = new ActivityMonitorService();

        for (var i = 0; i < 60; i++)
        {
            var requestId = sut.StartRequest(RoutingTarget.Local, $"model{i}", i);
            sut.CompleteRequest(requestId, new ActivityLogEntry(
                DateTimeOffset.UtcNow, RoutingTarget.Local, $"model{i}", i, i, i, 100, 200, true));
        }

        var recent = sut.GetSnapshot().RecentRequests;

        Assert.Equal(50, recent.Count);
    }

    [Fact]
    public void CompleteRequest_UnknownRequestId_DoesNotThrow_AndStillRecordsHistory()
    {
        var sut = new ActivityMonitorService();

        var entry = new ActivityLogEntry(DateTimeOffset.UtcNow, RoutingTarget.Remote, "llama3", 5, 5, 5, 100, 200, true);

        sut.CompleteRequest(Guid.NewGuid(), entry);

        var recorded = Assert.Single(sut.GetSnapshot().RecentRequests);
        Assert.Equal(entry, recorded);
    }
}
