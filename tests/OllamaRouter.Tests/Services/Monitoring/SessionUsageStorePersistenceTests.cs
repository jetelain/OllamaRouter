using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaRouter.Options;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Services.Monitoring;

/// <summary>
/// Task #105: per-session usage persistence (session-usage.json) — save/reload round-trips,
/// LRU order preservation, cap enforcement on load, corrupt-file resilience and the
/// graceful-shutdown flush. Each test uses its own temp directory.
/// </summary>
public class SessionUsageStorePersistenceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("session-usage-test-");

    public void Dispose()
    {
        // Fire-and-forget saves write to this directory; give any in-flight one a moment and
        // tolerate the best-effort cleanup race.
        try
        {
            Directory.Delete(_dir.FullName, recursive: true);
        }
        catch (IOException)
        {
            // A throttled background save may still hold a temp file; retry once after settle.
            Thread.Sleep(250);
            Directory.Delete(_dir.FullName, recursive: true);
        }
    }

    private string StateFile(string subPath = "session-usage.json") => Path.Combine(_dir.FullName, subPath);

    private static SessionUsageStore CreateStore(
        int maxSessions = 1000,
        string? stateFile = null,
        IHostApplicationLifetime? lifetime = null,
        OllamaRouterOptions? options = null) =>
        new SessionUsageStore(
            new TestOptionsMonitor<OllamaRouterOptions>(options ?? new OllamaRouterOptions { SessionUsageMaxSessions = maxSessions }),
            NullLogger<SessionUsageStore>.Instance,
            stateFile,
            lifetime);

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

    [Fact]
    public void Save_ThenNewStoreInstance_RoundTripsCounters_LruOrder_AndLastSeen()
    {
        var stateFile = StateFile();
        var lifetime = new FakeHostApplicationLifetime();
        var store1 = CreateStore(stateFile: stateFile, lifetime: lifetime);

        // Mixed multi-target sessions (incl. an elapsed-only success), interleaved to scramble
        // the LRU order (s1 is the most-recently-active, s3 the least-recently-active).
        store1.Record(Entry("s2", target: (RoutingTarget)"server", actualPrompt: 30, actualResponse: 11, elapsedMs: 300, timestamp: T0));
        store1.Record(Entry("s1", target: (RoutingTarget)"desktop", actualPrompt: 20, actualResponse: 5, cached: 2, elapsedMs: 200, timestamp: T0));
        store1.Record(Entry("s3", actualResponse: null, elapsedMs: 150, timestamp: T0)); // elapsed-only (no usage)
        store1.Record(Entry("s1", target: (RoutingTarget)"desktop", actualPrompt: 20, actualResponse: 11, cached: 5, elapsedMs: 200, timestamp: T0.AddSeconds(10)));
        store1.Record(Entry("s1", target: RoutingTarget.Cloud, actualPrompt: 4, actualResponse: 1, elapsedMs: 50, timestamp: T0.AddSeconds(20)));

        store1.Save();

        var store2 = CreateStore(stateFile: stateFile);
        var sessions = store2.GetSessions();
        // LRU order preserved (s1 recorded last = MRU; s3 recorded third = second-to-last).
        Assert.Equal(["s1", "s3", "s2"], sessions.Select(s => s.SessionId).ToArray());

        var s1 = sessions[0]!;
        Assert.Equal(T0.AddSeconds(20), s1.LastSeenUtc);
        Assert.Equal(2, s1.ByTarget.Count);
        var s1Desktop = s1.ByTarget[(RoutingTarget)"desktop"];
        Assert.Equal(2, s1Desktop.Requests);
        Assert.Equal(40, s1Desktop.InputTokens);
        Assert.Equal(7, s1Desktop.CachedInputTokens);
        Assert.Equal(16, s1Desktop.OutputTokens);
        Assert.Equal(400, s1Desktop.ElapsedMilliseconds);
        var s1Cloud = s1.ByTarget[RoutingTarget.Cloud];
        Assert.Equal(1, s1Cloud.Requests);
        Assert.Equal(4, s1Cloud.InputTokens);
        Assert.Equal(1, s1Cloud.OutputTokens);
        Assert.Equal(50, s1Cloud.ElapsedMilliseconds);

        // The elapsed-only success round-trips with zero token counters (no requests counted).
        var s3 = sessions[1]!;
        var s3Desktop = s3.ByTarget[(RoutingTarget)"desktop"];
        Assert.Equal(0, s3Desktop.Requests);
        Assert.Equal(0, s3Desktop.InputTokens);
        Assert.Equal(0, s3Desktop.OutputTokens);
        Assert.Equal(150, s3Desktop.ElapsedMilliseconds);

        var s2 = sessions[2]!;
        Assert.Equal(T0, s2.LastSeenUtc);
        var s2Server = s2.ByTarget[(RoutingTarget)"server"];
        Assert.Equal(1, s2Server.Requests);
        Assert.Equal(30, s2Server.InputTokens);
        Assert.Equal(11, s2Server.OutputTokens);
        Assert.Equal(300, s2Server.ElapsedMilliseconds);

        // Case-insensitive parity across the restart.
        Assert.NotNull(store2.TryGetSession("S1"));
        Assert.Equal("s1", store2.TryGetSession("S1")!.SessionId);
        Assert.Null(store2.TryGetSession("missing"));
        Assert.Equal(3, store2.Count);
    }

    [Fact]
    public void Load_PreservesLruOrder_HeadToTail()
    {
        var stateFile = StateFile();
        var store1 = CreateStore(stateFile: stateFile);

        // Interleaved activity: a, b, c, then a again, then d ⇒ head→tail order d, a, c, b.
        store1.Record(Entry("a", timestamp: T0));
        store1.Record(Entry("b", timestamp: T0.AddSeconds(1)));
        store1.Record(Entry("c", timestamp: T0.AddSeconds(2)));
        store1.Record(Entry("a", timestamp: T0.AddSeconds(3)));
        store1.Record(Entry("d", timestamp: T0.AddSeconds(4)));
        store1.Save();

        var store2 = CreateStore(stateFile: stateFile);
        Assert.Equal(["d", "a", "c", "b"], store2.GetSessions().Select(s => s.SessionId).ToArray());
        Assert.Equal(T0.AddSeconds(3), store2.TryGetSession("a")!.LastSeenUtc);
        Assert.Equal(T0.AddSeconds(4), store2.TryGetSession("d")!.LastSeenUtc);
    }

    [Fact]
    public void Load_TruncatesToSessionUsageMaxSessions_KeepingMostRecentlyActive()
    {
        var stateFile = StateFile();
        var store1 = CreateStore(stateFile: stateFile);

        for (var i = 1; i <= 5; i++)
        {
            store1.Record(Entry($"s{i}", actualPrompt: i, actualResponse: i, elapsedMs: i, timestamp: T0.AddSeconds(i)));
        }

        store1.Save();
        Assert.Equal(5, store1.Count);

        // Reload with a SMALLER cap: only the most-recently-active prefix (s5, s4, s3) survives.
        var store2 = CreateStore(maxSessions: 3, stateFile: stateFile);
        var sessions = store2.GetSessions();
        Assert.Equal(["s5", "s4", "s3"], sessions.Select(s => s.SessionId).ToArray());
        Assert.Equal(5, sessions[0]!.ByTarget[(RoutingTarget)"desktop"].OutputTokens); // s5's counters intact
        Assert.Equal(3, sessions[2]!.ByTarget[(RoutingTarget)"desktop"].OutputTokens); // s3's counters intact
    }

    [Fact]
    public void Load_SkipsEmptyOversizedIds_AndKeepsFirstDuplicateCase_AndClampsNegatives()
    {
        var stateFile = StateFile();
        // Hand-written file: an empty ID, a >256-char ID, a case-duplicate (first occurrence =
        // the MRU one must win), and negative (hand-edited) counters that must clamp to 0.
        File.WriteAllText(stateFile, """
            [
              { "SessionId": "", "LastSeenUtc": "2026-11-09T12:00:00+00:00", "ByTarget": { "Desktop": { "Requests": 9 } } },
              { "SessionId": "XXXXX", "LastSeenUtc": "2026-11-09T12:00:00+00:00" },
              { "SessionId": "SESS-DUP", "LastSeenUtc": "2026-11-09T12:00:05+00:00", "ByTarget": { "Desktop": { "Requests": 1, "InputTokens": 10, "CachedInputTokens": 3, "OutputTokens": 2, "ElapsedMilliseconds": 100 } } },
              { "SessionId": "sess-dup", "LastSeenUtc": "2026-11-09T12:00:06+00:00", "ByTarget": { "Desktop": { "Requests": 99 } } },
              { "SessionId": "neg-1", "LastSeenUtc": "2026-11-09T12:00:07+00:00", "ByTarget": { "Desktop": { "Requests": -5, "InputTokens": -1, "CachedInputTokens": -1, "OutputTokens": -1, "ElapsedMilliseconds": -42 } } }
            ]
            """);
        // The oversized ID above ("XXXXX") is only a placeholder — rewrite it as a real 257-char ID.
        var json = File.ReadAllText(stateFile);
        File.WriteAllText(stateFile, json.Replace("\"SessionId\": \"XXXXX\"", $"\"SessionId\": \"{new string('x', 257)}\""));

        var store = CreateStore(stateFile: stateFile);
        var sessions = store.GetSessions();
        // Empty + oversized IDs skipped; the FIRST case-duplicate (SESS-DUP, Requests 1) wins.
        Assert.Equal(["SESS-DUP", "neg-1"], sessions.Select(s => s.SessionId).ToArray());
        var dup = sessions[0]!.ByTarget[(RoutingTarget)"desktop"];
        Assert.Equal(1, dup.Requests);
        Assert.Equal(100, dup.ElapsedMilliseconds);
        var neg = sessions[1]!.ByTarget[(RoutingTarget)"desktop"];
        Assert.Equal(0, neg.Requests);
        Assert.Equal(0, neg.InputTokens);
        Assert.Equal(0, neg.CachedInputTokens);
        Assert.Equal(0, neg.OutputTokens);
        Assert.Equal(0, neg.ElapsedMilliseconds);
    }

    [Fact]
    public void Load_CorruptFile_FailsOpen_WithEmptyStore_NoThrow()
    {
        var stateFile = StateFile();
        File.WriteAllBytes(stateFile, [0xFF, 0xFE, 0x00, 0x01, 0x67, 0x61, 0x72, 0x62, 0x61, 0x67, 0x65]);

        var store = CreateStore(stateFile: stateFile);
        Assert.Empty(store.GetSessions());
        Assert.Equal(0, store.Count);
        Assert.Null(store.TryGetSession("anything"));

        // The store remains fully usable after the failed load.
        store.Record(Entry("fresh", actualPrompt: 1, actualResponse: 1));
        Assert.Equal(["fresh"], store.GetSessions().Select(s => s.SessionId).ToArray());
    }

    [Fact]
    public void Load_MissingFile_StartsEmpty_AndSaveCreatesDirectory()
    {
        // Nonexistent nested directory: load is a silent first run and Save creates it.
        var stateFile = StateFile("nested/deeper/session-usage.json");
        var store = CreateStore(stateFile: stateFile);
        Assert.Empty(store.GetSessions());

        store.Record(Entry("s1", actualPrompt: 7, actualResponse: 3, cached: 1, elapsedMs: 250));
        store.Save();
        Assert.True(File.Exists(stateFile));
        var persisted = File.ReadAllText(stateFile);
        Assert.Contains("\"SessionId\": \"s1\"", persisted);
        Assert.Contains("\"Requests\": 1", persisted);
        Assert.Contains("\"ElapsedMilliseconds\": 250", persisted);

        var store2 = CreateStore(stateFile: stateFile);
        var snapshot = store2.TryGetSession("s1")!;
        var totals = snapshot.ByTarget[(RoutingTarget)"desktop"];
        Assert.Equal(1, totals.Requests);
        Assert.Equal(7, totals.InputTokens);
        Assert.Equal(1, totals.CachedInputTokens);
        Assert.Equal(3, totals.OutputTokens);
        Assert.Equal(250, totals.ElapsedMilliseconds);
    }

    [Fact]
    public void Record_FirstPersistedRecord_SchedulesBackgroundSave_NoTempLeftovers()
    {
        var stateFile = StateFile();
        var store = CreateStore(stateFile: stateFile);
        Assert.False(File.Exists(stateFile));

        store.Record(Entry("s1", actualPrompt: 5, actualResponse: 2, elapsedMs: 90));

        // The throttled Task.Run(Save) must produce the file within 2 s (bounded polling).
        var deadline = Environment.TickCount64 + 2_000;
        while (!File.Exists(stateFile) && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(25);
        }
        Assert.True(File.Exists(stateFile));

        // Settle, then verify no temp leftovers from the atomic write.
        Thread.Sleep(250);
        Assert.Empty(Directory.EnumerateFiles(_dir.FullName, "session-usage.json.tmp-*", SearchOption.AllDirectories));
        Assert.Contains("\"Requests\": 1", File.ReadAllText(stateFile));
    }

    [Fact]
    public void ShutdownFlush_ApplicationStopped_FlushesPendingState()
    {
        var stateFile = StateFile();
        var lifetime = new FakeHostApplicationLifetime();
        var store = CreateStore(stateFile: stateFile, lifetime: lifetime);

        // Record WITHOUT an explicit Save (only the throttled background save may have run so
        // far); the graceful-shutdown flush must synchronously persist the pending state.
        store.Record(Entry("s1", target: (RoutingTarget)"desktop", actualPrompt: 9, actualResponse: 4, cached: 1, elapsedMs: 120, timestamp: T0));
        store.Record(Entry("s2", target: RoutingTarget.Cloud, actualPrompt: 2, actualResponse: 1, elapsedMs: 30, timestamp: T0.AddSeconds(1)));

        lifetime.StopApplication();

        // Give any racing background save a moment to settle (both write the SAME snapshot —
        // no further records — so the file content is deterministic either way).
        Thread.Sleep(250);
        var store2 = CreateStore(stateFile: stateFile);
        var sessions = store2.GetSessions();
        Assert.Equal(["s2", "s1"], sessions.Select(s => s.SessionId).ToArray());
        Assert.Equal(9, sessions[1]!.ByTarget[(RoutingTarget)"desktop"].InputTokens);
        Assert.Equal(120, sessions[1]!.ByTarget[(RoutingTarget)"desktop"].ElapsedMilliseconds);
        Assert.Equal(2, sessions[0]!.ByTarget[RoutingTarget.Cloud].InputTokens);
    }

    [Fact]
    public void InMemoryOnly_NoStateFile_NeverTouchesDisk()
    {
        // Default constructor (no state file): strictly in-memory — records and an explicit
        // Save() must never create the file or any directory content.
        var stateFile = StateFile();
        var store = CreateStore();
        store.Record(Entry("s1", actualPrompt: 3, actualResponse: 1, elapsedMs: 40));
        store.Save(); // no-op in in-memory mode

        Assert.False(File.Exists(stateFile));
        Assert.Empty(Directory.EnumerateFiles(_dir.FullName, "*", SearchOption.AllDirectories));
        Assert.Equal(["s1"], store.GetSessions().Select(s => s.SessionId).ToArray());
    }

    // Bounded-poll counterpart of ActivityStatisticsServiceTests.WaitForPendingSaves (never hangs).
    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted { get; } = CancellationToken.None;
        public CancellationToken ApplicationStarting { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopped => stopped.Token;

        public void StopApplication() => stopped.Cancel();
    }
}
