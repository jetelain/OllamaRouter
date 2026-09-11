using System.Collections.Concurrent;

namespace OllamaRouter.Services;

/// <summary>
/// In-memory implementation of <see cref="IActivityMonitorService"/>. Keeps only a bounded
/// number of recent requests, so memory usage stays negligible and no VRAM is involved.
/// </summary>
public sealed class ActivityMonitorService : IActivityMonitorService
{
    private const int MaxHistory = 50;

    private readonly ConcurrentDictionary<Guid, InProgressRequest> _inProgress = new();
    private readonly ConcurrentQueue<ActivityLogEntry> _history = new();

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
            [RoutingTarget.Remote] = inProgress.Any(r => r.Target == RoutingTarget.Remote)
        };

        var recent = _history.ToArray().OrderByDescending(e => e.Timestamp).ToList();

        return new ActivityMonitorSnapshot(busy, inProgress, recent);
    }
}
