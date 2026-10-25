namespace OllamaRouter.Services;

/// <summary>
/// Per-candidate scoring artifact of the two-stage hot-model affinity pipeline
/// (Phase 3, item 3 of the Multi-Target Zero-Config Routing Plan). One record is produced
/// for every evaluated candidate (every effective fleet target in declaration order, plus
/// the Cloud pseudo-target when Cloud Overflow is eligible). Fleet candidates that fail a
/// Stage-1 hard qualification carry <see cref="Qualified"/> false and <see cref="Score"/>
/// null; qualified candidates carry the calibrated score
/// <c>Score = AffinityBonus + HardwareBonus − EvictionPenalty − ConcurrencyPenalty</c>
/// (the Cloud pseudo-target always carries the fixed score 0). Selection is the stable sort
/// by (score descending, declaration index ascending), so equal scores resolve by
/// declaration order and the Cloud candidate (sorted after all fleet targets) loses
/// 0-vs-0 ties.
/// </summary>
/// <param name="Target">The candidate target (a fleet target or the Cloud pseudo-target).</param>
/// <param name="Qualified">Whether the candidate passed all Stage-1 hard qualifications.</param>
/// <param name="Score">The Stage-2 score (null when the candidate was disqualified).</param>
/// <param name="DisqualifiedReason">Why the candidate was disqualified (null when qualified).</param>
/// <param name="AffinityBonus">+100 when the requested model is already loaded on the target.</param>
/// <param name="HardwareBonus">+20 on a role-matching target (see the scoring pipeline rules).</param>
/// <param name="EvictionPenalty">+80 (applied negatively) when the load would evict a different model.</param>
/// <param name="ConcurrencyPenalty">(InFlight × 40) + (InFlight ≥ MaxParallel ? 100 : 0), applied negatively.</param>
/// <param name="Detail">Free-form human-readable breakdown for logs and tests.</param>
public sealed record RoutingCandidateEvaluation(
    RoutingTarget Target,
    bool Qualified,
    int? Score,
    string? DisqualifiedReason,
    int AffinityBonus,
    int HardwareBonus,
    int EvictionPenalty,
    int ConcurrencyPenalty,
    string? Detail);
