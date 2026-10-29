using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// The two-stage hot-model affinity routing pipeline (Phase 3, item 3 of the Multi-Target
/// Zero-Config Routing Plan). Replaces the legacy busy/VRAM first-fit walk: Stage 1
/// qualifies every effective fleet target (admin enabled, liveness online with the 10 s
/// startup grace, catalog presence, hardware capability, context fit), Stage 2 scores the
/// qualified candidates with the calibrated formula
/// <c>Score = SpeedBonus(+0…150, per-decision normalized) + SessionAffinityBonus(+150) + AffinityBonus(+100) + HardwareBonus(+20) − EvictionPenalty(−80) − ConcurrencyPenalty</c>,

/// where the per-decision normalized SpeedBonus rewards the fastest-configured qualified fleet
/// candidate (maxHint ≤ 1.0 ⇒ every SpeedBonus = 0, legacy byte-identical). The highest score wins with
/// equal scores resolved by declaration order in the <see cref="OllamaRouterOptions.Targets"/> list. The
/// Cloud pseudo-target participates as a
/// fixed score-0 candidate appended after all fleet targets (it therefore loses 0-vs-0 ties
/// but beats every negative fleet score), never earns the SessionAffinityBonus, and is never
/// leased to a session. When a request carries a session ID, the final winning target is
/// leased back via <see cref="ISessionAffinityService"/> (failover re-binds automatically).
/// </summary>
public sealed class RoutingDecisionService : IRoutingDecisionService
{
    /// <summary>Affinity bonus: the requested model is already loaded (hot) on the target.</summary>
    private const int AffinityBonus = 100;

    /// <summary>Hardware bonus: role-matching target for the request's architecture class.</summary>
    private const int HardwareBonus = 20;

    /// <summary>Eviction penalty: loading the model would evict a different loaded model.</summary>
    private const int EvictionPenalty = 80;

    /// <summary>Concurrency penalty per in-flight request.</summary>
    private const int ConcurrencyPenaltyPerInFlight = 40;

    /// <summary>Concurrency cliff penalty when the target's MaxParallel is reached.</summary>
    private const int ConcurrencyCliffPenalty = 100;

    /// <summary>Session affinity bonus: the candidate is this session's leased (sticky) target.</summary>
    private const int SessionAffinityBonus = 150;

    /// <summary>
    /// Maximum speed bonus: the fastest-configured qualified fleet candidate (per-decision
    /// normalized against the fleet's max effective SpeedFactor). Calibrated to outrank
    /// AffinityBonus (+100) so the fastest cold node beats a hot slower node, to sit below
    /// SessionAffinity + Affinity (250) so a session-leased hot node still wins, and to keep
    /// an evicting fast candidate (150 − 80 = 70) or a MaxParallel-cliff candidate
    /// (150 − 140 = 10) below a hot slower node (100).
    /// </summary>
    private const int SpeedBonusMax = 150;

    /// <summary>Bytes per MiB for the eviction VRAM-sum comparison (mirrors ModelCapabilityEngine's weight→MB conversion).</summary>
    private const long BytesPerMiB = 1_048_576;

    private readonly IOptions<OllamaRouterOptions> _options;
    private readonly IModelCatalogCacheService _modelCatalogCacheService;
    private readonly ITargetLivenessService _liveness;
    private readonly ITargetAvailabilityService _availability;
    private readonly ITargetExecutionTracker _executionTracker;
    private readonly ModelCapabilityEngine? _capabilityEngine;
    private readonly CloudRoutingTargetHandler _cloudHandler;
    private readonly ILogger<RoutingDecisionService> _logger;
    private readonly ITargetFailureStreakService _streakTracker;
    private readonly ISessionAffinityService? _sessionAffinity;

    /// <summary>
    /// Creates the routing decision pipeline over the configured fleet. The Cloud terminal
    /// fallback handler is resolved directly; fleet targets are taken from the options' effective target list
    /// (including the legacy Local/Remote synthesis) and evaluated in declaration order.
    /// <paramref name="sessionAffinity"/> enables the multi-agent sticky-session Stage-2 bonus;
    /// when null (e.g. existing test constructions) the pipeline is fully stateless.
    /// </summary>
    public RoutingDecisionService(
        IOptions<OllamaRouterOptions> options,
        IModelCatalogCacheService modelCatalogCacheService,
        ITargetLivenessService liveness,
        ITargetAvailabilityService availability,
        ITargetExecutionTracker executionTracker,
        ModelCapabilityEngine? capabilityEngine,
        CloudRoutingTargetHandler cloudHandler,
        ILogger<RoutingDecisionService> logger,
        ITargetFailureStreakService? streakTracker = null,
        ISessionAffinityService? sessionAffinity = null)
    {
        _options = options;
        _modelCatalogCacheService = modelCatalogCacheService;
        _liveness = liveness;
        _availability = availability;
        _executionTracker = executionTracker;
        _capabilityEngine = capabilityEngine;
        _cloudHandler = cloudHandler;
        _logger = logger;
        // A per-instance default keeps tests source-compatible; Program.cs injects the shared
        // singleton via the factory registration so all decision requests share one streak table.
        _streakTracker = streakTracker ?? new TargetFailureStreakService();
        _sessionAffinity = sessionAffinity;
    }

    public Task<RoutingTarget> DecideAsync(int tokenCount, string modelName, CancellationToken cancellationToken = default)
    {
        return DecideAsync(tokenCount, modelName, clientRequestedNumCtx: null, cancellationToken);
    }

    public Task<RoutingTarget> DecideAsync(int tokenCount, string modelName, int? clientRequestedNumCtx, CancellationToken cancellationToken = default)
    {
        return DecideAsync(tokenCount, modelName, clientRequestedNumCtx, sessionId: null, cancellationToken);
    }

    public Task<RoutingTarget> DecideAsync(int tokenCount, string modelName, int? clientRequestedNumCtx, string? sessionId, CancellationToken cancellationToken = default)
    {
        // Per the scoring plan §1.6, every per-candidate evaluation artifact is logged at
        // Debug on the production path (the tests consume the same artifacts through the
        // DecideWithEvaluationsAsync reporting callback).
        return DecideWithEvaluationsAsync(
            tokenCount,
            modelName,
            clientRequestedNumCtx,
            evaluation => _logger.LogDebug(
                "{Model}: Candidate {Target} qualified={Qualified} score={Score}: session={Session} {Detail}",
                NormalizeModelName(modelName), evaluation.Target, evaluation.Qualified, evaluation.Score, evaluation.SessionAffinityBonus, evaluation.Detail),
            cancellationToken,
            sessionId);
    }

    public Task<RoutingDecisionResult> DecideDetailedAsync(
        int tokenCount,
        string modelName,
        int? clientRequestedNumCtx = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return DecideWithEvaluationsDetailedAsync(
            tokenCount,
            modelName,
            clientRequestedNumCtx,
            evaluation => _logger.LogDebug(
                "{Model}: Candidate {Target} qualified={Qualified} score={Score}: session={Session} {Detail}",
                NormalizeModelName(modelName), evaluation.Target, evaluation.Qualified, evaluation.Score, evaluation.SessionAffinityBonus, evaluation.Detail),
            cancellationToken,
            sessionId);
    }

    /// <summary>
    /// Runs the full two-stage pipeline and reports every per-candidate evaluation (fleet
    /// targets in declaration order, then the Cloud pseudo-target when eligible) through
    /// <paramref name="reportEvaluation"/> BEFORE the selection is made — so the artifacts
    /// survive even when no candidate qualifies and <see cref="NoAvailableTargetException"/>
    /// is thrown. Returns the selected target.
    /// </summary>
    public async Task<RoutingTarget> DecideWithEvaluationsAsync(
        int tokenCount,
        string modelName,
        int? clientRequestedNumCtx,
        Action<RoutingCandidateEvaluation> reportEvaluation,
        CancellationToken cancellationToken = default,
        string? sessionId = null)
    {
        var result = await DecideWithEvaluationsDetailedAsync(
            tokenCount,
            modelName,
            clientRequestedNumCtx,
            reportEvaluation,
            cancellationToken,
            sessionId);
        return result.Target;
    }

    /// <summary>
    /// Runs the full two-stage pipeline and returns a detailed <see cref="RoutingDecisionResult"/>
    /// including whether fallback was applied and candidate evaluations.
    /// </summary>
    public async Task<RoutingDecisionResult> DecideWithEvaluationsDetailedAsync(
        int tokenCount,
        string modelName,
        int? clientRequestedNumCtx,
        Action<RoutingCandidateEvaluation> reportEvaluation,
        CancellationToken cancellationToken = default,
        string? sessionId = null)
    {
        var settings = _options.Value;
        var normalizedName = NormalizeModelName(modelName);
        settings.Models.TryGetValue(normalizedName, out var thresholds);

        var context = new RoutingContext(
            tokenCount,
            modelName,
            normalizedName,
            thresholds,
            _modelCatalogCacheService,
            cancellationToken,
            _capabilityEngine,
            settings.GetEffectiveTargets(_logger),
            _executionTracker,
            clientRequestedNumCtx,
            sessionId);

        var fleetTargets = settings.GetEffectiveTargets(_logger);
        var evaluations = new List<RoutingCandidateEvaluation>(fleetTargets.Count + 1);

        // ---- Session lease lookup (resolved ONCE before the fleet loop). ----
        // A null/absent store (null _sessionAffinity) or a blank sessionId ⇒ no pinned target:
        // the pipeline behaves byte-identically to the stateless (pre-affinity) formula.
        RoutingTarget? pinnedTarget = null;
        if (_sessionAffinity is { } sessionAffinityService && !string.IsNullOrWhiteSpace(sessionId))
        {
            pinnedTarget = sessionAffinityService.TryGetAffinity(sessionId, out var pinned) ? pinned : null;
        }

        // ---- Stage 1 + Stage 2: every fleet target, in declaration order. ----
        // Evaluations are accumulated first and reported only AFTER the per-decision speed
        // post-pass (ApplySpeedBonuses), because the SpeedBonus depends on the WHOLE
        // qualified fleet set (its max factor) — reporting inside the loop would emit
        // pre-bonus artifacts into the test Evaluations lists and the Debug log.
        var offloadSlotTaken = false;
        foreach (var targetOptions in fleetTargets)
        {
            var evaluation = await EvaluateFleetTargetAsync(context, targetOptions, offloadSlotTaken, pinnedTarget, cancellationToken);
            // The MoE/SmallDense offload slot is single per decision: once the first
            // small-class target has claimed it (when justified), later ones never get the
            // HardwareBonus (role slot is single, per the scoring plan §1.2).
            if (evaluation.Qualified && evaluation.HardwareBonus > 0 && IsSmallClass(targetOptions))
            {
                offloadSlotTaken = true;
            }

            evaluations.Add(evaluation);
        }

        // ---- Per-decision speed post-pass (whole qualified fleet set; see ApplySpeedBonuses). ----
        ApplySpeedBonuses(evaluations, fleetTargets);

        // ---- Report: fleet candidates in declaration order, post-bonus artifacts. ----
        foreach (var evaluation in evaluations)
        {
            reportEvaluation(evaluation);
        }

        // ---- Cloud pseudo-target: fixed score 0, appended after all fleet targets. ----
        if (await _cloudHandler.CanProcessAsync(context, cancellationToken))
        {
            var cloudEvaluation = new RoutingCandidateEvaluation(
                RoutingTarget.Cloud,
                Qualified: true,
                Score: 0,
                DisqualifiedReason: null,
                AffinityBonus: 0,
                HardwareBonus: 0,
                EvictionPenalty: 0,
                ConcurrencyPenalty: 0,
                Detail: "Cloud overflow: fixed score 0 (terminal fallback, sorted after all fleet targets).");
            evaluations.Add(cloudEvaluation);
            reportEvaluation(cloudEvaluation);
        }

        // ---- Selection: highest score; ties by declaration order (stable). ----
        var qualified = evaluations.Where(e => e.Qualified).ToList();
        if (qualified.Count == 0)
        {
            if (settings.DispatchToLargestCapacityOnOverflow)
            {
                var fallbackCandidates = new List<(RoutingTarget Target, OllamaTargetOptions Options, TargetModelCapability Capability, int InFlight, int Index)>();
                for (var i = 0; i < fleetTargets.Count; i++)
                {
                    var targetOptions = fleetTargets[i];
                    var target = new RoutingTarget(targetOptions.Name!);
                    var eval = evaluations.FirstOrDefault(e => e.Target == target);
                    if (eval is not null && !eval.Qualified && eval.DisqualifiedReason != null && eval.DisqualifiedReason.StartsWith("context overflow", StringComparison.OrdinalIgnoreCase))
                    {
                        var capability = await context.GetTargetCapabilityAsync(target, targetOptions.Url!, cancellationToken);
                        if (capability is not null)
                        {
                            var inFlight = context.GetExecutionState(target)?.InFlightRequests ?? 0;
                            fallbackCandidates.Add((target, targetOptions, capability, inFlight, i));
                        }
                    }
                }

                if (fallbackCandidates.Count > 0)
                {
                    var fallbackRanked = fallbackCandidates
                        .OrderByDescending(c => ContextCapacityForFit(c.Capability, thresholds))
                        .ThenBy(c => c.InFlight)
                        .ThenBy(c => c.Index)
                        .ToList();

                    var maxAvailableCandidate = fallbackRanked[0];
                    var winnerCandidate = maxAvailableCandidate;
                    var finalFallbackWinner = winnerCandidate.Target;

                    // The true largest-capacity candidate (drives the warning header's
                    // "Max available" figure) — the MoE-aware capacity for MoE candidates,
                    // the pre-configured CMax for dense (task #26, Option A).
                    var maxAvailableCapacity = ContextCapacityForFit(maxAvailableCandidate.Capability, thresholds);

                    var maxFailuresFallback = settings.MaxConsecutiveFailures;
                    if (maxFailuresFallback > 0 && _streakTracker.ShouldSkip(winnerCandidate.Target, maxFailuresFallback))
                    {
                        var nextCandidate = fallbackRanked
                            .Skip(1)
                            .FirstOrDefault(c => !_streakTracker.ShouldSkip(c.Target, maxFailuresFallback));

                        if (nextCandidate.Options is not null)
                        {
                            _logger.LogInformation(
                                "{Model}: Target {FailedTarget} has a failure streak of {Streak} above threshold {MaxFailures}. Switching to next fallback target {NewTarget}.",
                                normalizedName, winnerCandidate.Target, _streakTracker.GetStreak(winnerCandidate.Target), maxFailuresFallback, nextCandidate.Target);
                            winnerCandidate = nextCandidate;
                            finalFallbackWinner = nextCandidate.Target;
                        }
                        else
                        {
                            _logger.LogWarning(
                                "{Model}: Target {FailedTarget} has a failure streak of {Streak} above threshold {MaxFailures}, but no subsequent fallback target is enabled or can process the request. Keeping {FailedTarget}.",
                                normalizedName, winnerCandidate.Target, _streakTracker.GetStreak(winnerCandidate.Target), maxFailuresFallback, winnerCandidate.Target);
                        }
                    }

                    if (_sessionAffinity is { } fallbackAffinity &&
                        !string.IsNullOrWhiteSpace(sessionId) &&
                        finalFallbackWinner != RoutingTarget.Cloud)
                    {
                        fallbackAffinity.SetAffinity(sessionId, finalFallbackWinner);
                    }

                    // The capacity actually served by the (possibly streak-switched) winner
                    // (MoE: extensible limit; dense: pre-configured CMax) — drives the
                    // middleware's explicit clamp and the warning header. Computed AFTER the
                    // failure-streak switch so it reflects the final winner.
                    var winnerCapacity = ContextCapacityForFit(winnerCandidate.Capability, thresholds);

                    var margin = Math.Max(0, settings.ContextFitMarginTokens);
                    var breakdown = BuildCandidateBreakdown(fleetTargets, evaluations, context);

                    _logger.LogWarning(
                        "Request for '{Model}' (estimated {Tokens} tokens + margin {Margin}) exceeds all fleet context capacities (Max available: {MaxAvailableTarget} with {MaxAvailableCMax}).\n" +
                        "Dispatching best-effort to largest-capacity target '{WinnerTarget}' with num_ctx clamped to {WinnerCMax}.\n" +
                        "Candidate breakdown:\n{Breakdown}",
                        modelName,
                        tokenCount,
                        margin,
                        maxAvailableCandidate.Options.Name,
                        maxAvailableCapacity,
                        winnerCandidate.Options.Name,
                        winnerCapacity,
                        breakdown);

                    return new RoutingDecisionResult(
                        finalFallbackWinner,
                        IsFallback: true,
                        FallbackStrategy: "LargestCapacityOverflow",
                        TargetCMax: winnerCapacity);
                }
            }

            var rejectionMargin = Math.Max(0, settings.ContextFitMarginTokens);
            var rejectionBreakdown = BuildCandidateBreakdown(fleetTargets, evaluations, context);
            _logger.LogWarning(
                "Request for '{Model}' (estimated {Tokens} tokens + margin {Margin}): No enabled target can handle the request.\n" +
                "Candidate breakdown:\n{Breakdown}",
                modelName,
                tokenCount,
                rejectionMargin,
                rejectionBreakdown);

            throw new NoAvailableTargetException($"No enabled routing target is available to handle the request for model '{modelName}'.");
        }

        var ranked = qualified
            .Select((e, index) => (Evaluation: e, Index: index))
            .OrderByDescending(x => x.Evaluation.Score!.Value)
            .ThenBy(x => x.Index)
            .Select(x => x.Evaluation)
            .ToList();

        var winner = ranked[0];
        _logger.LogDebug(
            "{Model}: Routing decision {Winner} (score {Score}; {Detail})",
            normalizedName, winner.Target, winner.Score, winner.Detail);

        // ---- Failure-streak switch over the ranked list (task #8 streak semantics). ----
        // If the winner's uncommitted/mid-stream failure streak is ABOVE the threshold, take
        // the next-ranked candidate (including the Cloud pseudo-target) whose streak is below
        // the threshold; otherwise keep the winner (a forced switch to another failing target
        // would be worse).
        var finalWinner = winner.Target;
        var maxFailures = settings.MaxConsecutiveFailures;
        if (maxFailures > 0 && _streakTracker.ShouldSkip(winner.Target, maxFailures))
        {
            var nextCandidate = ranked
                .Skip(1)
                .FirstOrDefault(e => !_streakTracker.ShouldSkip(e.Target, maxFailures));

            if (nextCandidate is not null)
            {
                _logger.LogInformation(
                    "{Model}: Target {FailedTarget} has a failure streak of {Streak} above threshold {MaxFailures}. Switching to next target {NewTarget}.",
                    normalizedName, winner.Target, _streakTracker.GetStreak(winner.Target), maxFailures, nextCandidate.Target);
                finalWinner = nextCandidate.Target;
            }
            else
            {
                _logger.LogWarning(
                    "{Model}: Target {FailedTarget} has a failure streak of {Streak} above threshold {MaxFailures}, but no subsequent target is enabled or can process the request. Keeping {FailedTarget}.",
                    normalizedName, winner.Target, _streakTracker.GetStreak(winner.Target), maxFailures, winner.Target);
            }
        }

        // ---- Session lease write (on the FINAL winner, including the streak switch-over). ----
        // The Cloud pseudo-target is NEVER leased (relay, no stable KV-cache node): a session
        // that lands on Cloud keeps no lease and re-decides on the next turn, rejoining the
        // fleet as soon as a node recovers. Writing the lease on the final winner also means a
        // streak-skipped or offline pinned target is automatically rebound to the failover
        // target — no extra invalidation path is needed here.
        if (_sessionAffinity is { } writeAffinity &&
            !string.IsNullOrWhiteSpace(sessionId) &&
            finalWinner != RoutingTarget.Cloud)
        {
            writeAffinity.SetAffinity(sessionId, finalWinner);
        }

        return new RoutingDecisionResult(finalWinner);
    }

    // ---- Stage 1 + Stage 2 for one fleet target. ----

    private async Task<RoutingCandidateEvaluation> EvaluateFleetTargetAsync(
        RoutingContext context,
        OllamaTargetOptions targetOptions,
        bool offloadSlotTaken,
        RoutingTarget? pinnedTarget,
        CancellationToken cancellationToken)
    {
        var target = new RoutingTarget(targetOptions.Name!);
        var maxVramMB = targetOptions.GetEffectiveMaxVramMB();

        // 1. Admin enabled (configuration flag AND availability state file).
        if (!targetOptions.Enabled || !_availability.IsEnabled(target))
        {
            return Disqualified(target, "disabled (administratively unavailable or Enabled=false)");
        }

        // 2. Online: shared routable predicate (IsOnline || within 10 s startup grace && Unknown).
        if (!TargetRoutability.IsRoutable(targetOptions, _availability, _liveness))
        {
            return Disqualified(target, "offline (liveness not Online and outside the startup grace window)");
        }

        // 3. Catalog present.
        var exists = await _modelCatalogCacheService.ModelExistsAsync(targetOptions.Url, context.ModelName, cancellationToken);
        if (!exists)
        {
            return Disqualified(target, "model not present in the target catalog");
        }

        // 4. Hardware capable (capability engine + suitability rules) with the permissive
        //    fallback for unknown/degraded metadata (the config-pinned Dense veto still applies).
        // 5. Context fit: min(estimated + ContextFitMarginTokens, clientNumCtx ?? ∞) ≤ capacity,
        //    where capacity is MoE-aware (task #26, Option A): MoE targets qualify at the
        //    extensible limit min(native ?? 128k, MaxContextTokens ?? ∞) — a 184,320-
        //    preconfigured node can still serve a 205k prompt via the two-bucket expansion;
        //    dense targets stay at the pre-configured CMax (no elastic excuse).
        var capability = await context.GetTargetCapabilityAsync(target, targetOptions.Url!, cancellationToken);
        if (capability is null)
        {
            // Unknown/degraded capability ⇒ permissively qualified, EXCEPT the operator-pinned
            // Dense hard veto (unknown count ⇒ LargeDense) on a small-class target (< 12 GB):
            // the 27B-on-8GB-laptop veto survives capability-discovery outages.
            if (IsPinnedDenseVetoed(context, maxVramMB))
            {
                return Disqualified(target, "pinned-Dense model vetoed on a small-class target (MaxVramMB < 12000) with unknown capability metadata");
            }

            var permissiveSession = pinnedTarget is not null && target == pinnedTarget ? SessionAffinityBonus : 0;
            return Scored(target, permissiveSession, 0, 0, 0, 0, permissiveSession,
                "permissive qualification (no capability metadata discoverable)");
        }

        if (!DynamicContextSizingEngine.IsHardwareSuitable(capability))
        {
            var maxVramDescription = maxVramMB?.ToString() ?? "unknown";
            return Disqualified(target, $"hardware incapable ({capability.Architecture} on target with MaxVramMB {maxVramDescription})");
        }

        var effectiveRequired = (long)context.TokenCount + Math.Max(0, _options.Value.ContextFitMarginTokens);
        if (context.ClientRequestedNumCtx.HasValue)
        {
            effectiveRequired = Math.Min(effectiveRequired, context.ClientRequestedNumCtx.Value);
        }

        var capacityForFit = ContextCapacityForFit(capability, context.Thresholds);

        if (!ModelCapabilityEngine.EvaluateClientContext(
                context.TokenCount,
                context.ClientRequestedNumCtx,
                capacityForFit,
                _options.Value.ContextFitMarginTokens).Qualified)
        {
            return Disqualified(target, $"context overflow (CMax: {capacityForFit} < Required: {effectiveRequired})");
        }

        // ---- Stage 2 scoring for the qualified candidate. ----
        var state = context.GetExecutionState(target);
        var loadedModels = state?.LoadedModels ?? [];
        var inFlight = state?.InFlightRequests ?? 0;

        // Affinity: the requested model is already loaded (hot) on this target.
        var isLoaded = loadedModels.Any(m => string.Equals(m.Name, context.ModelName, StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(m.Name, context.NormalizedModelName, StringComparison.OrdinalIgnoreCase));
        var affinity = isLoaded ? AffinityBonus : 0;

        // Eviction penalty (VRAM-sum based, Q1 resolution; a stale snapshot is still applied —
        // better than none for anti-thrashing). The model weight comes from the same capability
        // metadata already fetched for Stage 1 (context-cached).
        var metadata = await context.GetModelMetadataAsync(targetOptions.Url!, cancellationToken);
        var eviction = WouldEvict(isLoaded, loadedModels, state?.TotalVramBytes, metadata?.WeightSizeBytes, maxVramMB) ? EvictionPenalty : 0;

        // Concurrency: (InFlight × 40) + (InFlight ≥ MaxParallel ? 100 : 0).
        var maxParallel = targetOptions.MaxParallel <= 0 ? 1 : targetOptions.MaxParallel;
        var concurrency = inFlight * ConcurrencyPenaltyPerInFlight + (inFlight >= maxParallel ? ConcurrencyCliffPenalty : 0);

        // Hardware role bonus (request architecture + fleet-wide hot/busy facts). The MoE/
        // SmallDense offload slot is single per decision: only the FIRST small-class target in
        // declaration order is eligible (enforced by the caller's offloadSlotTaken flag).
        var hardware = await ComputeHardwareBonus(context, capability, maxVramMB, offloadSlotTaken, cancellationToken);

        // Session affinity: the candidate is this session's leased (sticky) target. Only ever
        // applied to candidates that already passed Stage 1 (disqualified candidates return
        // earlier with no bonus — no affinity rescue).
        var session = pinnedTarget is not null && target == pinnedTarget ? SessionAffinityBonus : 0;

        var score = session + affinity + hardware - eviction - concurrency;
        return Scored(target, score, affinity, hardware, eviction, concurrency, session,
            $"session={session:+#;-#;0} affinity={affinity:+#;-#;0} hardware={hardware:+#;-#;0} eviction=-{eviction} concurrency=-{concurrency} " +
            $"(inFlight={inFlight}, maxParallel={maxParallel}, loaded={loadedModels.Count}, stale={state?.Stale ?? true})");
    }

    // Permissive-fallback veto: operator pinned Models[model].Architecture = "Dense" (unknown
    // count ⇒ LargeDense) and the target's effective VRAM is below the small-target threshold.
    private static bool IsPinnedDenseVetoed(RoutingContext context, int? maxVramMB)
    {
        if (!string.Equals(context.Thresholds?.Architecture, "Dense", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return maxVramMB is null || maxVramMB < ModelCapabilityEngine.SmallTargetVramThresholdMB;
    }

    /// <summary>
    /// The MoE-aware capacity used for Stage-1 context fit and the Largest-Capacity overflow
    /// ranking (task #26, Option A): MoE candidates qualify at the EXTENSIBLE limit
    /// <c>min(native ?? 128k, MaxContextTokens ?? ∞)</c> (the two-bucket ladder's max bucket
    /// — a pre-configured 184,320 node still serves 205k prompts via elastic expansion),
    /// while dense candidates stay at the pre-configured <c>CMax</c> (no elastic excuse —
    /// a dense long prompt on a small pre-configured node is disqualified, as in task #25).
    /// The capability record's <c>CMax</c> itself is NOT modified.
    /// </summary>
    internal static int ContextCapacityForFit(TargetModelCapability capability, ModelThresholds? thresholds)
    {
        return capability.Architecture == ModelArchitectureKind.Moe
            ? ModelCapabilityEngine.GetMoeExtensibleCapacity(capability, thresholds?.MaxContextTokens)
            : capability.CMax;
    }

    /// <summary>
    /// Stage-2 HardwareBonus (+20) role matching:
    /// <list type="bullet">
    /// <item>LargeDense: +20 to the target whose OWN pre-configured capacity (CMax) covers the
    /// margin-adjusted requirement (estimated + ContextFitMarginTokens, client upper-bound
    /// clamp) — the role is "the class whose pre-configured capacity covers the request":
    /// the desktop-class target when it covers it, otherwise the server-class target. At
    /// most one target kind is role-matched per request (by its own class).</item>
    /// <item>MoE / SmallDense: +20 to the FIRST small-class (Laptop) target only when any
    /// desktop/server-class fleet target holds a dense model hot (per /api/ps classification)
    /// or is busy (InFlight &gt; 0). Otherwise no bonus — an idle desktop wins a standalone
    /// MoE request by declaration order / affinity (AC2).</item>
    /// </list>
    /// </summary>
    private async Task<int> ComputeHardwareBonus(
        RoutingContext context,
        TargetModelCapability capability,
        int? maxVramMB,
        bool offloadSlotTaken,
        CancellationToken cancellationToken)
    {
        var targetClass = GetTargetClass(maxVramMB);

        if (capability.Architecture == ModelArchitectureKind.LargeDense)
        {
            // The role is decided by the target's OWN pre-configured capacity (task #25):
            // the same additive margin-adjusted requirement used for Stage-1 qualification
            // (no hardcoded capacity ceiling).
            var effectiveRequired = (long)context.TokenCount + Math.Max(0, _options.Value.ContextFitMarginTokens);
            if (context.ClientRequestedNumCtx.HasValue)
            {
                effectiveRequired = Math.Min(effectiveRequired, context.ClientRequestedNumCtx.Value);
            }

            if (effectiveRequired <= capability.CMax)
            {
                return targetClass == TargetClass.Desktop ? HardwareBonus : 0;
            }

            return targetClass == TargetClass.Server ? HardwareBonus : 0;
        }

        // MoE / SmallDense: Laptop offload slot (single; first small-class target in declaration
        // order) only when a desktop/server-class target holds a dense model hot or is busy.
        if (targetClass != TargetClass.Small)
        {
            return 0;
        }

        if (offloadSlotTaken)
        {
            return 0; // A previous (earlier-declared) small-class target already claims the slot.
        }

        var offloadJustified = await HasDenseHotOrBusyLargeTargetAsync(context, cancellationToken);
        return offloadJustified ? HardwareBonus : 0;
    }

    private static bool IsSmallClass(OllamaTargetOptions target) =>
        GetTargetClass(target.GetEffectiveMaxVramMB()) == TargetClass.Small;

    /// <summary>
    /// Returns true when any desktop/server-class fleet target (a) holds a loaded model
    /// classified as dense (per its /api/ps entry's capability metadata — SmallDense or
    /// LargeDense, never MoE) or (b) is busy (InFlightRequests &gt; 0). Evaluated per decision
    /// over the current tracker states (no caching across requests; the capability engine's
    /// own 30 s metadata cache bounds the /api/show traffic).
    /// </summary>
    private async Task<bool> HasDenseHotOrBusyLargeTargetAsync(RoutingContext context, CancellationToken cancellationToken)
    {
        var fleetTargets = _options.Value.GetEffectiveTargets(_logger);
        foreach (var candidate in fleetTargets)
        {
            var maxVram = candidate.GetEffectiveMaxVramMB();
            var candidateClass = GetTargetClass(maxVram);
            if (candidateClass != TargetClass.Desktop && candidateClass != TargetClass.Server)
            {
                continue;
            }

            var state = context.GetExecutionState(new RoutingTarget(candidate.Name!));
            if (state is null)
            {
                continue;
            }

            if (state.InFlightRequests > 0)
            {
                return true;
            }

            foreach (var loaded in state.LoadedModels)
            {
                if (await IsLoadedModelDenseAsync(candidate, loaded.Name, cancellationToken))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Classifies one /api/ps loaded-model entry via the capability engine's metadata
    /// (same source of truth as Stage 1). Any discovery failure degrades to "not dense"
    /// (permissive: no offload bonus, never a disqualification).
    /// </summary>
    private async Task<bool> IsLoadedModelDenseAsync(OllamaTargetOptions target, string modelName, CancellationToken cancellationToken)
    {
        if (_capabilityEngine is null)
        {
            return false;
        }

        try
        {
            var digest = await _modelCatalogCacheService.GetCurrentDigestAsync(target.Url, cancellationToken);
            if (string.IsNullOrEmpty(digest))
            {
                return false;
            }

            var metadata = await _capabilityEngine.GetMetadataAsync(target.Url, modelName, digest, cancellationToken);
            var (architecture, _) = _capabilityEngine.ClassifyArchitecture(metadata, null);
            return architecture == ModelArchitectureKind.SmallDense || architecture == ModelArchitectureKind.LargeDense;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Stage-2 EvictionPenalty (VRAM-sum based, Q1 resolution): penalize iff
    /// <list type="number">
    /// <item>the requested model is NOT already loaded (Affinity applies instead), and</item>
    /// <item>the target has ≥ 1 OTHER loaded model, and</item>
    /// <item>the load overflows: TotalVramBytes + modelWeightMB &gt; MaxVramMB when MaxVramMB
    /// is known; when unknown (or the weight is unknown and MaxVramMB is known), apply the
    /// penalty only for non-server-class targets (server has enough headroom — we cannot
    /// prove eviction). Stale snapshots still apply the rule (better than none for anti-thrash).</item>
    /// </list>
    /// </summary>
    private static bool WouldEvict(
        bool requestedModelIsLoaded,
        IReadOnlyList<TargetLoadedModel> loadedModels,
        long? totalVramBytes,
        long? weightSizeBytes,
        int? maxVramMB)
    {
        if (requestedModelIsLoaded)
        {
            return false;
        }

        if (loadedModels.Count == 0)
        {
            return false;
        }

        var isServerClass = maxVramMB is not null && maxVramMB >= DynamicContextSizingEngine.ServerClassVramThresholdMB;

        if (maxVramMB is null)
        {
            // Unknown VRAM budget: pessimistic anti-thrash for non-server-class targets only.
            return !isServerClass;
        }

        if (weightSizeBytes is not null && weightSizeBytes > 0)
        {
            var total = totalVramBytes ?? 0L;
            return total + weightSizeBytes > (long)maxVramMB.Value * BytesPerMiB;
        }

        // Known budget but unknown (degraded) weight: conservative for non-server-class targets.
        return !isServerClass;
    }

    private enum TargetClass
    {
        Small,
        Desktop,
        Server,
        Unknown
    }

    private static TargetClass GetTargetClass(int? maxVramMB)
    {
        if (maxVramMB is null)
        {
            return TargetClass.Unknown;
        }

        if (maxVramMB < ModelCapabilityEngine.SmallTargetVramThresholdMB)
        {
            return TargetClass.Small;
        }

        return maxVramMB >= DynamicContextSizingEngine.ServerClassVramThresholdMB
            ? TargetClass.Server
            : TargetClass.Desktop;
    }

    private static RoutingCandidateEvaluation Disqualified(RoutingTarget target, string reason)
    {
        return new RoutingCandidateEvaluation(
            target,
            Qualified: false,
            Score: null,
            DisqualifiedReason: reason,
            AffinityBonus: 0,
            HardwareBonus: 0,
            EvictionPenalty: 0,
            ConcurrencyPenalty: 0,
            Detail: $"disqualified: {reason}");
    }

    /// <summary>
    /// Stage-2 SpeedBonus post-pass (per-decision linear normalization, self-calibrating):
    /// <list type="number">
    /// <item>Map each qualified fleet evaluation to its target options by <see cref="RoutingTarget"/> name
    /// (fleet evaluations are in declaration order, 1:1 with <paramref name="fleetTargets"/>).</item>
    /// <item><c>maxHint = max(GetEffectiveSpeedFactor())</c> over the qualified fleet targets.
    /// <c>maxHint ≤ 1.0</c> ⇒ return unchanged (byte-identical legacy path — no factors
    /// configured anywhere, or a uniformly slow fleet).</item>
    /// <item>Otherwise <c>SpeedBonus_i = round(SpeedBonusMax × (factor_i − 1) / (maxHint − 1))</c>,
    /// clamped ≥ 0 (MidpointRounding.AwayFromZero); 1.0-factor candidates get 0. The bonus is
    /// ADDED to the existing Score (which already includes session/affinity/hardware/eviction/
    /// concurrency) — no other component is recomputed.</item>
    /// </list>
    /// Calibration interplay (all vs a hot slower node at +100): fast cold winner 150 > 100;
    /// fast + evicting 150 − 80 = 70 < 100 (anti-thrash preserved); fast + MaxParallel cliff
    /// 150 − 140 = 10 < 100; session-leased hot 150 + 100 = 250 > 150 (stickiness preserved).
    /// Disqualified fleet evaluations and the Cloud pseudo-target never receive a speed bonus.
    /// </summary>
    private void ApplySpeedBonuses(List<RoutingCandidateEvaluation> fleetEvaluations, IReadOnlyList<OllamaTargetOptions> fleetTargets)
    {
        // Max effective factor over the qualified fleet TARGETS (the normalization base is the
        // whole qualified set, per the resolved normalization model).
        double maxHint = 1.0;
        for (var i = 0; i < fleetEvaluations.Count; i++)
        {
            var evaluation = fleetEvaluations[i];
            if (!evaluation.Qualified || evaluation.Target == RoutingTarget.Cloud)
            {
                continue;
            }

            var factor = fleetTargets[i].GetEffectiveSpeedFactor();
            if (factor > maxHint)
            {
                maxHint = factor;
            }
        }

        if (maxHint <= 1.0)
        {
            return; // No configured factor ⇒ every SpeedBonus = 0 (legacy byte-identical).
        }

        for (var i = 0; i < fleetEvaluations.Count; i++)
        {
            var e = fleetEvaluations[i];
            if (!e.Qualified || e.Target == RoutingTarget.Cloud)
            {
                continue;
            }

            var factor = fleetTargets[i].GetEffectiveSpeedFactor();
            if (factor <= 1.0)
            {
                continue; // Baseline/unset factor ⇒ 0 bonus.
            }

            int bonus = (int)Math.Round(SpeedBonusMax * (factor - 1.0) / (maxHint - 1.0), MidpointRounding.AwayFromZero);
            bonus = Math.Max(0, bonus);
            if (bonus <= 0)
            {
                continue;
            }

            fleetEvaluations[i] = e with
            {
                Score = e.Score!.Value + bonus,
                SpeedBonus = bonus,
                Detail = e.Detail is null ? null : $"{e.Detail} speed=+{bonus} (factor {factor.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)})"
            };
        }
    }

    private static RoutingCandidateEvaluation Scored(
        RoutingTarget target,
        int score,
        int affinity,
        int hardware,
        int eviction,
        int concurrency,
        int sessionAffinity,
        string detail,
        int speedBonus = 0)
    {
        return new RoutingCandidateEvaluation(
            target,
            Qualified: true,
            Score: score,
            DisqualifiedReason: null,
            AffinityBonus: affinity,
            HardwareBonus: hardware,
            EvictionPenalty: eviction,
            ConcurrencyPenalty: concurrency,
            Detail: detail,
            SessionAffinityBonus: sessionAffinity,
            SpeedBonus: speedBonus);
    }

    /// <summary>
    /// Strips the tag suffix from a full Ollama model name (e.g. "qwen3:8b" → "qwen3",
    /// "Qwen3.8-27B:latest" → "Qwen3.8-27B"), matching the convention that configuration keys
    /// are written without tag — the .NET configuration binder does not accept ":" in keys.
    /// The full name is preserved for downstream catalog queries that need the tag.
    /// </summary>
    public static string NormalizeModelName(string modelName)
    {
        if (string.IsNullOrEmpty(modelName))
            return modelName;

        var idx = modelName.IndexOf(':');
        return idx > 0 ? modelName[..idx] : modelName;
    }

    private string BuildCandidateBreakdown(
        IReadOnlyList<OllamaTargetOptions> fleetTargets,
        List<RoutingCandidateEvaluation> evaluations,
        RoutingContext context)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < fleetTargets.Count; i++)
        {
            var targetOptions = fleetTargets[i];
            var target = new RoutingTarget(targetOptions.Name);
            var targetName = !string.IsNullOrEmpty(targetOptions.Name) ? targetOptions.Name : "Unknown";
            var eval = evaluations.FirstOrDefault(e => e.Target == target);

            if (eval?.Qualified == true)
            {
                sb.Append(" - ")
                  .Append(targetName)
                  .Append(" (Url: ")
                  .Append(targetOptions.Url)
                  .AppendLine("): Qualified");
                continue;
            }

            var reason = eval?.DisqualifiedReason ?? "Unknown";
            if (reason.StartsWith("context overflow (", StringComparison.OrdinalIgnoreCase) && reason.EndsWith(')'))
            {
                reason = reason["context overflow (".Length..^1];
            }

            sb.Append(" - ")
              .Append(targetName)
              .Append(" (Url: ")
              .Append(targetOptions.Url)
              .Append("): Disqualified (")
              .Append(reason)
              .AppendLine(")");
        }

        string cloudStatus;
        if (!_cloudHandler.IsEnabled)
        {
            cloudStatus = "Administratively Disabled";
        }
        else if (!(_options.Value?.CloudOverflow?.Enabled ?? true))
        {
            cloudStatus = "Statically Disabled";
        }
        else if (_options.Value is { } opt && string.IsNullOrEmpty(CloudRelayResolver.ResolveCloudModel(opt, context.ModelName)))
        {
            cloudStatus = "No CloudModel configured";
        }
        else
        {
            cloudStatus = "Unavailable";
        }

        sb.Append(" - Cloud: ").Append(cloudStatus);
        return sb.ToString();
    }
}
