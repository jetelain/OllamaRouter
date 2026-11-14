using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace OllamaRouter.Services.Clients;

/// <summary>
/// Pooling implementation of <see cref="IOpenAiChatClientFactory"/> (task #60; task #112: the
/// pooled client is now the first-party Microsoft.Extensions.AI.OpenAI native <see cref="IChatClient"/>
/// — <c>OpenAIClient.GetChatClient(placeholder).AsIChatClient()</c> over the factory-owned
/// <c>HttpClient</c> pipeline — wrapped in the router's <see cref="OpenAiChatClient"/> delegating
/// adapter). One pooled client (with its dedicated <see cref="SocketsHttpHandler"/> +
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
/// convention as the middleware's proxy client, which deliberately imposes none). The SDK
/// pipeline's <c>NetworkTimeout</c> stays at its null default — no per-operation timeout is
/// layered on top of the factory's infinite <see cref="HttpClient.Timeout"/>.</item>
/// </list>
/// <para>
/// Each pool entry composes (task #112):
/// <c>SocketsHttpHandler</c> (keyless endpoints are wrapped in the <see cref="StripAuthorizationHandler"/>
/// so NO <c>Authorization</c> header reaches the wire) → factory-owned <c>HttpClient</c>
/// (infinite timeout) → <c>HttpClientPipelineTransport</c> → <c>OpenAIClient</c> (SDK-internal
/// retries DISABLED via <c>ClientRetryPolicy(maxRetries: 0)</c> — the router's own failover loop
/// owns retry, and a retried 429 would break the "terminal upstream 429" classification) →
/// <see cref="OpenAiChatClient"/> adapter (the <c>IChatClient</c> handed to consumers). The
/// client-level model is a private placeholder that is NEVER sent: the adapter's per-request
/// mapping overrides it with <c>ChatOptions.ModelId</c> (verified on the wire), so one pooled
/// client serves ANY model. Disposal (verified against OpenAI 2.13.0): the SDK's
/// <c>OpenAIClient</c> implements NO <c>IDisposable</c>, and neither the transport nor the
/// adapter <c>IChatClient</c> ever disposes the caller-supplied <c>HttpClient</c> — so the
/// factory's <c>HttpClient</c> dispose (disposeHandler: true) is the single deterministic release
/// of the pipeline, handler, and connection pool.
/// </para>
/// <para>
/// Pooled clients are created through the internal NON-OWNING adapter constructor: an MEAI consumer
/// disposing a pooled client mid-fleet must not tear down the shared connection pool — the factory
/// alone disposes the underlying handler/client (on <see cref="IOpenAiChatClientFactory.DropClient"/>
/// or factory disposal).
/// </para>
/// </summary>
public sealed class OpenAiChatClientFactory : IOpenAiChatClientFactory, IDisposable
{
    /// <summary>
    /// Client-level placeholder model id (never sent on the wire — the native adapter patches
    /// <c>ChatOptions.ModelId</c> into every request body, and the <see cref="OpenAiChatClient"/>
    /// model guard rejects blank ids BEFORE any I/O).
    /// </summary>
    private const string PlaceholderModel = "ollamarouter-default";

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
        if (string.IsNullOrEmpty(endpoint.ApiKey))
        {
            // The SDK's ApiKeyCredential rejects an empty key; the placeholder credential below
            // would add an Authorization header — strip it before the network so keyless targets
            // keep sending NONE (wire parity with the deleted hand-rolled client).
            handler = new StripAuthorizationHandler(handler);
        }

        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            // No client-level timeout: long generations survive; per-request budgets come from
            // the caller's CancellationToken.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // RFC 3986 relative merge: a non-slash-terminated base path would swallow the endpoint's
        // first segment ("http://h/v1" + "chat/completions" → "http://h/chat/completions"), so a
        // trailing slash makes path-prefixed bases compose correctly (same normalization as
        // OllamaChatClientFactory).
        var options = new OpenAIClientOptions
        {
            Endpoint = endpoint.BaseAddress.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? endpoint.BaseAddress
                : new Uri(endpoint.BaseAddress.AbsoluteUri + "/"),
            Transport = new HttpClientPipelineTransport(httpClient),
            // MANDATORY (verified): the SDK's DEFAULT retry policy (3 retries with backoff)
            // retries 429/5xx AND transport failures — that would break the router's own
            // failover loop and the "terminal upstream 429" classification. Single attempt:
            // the middleware's dispatch loop owns retry/failover.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            // DELIBERATELY NOT set (null default): no per-operation network timeout — the
            // infinite-timeout factory HttpClient + the caller's CancellationToken govern.
        };

        // ApiKeyCredential rejects an empty key ("none" placeholder + the strip handler above
        // preserve the no-Authorization-header wire parity for keyless targets).
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(endpoint.ApiKey) ? "none" : endpoint.ApiKey);
        var openAiClient = new OpenAIClient(credential, options);
        var nativeChatClient = openAiClient.GetChatClient(PlaceholderModel).AsIChatClient();

        // The factory owns the lifecycle, so the adapter must NOT own the transport.
        // (The OpenAIClient itself is NOT IDisposable in OpenAI 2.13.0 — nothing to release; its
        // pipeline rides the factory-owned HttpClient/transport above.)
        return new PooledEntry(
            new OpenAiChatClient(nativeChatClient),
            httpClient);
    }

    private static void DisposeEntry(PooledEntry entry)
    {
        // Verified: the SDK's OpenAIClient implements no IDisposable and neither the transport
        // nor the adapter IChatClient dispose the caller-supplied HttpClient — disposing the
        // HttpClient (created with disposeHandler: true) releases the pipeline, the
        // SocketsHttpHandler, and its connection pool deterministically.
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

    /// <summary>
    /// Removes the <c>Authorization</c> header before the network: keyless endpoints must not
    /// carry the placeholder credential's bearer header on the wire.
    /// </summary>
    private sealed class StripAuthorizationHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Authorization = null;
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Pool bookkeeping: the client plus the factory-owned resources it must release.</summary>
    private sealed class PooledEntry(
        OpenAiChatClient client,
        HttpClient httpClient)
    {
        public OpenAiChatClient Client { get; } = client;

        public HttpClient HttpClient { get; } = httpClient;
    }
}
