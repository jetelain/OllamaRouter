using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="ISessionAffinityService"/>: a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> of sliding-expiration session leases
/// (case-insensitive session-ID keys) with a bounded lazy sweep on access — no background
/// hosted service (max ~100 entries swept per operation, and only when the table holds more
/// than 100 entries, so small tables never pay a sweep cost). The clock is injectable so
/// tests advance time deterministically.
/// </summary>
public sealed class SessionAffinityService : ISessionAffinityService
{
    /// <summary>
    /// One lease: the pinned target and the last access (the sliding-TTL timestamp).
    /// </summary>
    private sealed record SessionLease(RoutingTarget Target, DateTimeOffset LastAccessUtc);

    // Bounded lazy sweep: only run (and at most this many entries) when the table outgrows it.
    private const int SweepTriggerCount = 100;
    private const int SweepMaxEntries = 100;

    private readonly ConcurrentDictionary<string, SessionLease> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMonitor<OllamaRouterOptions> _options;
    private readonly ILogger<SessionAffinityService> _logger;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="options">Router options (reads <see cref="OllamaRouterOptions.SessionAffinityTtlMinutes"/>).</param>
    /// <param name="logger">Logger for eviction diagnostics.</param>
    /// <param name="clock">
    /// Injectable clock (defaults to <see cref="DateTimeOffset.UtcNow"/>; tests inject a fake).
    /// </param>
    public SessionAffinityService(
        IOptionsMonitor<OllamaRouterOptions> options,
        ILogger<SessionAffinityService> logger,
        Func<DateTimeOffset>? clock = null)
    {
        _options = options;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    // Live sliding TTL: a positive configured value, else the 30-minute default (read per use so
    // config edits rebind without a restart).
    private TimeSpan GetTtl()
    {
        var ttlMinutes = _options.CurrentValue.SessionAffinityTtlMinutes > 0
            ? _options.CurrentValue.SessionAffinityTtlMinutes
            : 30;
        return TimeSpan.FromMinutes(ttlMinutes);
    }

    public bool TryGetAffinity(string sessionId, out RoutingTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        LazySweep();

        if (!_leases.TryGetValue(sessionId, out var lease))
        {
            return false;
        }

        var now = _clock();
        var ttl = GetTtl();
        if (now - lease.LastAccessUtc > ttl)
        {
            // TTL expired: remove lazily (TryRemove is safe against a concurrent re-pin).
            if (_leases.TryGetValue(sessionId, out var current) &&
                now - current.LastAccessUtc > ttl &&
                _leases.TryRemove(sessionId, out _))
            {
                _logger.LogDebug("Session affinity lease for '{SessionId}' on {Target} expired after {Ttl}.",
                    sessionId, lease.Target.Value, ttl);
            }

            return false;
        }

        // Sliding expiration: a hit refreshes the TTL.
        _leases[sessionId] = new SessionLease(lease.Target, now);
        target = lease.Target;
        return true;
    }

    public void SetAffinity(string sessionId, RoutingTarget target)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || target == RoutingTarget.Cloud)
        {
            // Session IDs must be non-blank; the Cloud pseudo-target is a relay (no stable
            // KV-cache node) and is NEVER leased.
            return;
        }

        _leases[sessionId] = new SessionLease(target, _clock());
    }

    public void RemoveAffinity(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _leases.TryRemove(sessionId, out _);
        }
    }

    public void InvalidateTarget(RoutingTarget target)
    {
        foreach (var (sessionId, lease) in _leases)
        {
            if (lease.Target == target)
            {
                _leases.TryRemove(sessionId, out _);
            }
        }

        _logger.LogDebug("Invalidated all session affinity leases pinned to {Target}.", target.Value);
    }

    // Bounded lazy sweep: evict TTL-expired leases, at most <see cref="SweepMaxEntries"/> per
    // call and only when the table holds more than <see cref="SweepTriggerCount"/> entries.
    // This keeps per-request cost O(1) for small tables and amortized for large ones, with no
    // background timer thread (test-host determinism).
    private void LazySweep()
    {
        if (_leases.Count <= SweepTriggerCount)
        {
            return;
        }

        var now = _clock();
        var ttl = GetTtl();
        var swept = 0;
        foreach (var (sessionId, lease) in _leases)
        {
            if (swept >= SweepMaxEntries)
            {
                break;
            }

            if (now - lease.LastAccessUtc > ttl && _leases.TryRemove(sessionId, out _))
            {
                swept++;
            }
        }
    }
}
