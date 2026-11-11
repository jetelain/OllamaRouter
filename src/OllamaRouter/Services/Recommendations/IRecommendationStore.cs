namespace OllamaRouter.Services.Recommendations;

/// <summary>
/// The live recommendation matrix store (task #66): the in-memory singleton over
/// <c>&lt;effectiveStateDirectory&gt;/recommended.json</c>. The constructor loads the file
/// FAIL-SOFT (missing → empty matrix; corrupt → warn-log + empty matrix — the router never
/// fails to start because of a stale file) so the node context ceilings survive router
/// restarts without any extra call. The matrix is keyed by native model name, so ceilings
/// are enforced for requests addressed by the native name OR by a translated role alias —
/// translation state is irrelevant to enforcement.
/// </summary>
public interface IRecommendationStore
{
    /// <summary>The current active matrix (thread-safe reference swap; empty when inactive).</summary>
    FleetRecommendations GetMatrix();

    /// <summary>
    /// Canonical alias lookup (OrdinalIgnoreCase) in the active matrix; null when no
    /// recommendation is active for that role (absent roles are the "benchmark this
    /// role's candidates" signal for the wizard).
    /// </summary>
    RoleRecommendation? GetRole(string alias);

    /// <summary>
    /// The Stage-1/sizing hook: the active per-node <c>ContextCeiling</c> for one
    /// (target, model) pair. The model name is normalized with the existing tag-stripping
    /// <c>RoutingDecisionService.NormalizeModelName</c> and matched OrdinalIgnoreCase
    /// against the <see cref="FleetRecommendations.Models"/> index (so both
    /// <c>qwen3.8:27b</c> and <c>qwen3.8</c> resolve); the target is matched
    /// OrdinalIgnoreCase against the profile's nodes. False when no ceiling is active for
    /// the pair (fail-open: enforcement is byte-identical to today).
    /// </summary>
    bool TryGetNodeContextCeiling(string targetName, string modelName, out int ceiling);

    /// <summary>
    /// The active per-node model profile for a NORMALIZED model name (tag-stripped, as
    /// produced by <c>RoutingDecisionService.NormalizeModelName</c>; OrdinalIgnoreCase
    /// match against the <see cref="FleetRecommendations.Models"/> index). Supplies the MoE
    /// <see cref="NodeRoleProfile.BaselineContext"/> values for dynamic sizing (task #66);
    /// false when no recommendation is active for the model.
    /// </summary>
    bool TryGetModelProfile(string normalizedModelName, out ModelNodeProfile? profile);

    /// <summary>
    /// Atomically swaps the active in-memory matrix (used by
    /// <c>POST /api/router/recommendations/generate</c>).
    /// </summary>
    void Activate(FleetRecommendations matrix);

    /// <summary>
    /// Re-reads <c>recommended.json</c> from disk and activates it (used by
    /// <c>POST /api/router/recommendations/apply</c> — idempotent re-sync after a manual
    /// edit, a fleet/target change or a post-restart re-sync). Same fail-soft rules as the
    /// constructor; false (active matrix unchanged) when the file is missing/empty.
    /// </summary>
    bool TryActivateFromFile(out FleetRecommendations matrix);
}
