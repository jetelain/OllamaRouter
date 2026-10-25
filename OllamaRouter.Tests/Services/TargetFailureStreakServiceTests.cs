using OllamaRouter.Services;
using Xunit;

namespace OllamaRouter.Tests.Services;

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
}
