using Microsoft.Extensions.Logging.Abstractions;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// Hysteresis sequence tests for the Auto-Shrink Manager (Phase 3, item 2 of the
/// Multi-Target Zero-Config Routing Plan). All sequences drive a mutable fake clock —
/// no sleeps, no background timers, no network. The MoE profile ships the plan's
/// explicit buckets: 184,320 (baseline) / 221,184 (intermediate) / 262,144 (max).
/// </summary>
/// <remarks>
/// <para>
/// Token-to-bucket mapping used below (MoE profile, full 262,144 ceiling): a prompt of 195k
/// (or 200k) needs the 221,184 intermediate bucket; 240k needs the 262,144 max bucket; a
/// prompt of 100k (or 92,160, or 30k) selects the 184,320 baseline bucket. Crucially, a
/// prompt is a "normal turn" (cooldown/idle-eligible) only when it selects the baseline bucket
/// AND is NOT below 50% of baseline (92,160 tokens) — a sub-92,160 prompt is an immediate
/// drop-triggered shrink and never reaches the cooldown/idle logic. The tests therefore use
/// <b>100,000</b> tokens as the canonical "normal turn" (baseline bucket, above the drop
/// threshold) so the cooldown and idle triggers are exercised in isolation, and use
/// <b>30,000</b> / <b>92,000</b> tokens for the dedicated 50%-drop-trigger cases.
/// </para>
/// </remarks>
public class ContextAutoShrinkManagerTests
{
    private const int MoEBaseline = 184_320;
    private const int MoEIntermediate = 221_184;
    private const int MoEMax = 262_144;

    // 100k tokens select the baseline bucket but sit above the 50%-of-baseline drop threshold
    // (92,160), so they are the canonical "normal turn" that exercises the cooldown/idle logic.
    private const long NormalTurnTokens = 100_000;

    private static readonly RoutingTarget Target = new("Alpha");

    private readonly DateTimeOffset _t0 = DateTimeOffset.UtcNow;
    private DateTimeOffset _now;
    private readonly ContextAutoShrinkManager _sut;
    private readonly DynamicContextOptions _options;

    public ContextAutoShrinkManagerTests()
    {
        _now = _t0;
        _sut = new ContextAutoShrinkManager(NullLogger<ContextAutoShrinkManager>.Instance, () => _now);
        _options = new DynamicContextOptions(); // Defaults: 3 turns / 60 s, MoE 184,320/221,184/262,144.
    }

    // Runs the stateless engine for a MoE prompt on a 96 GB-class target (ceiling 262,144) and
    // feeds the result through the lease manager.
    private AutoShrinkLeaseDecision DecideAt(long tokens, string model = "qwen3", int? clientNumCtx = null, int ceiling = MoEMax)
    {
        var engine = DynamicContextSizingEngine.Decide(
            _options,
            ModelArchitectureKind.Moe,
            98_304,
            ceiling,
            tokens,
            clientNumCtx);
        return _sut.Decide(Target, model, engine, _options, tokens, _now);
    }

    private void Advance(double seconds)
    {
        _now = _now.AddSeconds(seconds);
    }

    [Fact]
    public void Moe_Sequence_195k_Then_175k_Then_30k_RetainsThenShrinks()
    {
        // 195k prompt -> engine bucket 221,184 (above baseline) -> fresh expansion.
        var d1 = DecideAt(195_000);
        Assert.Equal(MoEIntermediate, d1.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.FreshExpand, d1.State);
        Assert.False(d1.ShrinkExecuted);

        // +5 s, 175k prompt: the engine selects the baseline bucket for 175k, but the LEASE
        // retains the expanded 221,184 bucket (KV prefix cache preserved; no shrink injection)
        // because the cooldown counter (turn 1) and the idle window have not elapsed.
        Advance(5);
        var d2 = DecideAt(175_000);
        Assert.Equal(MoEIntermediate, d2.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.RetainedExpanded, d2.State);
        Assert.False(d2.ShrinkExecuted);

        // +5 s, 30k prompt -> below 50% of baseline (92,160) -> immediate shrink to baseline.
        Advance(5);
        var d3 = DecideAt(30_000);
        Assert.Equal(MoEBaseline, d3.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.ShrunkToBaseline, d3.State);
        Assert.True(d3.ShrinkExecuted);
        Assert.Equal(ShrinkTrigger.DropBelowHalfBaseline, d3.Trigger);
    }

    [Fact]
    public void FourthConsecutiveNormalTurn_AfterSpike_TriggersShrink()
    {
        DecideAt(195_000); // Expand (bucket 221,184).
        Advance(5);
        var n1 = DecideAt(NormalTurnTokens); // Normal turn 1 (retained, counter 1).
        Advance(5);
        var n2 = DecideAt(NormalTurnTokens); // Normal turn 2 (retained, counter 2).
        Advance(5);
        var n3 = DecideAt(NormalTurnTokens); // Normal turn 3 (retained, counter reaches the budget).
        Advance(5);
        var n4 = DecideAt(NormalTurnTokens); // 4th consecutive normal turn -> cooldown shrink.

        Assert.Equal(MoEIntermediate, n1.InjectedNumCtx);
        Assert.Equal(MoEIntermediate, n2.InjectedNumCtx);
        Assert.Equal(MoEIntermediate, n3.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.RetainedExpanded, n1.State);
        Assert.Equal(LeaseBucketState.RetainedExpanded, n2.State);
        Assert.Equal(LeaseBucketState.RetainedExpanded, n3.State);
        Assert.False(n1.ShrinkExecuted);
        Assert.False(n2.ShrinkExecuted);
        Assert.False(n3.ShrinkExecuted);

        Assert.Equal(MoEBaseline, n4.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.ShrunkToBaseline, n4.State);
        Assert.True(n4.ShrinkExecuted);
        Assert.Equal(ShrinkTrigger.Cooldown, n4.Trigger);

        // A subsequent normal turn stays at baseline (fresh stateless baseline turn, no repeated
        // shrink event).
        Advance(5);
        var n5 = DecideAt(NormalTurnTokens);
        Assert.Equal(MoEBaseline, n5.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.Baseline, n5.State);
        Assert.False(n5.ShrinkExecuted);
    }

    [Fact]
    public void IdleTimeoutExpiry_TriggersShrink_RegardlessOfTurnCount()
    {
        DecideAt(195_000); // Expand at t0.
        Advance(5);
        var n1 = DecideAt(NormalTurnTokens); // Normal turn 1 (counter 1, still < 3) -> retained.
        Assert.Equal(LeaseBucketState.RetainedExpanded, n1.State);
        Assert.False(n1.ShrinkExecuted);
        // n1 refreshed the idle clock to t+5 s.

        // 59 s later (only 54 s since the last activity at t+5 s): idle NOT elapsed -> retained.
        Advance(54);
        var retain = DecideAt(NormalTurnTokens);
        Assert.Equal(MoEIntermediate, retain.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.RetainedExpanded, retain.State);
        Assert.False(retain.ShrinkExecuted);
        // retain refreshed the idle clock to t+59 s.

        // Then 60 s of idle (t+119 s): the NEXT request shrinks even though the cooldown
        // counter (2 normal turns) has not expired.
        Advance(60);
        var d = DecideAt(NormalTurnTokens);
        Assert.Equal(MoEBaseline, d.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.ShrunkToBaseline, d.State);
        Assert.True(d.ShrinkExecuted);
        Assert.Equal(ShrinkTrigger.Idle, d.Trigger);
    }

    [Fact]
    public void IdleTimeout_SubSecondBoundary_RetentionHolds()
    {
        DecideAt(195_000); // Expand at t0 (idle clock = t0).
        // Exactly 59 s after the spike: idle NOT elapsed (>= 60 required) -> retained.
        Advance(59);
        var d = DecideAt(NormalTurnTokens);
        Assert.Equal(MoEIntermediate, d.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.RetainedExpanded, d.State);
        Assert.False(d.ShrinkExecuted);
    }

    [Fact]
    public void CooldownCounter_Resets_WhenSpikeRecurs()
    {
        DecideAt(195_000); // Expand to 221,184.
        Advance(5);
        DecideAt(NormalTurnTokens); // Normal 1 (counter 1).
        Advance(5);
        DecideAt(NormalTurnTokens); // Normal 2 (counter 2).

        // Spike again (240k -> bucket 262,144): the lease is re-raised and the cooldown counter
        // resets to 0.
        Advance(5);
        var spike = DecideAt(240_000);
        Assert.Equal(MoEMax, spike.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.RetainedExpanded, spike.State);
        Assert.False(spike.ShrinkExecuted);

        // Then four normal turns: three retained (1->2->3), the 4th shrinks.
        Advance(5);
        var n1 = DecideAt(NormalTurnTokens);
        Advance(5);
        var n2 = DecideAt(NormalTurnTokens);
        Advance(5);
        var n3 = DecideAt(NormalTurnTokens);
        Advance(5);
        var n4 = DecideAt(NormalTurnTokens);

        Assert.Equal(MoEMax, n1.InjectedNumCtx);
        Assert.Equal(MoEMax, n2.InjectedNumCtx);
        Assert.Equal(MoEMax, n3.InjectedNumCtx);
        Assert.Equal(MoEBaseline, n4.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.ShrunkToBaseline, n4.State);
        Assert.True(n4.ShrinkExecuted);
        Assert.Equal(ShrinkTrigger.Cooldown, n4.Trigger);
    }

    [Fact]
    public void ResetTargetLeases_ClearsExpandedState()
    {
        var d1 = DecideAt(195_000);
        Assert.Equal(LeaseBucketState.FreshExpand, d1.State);

        // The target goes offline/online: ALL its leases are invalidated.
        _sut.ResetTargetLeases(Target);

        // The next request starts fresh at baseline — a fresh start, NOT a shrink event.
        Advance(5);
        var d2 = DecideAt(NormalTurnTokens);
        Assert.Equal(MoEBaseline, d2.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.Baseline, d2.State);
        Assert.False(d2.ShrinkExecuted);
    }

    [Fact]
    public void ResetModelLease_ClearsOnlyThatModel()
    {
        // Two models on one target both expanded.
        DecideAt(195_000, model: "alpha-model");
        Advance(5);
        DecideAt(195_000, model: "beta-model");
        Advance(5);

        // Evict only model A (name given with the ":latest" tag; normalized for comparison).
        _sut.ResetModelLease(Target, "alpha-model:latest");

        // Model B's lease is untouched: a normal turn retains its (still active) bucket.
        var beta = DecideAt(NormalTurnTokens, model: "beta-model");
        Assert.Equal(MoEIntermediate, beta.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.RetainedExpanded, beta.State);

        // Model A starts fresh at baseline (a normal turn is a stateless baseline turn).
        var alpha = DecideAt(NormalTurnTokens, model: "alpha-model");
        Assert.Equal(MoEBaseline, alpha.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.Baseline, alpha.State);
        Assert.False(alpha.ShrinkExecuted);
    }

    [Fact]
    public void NormalTurn_NeverTruncatesRequiredBucket()
    {
        DecideAt(195_000); // Expand to 221,184.

        // Requests that still NEED the expanded bucket (200k -> 221,184) are always retained at
        // 221,184 — even far past the 3-normal-turn threshold — because a shrink may never
        // truncate a prompt that requires the expanded bucket, and such turns reset (not accrue)
        // the cooldown counter.
        for (var i = 0; i < 6; i++)
        {
            Advance(5);
            var retained = DecideAt(200_000);
            Assert.Equal(MoEIntermediate, retained.InjectedNumCtx);
            Assert.Equal(LeaseBucketState.RetainedExpanded, retained.State);
            Assert.False(retained.ShrinkExecuted);
        }

        // The following 30k turn (below 50% of baseline) fires the immediate drop shrink.
        Advance(5);
        var shrink = DecideAt(30_000);
        Assert.Equal(MoEBaseline, shrink.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.ShrunkToBaseline, shrink.State);
        Assert.True(shrink.ShrinkExecuted);
        Assert.Equal(ShrinkTrigger.DropBelowHalfBaseline, shrink.Trigger);
    }

    [Fact]
    public void CloudTarget_NeverLeased()
    {
        var engine = DynamicContextSizingEngine.Decide(
            _options, ModelArchitectureKind.Moe, 98_304, MoEMax, 195_000, null);
        var d1 = _sut.Decide(RoutingTarget.Cloud, "qwen3", engine, _options, 195_000, _now);
        Assert.Equal(MoEIntermediate, d1.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.Baseline, d1.State);
        Assert.False(d1.ShrinkExecuted);

        // Stateless: the next cloud request is independent (no slot was created).
        Advance(5);
        var d2 = _sut.Decide(RoutingTarget.Cloud, "qwen3", engine, _options, 195_000, _now);
        Assert.Equal(MoEIntermediate, d2.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.Baseline, d2.State);
        Assert.False(d2.ShrinkExecuted);
    }

    [Fact]
    public void OptionsDefaults_AreConsumed()
    {
        Assert.Equal(3, _options.AutoShrinkCooldownTurns);
        Assert.Equal(60, _options.AutoShrinkIdleTimeoutSeconds);
    }

    [Fact]
    public void ZeroCooldownTurns_FirstNormalTurnShrinks()
    {
        _options.AutoShrinkCooldownTurns = 0;
        DecideAt(195_000); // Expand.
        Advance(5);
        var d = DecideAt(NormalTurnTokens); // First normal turn -> cooldown already expired.
        Assert.Equal(MoEBaseline, d.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.ShrunkToBaseline, d.State);
        Assert.True(d.ShrinkExecuted);
        Assert.Equal(ShrinkTrigger.Cooldown, d.Trigger);
    }

    [Fact]
    public void DropBelowHalfBaseline_ShadowsCooldown_MidCooldown()
    {
        // Mid-cooldown (1 normal turn so far): a prompt of 92,000 tokens (< 92,160) shrinks
        // IMMEDIATELY — the 50%-drop trigger has priority over cooldown retention.
        DecideAt(195_000);
        Advance(5);
        DecideAt(NormalTurnTokens); // Normal turn 1.
        Advance(5);
        var d = DecideAt(92_000);
        Assert.Equal(MoEBaseline, d.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.ShrunkToBaseline, d.State);
        Assert.Equal(ShrinkTrigger.DropBelowHalfBaseline, d.Trigger);
    }

    [Fact]
    public void DropAtExactlyHalfBaseline_DoesNotShrink()
    {
        // The drop trigger is STRICTLY below 50%: exactly 92,160 tokens (== baseline/2) is
        // retained (cooldown counter 0 < 3, within the idle window).
        DecideAt(195_000);
        Advance(5);
        var d = DecideAt(92_160);
        Assert.Equal(MoEIntermediate, d.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.RetainedExpanded, d.State);
        Assert.False(d.ShrinkExecuted);
    }

    [Fact]
    public void ShrinkInjection_NeverExceedsModelfilePinnedCeiling()
    {
        // A Modelfile pins num_ctx to 200,000: the engine's effective ceiling is 200,000, so the
        // expansion is clamped to 200,000 (not the profile's 221,184). A subsequent 30k prompt
        // (below the drop threshold) must shrink to min(baseline, ceiling) = 184,320, which is
        // never above the Modelfile-pinned ceiling.
        var expand = DecideAt(195_000, ceiling: 200_000);
        Assert.Equal(200_000, expand.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.FreshExpand, expand.State);

        Advance(5);
        var d = DecideAt(30_000, ceiling: 200_000);
        Assert.Equal(MoEBaseline, d.InjectedNumCtx); // min(184,320, 200,000) = 184,320 <= ceiling.
        Assert.Equal(LeaseBucketState.ShrunkToBaseline, d.State);
        Assert.True(d.ShrinkExecuted);
    }

    [Fact]
    public void ResetTargetLeases_LeavesOtherTargetsUnaffected()
    {
        var other = new RoutingTarget("Beta");
        DecideAt(195_000); // Alpha expanded.
        var engine = DynamicContextSizingEngine.Decide(_options, ModelArchitectureKind.Moe, 98_304, MoEMax, 195_000, null);
        _sut.Decide(other, "qwen3", engine, _options, 195_000, _now); // Beta expanded.

        _sut.ResetTargetLeases(Target); // Alpha only.

        // Beta's lease survives the Alpha reset: a normal turn retains its (still active) bucket.
        Advance(5);
        var beta = _sut.Decide(other, "qwen3", engine, _options, NormalTurnTokens, _now);
        Assert.Equal(MoEIntermediate, beta.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.RetainedExpanded, beta.State);

        // Alpha starts fresh.
        var alpha = DecideAt(NormalTurnTokens);
        Assert.Equal(MoEBaseline, alpha.InjectedNumCtx);
        Assert.Equal(LeaseBucketState.Baseline, alpha.State);
    }
}
