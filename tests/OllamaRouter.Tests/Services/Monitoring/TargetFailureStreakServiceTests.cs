using Xunit;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Services.Monitoring;

public class TargetFailureStreakServiceTests
{
    [Fact]
    public void GetStreak_WhenNoFailures_ReturnsZero()
    {
        var sut = new TargetFailureStreakService();

        Assert.Equal(0, sut.GetStreak(RoutingTarget.Remote));
    }

    [Fact]
    public void RecordFailure_IncrementsStreakMonotonically()
    {
        var sut = new TargetFailureStreakService();

        sut.RecordFailure(RoutingTarget.Remote);
        Assert.Equal(1, sut.GetStreak(RoutingTarget.Remote));

        sut.RecordFailure(RoutingTarget.Remote);
        sut.RecordFailure(RoutingTarget.Remote);
        Assert.Equal(3, sut.GetStreak(RoutingTarget.Remote));
    }

    [Fact]
    public void RecordFailure_TracksEachTargetIndependently()
    {
        var sut = new TargetFailureStreakService();

        sut.RecordFailure(RoutingTarget.Local);
        sut.RecordFailure(RoutingTarget.Local);
        sut.RecordFailure(RoutingTarget.Remote);

        Assert.Equal(2, sut.GetStreak(RoutingTarget.Local));
        Assert.Equal(1, sut.GetStreak(RoutingTarget.Remote));
    }

    [Fact]
    public void RecordSuccess_ResetsStreakToZero()
    {
        var sut = new TargetFailureStreakService();

        sut.RecordFailure(RoutingTarget.Remote);
        sut.RecordFailure(RoutingTarget.Remote);
        sut.RecordSuccess(RoutingTarget.Remote);

        Assert.Equal(0, sut.GetStreak(RoutingTarget.Remote));
    }

    [Fact]
    public void RecordSuccess_WhenNoFailures_RemainsZero()
    {
        var sut = new TargetFailureStreakService();

        sut.RecordSuccess(RoutingTarget.Remote);

        Assert.Equal(0, sut.GetStreak(RoutingTarget.Remote));
    }

    [Fact]
    public void ShouldSkip_ReturnsTrueOnlyWhenStreakExceedsThreshold()
    {
        var sut = new TargetFailureStreakService();
        // Threshold semantics: "above" = streak > maxStreak (strictly greater).
        for (var i = 0; i < 3; i++)
        {
            sut.RecordFailure(RoutingTarget.Remote);
        }

        // 3 is NOT above 3.
        Assert.False(sut.ShouldSkip(RoutingTarget.Remote, 3));

        // 4 IS above 3.
        sut.RecordFailure(RoutingTarget.Remote);
        Assert.True(sut.ShouldSkip(RoutingTarget.Remote, 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldSkip_WhenMaxStreakNonPositive_DisablesSkipping(int maxStreak)
    {
        var sut = new TargetFailureStreakService();

        for (var i = 0; i < 10; i++)
        {
            sut.RecordFailure(RoutingTarget.Remote);
        }

        // "Set to 0 to disable": a non-positive threshold never skips.
        Assert.False(sut.ShouldSkip(RoutingTarget.Remote, maxStreak));
    }

    [Fact]
    public void GetSnapshot_ReturnsOnlyTrackedTargets_WithCurrentCounts()
    {
        var sut = new TargetFailureStreakService();

        sut.RecordFailure(RoutingTarget.Local);
        sut.RecordFailure(RoutingTarget.Local);
        sut.RecordFailure(RoutingTarget.Remote);

        var snapshot = sut.GetSnapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(2, snapshot[RoutingTarget.Local]);
        Assert.Equal(1, snapshot[RoutingTarget.Remote]);
        Assert.False(snapshot.ContainsKey(RoutingTarget.Cloud));
    }

    [Fact]
    public void GetSnapshot_WhenNothingTracked_IsEmpty()
    {
        var sut = new TargetFailureStreakService();

        Assert.Empty(sut.GetSnapshot());
    }

    // ------------------------------------------------------------------
    // Task #94: failure-streak circuit breaker (cooldown + half-open trial)
    // ------------------------------------------------------------------

    private static (TargetFailureStreakService Sut, ManualTimeProvider Clock) BuildCircuit(int failures = 0)
    {
        var clock = new ManualTimeProvider();
        var sut = new TargetFailureStreakService(clock);
        for (var i = 0; i < failures; i++)
        {
            sut.RecordFailure(RoutingTarget.Remote);
        }

        return (sut, clock);
    }

    [Fact]
    public void GetCircuitState_Closed_BelowAndAtThreshold()
    {
        var (sut, _) = BuildCircuit(failures: 3);
        var cooldown = TimeSpan.FromSeconds(60);

        // streak 3 is NOT above 3 (Closed), and skipping is disabled (maxStreak <= 0 ⇒ Closed).
        Assert.Equal(FailureStreakCircuitState.Closed, sut.GetCircuitState(RoutingTarget.Remote, 3, cooldown));
        Assert.Equal(FailureStreakCircuitState.Closed, sut.GetCircuitState(RoutingTarget.Remote, 0, cooldown));
        Assert.Equal(FailureStreakCircuitState.Closed, sut.GetCircuitState(RoutingTarget.Remote, -5, cooldown));

        // An untracked target is always Closed.
        Assert.Equal(FailureStreakCircuitState.Closed, sut.GetCircuitState(RoutingTarget.Local, 3, cooldown));
    }

    [Fact]
    public void ShouldSkip_CooldownActive_TrippedTargetSkippedWhileOpen()
    {
        var (sut, clock) = BuildCircuit(failures: 4); // 4 > 3 ⇒ tripped
        var cooldown = TimeSpan.FromSeconds(60);

        // Inside the cooldown window: Open ⇒ skip.
        Assert.Equal(FailureStreakCircuitState.Open, sut.GetCircuitState(RoutingTarget.Remote, 3, cooldown));
        Assert.True(sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));

        // Halfway through the window still Open.
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(FailureStreakCircuitState.Open, sut.GetCircuitState(RoutingTarget.Remote, 3, cooldown));
        Assert.True(sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));
    }

    [Fact]
    public void ShouldSkip_CooldownElapsed_TargetHalfOpen()
    {
        var (sut, clock) = BuildCircuit(failures: 4); // 4 > 3 ⇒ tripped
        var cooldown = TimeSpan.FromSeconds(60);

        Assert.Equal(FailureStreakCircuitState.Open, sut.GetCircuitState(RoutingTarget.Remote, 3, cooldown));

        // Advance the clock past the window: HalfOpen ⇒ NOT skipped (no trial in flight).
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(FailureStreakCircuitState.HalfOpen, sut.GetCircuitState(RoutingTarget.Remote, 3, cooldown));
        Assert.False(sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));
    }

    [Fact]
    public void ShouldSkip_TrippedTarget_WithCooldownZero_IsImmediatelyHalfOpen()
    {
        var (sut, _) = BuildCircuit(failures: 4); // 4 > 3 ⇒ tripped

        // A zero window disables the Open phase: a tripped target is immediately HalfOpen.
        Assert.Equal(FailureStreakCircuitState.HalfOpen, sut.GetCircuitState(RoutingTarget.Remote, 3, TimeSpan.Zero));
        Assert.False(sut.ShouldSkip(RoutingTarget.Remote, 3, TimeSpan.Zero));
    }

    [Fact]
    public void TryReserveTrial_SingleConcurrentLease()
    {
        var (sut, clock) = BuildCircuit(failures: 4); // 4 > 3 ⇒ tripped
        var cooldown = TimeSpan.FromSeconds(60);
        clock.Advance(TimeSpan.FromSeconds(61)); // HalfOpen

        // First claim wins; the second is rejected (single in-flight trial slot).
        Assert.True(sut.TryReserveTrial(RoutingTarget.Remote, 3, cooldown));
        Assert.False(sut.TryReserveTrial(RoutingTarget.Remote, 3, cooldown));

        // While leased, a HalfOpen target IS skipped by other requests.
        Assert.True(sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));

        // After release the slot is reservable again (and ShouldSkip goes back to false).
        sut.ReleaseTrial(RoutingTarget.Remote);
        Assert.False(sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));
        Assert.True(sut.TryReserveTrial(RoutingTarget.Remote, 3, cooldown));

        // ReleaseTrial is idempotent.
        sut.ReleaseTrial(RoutingTarget.Remote);
        sut.ReleaseTrial(RoutingTarget.Remote);
        Assert.True(sut.TryReserveTrial(RoutingTarget.Remote, 3, cooldown));
        sut.ReleaseTrial(RoutingTarget.Remote);
    }

    [Theory]
    [InlineData(true)]   // inside the cooldown (Open)
    [InlineData(false)]  // untripped (Closed)
    public void TryReserveTrial_OpenOrClosed_ReturnsFalse(bool insideCooldown)
    {
        var (sut, clock) = BuildCircuit(failures: insideCooldown ? 4 : 0);
        var cooldown = TimeSpan.FromSeconds(60);

        Assert.False(sut.TryReserveTrial(RoutingTarget.Remote, 3, cooldown));

        if (!insideCooldown)
        {
            // Even after the cooldown elapses an untripped target never leases.
            clock.Advance(TimeSpan.FromSeconds(120));
            Assert.False(sut.TryReserveTrial(RoutingTarget.Remote, 3, cooldown));
        }

        // Nothing is leased in either case: ShouldSkip on a fresh tripped target stays the gate's word.
        Assert.Equal(insideCooldown, sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));
    }

    [Fact]
    public void RecordSuccess_ClosesCircuitAndClearsLastFailure()
    {
        var (sut, _) = BuildCircuit(failures: 5); // 5 > 3 ⇒ tripped
        var cooldown = TimeSpan.FromSeconds(60);

        Assert.NotNull(sut.GetLastFailureUtc(RoutingTarget.Remote));

        sut.RecordSuccess(RoutingTarget.Remote);

        Assert.Equal(0, sut.GetStreak(RoutingTarget.Remote));
        Assert.Null(sut.GetLastFailureUtc(RoutingTarget.Remote));
        Assert.Equal(FailureStreakCircuitState.Closed, sut.GetCircuitState(RoutingTarget.Remote, 3, cooldown));
        Assert.False(sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));
    }

    [Fact]
    public void RecordFailure_ReArmsCooldown()
    {
        var (sut, clock) = BuildCircuit(failures: 4); // 4 > 3 ⇒ tripped
        var cooldown = TimeSpan.FromSeconds(60);
        clock.Advance(TimeSpan.FromSeconds(61)); // HalfOpen

        // A trial failure from HalfOpen increments the streak and refreshes the timestamp:
        // the circuit is Open again IMMEDIATELY (no further clock advance needed).
        sut.RecordFailure(RoutingTarget.Remote);

        Assert.Equal(5, sut.GetStreak(RoutingTarget.Remote));
        Assert.Equal(clock.UtcNow, sut.GetLastFailureUtc(RoutingTarget.Remote));
        Assert.Equal(FailureStreakCircuitState.Open, sut.GetCircuitState(RoutingTarget.Remote, 3, cooldown));
        Assert.True(sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));

        // ...and for a full window after.
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));
    }

    [Fact]
    public void RecordFailure_ClearsTrialLeaseAsSafetyNet()
    {
        var (sut, clock) = BuildCircuit(failures: 4); // 4 > 3 ⇒ tripped
        var cooldown = TimeSpan.FromSeconds(60);
        clock.Advance(TimeSpan.FromSeconds(61)); // HalfOpen

        Assert.True(sut.TryReserveTrial(RoutingTarget.Remote, 3, cooldown));
        Assert.True(sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));

        // A new failure re-arms the Open window AND defensively releases the lease.
        sut.RecordFailure(RoutingTarget.Remote);

        Assert.Equal(FailureStreakCircuitState.Open, sut.GetCircuitState(RoutingTarget.Remote, 3, cooldown));
        Assert.True(sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));

        // After the re-armed window elapses the circuit is HalfOpen again and the slot is
        // FREE (the lease was defensively cleared by the failure, never stranded).
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(FailureStreakCircuitState.HalfOpen, sut.GetCircuitState(RoutingTarget.Remote, 3, cooldown));
        Assert.False(sut.ShouldSkip(RoutingTarget.Remote, 3, cooldown));
        Assert.True(sut.TryReserveTrial(RoutingTarget.Remote, 3, cooldown));
        sut.ReleaseTrial(RoutingTarget.Remote);
    }

    [Fact]
    public void ResetStreak_ClearsStreakAndTimestamp()
    {
        var (sut, _) = BuildCircuit(failures: 7); // 7 > 3 ⇒ tripped

        Assert.Equal(7, sut.GetStreak(RoutingTarget.Remote));
        Assert.NotNull(sut.GetLastFailureUtc(RoutingTarget.Remote));

        sut.ResetStreak(RoutingTarget.Remote);

        Assert.Equal(0, sut.GetStreak(RoutingTarget.Remote));
        Assert.Null(sut.GetLastFailureUtc(RoutingTarget.Remote));
    }

    [Fact]
    public void ResetStreak_UntrackedTarget_IsNoOpWithoutCreatingEntry()
    {
        var sut = new TargetFailureStreakService();

        sut.ResetStreak(RoutingTarget.Remote);

        Assert.Empty(sut.GetSnapshot());
        Assert.Equal(0, sut.GetStreak(RoutingTarget.Remote));
        Assert.Null(sut.GetLastFailureUtc(RoutingTarget.Remote));
    }

    [Fact]
    public void GetLastFailureUtc_UntrackedTarget_ReturnsNull()
    {
        var sut = new TargetFailureStreakService();

        Assert.Null(sut.GetLastFailureUtc(RoutingTarget.Cloud));
    }
}
