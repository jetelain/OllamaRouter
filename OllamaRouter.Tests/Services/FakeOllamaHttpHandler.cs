using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// A deterministic in-process fake Ollama daemon. Requests are routed by the request
/// <see cref="Uri.Authority"/> (host[:port]), so a single handler can stand in for several
/// fleet targets at once. Per authority you configure responses (keyed by "{method} {path}",
/// mapped to a status code + body) and an optional artificial latency (used to simulate a
/// slow/asleep daemon that misses the aggregation budget). Every request is recorded in
/// <see cref="CallCounts"/> under the key "{authority} {method} {path}" so tests can assert
/// that an offline target was never contacted for a given endpoint.
/// </summary>
public sealed class FakeOllamaHttpHandler : HttpMessageHandler
{
    public sealed class TargetBehavior
    {
        /// <summary>
        /// Response bodies keyed by "{HTTP method} {absolute path}". A leading integer followed
        /// by a space sets a non-200 status code (e.g. "404 {\"error\":\"nope\"}"); otherwise 200.
        /// </summary>
        public Dictionary<string, string> Responses { get; } = new();

        /// <summary>Artificial latency applied before responding (bounded by the request token).</summary>
        public TimeSpan Latency { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// When true the handler waits a long time (simulating an unresponsive/asleep daemon
        /// that never answers in time) but DOES observe the request cancellation token, exactly
        /// like a real hung TCP connection that the client can abandon — so the per-target
        /// budget CTS is what cancels the fetch. (A daemon that ignored cancellation would not
        /// be a fair simulation: a real client can always abort a dead socket.)
        /// </summary>
        public bool Hang { get; set; }
    }

    public ConcurrentDictionary<string, int> CallCounts { get; } = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, TargetBehavior> _behaviors = new(StringComparer.Ordinal);

    public FakeOllamaHttpHandler ForAuthority(string authority, TargetBehavior behavior)
    {
        _behaviors[authority] = behavior;
        return this;
    }

    public TargetBehavior Behavior(string authority) => _behaviors[authority];

    /// <summary>
    /// Number of requests recorded for "{method} {path}" on the given authority (0 if none).
    /// </summary>
    public int CountCalls(string authority, string methodAndPath) =>
        CallCounts.TryGetValue($"{authority} {methodAndPath}", out var count) ? count : 0;

    /// <summary>Total number of requests recorded for the given authority across all endpoints.</summary>
    public int TotalCalls(string authority)
    {
        var prefix = $"{authority} ";
        var total = 0;
        foreach (var (key, value) in CallCounts)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                total += value;
            }
        }

        return total;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var authority = request.RequestUri?.Authority ?? "";
        var path = request.RequestUri!.AbsolutePath;
        var methodAndPath = $"{request.Method} {path}";

        CallCounts.AddOrUpdate($"{authority} {methodAndPath}", 1, (_, count) => count + 1);

        if (!_behaviors.TryGetValue(authority, out var behavior))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (behavior.Hang)
        {
            // Waits far longer than any budget but observes cancellation, so the per-target
            // budget CTS is what aborts the fetch (mirrors abandoning a dead socket).
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
        }
        else if (behavior.Latency > TimeSpan.Zero)
        {
            await Task.Delay(behavior.Latency, cancellationToken);
        }

        if (!behavior.Responses.TryGetValue(methodAndPath, out var body))
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"no route\"}", Encoding.UTF8, "application/json")
            };
        }

        var status = HttpStatusCode.OK;
        var trimmed = body;
        if (body.Length > 0 && char.IsDigit(body[0]))
        {
            var spaceIndex = body.IndexOf(' ');
            if (spaceIndex > 0 && int.TryParse(body.AsSpan(0, spaceIndex), out var statusCode))
            {
                status = (HttpStatusCode)statusCode;
                trimmed = body[(spaceIndex + 1)..];
            }
        }

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(trimmed, Encoding.UTF8, "application/json")
        };
    }
}
