using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Default implementation of <see cref="IContextAutoShrinkManager"/>: a thread-safe
/// per-(target, normalized model name) lease state machine. A single lock guards the
/// lease dictionary and its slots (request volume is far below lock-contention thresholds);
/// <see cref="Decide"/> is the only mutator, and the reset APIs are idempotent.
/// <para>
/// <b>Hysteresis rules</b> (all evaluated inside the lease lock):
/// <list type="number">
/// <item>Expansion: a request whose required bucket exceeds the baseline creates a lease
/// (or raises an existing one, resetting the cooldown counter on a RAISE). A turn that
/// still needs an expanded bucket (required &gt; baseline) is NEVER shrunk - it retains the
/// active bucket and is counted as activity.</item>
/// <item>Normal turns while expanded (required &le; baseline) evaluate the shrink triggers in
/// priority order: (a) 50%-of-baseline token drop (immediate, even mid-cooldown),
/// (b) cooldown expiry (<see cref="DynamicContextOptions.AutoShrinkCooldownTurns"/> consecutive
/// normal turns), (c) idle timeout
/// (<see cref="DynamicContextOptions.AutoShrinkIdleTimeoutSeconds"/> since last activity,
/// detected at the next request - no background timer, per the resource-protection rule).</item>
/// <item>Baseline turns without an expanded lease are stateless (the engine's bucket is
/// injected as-is).</item>
/// </list>
/// The injected value NEVER drops below the bucket the engine decision still requires, and a
/// shrink injection is clamped to the effective ceiling (a Modelfile-pinned ceiling is never
/// violated). <see cref="RoutingTarget.Cloud"/> never carries a lease (cloud is never
/// sized/rewritten). <see cref="Decide"/> never throws: any internal failure degrades to the
/// stateless engine decision (a lease bug must never block inference).
/// </para>
/// </summary>
public sealed class ContextAutoShrinkManager : IContextAutoShrinkManager
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<RoutingTarget, Dictionary<string, ContextLeaseSlot>> _leasesByTarget = new();
    private readonly Func<DateTimeOffset> _clockUtc;
    private readonly ILogger<ContextAutoShrinkManager>? _logger;

    /// <summary>
    /// Creates the manager. <paramref name="logger"/> is optional (null = no logging);
    /// <paramref name="clockUtc"/> is the injected clock (default <see cref="DateTimeOffset.UtcNow"/>)
    /// used by callers that do not pass an explicit time to <see cref="Decide"/> — unit tests
    /// drive a mutable fake clock through it for deterministic, sleep-free sequences.
    /// </summary>
    public ContextAutoShrinkManager(
        ILogger<ContextAutoShrinkManager>? logger = null,
        Func<DateTimeOffset>? clockUtc = null)
    {
        _logger = logger;
        _clockUtc = clockUtc ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The injected clock (test hook; defaults to <see cref="DateTimeOffset.UtcNow"/>).</summary>
    public Func<DateTimeOffset> ClockUtc => _clockUtc;

    public AutoShrinkLeaseDecision Decide(
        RoutingTarget target,
        string modelName,
        ContextSizingDecision engineDecision,
        DynamicContextOptions dynamic,
        long estimatedTokens,
        DateTimeOffset nowUtc)
    {
        try
        {
            // Cloud is a relay that is never sized/rewritten: behave statelessly (no slot).
            if (target == RoutingTarget.Cloud)
            {
                return new AutoShrinkLeaseDecision(engineDecision.InjectedNumCtx, LeaseBucketState.Baseline, false, null);
            }

            var profile = dynamic?.GetProfile(engineDecision.ProfileName) ?? new DynamicContextProfile(32_768, null, 32_768);
            var baseline = profile.DefaultContext > 0
                ? profile.DefaultContext
                : DynamicContextOptions.DefaultBucketStepTokens;
            var required = engineDecision.InjectedNumCtx;
            var ceiling = Math.Max(0, engineDecision.EffectiveCeiling);
            var normalizedName = RoutingDecisionService.NormalizeModelName(modelName);

            lock (_gate)
            {
                var byModel = _leasesByTarget.GetOrAdd(target, _ => new Dictionary<string, ContextLeaseSlot>(StringComparer.OrdinalIgnoreCase));
                if (!byModel.TryGetValue(normalizedName, out var slot))
                {
                    slot = new ContextLeaseSlot();
                    byModel[normalizedName] = slot;
                }

                if (required > baseline)
                {
                    // Expansion required (the prompt still needs an expanded bucket).
                    if (!slot.IsExpanded)
                    {
                        // Fresh expansion: no prior expanded lease on this (target, model).
                        slot.ActiveBucket = required;
                        slot.IsExpanded = true;
                        slot.NormalTurnsSinceSpike = 0;
                        slot.LastActivityUtc = nowUtc;
                        return new AutoShrinkLeaseDecision(required, LeaseBucketState.FreshExpand, false, null);
                    }

                    if (required > slot.ActiveBucket)
                    {
                        // Spike recurrence: raise the lease, reset the cooldown counter.
                        slot.ActiveBucket = required;
                        slot.NormalTurnsSinceSpike = 0;
                        slot.LastActivityUtc = nowUtc;
                        return new AutoShrinkLeaseDecision(required, LeaseBucketState.RetainedExpanded, false, null);
                    }

                    // Still needs the expanded bucket (required <= ActiveBucket): retention is
                    // mandatory (a shrink may never truncate a prompt that needs the expanded
                    // bucket). The prompt is still at (or above) expansion size, so it is NOT a
                    // "normal turn": reset the cooldown counter so a lease under sustained heavy
                    // use never accrues toward a shrink, and refresh the idle clock.
                    slot.NormalTurnsSinceSpike = 0;
                    slot.LastActivityUtc = nowUtc;
                    return new AutoShrinkLeaseDecision(slot.ActiveBucket, LeaseBucketState.RetainedExpanded, false, null);
                }

                // required <= baseline.
                if (!slot.IsExpanded)
                {
                    // Baseline turn without an expanded lease: stateless engine decision.
                    slot.LastActivityUtc = nowUtc;
                    return new AutoShrinkLeaseDecision(required, LeaseBucketState.Baseline, false, null);
                }

                // Normal turn while expanded: evaluate the shrink triggers in priority order.
                ShrinkTrigger? trigger = null;
                if (estimatedTokens * 2 < baseline)
                {
                    // (a) 50%-of-baseline drop: immediate shrink even mid-cooldown.
                    trigger = ShrinkTrigger.DropBelowHalfBaseline;
                }
                else if (slot.NormalTurnsSinceSpike >= Math.Max(0, dynamic?.AutoShrinkCooldownTurns ?? 3))
                {
                    // (b) Cooldown expiry: N consecutive normal turns without a spike.
                    trigger = ShrinkTrigger.Cooldown;
                }
                else
                {
                    var idleSeconds = dynamic?.AutoShrinkIdleTimeoutSeconds ?? 60;
                    if (idleSeconds >= 0 && (nowUtc - slot.LastActivityUtc).TotalSeconds >= idleSeconds)
                    {
                        // (c) Idle timeout: detected at the next request (no background timer).
                        trigger = ShrinkTrigger.Idle;
                    }
                }

                if (trigger is { } shrinkTrigger)
                {
                    // Shrink: clamp so the injected value never drops below the required
                    // bucket (required <= baseline here) and never exceeds the effective ceiling.
                    var injected = Math.Min(Math.Max(baseline, required), ceiling);
                    var previous = slot.ActiveBucket;
                    slot.ActiveBucket = injected;
                    slot.IsExpanded = false;
                    slot.NormalTurnsSinceSpike = 0;
                    slot.LastActivityUtc = nowUtc;
                    _logger?.LogDebug(
                        "Auto-shrink lease ({Target}, {Model}): shrinking {Previous} -> {Injected} (trigger: {Trigger}).",
                        target.Value, normalizedName, previous, injected, shrinkTrigger);
                    return new AutoShrinkLeaseDecision(injected, LeaseBucketState.ShrunkToBaseline, true, shrinkTrigger);
                }

                // Retain the expanded bucket (preserves the KV prefix cache).
                slot.NormalTurnsSinceSpike++;
                slot.LastActivityUtc = nowUtc;
                return new AutoShrinkLeaseDecision(slot.ActiveBucket, LeaseBucketState.RetainedExpanded, false, null);
            }
        }
        catch (Exception ex)
        {
            // A lease bug must degrade to the stateless engine decision, never throw.
            _logger?.LogWarning(ex, "Auto-shrink lease decision failed for target {Target}, model {Model}; degrading to the stateless engine decision.", target.Value, modelName);
            return new AutoShrinkLeaseDecision(engineDecision.InjectedNumCtx, LeaseBucketState.Baseline, false, null);
        }
    }

    public void ResetTargetLeases(RoutingTarget target)
    {
        if (target == RoutingTarget.Cloud)
        {
            return;
        }

        lock (_gate)
        {
            if (_leasesByTarget.TryRemove(target, out _))
            {
                _logger?.LogDebug("Auto-shrink leases for target {Target} were reset (target offline/online transition).", target.Value);
            }
        }
    }

    public void ResetModelLease(RoutingTarget target, string modelName)
    {
        if (target == RoutingTarget.Cloud || string.IsNullOrWhiteSpace(modelName))
        {
            return;
        }

        var normalizedName = RoutingDecisionService.NormalizeModelName(modelName);
        lock (_gate)
        {
            if (_leasesByTarget.TryGetValue(target, out var byModel) &&
                byModel.Remove(normalizedName, out _))
            {
                _logger?.LogDebug("Auto-shrink lease for ({Target}, {Model}) was reset (model eviction/reload).", target.Value, normalizedName);
            }
        }
    }

    /// <summary>
    /// Mutable lease state for one (target, model): the active (last injected) context bucket,
    /// whether it is expanded above the profile baseline, the last activity time, and the
    /// count of consecutive normal-size turns since the last spike (bucket raise).
    /// Guarded exclusively by the manager-wide <c>_gate</c> lock.
    /// </summary>
    private sealed class ContextLeaseSlot
    {
        public int ActiveBucket;
        public bool IsExpanded;
        public DateTimeOffset LastActivityUtc = DateTimeOffset.MinValue;
        public int NormalTurnsSinceSpike;
    }
}
