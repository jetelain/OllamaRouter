using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Lease bucket state of one (target, model) after a sizing decision:
/// <list type="bullet">
/// <item><see cref="Baseline"/> - no expanded lease active; the injected value is the engine's
/// (baseline) bucket.</item>
/// <item><see cref="FreshExpand"/> - this request grew the lease to an expanded bucket.</item>
/// <item><see cref="RetainedExpanded"/> - the expanded lease was kept via the cooldown turn
/// budget or the idle window (KV prefix cache preserved); the injected value is the retained
/// active bucket.</item>
/// <item><see cref="ShrunkToBaseline"/> - a shrink fired on this turn; the injected value is
/// the baseline bucket and the lease was reset.</item>
/// </list>
/// </summary>
public enum LeaseBucketState
{
    Baseline,
    FreshExpand,
    RetainedExpanded,
    ShrunkToBaseline
}

/// <summary>
/// The trigger that fired a shrink transition (absent when no shrink happened).
/// </summary>
public enum ShrinkTrigger
{
    /// <summary>The cooldown budget of consecutive normal-size turns elapsed without a new spike.</summary>
    Cooldown,

    /// <summary>The idle timeout elapsed since the last activity on the lease.</summary>
    Idle,

    /// <summary>The estimated prompt dropped below 50% of the profile baseline (immediate shrink).</summary>
    DropBelowHalfBaseline
}

/// <summary>
/// The per-request outcome of the Auto-Shrink Manager's lease decision.
/// <see cref="InjectedNumCtx"/> is the value to write into the outgoing payload's
/// <c>options.num_ctx</c> (it never drops below the bucket the engine decision still requires);
/// <see cref="State"/> describes the lease transition; <see cref="ShrinkExecuted"/> is true iff
/// this call performed an expanded-to-baseline transition; <see cref="Trigger"/> carries the
/// shrink reason for operator observability (null when no shrink happened).
/// </summary>
public sealed record AutoShrinkLeaseDecision(
    int InjectedNumCtx,
    LeaseBucketState State,
    bool ShrinkExecuted,
    ShrinkTrigger? Trigger);

/// <summary>
/// Per-(target, model) context-bucket lease tracker with hysteresis (Phase 3, item 2 of the
/// Multi-Target Zero-Config Routing Plan): because changing <c>options.num_ctx</c> destroys and
/// reallocates the KV cache (invalidating the prompt prefix cache), the manager RETAINS an
/// expanded bucket across the cooldown turn budget (<see cref="DynamicContextOptions.AutoShrinkCooldownTurns"/>)
/// and the idle window (<see cref="DynamicContextOptions.AutoShrinkIdleTimeoutSeconds"/>) before
/// injecting a shrink back to the baseline bucket. Shrink triggers, in priority order:
/// <list type="number">
/// <item>50%-of-baseline token drop (immediate, even mid-cooldown);</item>
/// <item>cooldown counter expired (Nth consecutive normal turn);</item>
/// <item>idle timeout elapsed (detected at the next request — no background timer).</item>
/// </list>
/// The Cloud pseudo-target never carries a lease (cloud is never sized/rewritten).
/// </summary>
public interface IContextAutoShrinkManager
{
    /// <summary>
    /// The single per-request entry point: evaluates the lease for (target, model) against the
    /// stateless <see cref="ContextSizingDecision"/> and returns the value to inject.
    /// <paramref name="nowUtc"/> is the injected clock (testability — no internal timers).
    /// Never throws: any internal failure degrades to the stateless engine decision
    /// (baseline bucket, <see cref="LeaseBucketState.Baseline"/>).
    /// </summary>
    AutoShrinkLeaseDecision Decide(
        RoutingTarget target,
        string modelName,
        ContextSizingDecision engineDecision,
        DynamicContextOptions dynamic,
        long estimatedTokens,
        DateTimeOffset nowUtc);

    /// <summary>
    /// Invalidates EVERY lease on <paramref name="target"/>: called when the target transitions
    /// to Offline OR back to Online (the KV cache of the freshly (re)loaded daemon is unknown).
    /// The next request for any model on that target starts at baseline.
    /// </summary>
    void ResetTargetLeases(RoutingTarget target);

    /// <summary>
    /// Invalidates the single (target, model) lease: called when the model is evicted (or
    /// reloaded) on the target. The next request for that model starts at baseline; other
    /// models' leases are untouched.
    /// </summary>
    void ResetModelLease(RoutingTarget target, string modelName);

    /// <summary>
    /// Read-only snapshot of all currently held leases, for operator observability (monitor
    /// UI / TUI): per target (Cloud excluded), the active bucket per model and whether the
    /// lease is currently expanded above its profile baseline. Never throws and never
    /// mutates lease state; callers may invoke it from any thread.
    /// </summary>
    IReadOnlyDictionary<RoutingTarget, IReadOnlyList<ContextLeaseView>> GetLeaseSnapshot();
}

/// <summary>
/// Immutable read-only view of a single (target, model) lease for monitoring:
/// <see cref="ActiveBucket"/> is the context bucket (tokens) currently held by the lease
/// (the last value injected into <c>options.num_ctx</c>); <see cref="IsExpanded"/> is true
/// while the lease is retained above the profile baseline.
/// </summary>
public sealed record ContextLeaseView(string Model, int ActiveBucket, bool IsExpanded);
