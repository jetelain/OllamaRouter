namespace OllamaRouter.Services;

/// <summary>
/// A single completed (or failed) chat/generate request, kept for display in the monitoring UI.
/// </summary>
public sealed record ActivityLogEntry(
    DateTimeOffset Timestamp,
    RoutingTarget Target,
    string Model,
    int EstimatedPromptTokens,
    int? ActualPromptTokens,
    int? ActualResponseTokens,
    long ElapsedMilliseconds,
    int StatusCode,
    bool Success,
    int? RawPromptTokens = null);

/// <summary>
/// A chat/generate request that is currently being processed by an instance.
/// </summary>
public sealed record InProgressRequest(
    Guid Id,
    DateTimeOffset StartedAt,
    RoutingTarget Target,
    string Model,
    int EstimatedPromptTokens);

/// <summary>
/// Current state of the monitoring: which instances are busy, the requests currently in
/// progress, and the recent request history.
/// </summary>
public sealed record ActivityMonitorSnapshot(
    IReadOnlyDictionary<RoutingTarget, bool> Busy,
    IReadOnlyList<InProgressRequest> InProgressRequests,
    IReadOnlyList<ActivityLogEntry> RecentRequests);

/// <summary>
/// Tracks in-memory activity (busy state per instance, in-progress requests, and recent request
/// log) for the lightweight monitoring UI. This has no impact on VRAM/model usage: it is pure
/// bookkeeping.
/// </summary>
public interface IActivityMonitorService
{
    /// <summary>
    /// Registers the start of a request being routed to the given instance. Returns an id to be
    /// passed to <see cref="CompleteRequest"/> once the request finishes.
    /// </summary>
    Guid StartRequest(RoutingTarget target, string model, int estimatedPromptTokens);

    /// <summary>
    /// Marks a request (previously registered via <see cref="StartRequest"/>) as finished, and
    /// records it in the recent history.
    /// </summary>
    void CompleteRequest(Guid requestId, ActivityLogEntry entry);

    /// <summary>
    /// Returns the current busy state, in-progress requests and recent request history.
    /// </summary>
    ActivityMonitorSnapshot GetSnapshot();

    /// <summary>
    /// Returns whether the given target currently has at least one request in progress. Cheaper
    /// than <see cref="GetSnapshot"/> when only the busy state of a single target is needed.
    /// </summary>
    bool IsBusy(RoutingTarget target);

    /// <summary>
    /// Returns the per-target execution-state snapshots from the
    /// <see cref="ITargetExecutionTracker"/> (loaded models, VRAM, in-flight request counts),
    /// feeding the monitor UI's per-target display and the busy flags in later phases.
    /// Returns an empty list when no tracker was injected.
    /// </summary>
    IReadOnlyList<TargetExecutionState> GetTargetStates();

    /// <summary>
    /// Returns the number of consecutive failed adjacent requests for the specified target and estimated token count
    /// (within the specified token tolerance margin), scanning backwards from the most recent completed request
    /// until a request outside the token margin or a successful request is encountered.
    /// </summary>
    int GetConsecutiveFailures(RoutingTarget target, int estimatedPromptTokens, int tokenTolerance = 4000);
}
