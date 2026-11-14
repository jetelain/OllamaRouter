using Microsoft.Extensions.AI;
using OllamaRouter.Serialization;

namespace OllamaRouter.Services.Clients;

/// <summary>
/// Produces and pools Ollama <see cref="IChatClient"/> instances per base address (task #61,
/// task #111: the pooled client is now the native OllamaSharp <see cref="OllamaSharp.OllamaApiClient"/>
/// wrapped in the router's <see cref="OllamaChatClient"/> delegating adapter).
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
/// Pooling implementation of <see cref="IOllamaChatClientFactory"/> (task #61 / #111) — the
/// mirror image of <see cref="OpenAiChatClientFactory"/> (task #60):
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
/// <para>
/// Each pool entry composes (task #111):
/// <c>SocketsHttpHandler</c> → factory-owned <c>HttpClient</c> (infinite timeout) →
/// <c>RouterOllamaApiClient</c> (native OllamaSharp client, constructed over the caller-supplied
/// client so OllamaSharp's non-owning disposal contract holds — its <c>Dispose</c> never touches
/// the shared <c>HttpClient</c>) → <see cref="OllamaChatClient"/> adapter (the <c>IChatClient</c>
/// handed to consumers). A single factory-owned image-fetch <c>HttpClient</c> (standard pooling
/// policy, 30 s timeout) is shared by every adapter for <c>UriContent</c> resolution and disposed
/// with the factory. Pooled clients are created through the internal NON-OWNING adapter
/// constructor: an MEAI consumer disposing a pooled client mid-fleet must not tear down the shared
/// connection pool — the factory alone disposes the native client + <c>HttpClient</c> (on
/// <see cref="IOllamaChatClientFactory.DropClient"/> or factory disposal).
/// </para>
/// </summary>
public sealed class OllamaChatClientFactory : IOllamaChatClientFactory, IDisposable
{
    private readonly Func<Uri, HttpMessageHandler> _handlerFactory;
    private readonly HttpClient _imageFetchClient;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PooledEntry> _clients =
        new(StringComparer.Ordinal);

    /// <summary>Production constructor: standard <see cref="SocketsHttpHandler"/> per pooling policy.</summary>
    public OllamaChatClientFactory()
        : this(_ => CreateStandardHandler(), imageHandlerFactory: null)
    {
    }

    /// <summary>
    /// The codebase-standard pooling handler (task #61) — 10 s <c>PooledConnectionLifetime</c> +
    /// 2 min <c>PooledConnectionIdleTimeout</c> (matching the LivenessProbe / HardwareProbe /
    /// Benchmark clients) so sleeping-then-woken peers never hit dead pooled sockets. Internal
    /// so tests can assert the exact policy the production constructor wires in.
    /// </summary>
    internal static SocketsHttpHandler CreateStandardHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromSeconds(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    };

    /// <summary>
    /// Testable constructor: injects the per-base-address <see cref="HttpMessageHandler"/> factory
    /// (tests can observe disposal) and optionally the image-fetch handler factory; the production
    /// defaults are the codebase-standard <c>SocketsHttpHandler</c> with
    /// <c>PooledConnectionLifetime = 10s</c> / <c>PooledConnectionIdleTimeout = 2min</c>.
    /// </summary>
    internal OllamaChatClientFactory(
        Func<Uri, HttpMessageHandler> handlerFactory,
        Func<HttpMessageHandler>? imageHandlerFactory = null)
    {
        _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));

        var fetchHandler = imageHandlerFactory?.Invoke() ?? CreateStandardHandler();
        // 30 s budget for router-side image fetches (long generations use the request token;
        // a hung image endpoint must not stall the dispatch).
        _imageFetchClient = new HttpClient(fetchHandler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
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
            DisposeEntry(entry);
        }
    }

    /// <summary>Disposes every pooled client (and its handler/HttpClient) + the shared image-fetch client deterministically.</summary>
    public void Dispose()
    {
        foreach (var entry in _clients.Values)
        {
            DisposeEntry(entry);
        }

        _clients.Clear();
        _imageFetchClient.Dispose();
    }

    private void DisposeEntry(PooledEntry entry)
    {
        // The native client's Dispose never touches the caller-supplied HttpClient (verified
        // against OllamaSharp 5.5.2) — disposing it is harmless bookkeeping; the HttpClient
        // (and with it the handler) is the factory's to release.
        entry.ApiClient.Dispose();
        entry.HttpClient.Dispose();
    }

    private PooledEntry CreateEntry(Uri baseAddress)
    {
        var handler = _handlerFactory(baseAddress);
        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            // RFC 3986 relative merge: a non-slash-terminated base path would swallow the
            // endpoint's first segment ("http://h/ollama" + "api/chat" → "http://h/api/chat"),
            // so a trailing slash makes path-prefixed bases compose correctly.
            BaseAddress = baseAddress.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? baseAddress
                : new Uri(baseAddress.AbsoluteUri + "/"),
            // No client-level timeout: long generations survive; per-request budgets come from
            // the caller's CancellationToken.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // Native OllamaSharp client over the factory-owned transport (non-owning by
        // construction — the HttpClient ctor leaves OllamaSharp's _disposeHttpClient false),
        // with the AOT context MANDATORY for a clean Native AOT publish, wrapped in the
        // delegating adapter (same name/namespace as the deleted hand-rolled client).
        var apiClient = new RouterOllamaApiClient(httpClient, OllamaSharpWireContext.Default);
        var client = new OllamaChatClient(apiClient, _imageFetchClient, ownsNativeClient: false, ownsImageFetchClient: false);

        return new PooledEntry(client, apiClient, httpClient);
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

    /// <summary>Pool bookkeeping: the consumer-facing client plus the factory-owned resources to release.</summary>
    private sealed class PooledEntry(
        OllamaChatClient client,
        OllamaSharp.OllamaApiClient apiClient,
        HttpClient httpClient)
    {
        public OllamaChatClient Client { get; } = client;

        public OllamaSharp.OllamaApiClient ApiClient { get; } = apiClient;

        public HttpClient HttpClient { get; } = httpClient;
    }
}
