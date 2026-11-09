using Microsoft.Extensions.Logging.Abstractions;
using OllamaRouter.Options;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Services.Monitoring;

public class SessionUsageStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static SessionUsageStore CreateStore(int maxSessions = 1000) =>
        new(new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions { SessionUsageMaxSessions = maxSessions }));

    private static ActivityLogEntry Entry(
        string? sessionId,
        RoutingTarget? target = null,
        bool success = true,
        int estimated = 10,
        int? actualPrompt = null,
        int? actualResponse = null,
        long elapsedMs = 100,
        int? cached = null,
        DateTimeOffset? timestamp = null) => new(
        timestamp ?? T0,
        target ?? (RoutingTarget)"desktop",
        "model",
        estimated,
        actualPrompt,
        actualResponse,
        elapsedMs,
        success ? 200 : 500,
        success,
        SessionId: sessionId,
        CachedInputTokens: cached);

    // ------------------------------------------------------------------
    //  Aggregation branches
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Record_NullOrEmptySessionId_NeverStored(string? sessionId)
    {
        var store = CreateStore();
        store.Record(Entry(sessionId));

        Assert.Equal(0, store.Count);
        Assert.Empty(store.GetSessions());
    }

    [Fact]
    public void Record_FailedRequest_ContributesNothing_AndCreatesNoEntry()
    {
        var store = CreateStore();
        store.Record(Entry("s1", success: false, actualPrompt: 50, actualResponse: 5, cached: 3, elapsedMs: 999));

        Assert.Equal(0, store.Count);
        Assert.Null(store.TryGetSession("s1"));
    }

    [Fact]
    public void Record_SuccessWithoutUsage_AddsElapsedOnly()
    {
        var store = CreateStore();
        store.Record(Entry("s1", success: true, actualPrompt: 40, actualResponse: null, cached: 7, elapsedMs: 250));
        store.Record(Entry("s1", success: true, actualResponse: null, elapsedMs: 150));

        var snapshot = store.TryGetSession("s1");
        Assert.NotNull(snapshot);
        var totals = snapshot!.ByTarget[(RoutingTarget)"desktop"];
        Assert.Equal(0, totals.Requests);
        Assert.Equal(0, totals.InputTokens);
        Assert.Equal(0, totals.CachedInputTokens);
        Assert.Equal(0, totals.OutputTokens);
        Assert.Equal(400, totals.ElapsedMilliseconds); // 250 + 150; the request/token counters stay untouched
    }

    [Fact]
    public void Record_SuccessWithUsage_AddsFullCounters_InputFallsBackToEstimate_CachedClampedToInput()
    {
        var store = CreateStore();
        // actualPrompt 100, cached 30, output 12, elapsed 500.
        store.Record(Entry("s1", actualPrompt: 100, actualResponse: 12, cached: 30, elapsedMs: 500));
        // No actual prompt ⇒ estimated (10) is used; corrupt cached > input (500) clamps to input (10).
        store.Record(Entry("s1", estimated: 10, actualPrompt: null, actualResponse: 8, cached: 500, elapsedMs: 100));

        var totals = store.TryGetSession("s1")!.ByTarget[(RoutingTarget)"desktop"];
        Assert.Equal(2, totals.Requests);
        Assert.Equal(110, totals.InputTokens); // 100 + 10 (estimated fallback)
        Assert.Equal(40, totals.CachedInputTokens); // 30 + clamp(500, 0, 10)
        Assert.Equal(20, totals.OutputTokens); // 12 + 8
        Assert.Equal(600, totals.ElapsedMilliseconds); // 500 + 100
    }

    [Fact]
    public void Record_MultipleTargets_BucketSeparately_TotalsArePerTargetSums()
    {
        var store = CreateStore();
        store.Record(Entry("s1", target: (RoutingTarget)"desktop", actualPrompt: 20, actualResponse: 2, elapsedMs: 100));
        store.Record(Entry("s1", target: (RoutingTarget)"server", actualPrompt: 30, actualResponse: 3, elapsedMs: 200));
        store.Record(Entry("s1", target: RoutingTarget.Cloud, actualPrompt: 5, actualResponse: 1, elapsedMs: 50));

        var snapshot = store.TryGetSession("s1")!;
        Assert.Equal(3, snapshot.ByTarget.Count);
        Assert.Equal(20, snapshot.ByTarget[(RoutingTarget)"desktop"].InputTokens);
        Assert.Equal(30, snapshot.ByTarget[(RoutingTarget)"server"].InputTokens);
        Assert.Equal(5, snapshot.ByTarget[RoutingTarget.Cloud].InputTokens);

        // The snapshot totals (what the endpoints sum) equal the per-target sums.
        Assert.Equal(3, snapshot.ByTarget.Values.Sum(t => t.Requests));
        Assert.Equal(55, snapshot.ByTarget.Values.Sum(t => t.InputTokens));
        Assert.Equal(6, snapshot.ByTarget.Values.Sum(t => t.OutputTokens));
        Assert.Equal(350, snapshot.ByTarget.Values.Sum(t => t.ElapsedMilliseconds));
    }

    // ------------------------------------------------------------------
    //  LRU semantics
    // ------------------------------------------------------------------

    [Fact]
    public void GetSessions_ReturnsMostRecentlyActiveFirst_AndReadsNeverRefreshRecency()
    {
        var store = CreateStore();
        store.Record(Entry("a"));
        store.Record(Entry("b"));
        // A read of "a" must NOT refresh its recency.
        _ = store.TryGetSession("a");
        _ = store.GetSessions();

        var ids = store.GetSessions().Select(s => s.SessionId).ToArray();
        Assert.Equal(["b", "a"], ids);
    }

    [Fact]
    public void Record_WhenNewSessionExceedsCap_EvictsLeastRecentlyActive_NeverJustRecorded()
    {
        var store = CreateStore(maxSessions: 2);
        store.Record(Entry("a"));
        store.Record(Entry("b"));
        // Re-activate "a": order becomes a, b (b least-recently-active).
        store.Record(Entry("a"));
        // "c" is NEW and the store is at the cap ⇒ "b" (least-recently-active) is evicted.
        store.Record(Entry("c"));

        var ids = store.GetSessions().Select(s => s.SessionId).ToArray();
        Assert.Equal(["c", "a"], ids);
        Assert.Null(store.TryGetSession("b"));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void TryGetSession_IsCaseInsensitive_AndFirstSeenCasingIsReturned()
    {
        var store = CreateStore();
        store.Record(Entry("Sess-85"));

        var lower = store.TryGetSession("sess-85");
        var mixed = store.TryGetSession("SESS-85");
        Assert.NotNull(lower);
        Assert.NotNull(mixed);
        Assert.Equal("Sess-85", lower!.SessionId);
        Assert.Equal("Sess-85", mixed!.SessionId);

        // A re-record with different casing updates the SAME entry (no duplicate).
        store.Record(Entry("sess-85"));
        Assert.Equal(1, store.Count);
        Assert.Equal("Sess-85", store.TryGetSession("sESS-85")!.SessionId);
    }

    [Fact]
    public void Record_SessionIdLongerThan256Chars_NeverStored()
    {
        var store = CreateStore();
        store.Record(Entry(new string('x', 257), actualResponse: 5, elapsedMs: 10));
        Assert.Equal(0, store.Count);

        // Exactly 256 chars IS accepted (only > 256 is rejected).
        store.Record(Entry(new string('x', 256), actualResponse: 5));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Record_LiveCapShrink_EvictsDownToNewCapOnNextRecord()
    {
        var options = new OllamaRouterOptions { SessionUsageMaxSessions = 5 };
        var monitor = new TestOptionsMonitor<OllamaRouterOptions>(options);
        var store = new SessionUsageStore(monitor);

        for (var i = 0; i < 5; i++)
        {
            store.Record(Entry($"s{i}"));
        }
        Assert.Equal(5, store.Count);

        // Shrink the cap live: nothing is evicted until the NEXT record.
        monitor.SetValue(new OllamaRouterOptions { SessionUsageMaxSessions = 2 });
        Assert.Equal(5, store.Count);

        // A NEW session beyond the shrunk cap evicts until the cap holds.
        store.Record(Entry("new"));
        var ids = store.GetSessions().Select(s => s.SessionId).ToArray();
        Assert.Equal(2, store.Count);
        // Only the NEW session and the previous most-recently-active ("s4") survive.
        Assert.Equal(["new", "s4"], ids);
    }

    [Fact]
    public void Record_UpdatesLastSeenUtc_ToEntryTimestamp()
    {
        var store = CreateStore();
        store.Record(Entry("s1", timestamp: T0));
        store.Record(Entry("s1", timestamp: T0.AddHours(2)));

        Assert.Equal(T0.AddHours(2), store.TryGetSession("s1")!.LastSeenUtc);
    }

    [Fact]
    public void Record_ConcurrentCalls_YieldExactTotals_NoLostUpdates_NoCorruption()
    {
        var store = CreateStore(maxSessions: 1000);
        const int threads = 8;
        const int total = threads * 250;

        // Partition the records across exactly `threads` tasks (each record contributes one
        // request) and run them concurrently.
        var tasks = Enumerable.Range(0, threads)
            .Select(t => Task.Run(() =>
            {
                for (var i = 0; i < total / threads; i++)
                {
                    store.Record(Entry("shared", actualPrompt: 4, actualResponse: 2, cached: 1, elapsedMs: 3));
                }
            }))
            .ToArray();
        Task.WaitAll(tasks);

        Assert.Equal(1, store.Count);
        var totals = store.TryGetSession("shared")!.ByTarget[(RoutingTarget)"desktop"];
        // Every record contributes exactly one request — no lost updates, no corruption.
        Assert.Equal(total, totals.Requests);
        Assert.Equal(total * 4, totals.InputTokens);
        Assert.Equal(total * 1, totals.CachedInputTokens);
        Assert.Equal(total * 2, totals.OutputTokens);
        Assert.Equal((long)total * 3, totals.ElapsedMilliseconds);
    }
}
