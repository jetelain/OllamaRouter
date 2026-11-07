using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace OllamaRouter.Services.Clients;

/// <summary>
/// Pooling implementation of <see cref="IOpenAiChatClientFactory"/> (task #60).
/// <para>
/// One <see cref="OpenAiHttpChatClient"/> (with its dedicated <see cref="SocketsHttpHandler"/> +
/// <see cref="HttpClient"/>) is pooled per normalized (base address, API key) pair:
/// </para>
/// <list type="bullet">
/// <item>the pool key is the lowercased, default-port-stripped, trailing-slash-trimmed absolute
/// base URI + "|" + the first 8 hex chars of the SHA-256 of the API key ("none" when empty) — so a
/// key change for the same URL yields a fresh client, and URI case / trailing-slash / default-port
/// differences never double-pool the same endpoint;</item>
/// <item>every handler carries the codebase-standard 10 s <c>PooledConnectionLifetime</c> + 2 min
/// idle timeout (matching the LivenessProbe / HardwareProbe / Benchmark clients) so
/// sleeping-then-woken peers never hit dead pooled sockets;</item>
/// <item>the <see cref="HttpClient"/> timeout is INFINITE on purpose: long generations must
/// survive; per-request budgets come from the caller's <see cref="CancellationToken"/> (the same
/// convention as the middleware's proxy client, which deliberately imposes none).</item>
/// </list>
/// Pooled clients are created through the internal NON-OWNING constructor: an MEAI consumer
/// disposing a pooled client mid-fleet must not tear down the shared connection pool — the factory
/// alone disposes the underlying handler/client (on <see cref="IOpenAiChatClientFactory.DropClient"/>
/// or factory disposal).
/// </summary>
public sealed class OpenAiChatClientFactory : IOpenAiChatClientFactory, IDisposable
{
    private readonly Func<OpenAiTargetEndpoint, HttpMessageHandler> _handlerFactory;
    private readonly ConcurrentDictionary<string, PooledEntry> _clients = new(StringComparer.Ordinal);

    /// <summary>Production constructor: standard <see cref="SocketsHttpHandler"/> per pooling policy.</summary>
    public OpenAiChatClientFactory()
        : this(_ => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        })
    {
    }

    /// <summary>
    /// Testable constructor: injects the per-endpoint <see cref="HttpMessageHandler"/> factory
    /// (tests can observe disposal); the production defaults are the codebase-standard
    /// <c>SocketsHttpHandler</c> with <c>PooledConnectionLifetime = 10s</c> /
    /// <c>PooledConnectionIdleTimeout = 2min</c>.
    /// </summary>
    internal OpenAiChatClientFactory(Func<OpenAiTargetEndpoint, HttpMessageHandler> handlerFactory)
    {
        _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
    }

    /// <inheritdoc/>
    public IChatClient GetClient(OpenAiTargetEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(endpoint.BaseAddress);

        var entry = _clients.GetOrAdd(ComputePoolKey(endpoint), _ => CreateEntry(endpoint));
        return entry.Client;
    }

    /// <inheritdoc/>
    public void DropClient(OpenAiTargetEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(endpoint.BaseAddress);

        var key = ComputePoolKey(endpoint);
        if (_clients.TryRemove(key, out var entry))
        {
            DisposeEntry(entry);
        }
    }

    /// <summary>Disposes every pooled client (and its handler/HttpClient) deterministically.</summary>
    public void Dispose()
    {
        foreach (var entry in _clients.Values)
        {
            DisposeEntry(entry);
        }

        _clients.Clear();
    }

    private PooledEntry CreateEntry(OpenAiTargetEndpoint endpoint)
    {
        var handler = _handlerFactory(endpoint);
        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = endpoint.BaseAddress,
            // No client-level timeout: long generations survive; per-request budgets come from the
            // caller's CancellationToken.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // The factory owns the lifecycle, so the pooled client must NOT own the HttpClient.
        return new PooledEntry(
            new OpenAiHttpChatClient(
                httpClient,
                ownsHttpClient: false,
                defaultModelId: null,
                apiKey: endpoint.ApiKey,
                providerName: endpoint.TargetName),
                httpClient);
                }

    private static void DisposeEntry(PooledEntry entry)
    {
        // Disposing the HttpClient (created with disposeHandler: true) releases the SocketsHttpHandler
        // and its connection pool deterministically.
        entry.HttpClient.Dispose();
    }

    /// <summary>
    /// Pool key: normalized base address + "|" + first 8 hex chars of the SHA-256 of the API key
    /// ("none" when empty). <see cref="Uri"/> equality alone would not normalize case-sensitively
    /// on the authority or trailing slashes, so an API-key change (or a cosmetically different URI)
    /// for the same endpoint would not be handled correctly without this key.
    /// </summary>
    private static string ComputePoolKey(OpenAiTargetEndpoint endpoint)
    {
        var builder = new UriBuilder(endpoint.BaseAddress);
        // Omit the default port for the scheme (80/443) so "http://h:80/" and "http://h/" share a key.
        var defaultPort = string.Equals(builder.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        if (builder.Port == defaultPort)
        {
            builder.Port = -1;
        }

        var normalizedAddress = builder.Uri.AbsoluteUri.TrimEnd('/').ToLowerInvariant();
        var keyHash = string.IsNullOrEmpty(endpoint.ApiKey)
            ? "none"
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.ApiKey)).AsSpan()[..4].ToArray());
        return normalizedAddress + "|" + keyHash;
    }

    /// <summary>Pool bookkeeping: the client plus the factory-owned resources it must release.</summary>
    private sealed class PooledEntry(
        OpenAiHttpChatClient client,
        HttpClient httpClient)
    {
        public OpenAiHttpChatClient Client { get; } = client;

        public HttpClient HttpClient { get; } = httpClient;
    }
}
