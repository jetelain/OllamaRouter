namespace OllamaRouter.Services;

/// <summary>
/// Decides which fleet target should serve a request. The decision — a two-stage
/// hot-model affinity pipeline (Stage 1 candidate qualification: admin enabled, liveness
/// online, catalog presence, hardware capability, context fit; Stage 2 calibrated scoring:
/// SessionAffinityBonus + AffinityBonus + HardwareBonus − EvictionPenalty − ConcurrencyPenalty,
/// with declaration order as the tie-breaker and the Cloud pseudo-target as a fixed score-0
/// terminal fallback) — is entirely encapsulated by the service: the caller only needs to
/// provide the estimated token count, the model name and (optionally) the client's static
/// <c>options.num_ctx</c>.
/// </summary>
public interface IRoutingDecisionService
{
    /// <summary>
    /// Determines the routing target for the request (no client <c>num_ctx</c> known).
    /// </summary>
    /// <param name="tokenCount">Estimated token count of the prompt.</param>
    /// <param name="modelName">Name of the model targeted by the request.</param>
    /// <exception cref="NoAvailableTargetException">Thrown when no candidate passes Stage 1 and Cloud is unavailable.</exception>
    Task<RoutingTarget> DecideAsync(int tokenCount, string modelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines the routing target for the request. The client's static
    /// <c>options.num_ctx</c> is an upper-bound preference (hierarchy level 2 of the
    /// context-sizing plan): it clamps the context-fit requirement but is NEVER a
    /// disqualifier (a large static value is clamped down to the target's CMax).
    /// </summary>
    /// <param name="tokenCount">Estimated token count of the prompt.</param>
    /// <param name="modelName">Name of the model targeted by the request.</param>
    /// <param name="clientRequestedNumCtx">The client's static <c>options.num_ctx</c>, if any.</param>
    /// <exception cref="NoAvailableTargetException">Thrown when no candidate passes Stage 1 and Cloud is unavailable.</exception>
    Task<RoutingTarget> DecideAsync(int tokenCount, string modelName, int? clientRequestedNumCtx, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines the routing target for the request WITH multi-agent session/agent affinity
    /// (KV-cache stickiness): when <paramref name="sessionId"/> is present and a live lease
    /// exists for it, the leased target earns a Stage-2 SessionAffinityBonus (+150) — enough
    /// to outrank hot-model affinity alone (+100) — and the FINAL winning target is leased
    /// back to the session (failover re-binds automatically). The Cloud pseudo-target never
    /// earns the bonus and is never leased. A null/blank <paramref name="sessionId"/> (or a
    /// deployment without a session-affinity store) behaves exactly like the stateless
    /// overload: no lease lookup, no bonus, no rebinding.
    /// </summary>
    /// <param name="tokenCount">Estimated token count of the prompt.</param>
    /// <param name="modelName">Name of the model targeted by the request.</param>
    /// <param name="clientRequestedNumCtx">The client's static <c>options.num_ctx</c>, if any.</param>
    /// <param name="sessionId">The opaque session/agent identifier (e.g. from the
    /// <c>X-OllamaRouter-Session-Id</c> header), or null/blank for stateless routing.</param>
    /// <exception cref="NoAvailableTargetException">Thrown when no candidate passes Stage 1 and Cloud is unavailable.</exception>
    Task<RoutingTarget> DecideAsync(int tokenCount, string modelName, int? clientRequestedNumCtx, string? sessionId, CancellationToken cancellationToken = default);
}
