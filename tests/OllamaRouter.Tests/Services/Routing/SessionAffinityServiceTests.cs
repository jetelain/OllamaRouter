using OllamaRouter.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Tests.Services.Routing;

/// <summary>
/// Tests for the thread-safe sliding-TTL session-affinity store
/// (<see cref="SessionAffinityService"/>): case-insensitive round-trip, sliding TTL with an
/// injected clock, single-session removal, target-wide invalidation, the bounded lazy
/// sweep on large tables, the task #77 read-only active-session reservation-window
/// queries (occupancy counts, other-session reservation, self-exemption, TTL non-refresh),
/// and the task #86 atomic <c>TryRemoveAffinity</c> live-lease classification.
/// </summary>
public class SessionAffinityServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; }

        public void Advance(TimeSpan delta) => Now += delta;
    }

    private static (SessionAffinityService Service, FakeClock Clock) Build(int ttlMinutes = 30)
    {
        var clock = new FakeClock { Now = Start };
        var service = new SessionAffinityService(
            new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions { SessionAffinityTtlMinutes = ttlMinutes }),
            NullLogger<SessionAffinityService>.Instance,
            () => clock.Now);
        return (service, clock);
    }

    [Fact]
    public void SetThenTryGet_RoundTrips_Target()
    {
        var (service, _) = Build();

        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));

        Assert.True(service.TryGetAffinity("agent-1", out var target));
        Assert.Equal(new RoutingTarget("aiserver1"), target);
    }

    [Fact]
    public void TryGet_UnknownSession_ReturnsFalse()
    {
        var (service, _) = Build();

        Assert.False(service.TryGetAffinity("nope", out _));
        Assert.False(service.TryGetAffinity("", out _));
        Assert.False(service.TryGetAffinity("   ", out _));
    }

    [Theory]
    [InlineData("agent-1", "agent-1")]
    [InlineData("AGENT-1", "agent-1")]
    [InlineData("Agent-1", "agent-1")]
    public void SessionIdKeys_AreCaseInsensitive(string setKey, string getKey)
    {
        var (service, _) = Build();

        service.SetAffinity(setKey, new RoutingTarget("aiserver1"));

        Assert.True(service.TryGetAffinity(getKey, out var target));
        Assert.Equal(new RoutingTarget("aiserver1"), target);
    }

    [Fact]
    public void TargetNameMatching_IsCaseInsensitive()
    {
        var (service, _) = Build();

        service.SetAffinity("agent-1", new RoutingTarget("AIsErVeR1"));

        Assert.True(service.TryGetAffinity("agent-1", out var target));
        Assert.Equal(new RoutingTarget("aiserver1"), target);

        // InvalidateTarget uses the same case-insensitive comparison.
        service.InvalidateTarget(new RoutingTarget("AISERVER1"));
        Assert.False(service.TryGetAffinity("agent-1", out _));
    }

    [Fact]
    public void SlidingTtl_TouchBeforeExpiry_KeepsLeaseAlive()
    {
        var (service, clock) = Build(ttlMinutes: 30);
        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));

        // 20 minutes idle, then a touch refreshes the window...
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.True(service.TryGetAffinity("agent-1", out _));

        // ...so 29 minutes after the TOUCH is still inside the 30-minute window.
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.True(service.TryGetAffinity("agent-1", out _));
    }

    [Fact]
    public void SlidingTtl_ExpiryAfterTtlOfInactivity_ReturnsFalseAndRemoves()
    {
        var (service, clock) = Build(ttlMinutes: 30);
        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));

        clock.Advance(TimeSpan.FromMinutes(31));

        Assert.False(service.TryGetAffinity("agent-1", out _));
        // The expired entry was lazily removed (a subsequent set is a fresh lease).
        service.SetAffinity("agent-1", new RoutingTarget("aiserver2"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(service.TryGetAffinity("agent-1", out var t));
        Assert.Equal(new RoutingTarget("aiserver2"), t);
    }

    [Fact]
    public void SetAffinity_OverwritesExistingLease()
    {
        var (service, _) = Build();

        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));
        service.SetAffinity("agent-1", new RoutingTarget("aiserver2"));

        Assert.True(service.TryGetAffinity("agent-1", out var target));
        Assert.Equal(new RoutingTarget("aiserver2"), target);
    }

    [Fact]
    public void Cloud_IsNeverLeased()
    {
        var (service, _) = Build();

        service.SetAffinity("agent-1", RoutingTarget.Cloud);

        Assert.False(service.TryGetAffinity("agent-1", out _));
    }

    [Fact]
    public void RemoveAffinity_ClearsOnlyThatSession()
    {
        var (service, _) = Build();

        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));
        service.SetAffinity("agent-2", new RoutingTarget("aiserver2"));
        service.RemoveAffinity("agent-1");

        Assert.False(service.TryGetAffinity("agent-1", out _));
        Assert.True(service.TryGetAffinity("agent-2", out _));
    }

    [Fact]
    public void RemoveAffinity_UnknownSession_IsNoOp()
    {
        var (service, _) = Build();

        service.RemoveAffinity("ghost");
        service.RemoveAffinity(null!);
        service.RemoveAffinity("");

        // Nothing throws and nothing is stored.
        Assert.False(service.TryGetAffinity("ghost", out _));
    }

    [Fact]
    public void InvalidateTarget_ClearsAllSessionsPinnedToThatTarget_OthersIntact()
    {
        var (service, _) = Build();

        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));
        service.SetAffinity("agent-2", new RoutingTarget("aiserver1"));
        service.SetAffinity("agent-3", new RoutingTarget("aiserver2"));

        service.InvalidateTarget(new RoutingTarget("aiserver1"));

        Assert.False(service.TryGetAffinity("agent-1", out _));
        Assert.False(service.TryGetAffinity("agent-2", out _));
        Assert.True(service.TryGetAffinity("agent-3", out _));
    }

    [Fact]
    public void LazySweep_LargeTable_MostlyExpired_RemovesExpired_KeepsLive()
    {
        var (service, clock) = Build(ttlMinutes: 30);

        // 150 leases: 140 expired, 10 live (set AFTER the advance).
        for (var i = 0; i < 140; i++)
        {
            service.SetAffinity($"expired-{i}", new RoutingTarget($"t{i % 3}"));
        }

        clock.Advance(TimeSpan.FromMinutes(31));

        for (var i = 0; i < 10; i++)
        {
            service.SetAffinity($"live-{i}", new RoutingTarget($"t{i % 3}"));
        }

        // A TryGet on a live entry triggers the bounded sweep (table > 100 entries) and the
        // live entry must survive the sweep.
        Assert.True(service.TryGetAffinity("live-0", out var target));
        Assert.Equal(new RoutingTarget("t0"), target);

        // Expired entries report false; the store keeps shrinking as operations continue.
        Assert.False(service.TryGetAffinity("expired-0", out _));
    }

    [Fact]
    public void DefaultTtl_IsThirtyMinutes()
    {
        var clock = new FakeClock { Now = Start };
        var service = new SessionAffinityService(
            new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions()),
            NullLogger<SessionAffinityService>.Instance,
            () => clock.Now);
        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));

        // A 29-min-old (touched) lease is still alive; after 30 more minutes of
        // inactivity from that touch the default 30-minute window has lapsed.
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.True(service.TryGetAffinity("agent-1", out _));

        clock.Advance(TimeSpan.FromMinutes(31)); // 31 min since the last touch
        Assert.False(service.TryGetAffinity("agent-1", out _));
    }

    [Fact]
    public void NonPositiveTtl_FallsBackToDefaultThirtyMinutes()
    {
        var clock = new FakeClock { Now = Start };
        var service = new SessionAffinityService(
            new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions { SessionAffinityTtlMinutes = 0 }),
            NullLogger<SessionAffinityService>.Instance,
            () => clock.Now);
        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.True(service.TryGetAffinity("agent-1", out _));

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False(service.TryGetAffinity("agent-1", out _));
    }

    // ==================================================================
    // Task #77 — active-session reservation window (read-only queries)
    // ==================================================================

    [Fact]
    public void GetActiveSessionCount_FreshLease_CountedWithinWindow()
    {
        var (service, _) = Build();
        var t1 = new RoutingTarget("aiserver1");

        service.SetAffinity("agent-1", t1);

        Assert.Equal(1, service.GetActiveSessionCount(t1, TimeSpan.FromSeconds(30)));
        // A different target is never counted (case-insensitive name match).
        Assert.Equal(0, service.GetActiveSessionCount(new RoutingTarget("aiserver2"), TimeSpan.FromSeconds(30)));
        Assert.Equal(0, service.GetActiveSessionCount(new RoutingTarget("AISERVER9"), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void GetActiveSessionCount_WindowElapsed_NotCounted_RepinRestores()
    {
        var (service, clock) = Build();
        var t1 = new RoutingTarget("aiserver1");
        service.SetAffinity("agent-1", t1);

        clock.Advance(TimeSpan.FromSeconds(31)); // 1 s past the 30 s window

        Assert.Equal(0, service.GetActiveSessionCount(t1, TimeSpan.FromSeconds(30)));

        // A re-pin re-arms the sliding timestamp and counts again.
        service.SetAffinity("agent-1", t1);
        Assert.Equal(1, service.GetActiveSessionCount(t1, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void GetActiveSessionCount_MultipleSessionsSameTarget_Counted_OthersUnaffected()
    {
        var (service, _) = Build();
        var t1 = new RoutingTarget("aiserver1");
        var t2 = new RoutingTarget("aiserver2");

        service.SetAffinity("agent-1", t1);
        service.SetAffinity("agent-2", t1);
        service.SetAffinity("agent-3", t2);

        Assert.Equal(2, service.GetActiveSessionCount(t1, TimeSpan.FromSeconds(30)));
        Assert.Equal(1, service.GetActiveSessionCount(t2, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void GetActiveSessionCount_ZeroOrNegativeWindow_AlwaysZero()
    {
        var (service, _) = Build();
        var t1 = new RoutingTarget("aiserver1");
        service.SetAffinity("agent-1", t1);

        Assert.Equal(0, service.GetActiveSessionCount(t1, TimeSpan.Zero));
        Assert.Equal(0, service.GetActiveSessionCount(t1, TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void IsTargetReservedByOtherSession_OtherSessionActive_True()
    {
        var (service, _) = Build();
        var t1 = new RoutingTarget("aiserver1");
        service.SetAffinity("agent-1", t1);

        Assert.True(service.IsTargetReservedByOtherSession(t1, "agent-2", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void IsTargetReservedByOtherSession_OwnSessionExempt_False_CaseInsensitive()
    {
        var (service, _) = Build();
        var t1 = new RoutingTarget("aiserver1");
        service.SetAffinity("AGENT-1", t1);

        // The leaser's own lease never counts as "another active session" (case-insensitive
        // ID match) — sticky affinity on the session's own target is preserved.
        Assert.False(service.IsTargetReservedByOtherSession(t1, "agent-1", TimeSpan.FromSeconds(30)));
        Assert.False(service.IsTargetReservedByOtherSession(t1, "Agent-1", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void IsTargetReservedByOtherSession_WindowElapsed_False()
    {
        var (service, clock) = Build();
        var t1 = new RoutingTarget("aiserver1");
        service.SetAffinity("agent-1", t1);

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.False(service.IsTargetReservedByOtherSession(t1, "agent-2", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void IsTargetReservedByOtherSession_NullOrBlankCurrentSession_AnyActiveLeaseCounts()
    {
        var (service, _) = Build();
        var t1 = new RoutingTarget("aiserver1");
        service.SetAffinity("agent-1", t1);

        Assert.True(service.IsTargetReservedByOtherSession(t1, null, TimeSpan.FromSeconds(30)));
        Assert.True(service.IsTargetReservedByOtherSession(t1, "", TimeSpan.FromSeconds(30)));
        Assert.True(service.IsTargetReservedByOtherSession(t1, "   ", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void IsTargetReservedByOtherSession_AfterInvalidateTarget_False()
    {
        var (service, _) = Build();
        var t1 = new RoutingTarget("aiserver1");
        service.SetAffinity("agent-1", t1);

        service.InvalidateTarget(t1);

        Assert.False(service.IsTargetReservedByOtherSession(t1, "agent-2", TimeSpan.FromSeconds(30)));
        Assert.Equal(0, service.GetActiveSessionCount(t1, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Queries_AreReadOnly_DoesNotRefreshSlidingTtl()
    {
        // A reservation-window query must NOT re-arm the sliding TTL (evaluating candidates
        // for session 2 must never extend session 1's reservation): the lease still expires
        // exactly 30 minutes after the last REAL touch, even though queries ran at +29 min.
        var (service, clock) = Build(ttlMinutes: 30);
        var t1 = new RoutingTarget("aiserver1");
        service.SetAffinity("agent-1", t1);

        clock.Advance(TimeSpan.FromMinutes(29)); // inside the 30-min TTL, far outside any window
        Assert.Equal(0, service.GetActiveSessionCount(t1, TimeSpan.FromSeconds(30)));
        Assert.False(service.IsTargetReservedByOtherSession(t1, "agent-2", TimeSpan.FromSeconds(30)));

        clock.Advance(TimeSpan.FromMinutes(2));  // 31 min since the last real touch
        Assert.False(service.TryGetAffinity("agent-1", out _));
    }

    // ==================================================================
    // Task #86 — TryRemoveAffinity (atomic remove + live-lease classification)
    // ==================================================================

    [Fact]
    public void TryRemoveAffinity_LiveLease_ReturnsTrue_AndLeaseIsGone()
    {
        var (service, _) = Build();
        var t1 = new RoutingTarget("aiserver1");
        service.SetAffinity("agent-1", t1);

        Assert.True(service.TryRemoveAffinity("agent-1"));
        Assert.False(service.TryGetAffinity("agent-1", out _));
    }

    [Fact]
    public void TryRemoveAffinity_UnknownOrBlank_ReturnsFalse_AndDoesNotThrow()
    {
        var (service, _) = Build();

        Assert.False(service.TryRemoveAffinity("ghost"));
        Assert.False(service.TryRemoveAffinity(null!));
        Assert.False(service.TryRemoveAffinity(""));
        Assert.False(service.TryRemoveAffinity("   "));
    }

    [Fact]
    public void TryRemoveAffinity_KeyIsCaseInsensitive()
    {
        var (service, _) = Build();
        service.SetAffinity("AGENT-1", new RoutingTarget("aiserver1"));

        Assert.True(service.TryRemoveAffinity("agent-1"));
        Assert.False(service.TryGetAffinity("Agent-1", out _));
    }

    [Fact]
    public void TryRemoveAffinity_TtlExpiredNotYetSwept_ReturnsFalse_AndEvictsTheEntry()
    {
        var (service, clock) = Build(ttlMinutes: 30);
        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));

        clock.Advance(TimeSpan.FromMinutes(31)); // expired; single entry ⇒ no LazySweep can have run

        Assert.False(service.TryRemoveAffinity("agent-1"));
        Assert.False(service.TryGetAffinity("agent-1", out _), "the expired entry must be evicted");
    }

    [Fact]
    public void TryRemoveAffinity_RemovesOnlyThatSession_OthersIntact()
    {
        var (service, _) = Build();
        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));
        service.SetAffinity("agent-2", new RoutingTarget("aiserver2"));

        Assert.True(service.TryRemoveAffinity("agent-1"));

        Assert.False(service.TryGetAffinity("agent-1", out _));
        Assert.True(service.TryGetAffinity("agent-2", out _));
    }

    [Fact]
    public void TryRemoveAffinity_AfterSlidingTtlRefresh_RemovedLeaseWasLive_True()
    {
        var (service, clock) = Build(ttlMinutes: 30);
        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));

        // 20 minutes idle, then a hit refreshes the sliding window...
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.True(service.TryGetAffinity("agent-1", out _));

        // ...so 29 minutes after the TOUCH the lease is still live and its removal reports
        // true (the refresh made it live again — the 20 min before the touch don't count).
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.True(service.TryRemoveAffinity("agent-1"));
    }

    [Fact]
    public void TryRemoveAffinity_Idempotent_SecondCallReturnsFalse()
    {
        var (service, _) = Build();
        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));

        Assert.True(service.TryRemoveAffinity("agent-1"));
        Assert.False(service.TryRemoveAffinity("agent-1"));
    }
}
