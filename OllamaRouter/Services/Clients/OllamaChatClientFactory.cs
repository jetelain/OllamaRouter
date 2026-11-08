using Microsoft.Extensions.AI;

namespace OllamaRouter.Services.Clients;

/// <summary>
/// Produces and pools Ollama <see cref="IChatClient"/> instances per base address (task #61).
/// One pooled client (and its dedicated <c>SocketsHttpHandler</c>/<c>HttpClient</c>) is created
/// per normalized base URI and reused for all dispatches to that daemon, so connection pools
/// survive across requests. Registered as a singleton via <c>AddOllamaRouterCore</c>;
/// disposing the factory releases every pooled client deterministically.
/// </summary>
public interface IOllamaChatClientFactory
{
    /// <summary>Returns the pooled client for <paramref name="baseAddress"/>, creating it on first use.</summary>
    IChatClient GetClient(Uri baseAddress);

    /// <summary>
    /// Disposes and removes the pooled client for <paramref name="baseAddress"/>
    /// (fleet/target removal or reconfiguration).
    /// </summary>
    void DropClient(Uri baseAddress);
}

/// <summary>
/// Pooling implementation of <see cref="IOllamaChatClientFactory"/> (task #61) — the mirror
/// image of <see cref="OpenAiChatClientFactory"/> (task #60):
/// <list type="bullet">
/// <item>the pool key is the lowercased, default-port-stripped, trailing-slash-trimmed absolute
/// base URI (Ollama daemons need no API key, so no key material participates in the key);</item>
/// <item>every handler carries the codebase-standard 10 s <c>PooledConnectionLifetime</c> +
/// 2 min idle timeout (matching the LivenessProbe / HardwareProbe / Benchmark clients) so
/// sleeping-then-woken peers never hit dead pooled sockets;</item>
/// <item>the <see cref="HttpClient"/> timeout is INFINITE on purpose: long generations must
/// survive; per-request budgets come from the caller's <see cref="CancellationToken"/> (the same
/// convention as the middleware's proxy client, which deliberately imposes none).</item>
/// </list>
/// Pooled clients are created through the internal NON-OWNING constructor: an MEAI consumer
/// disposing a pooled client mid-fleet must not tear down the shared connection pool — the
/// factory alone disposes the underlying handler/client (on <see cref="IOllamaChatClientFactory.DropClient"/>
/// or factory disposal).
/// </summary>
public sealed class OllamaChatClientFactory : IOllamaChatClientFactory, IDisposable
{
    private readonly Func<Uri, HttpMessageHandler> _handlerFactory;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PooledEntry> _clients =
        new(StringComparer.Ordinal);

    /// <summary>Production constructor: standard <see cref="SocketsHttpHandler"/> per pooling policy.</summary>
    public OllamaChatClientFactory()
        : this(_ => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        })
    {
    }

    /// <summary>
    /// Testable constructor: injects the per-base-address <see cref="HttpMessageHandler"/>
    /// factory (tests can observe disposal); the production defaults are the codebase-standard
    /// <c>SocketsHttpHandler</c> with <c>PooledConnectionLifetime = 10s</c> /
    /// <c>PooledConnectionIdleTimeout = 2min</c>.
    /// </summary>
    internal OllamaChatClientFactory(Func<Uri, HttpMessageHandler> handlerFactory)
    {
        _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
    }

    /// <inheritdoc/>
    public IChatClient GetClient(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        var entry = _clients.GetOrAdd(ComputePoolKey(baseAddress), _ => CreateEntry(baseAddress));
        return entry.Client;
    }

    /// <inheritdoc/>
    public void DropClient(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        if (_clients.TryRemove(ComputePoolKey(baseAddress), out var entry))
        {
            entry.HttpClient.Dispose();
        }
    }

    /// <summary>Disposes every pooled client (and its handler/HttpClient) deterministically.</summary>
    public void Dispose()
    {
        foreach (var entry in _clients.Values)
        {
            entry.HttpClient.Dispose();
        }

        _clients.Clear();
    }

    private PooledEntry CreateEntry(Uri baseAddress)
    {
        var handler = _handlerFactory(baseAddress);
        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            // No client-level timeout: long generations survive; per-request budgets come from
            // the caller's CancellationToken.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // The factory owns the lifecycle, so the pooled client must NOT own the HttpClient.
        return new PooledEntry(
            new OllamaChatClient(httpClient, ownsHttpClient: false, defaultModelId: null, providerName: "ollama"),
            httpClient);
    }

    /// <summary>
    /// Pool key: normalized base URI (lowercased, trailing-slash-trimmed, default port omitted
    /// for the scheme) — URI case / trailing-slash / default-port differences never double-pool
    /// the same daemon.
    /// </summary>
    private static string ComputePoolKey(Uri baseAddress)
    {
        var builder = new UriBuilder(baseAddress);
        var defaultPort = string.Equals(builder.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        if (builder.Port == defaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri.TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>Pool bookkeeping: the client plus the factory-owned resources it must release.</summary>
    private sealed class PooledEntry(
        OllamaChatClient client,
        HttpClient httpClient)
    {
        public OllamaChatClient Client { get; } = client;

        public HttpClient HttpClient { get; } = httpClient;
    }
}
