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
    public void StartRequest_WithReasoningSessionAndAgent_SnapshotCarriesValues()
    {
        var sut = new ActivityMonitorService();

        sut.StartRequest(RoutingTarget.Local, "llama3", 42, "high", "s1", "a2");

        var inProgress = Assert.Single(sut.GetSnapshot().InProgressRequests);

        Assert.Equal("high", inProgress.ReasoningEffort);
        Assert.Equal("s1", inProgress.SessionId);
        Assert.Equal("a2", inProgress.AgentId);
    }

    [Fact]
    public void StartRequest_LegacyThreeArgCall_ValuesAreNull()
    {
        var sut = new ActivityMonitorService();

        sut.StartRequest(RoutingTarget.Local, "llama3", 42);

        var inProgress = Assert.Single(sut.GetSnapshot().InProgressRequests);

        Assert.Null(inProgress.ReasoningEffort);
        Assert.Null(inProgress.SessionId);
        Assert.Null(inProgress.AgentId);
    }

    [Fact]
    public void CompleteRequest_EntryCarryingValues_RecentRequestsCarryValues()
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
            true,
            38,
            "true",
            "s1",
            "a2");

        sut.CompleteRequest(requestId, entry);

        var recorded = Assert.Single(sut.GetSnapshot().RecentRequests);

        Assert.Equal("true", recorded.ReasoningEffort);
        Assert.Equal("s1", recorded.SessionId);
        Assert.Equal("a2", recorded.AgentId);
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

    [Fact]
    public void GetConsecutiveFailures_EmptyHistory_ReturnsZero()
    {
        var sut = new ActivityMonitorService();

        var count = sut.GetConsecutiveFailures(RoutingTarget.Remote, 91_300);

        Assert.Equal(0, count);
    }

    [Fact]
    public void GetConsecutiveFailures_MultipleConsecutiveFailuresSameTokenCount_ReturnsCount()
    {
        var sut = new ActivityMonitorService();
        var tokenCount = 91_300;

        for (int i = 0; i < 3; i++)
        {
            var reqId = sut.StartRequest(RoutingTarget.Remote, "Qwen3.8-27B:latest", tokenCount);
            sut.CompleteRequest(reqId, new ActivityLogEntry(
                DateTimeOffset.UtcNow, RoutingTarget.Remote, "Qwen3.8-27B:latest", tokenCount, null, null, 300_000, 400, false));
        }

        var count = sut.GetConsecutiveFailures(RoutingTarget.Remote, tokenCount);

        Assert.Equal(3, count);
    }

    [Fact]
    public void GetConsecutiveFailures_BrokenBySuccess_ReturnsZero()
    {
        var sut = new ActivityMonitorService();
        var tokenCount = 91_300;

        for (int i = 0; i < 3; i++)
        {
            var reqId = sut.StartRequest(RoutingTarget.Remote, "Qwen3.8-27B:latest", tokenCount);
            sut.CompleteRequest(reqId, new ActivityLogEntry(
                DateTimeOffset.UtcNow, RoutingTarget.Remote, "Qwen3.8-27B:latest", tokenCount, null, null, 300_000, 400, false));
        }

        // Newer request with same tokenCount succeeds
        var successId = sut.StartRequest(RoutingTarget.Cloud, "glm-5.3-flash:cloud", tokenCount);
        sut.CompleteRequest(successId, new ActivityLogEntry(
            DateTimeOffset.UtcNow, RoutingTarget.Cloud, "glm-5.3-flash:cloud", tokenCount, tokenCount, 50, 1000, 200, true));

        var count = sut.GetConsecutiveFailures(RoutingTarget.Remote, tokenCount);

        Assert.Equal(0, count);
    }

    [Fact]
    public void GetConsecutiveFailures_BrokenByDifferentTokenCount_ReturnsZero()
    {
        var sut = new ActivityMonitorService();
        var tokenCount = 91_300;

        for (int i = 0; i < 3; i++)
        {
            var reqId = sut.StartRequest(RoutingTarget.Remote, "Qwen3.8-27B:latest", tokenCount);
            sut.CompleteRequest(reqId, new ActivityLogEntry(
                DateTimeOffset.UtcNow, RoutingTarget.Remote, "Qwen3.8-27B:latest", tokenCount, null, null, 300_000, 400, false));
        }

        // Newer request has different token count (e.g. 500) and also failed
        var differentId = sut.StartRequest(RoutingTarget.Local, "llama3", 500);
        sut.CompleteRequest(differentId, new ActivityLogEntry(
            DateTimeOffset.UtcNow, RoutingTarget.Local, "llama3", 500, null, null, 500, 500, false));

        var count = sut.GetConsecutiveFailures(RoutingTarget.Remote, tokenCount);

        Assert.Equal(0, count);
    }

    [Fact]
    public void GetConsecutiveFailures_WithMultiTargetStreak_CountsTargetFailuresAccurately()
    {
        var sut = new ActivityMonitorService();
        var tokenCount = 91_300;

        // 3 failures on Remote
        for (int i = 0; i < 3; i++)
        {
            var reqId = sut.StartRequest(RoutingTarget.Remote, "Qwen3.8-27B:latest", tokenCount);
            sut.CompleteRequest(reqId, new ActivityLogEntry(
                DateTimeOffset.UtcNow, RoutingTarget.Remote, "Qwen3.8-27B:latest", tokenCount, null, null, 300_000, 400, false));
        }

        // Then 1 failure on Cloud for the same tokenCount
        var cloudId = sut.StartRequest(RoutingTarget.Cloud, "glm-5.3-flash:cloud", tokenCount);
        sut.CompleteRequest(cloudId, new ActivityLogEntry(
            DateTimeOffset.UtcNow, RoutingTarget.Cloud, "glm-5.3-flash:cloud", tokenCount, null, null, 1000, 500, false));

        Assert.Equal(3, sut.GetConsecutiveFailures(RoutingTarget.Remote, tokenCount));
        Assert.Equal(1, sut.GetConsecutiveFailures(RoutingTarget.Cloud, tokenCount));
        Assert.Equal(0, sut.GetConsecutiveFailures(RoutingTarget.Local, tokenCount));
    }

    [Fact]
    public void GetConsecutiveFailures_SuccessiveRequestsIncreaseWithinTokenTolerance_CountsFailures()
    {
        var sut = new ActivityMonitorService();

        // 1st request failed with 90,000 tokens
        var req1 = sut.StartRequest(RoutingTarget.Remote, "Qwen3.8-27B:latest", 90_000);
        sut.CompleteRequest(req1, new ActivityLogEntry(
            DateTimeOffset.UtcNow, RoutingTarget.Remote, "Qwen3.8-27B:latest", 90_000, null, null, 30_000, 499, false));

        // 2nd request (retry) increased by 1,200 tokens (91,200) and failed
        var req2 = sut.StartRequest(RoutingTarget.Remote, "Qwen3.8-27B:latest", 91_200);
        sut.CompleteRequest(req2, new ActivityLogEntry(
            DateTimeOffset.UtcNow, RoutingTarget.Remote, "Qwen3.8-27B:latest", 91_200, null, null, 30_000, 499, false));

        // 3rd request (retry) increased by another 1,500 tokens (92,700) and failed
        var req3 = sut.StartRequest(RoutingTarget.Remote, "Qwen3.8-27B:latest", 92_700);
        sut.CompleteRequest(req3, new ActivityLogEntry(
            DateTimeOffset.UtcNow, RoutingTarget.Remote, "Qwen3.8-27B:latest", 92_700, null, null, 30_000, 499, false));

        // When evaluating the next retry with 93,500 tokens:
        // Each successive hop was <= 4,000 tokens apart, so all 3 failures are counted
        var count = sut.GetConsecutiveFailures(RoutingTarget.Remote, 93_500, tokenTolerance: 4_000);

        Assert.Equal(3, count);
    }

    [Fact]
    public void GetConsecutiveFailures_SuccessiveRequestsIncreaseBeyondTokenTolerance_ReturnsZero()
    {
        var sut = new ActivityMonitorService();

        var req1 = sut.StartRequest(RoutingTarget.Remote, "Qwen3.8-27B:latest", 90_000);
        sut.CompleteRequest(req1, new ActivityLogEntry(
            DateTimeOffset.UtcNow, RoutingTarget.Remote, "Qwen3.8-27B:latest", 90_000, null, null, 30_000, 499, false));

        // Next request is 95,000 tokens (delta = 5,000 > 4,000)
        var count = sut.GetConsecutiveFailures(RoutingTarget.Remote, 95_000, tokenTolerance: 4_000);

        Assert.Equal(0, count);
    }

    [Fact]
    public void GetConsecutiveFailures_WithZeroTokenTolerance_RequiresExactCount()
    {
        var sut = new ActivityMonitorService();

        var req1 = sut.StartRequest(RoutingTarget.Remote, "Qwen3.8-27B:latest", 90_000);
        sut.CompleteRequest(req1, new ActivityLogEntry(
            DateTimeOffset.UtcNow, RoutingTarget.Remote, "Qwen3.8-27B:latest", 90_000, null, null, 30_000, 499, false));

        // 100 tokens difference with tolerance = 0 should NOT match
        Assert.Equal(0, sut.GetConsecutiveFailures(RoutingTarget.Remote, 90_100, tokenTolerance: 0));
        // Exact match should count
        Assert.Equal(1, sut.GetConsecutiveFailures(RoutingTarget.Remote, 90_000, tokenTolerance: 0));
    }
}
