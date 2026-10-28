using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Services;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// Tests for the thread-safe sliding-TTL session-affinity store
/// (<see cref="SessionAffinityService"/>): case-insensitive round-trip, sliding TTL with an
/// injected clock, single-session removal, target-wide invalidation, and the bounded lazy
/// sweep on large tables.
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
            MsOptions.Create(new OllamaRouterOptions { SessionAffinityTtlMinutes = ttlMinutes }),
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
            MsOptions.Create(new OllamaRouterOptions()),
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
            MsOptions.Create(new OllamaRouterOptions { SessionAffinityTtlMinutes = 0 }),
            NullLogger<SessionAffinityService>.Instance,
            () => clock.Now);
        service.SetAffinity("agent-1", new RoutingTarget("aiserver1"));

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.True(service.TryGetAffinity("agent-1", out _));

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False(service.TryGetAffinity("agent-1", out _));
    }
}
