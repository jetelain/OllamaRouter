namespace OllamaRouter.Services;

/// <summary>
/// One model process currently loaded in memory on a target, as reported by
/// <c>GET /api/ps</c> of that target's Ollama daemon.
/// <para>
/// Ollama's /api/ps does NOT expose the per-process context length;
/// <see cref="ContextLength"/> is therefore best-effort (null when not present in
/// the payload) and may be enriched by later phases (e.g. from /api/show capabilities).
/// </para>
/// </summary>
/// <param name="Name">The model name + tag as reported by /api/ps (e.g. "llama3:latest").</param>
/// <param name="SizeVramBytes">The <c>size_vram</c> field of the /api/ps entry (0 when missing/invalid).</param>
/// <param name="ContextLength">Best-effort context length (tokens) when discoverable; null otherwise.</param>
public sealed record TargetLoadedModel(string Name, long SizeVramBytes, int? ContextLength = null);

/// <summary>
/// Immutable per-target execution-state snapshot handed out by the
/// <see cref="ITargetExecutionTracker"/> via atomic reference swap (all fields are immutable;
/// the snapshot is safe to share across threads without further synchronization).
/// </summary>
/// <param name="Target">The target this snapshot describes (never the Cloud pseudo-target).</param>
/// <param name="InFlightRequests">Number of router-dispatched requests currently in flight on this target.</param>
/// <param name="LoadedModels">Model processes currently loaded on this target (no cross-target dedupe).</param>
/// <param name="TotalVramBytes">Sum of <see cref="TargetLoadedModel.SizeVramBytes"/> over <see cref="LoadedModels"/>.</param>
/// <param name="LastRefreshedAtUtc">When the last successful /api/ps fetch for this target completed.</param>
/// <param name="Stale">True when the last refresh failed or missed its per-target budget (data may be old).</param>
public sealed record TargetExecutionState(
    RoutingTarget Target,
    int InFlightRequests,
    IReadOnlyList<TargetLoadedModel> LoadedModels,
    long TotalVramBytes,
    DateTimeOffset LastRefreshedAtUtc,
    bool Stale);

/// <summary>
/// Per-target execution state tracker for the routing decision pipeline: which models are
/// currently loaded in memory on each fleet target (and with how much VRAM), plus the number
/// of in-flight (dispatched, not-yet-completed) requests per target.
/// <para>
/// <b>Snapshot-only reads:</b> <see cref="GetState"/> and <see cref="GetAllStates"/> are
/// synchronous, non-blocking snapshot reads (atomic reference swap of immutable records) and
/// NEVER wait on HTTP — the routing path stays latency-neutral. Refresh is driven out-of-band
/// (the <c>TargetExecutionTrackerRefreshService</c> hosted-service timer, 2 s period) via
/// <see cref="RefreshAsync"/>, which fans out to <c>GET /api/ps</c> over all online targets in
/// parallel (each fetch bounded by the per-target budget) and is coalesced so that at most one
/// refresh runs at a time.
/// </para>
/// <para>
/// <b>In-flight counters:</b> the middleware calls <see cref="IncrementInFlight"/> exactly once
/// immediately before dispatch and <see cref="DecrementInFlight"/> exactly once in its
/// guaranteed-exit <c>finally</c> block (covering success, forwarder error, and client abort).
/// The Cloud pseudo-target is never counted (it is a relay, never probed, never has /api/ps)
/// and is excluded from the fan-out. Decrement floors at 0 so an unbalanced call can never
/// drive a counter negative.
/// </para>
/// </summary>
public interface ITargetExecutionTracker
{
    /// <summary>
    /// Synchronous, non-blocking snapshot for one target: returns null when the target is not a
    /// fleet target (e.g. the Cloud pseudo-target), otherwise a snapshot (possibly the fill-in
    /// "never seen" state with empty models and <c>Stale = true</c>).
    /// </summary>
    TargetExecutionState? GetState(RoutingTarget target);

    /// <summary>
    /// Synchronous, non-blocking snapshots for every fleet target (Cloud excluded), in fleet
    /// configuration order. Targets that have never been observed are filled in with an empty,
    /// stale state so callers always get an entry for each fleet target.
    /// </summary>
    IReadOnlyList<TargetExecutionState> GetAllStates();

    /// <summary>
    /// Marks one dispatched request as in flight on the target. Called by the routing middleware
    /// exactly once, immediately before the forward, for non-Cloud targets only.
    /// </summary>
    void IncrementInFlight(RoutingTarget target);

    /// <summary>
    /// Marks one dispatched request as completed on the target (success, failure, or client
    /// abort — all covered by the middleware's single guaranteed-exit <c>finally</c> block).
    /// Called exactly once per increment. Floors at 0: an unbalanced decrement can never drive
    /// the counter negative.
    /// </summary>
    void DecrementInFlight(RoutingTarget target);

    /// <summary>
    /// The refresh period (the tracker's TTL, default 2 s) that the hosted timer service
    /// (<c>TargetExecutionTrackerRefreshService</c>) uses as its <c>PeriodicTimer</c> interval.
    /// </summary>
    TimeSpan RefreshTtl { get; }

    /// <summary>
    /// Coalescing refresh: if the last successful refresh is still within the TTL (default 2 s)
    /// this returns without touching the network; at most one refresh runs at a time (concurrent
    /// callers wait on the same task). Each online target is queried in parallel via
    /// <c>GET {Url}/api/ps</c>, bounded by the per-target budget (a blackholed daemon can never
    /// hold up the tick); a per-target failure keeps the previous data and marks the slot stale
    /// instead of clearing it. Never throws (failures are logged at Warning/Debug).
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
