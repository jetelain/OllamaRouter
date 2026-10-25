using System.Collections.Concurrent;

namespace OllamaRouter.Services;

/// <summary>
/// Thread-safe, in-memory implementation of <see cref="ITargetFailureStreakService"/> backed
/// by a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed on
/// <see cref="RoutingTarget"/> (case-insensitive value-based equality is safe as a dictionary
/// key). Singleton lifetime; state is intentionally NOT persisted (streaks are a short-lived
/// transport-reliability signal that self-heals through the liveness re-prober plus the
/// next successful request).
/// </summary>
public sealed class TargetFailureStreakService : ITargetFailureStreakService
{
    private readonly ConcurrentDictionary<RoutingTarget, StreakEntry> _streaks = new();

    public void RecordFailure(RoutingTarget target)
    {
        _streaks.AddOrUpdate(target, _ => new StreakEntry(1), (_, existing) =>
        {
            lock (existing)
            {
                return new StreakEntry(existing.Count + 1);
            }
        });
    }

    public void RecordSuccess(RoutingTarget target)
    {
        if (_streaks.TryGetValue(target, out var entry))
        {
            lock (entry)
            {
                entry.Count = 0;
            }
        }
    }

    public int GetStreak(RoutingTarget target)
    {
        return _streaks.TryGetValue(target, out var entry)
            ? entry.ReadCount()
            : 0;
    }

    public bool ShouldSkip(RoutingTarget target, int maxStreak)
    {
        if (maxStreak <= 0)
        {
            return false;
        }

        return GetStreak(target) > maxStreak;
    }

    public IReadOnlyDictionary<RoutingTarget, int> GetSnapshot()
    {
        var result = new Dictionary<RoutingTarget, int>();
        foreach (var (target, entry) in _streaks)
        {
            result[target] = entry.ReadCount();
        }

        return result;
    }

    private sealed class StreakEntry(int count)
    {
        public int Count { get; set; } = count;

        public int ReadCount()
        {
            lock (this)
            {
                return Count;
            }
        }
    }
}
