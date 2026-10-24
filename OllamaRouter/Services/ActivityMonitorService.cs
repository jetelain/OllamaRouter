using System.Collections.Concurrent;

namespace OllamaRouter.Services;

/// <summary>
/// In-memory implementation of <see cref="IActivityMonitorService"/>. Keeps only a bounded
/// number of recent requests, so memory usage stays negligible and no VRAM is involved.
/// </summary>
public sealed class ActivityMonitorService(
    ITargetExecutionTracker? executionTracker = null) : IActivityMonitorService
{
    private const int MaxHistory = 50;

    private readonly ConcurrentDictionary<Guid, InProgressRequest> _inProgress = new();
    private readonly ConcurrentQueue<ActivityLogEntry> _history = new();
    private readonly ITargetExecutionTracker? _executionTracker = executionTracker;

    public Guid StartRequest(RoutingTarget target, string model, int estimatedPromptTokens)
    {
        var id = Guid.NewGuid();
        _inProgress[id] = new InProgressRequest(id, DateTimeOffset.UtcNow, target, model, estimatedPromptTokens);
        return id;
    }

    public void CompleteRequest(Guid requestId, ActivityLogEntry entry)
    {
        _inProgress.TryRemove(requestId, out _);

        _history.Enqueue(entry);

        while (_history.Count > MaxHistory && _history.TryDequeue(out _))
        {
        }
    }

    public ActivityMonitorSnapshot GetSnapshot()
    {
        var inProgress = _inProgress.Values.OrderBy(r => r.StartedAt).ToList();

        var busy = new Dictionary<RoutingTarget, bool>
        {
            [RoutingTarget.Local] = inProgress.Any(r => r.Target == RoutingTarget.Local),
            [RoutingTarget.Remote] = inProgress.Any(r => r.Target == RoutingTarget.Remote),
            [RoutingTarget.Cloud] = inProgress.Any(r => r.Target == RoutingTarget.Cloud)
        };

        var recent = _history.ToArray().OrderByDescending(e => e.Timestamp).ToList();

        return new ActivityMonitorSnapshot(busy, inProgress, recent);
    }

    public bool IsBusy(RoutingTarget target)
    {
        return _inProgress.Values.Any(r => r.Target == target);
    }

    public IReadOnlyList<TargetExecutionState> GetTargetStates() =>
        _executionTracker?.GetAllStates() ?? [];

    public int GetConsecutiveFailures(RoutingTarget target, int estimatedPromptTokens, int tokenTolerance = 4000)
    {
        var entries = _history.ToArray();
        int count = 0;
        int currentTokenCount = estimatedPromptTokens;

        for (int i = entries.Length - 1; i >= 0; i--)
        {
            var entry = entries[i];

            if (entry.Success || Math.Abs(entry.EstimatedPromptTokens - currentTokenCount) > tokenTolerance)
            {
                break;
            }

            if (entry.Target == target)
            {
                count++;
            }

            currentTokenCount = entry.EstimatedPromptTokens;
        }

        return count;
    }
}
