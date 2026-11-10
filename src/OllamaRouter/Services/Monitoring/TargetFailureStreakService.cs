using System.Collections.Concurrent;
using System.Diagnostics;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Services.Monitoring;

/// <summary>
/// Thread-safe, in-memory implementation of <see cref="ITargetFailureStreakService"/> backed
/// by a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed on
/// <see cref="RoutingTarget"/> (case-insensitive value-based equality is safe as a dictionary
/// key). Singleton lifetime; state is intentionally NOT persisted (streaks are a short-lived
/// transport-reliability signal that self-heals through the liveness re-prober plus the
/// next successful request).
/// <para>
/// Task #94: the entry now also carries the last-failure timestamp (re-arming the
/// circuit-breaker cooldown on every failure) and a single-trial lease (at most one in-flight
/// half-open trial request per target). The clock is injectable (default
/// <see cref="TimeProvider.System"/>) for deterministic testing; the DI registration stays
/// parameterless.
/// </para>
/// </summary>
public sealed class TargetFailureStreakService : ITargetFailureStreakService
{
    private readonly ConcurrentDictionary<RoutingTarget, StreakEntry> _streaks = new();
    private readonly TimeProvider _clock;

    public TargetFailureStreakService(TimeProvider? timeProvider = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
    }

    public void RecordFailure(RoutingTarget target)
    {
        var entry = _streaks.GetOrAdd(target, _ => new StreakEntry());
        lock (entry)
        {
            entry.Count++;
            entry.LastFailureUtc = _clock.GetUtcNow();
            // Idempotent safety net: a new failure ends any in-flight trial and re-arms the
            // cooldown, so no path can strand a lease.
            entry.TrialLeased = false;
        }
    }

    public void RecordSuccess(RoutingTarget target)
    {
        if (_streaks.TryGetValue(target, out var entry))
        {
            lock (entry)
            {
                entry.Count = 0;
                entry.LastFailureUtc = null;
                entry.TrialLeased = false;
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

    public bool ShouldSkip(RoutingTarget target, int maxStreak, TimeSpan cooldown)
    {
        return GetCircuitState(target, maxStreak, cooldown) switch
        {
            FailureStreakCircuitState.Closed => false,
            FailureStreakCircuitState.Open => true,
            // HalfOpen: skip only while ANOTHER request holds the single trial slot.
            _ => IsTrialLeased(target)
        };
    }

    public FailureStreakCircuitState GetCircuitState(RoutingTarget target, int maxStreak, TimeSpan cooldown)
    {
        if (maxStreak <= 0 || GetStreak(target) <= maxStreak)
        {
            return FailureStreakCircuitState.Closed;
        }

        // Tripped. Read the last-failure stamp under the entry lock.
        if (!_streaks.TryGetValue(target, out var entry))
        {
            return FailureStreakCircuitState.Closed;
        }

        var now = _clock.GetUtcNow();
        DateTimeOffset? lastFailure;
        lock (entry)
        {
            lastFailure = entry.LastFailureUtc;
        }

        TimeSpan? elapsed = lastFailure is { } stamped ? now - stamped : null;
        return cooldown <= TimeSpan.Zero || (elapsed is { } el && el >= cooldown)
            ? FailureStreakCircuitState.HalfOpen
            : FailureStreakCircuitState.Open;
    }

    public bool TryReserveTrial(RoutingTarget target, int maxStreak, TimeSpan cooldown)
    {
        if (maxStreak <= 0)
        {
            return false;
        }

        if (!_streaks.TryGetValue(target, out var entry))
        {
            return false;
        }

        // Evaluate the circuit state and claim the lease atomically under the entry lock.
        lock (entry)
        {
            if (entry.Count <= maxStreak)
            {
                return false; // Closed.
            }

            TimeSpan? elapsed = entry.LastFailureUtc is { } last
                ? _clock.GetUtcNow() - last
                : null;
            if (cooldown > TimeSpan.Zero && (elapsed is not { } el || el < cooldown))
            {
                return false; // Open (cooldown not elapsed).
            }

            if (entry.TrialLeased)
            {
                return false; // Another trial is in flight.
            }

            entry.TrialLeased = true;
            return true;
        }
    }

    public void ReleaseTrial(RoutingTarget target)
    {
        if (_streaks.TryGetValue(target, out var entry))
        {
            lock (entry)
            {
                entry.TrialLeased = false;
            }
        }
    }

    public void ResetStreak(RoutingTarget target)
    {
        if (_streaks.TryGetValue(target, out var entry))
        {
            lock (entry)
            {
                entry.Count = 0;
                entry.LastFailureUtc = null;
                entry.TrialLeased = false;
            }
        }
    }

    public DateTimeOffset? GetLastFailureUtc(RoutingTarget target)
    {
        return _streaks.TryGetValue(target, out var entry)
            ? entry.ReadLastFailureUtc()
            : null;
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

    private bool IsTrialLeased(RoutingTarget target) =>
        _streaks.TryGetValue(target, out var entry) && entry.ReadTrialLeased();

    private sealed class StreakEntry
    {
        public int Count { get; set; }
        public DateTimeOffset? LastFailureUtc { get; set; }
        public bool TrialLeased { get; set; }

        public int ReadCount()
        {
            lock (this)
            {
                return Count;
            }
        }

        public DateTimeOffset? ReadLastFailureUtc()
        {
            lock (this)
            {
                return LastFailureUtc;
            }
        }

        public bool ReadTrialLeased()
        {
            lock (this)
            {
                return TrialLeased;
            }
        }
    }
}
