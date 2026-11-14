using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaRouter.Converters;
using OllamaSharp;

namespace OllamaRouter.Services.Clients;

/// <summary>
/// Delegating <see cref="IChatClient"/> over the native OllamaSharp <see cref="OllamaApiClient"/>
/// (task #111, replacing the previous 814-line hand-rolled NativeAOT client — same public name and
/// namespace, so existing <c>&lt;see cref&gt;</c> references remain valid). It owns exactly four
/// per-request normalizations and then delegates <c>GetResponseAsync</c>/<c>GetStreamingResponseAsync</c>
/// UNCHANGED to the native client, whose explicit <c>IChatClient</c> implementation (with the
/// router's <c>OllamaSharpWireContext</c> AOT context) does the canonical → Ollama wire mapping,
/// the NDJSON stream reads, and the response → MEAI mapping:
/// <list type="number">
/// <item><b>Model guard</b> (parity with the old client, pre-I/O): a blank <c>ChatOptions.ModelId</c>
/// with no configured default <c>SelectedModel</c> throws <see cref="InvalidOperationException"/>
/// BEFORE any HTTP request — the middleware classifies pre-first-byte non-HTTP exceptions as
/// uncommitted (failover), whereas the daemon's 400 for an empty model would be a terminal
/// upstream error (different classification);</item>
/// <item><b>Options narrowing</b> (maintainer decision): standard <see cref="ChatOptions"/>
/// properties are copied into a fresh options object; <see cref="ChatOptions.AdditionalProperties"/>
/// is DROPPED (which also neutralizes the native mapper's ~35-name Ollama option whitelist —
/// <c>num_ctx</c>, <c>repeat_penalty</c>, <c>keep_alive</c>, ... never reach the wire) and
/// <see cref="ChatOptions.Instructions"/> is dropped (the old client never mapped it either).
/// <see cref="ChatOptions.ToolMode"/> is copied through but IGNORED by the native mapper —
/// OllamaSharp 5.5.2 has no <c>tool_choice</c> member, so tool CONSTRAINTS are dropped by design
/// (tools themselves round-trip: function schemas out, <c>tool_calls</c> in);</item>
/// <item><b>Message normalization</b>:
/// <list type="bullet">
/// <item><see cref="FunctionResultContent"/> → a single <see cref="TextContent"/> carrying the
/// stringified result (strings pass through verbatim, non-strings via the shared AOT-safe JSON
/// value writer) on the same tool-role message. This is AOT-MANDATORY (the native mapper would
/// serialize the result through the package-INTERNAL <c>OllamaFunctionResultContent</c>,
/// unserializable in consumer context mode) AND restores the old raw-string tool-result wire
/// shape (the native wrapper would double-encode results as <c>{"CallId":...,"Result":...}</c>);</item>
/// <item><see cref="UriContent"/> with an image (or null / <c>application/octet-stream</c>) media
/// type → FETCHED by the router via the shared fetch client (http/https only, 25 MiB cap, 30 s
/// timeout linked to the request token) and replaced by a <see cref="DataContent"/> so the native
/// mapper emits it in <c>images[]</c>. The octet-stream fallback matters: MEAI infers it for
/// URIs without a recognizable extension — exactly what the inbound OpenAI <c>image_url</c>
/// mapping produces. Fetch failure surfaces as a <see cref="HttpRequestException"/> with
/// <c>StatusCode == null</c> — a failed router-side fetch is NOT an upstream-daemon response, so
/// the middleware classifies transport failure (failover, no terminal error);</item>
/// <item>all other contents pass through untouched (in-memory image <see cref="DataContent"/>s
/// map to <c>images[]</c> natively); messages are rebuilt only when their contents changed
/// (role/<c>AuthorName</c> preserved);</item>
/// </list></item>
/// <item><b>Thinking preservation</b> (the 5.5.2 pin): request-side <see cref="TextReasoningContent"/>
/// → <c>message.thinking</c> and <c>options.Reasoning</c> → <c>think</c>; response-side
/// <c>thinking</c> → <see cref="TextReasoningContent"/> on streaming updates and the non-streaming
/// response message. Both directions flow through the native mapper without extra code.</item>
/// </list>
/// <para>
/// v1 limitations (documented in the README "Converter dispatch limitations" note):
/// <c>tool_choice</c>/<see cref="ChatOptions.ToolMode"/> dropped; Ollama-specific request options
/// dropped (no <c>AdditionalProperties</c> pass-through); <c>CachedInputTokenCount</c> no longer
/// surfaced (the 5.5.2 done-frame DTO has no <c>prompt_eval_cached_count</c>); <c>Usage</c> is
/// always present (0/0 when the daemon omits counts); response WIRE rendering of reasoning in
/// converted streams remains an encoder limitation (neither encoder emits reasoning on the wire).
/// </para>
/// <para>
/// <b>Non-owning disposal by construction:</b> this adapter never owns the daemon transport —
/// <see cref="OllamaApiClient.Dispose"/> is a no-op for a caller-supplied <c>HttpClient</c>
/// (verified against the 5.5.2 source), so a consumer disposing a pooled client never tears down
/// the shared connection pool; the <see cref="OllamaChatClientFactory"/> alone disposes the
/// handler/<c>HttpClient</c> on <c>DropClient</c>/factory disposal.
/// </para>
/// </summary>
public sealed class OllamaChatClient : IChatClient, IDisposable
{
    /// <summary>Hard cap for router-fetched <see cref="UriContent"/> images (SSRF-adjacent surface).</summary>
    private const long MaxImageBytes = 25L * 1024 * 1024;
    private const int ImageFetchReadBufferSize = 8 * 1024;

    private readonly OllamaApiClient _apiClient;
    private readonly IChatClient _chatClient;
    private readonly HttpClient _imageFetchClient;
    private readonly bool _ownsNativeClient;
    private readonly bool _ownsImageFetchClient;
    private int _disposed;

    /// <summary>
    /// Public constructor (tests / direct use): wraps the supplied transport with the native
    /// client (NON-owning — the caller keeps owning/disposing <paramref name="httpClient"/>) and
    /// creates an owned image-fetch client (30 s timeout) for <see cref="UriContent"/>
    /// resolution, disposed by <see cref="Dispose"/>. The client's <c>BaseAddress</c> is used
    /// verbatim for OllamaSharp's relative endpoint resolution (<c>api/chat</c>): path-prefixed
    /// bases must be slash-terminated (the <see cref="OllamaChatClientFactory"/> always produces
    /// them that way, per RFC 3986 relative-merge rules).
    /// </summary>
    public OllamaChatClient(HttpClient httpClient, string? defaultModelId = null)
        : this(
            new RouterOllamaApiClient(httpClient, OllamaRouter.Serialization.OllamaSharpWireContext.Default)
            {
                SelectedModel = defaultModelId ?? string.Empty,
            },
            CreateImageFetchClient(),
            ownsNativeClient: true,
            ownsImageFetchClient: true)
    {
    }

    /// <summary>
    /// Factory path: pooled instances do NOT own the native client or the shared image-fetch
    /// client — the <see cref="OllamaChatClientFactory"/> owns the full handler/client lifecycle
    /// so an MEAI consumer disposing a pooled client mid-fleet never tears down shared resources.
    /// </summary>
    internal OllamaChatClient(
        OllamaApiClient apiClient,
        HttpClient imageFetchClient,
        bool ownsNativeClient,
        bool ownsImageFetchClient)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _chatClient = apiClient; // the native IChatClient (explicit implementation — called through the interface)
        _imageFetchClient = imageFetchClient ?? throw new ArgumentNullException(nameof(imageFetchClient));
        _ownsNativeClient = ownsNativeClient;
        _ownsImageFetchClient = ownsImageFetchClient;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var (normalizedMessages, narrowedOptions) =
            await PrepareRequestAsync(messages, options, cancellationToken).ConfigureAwait(false);

        return await _chatClient
            .GetResponseAsync(normalizedMessages, narrowedOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var (normalizedMessages, narrowedOptions) =
            await PrepareRequestAsync(messages, options, cancellationToken).ConfigureAwait(false);

        await foreach (var update in _chatClient
            .GetStreamingResponseAsync(normalizedMessages, narrowedOptions, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    /// The ADAPTER is the client consumers must hold (its normalizations are what make the native
    /// client AOT-safe and policy-compliant), so IChatClient-compatible lookups return <c>this</c>;
    /// everything else (notably <c>ChatClientMetadata</c> — provider "ollama", the client base
    /// address, the selected model) delegates to the native client for metadata parity.
    /// </summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null && typeof(IChatClient).IsAssignableFrom(serviceType))
        {
            return this;
        }

        return _chatClient.GetService(serviceType, serviceKey);
    }

    /// <summary>
    /// Disposes the native client (a no-op for the caller-supplied transport — verified) and the
    /// image-fetch client, only when this instance owns them. Pooled instances are disposed
    /// deterministically by the <see cref="OllamaChatClientFactory"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsNativeClient)
        {
            _apiClient.Dispose();
        }

        if (_ownsImageFetchClient)
        {
            _imageFetchClient.Dispose();
        }
    }

    // ---------------------------------------------------------------------
    //  Request preparation (the four normalizations)
    // ---------------------------------------------------------------------

    private async Task<(List<ChatMessage> Messages, ChatOptions? Options)> PrepareRequestAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        // 1) Model guard — parity with the deleted client, enforced BEFORE any network I/O.
        if (string.IsNullOrWhiteSpace(options?.ModelId) &&
            string.IsNullOrWhiteSpace(_apiClient.SelectedModel))
        {
            throw new InvalidOperationException(
                "No model id available: set ChatOptions.ModelId or configure a default model id on the OllamaChatClient.");
        }

        // 2) Options narrowing (functional drop of AdditionalProperties/Instructions).
        var narrowedOptions = options is null ? null : BuildNarrowedOptions(options);

        // 3) Message normalization (tool-result stringification + UriContent resolution).
        var normalizedMessages = new List<ChatMessage>();
        foreach (var message in messages)
        {
            normalizedMessages.Add(await NormalizeMessageAsync(message, cancellationToken).ConfigureAwait(false));
        }

        return (normalizedMessages, narrowedOptions);
    }

    /// <summary>
    /// Copies the options into a fresh instance (MEAI copy constructor) and drops
    /// <see cref="ChatOptions.AdditionalProperties"/> (functional narrowing — Ollama-specific
    /// request options never reach the wire, which also neutralizes the native mapper's own
    /// ~35-name Ollama option whitelist) and <see cref="ChatOptions.Instructions"/> (the old
    /// client never mapped it). <see cref="ChatOptions.ToolMode"/> stays copied but has no
    /// native 5.5.2 mapping (no <c>tool_choice</c> member) — tool constraints are dropped by
    /// design (maintainer-approved).
    /// </summary>
    private static ChatOptions BuildNarrowedOptions(ChatOptions options) => new()
    {
        ModelId = options.ModelId,
        Temperature = options.Temperature,
        TopP = options.TopP,
        TopK = options.TopK,
        MaxOutputTokens = options.MaxOutputTokens,
        Seed = options.Seed,
        FrequencyPenalty = options.FrequencyPenalty,
        PresencePenalty = options.PresencePenalty,
        StopSequences = options.StopSequences,
        Tools = options.Tools,
        ToolMode = options.ToolMode, // copied through but NOT mapped natively (no tool_choice in 5.5.2)
        ResponseFormat = options.ResponseFormat,
        Reasoning = options.Reasoning,
        // DELIBERATELY NOT copied (functional narrowing): AdditionalProperties (Ollama-specific
        // request options — num_ctx, repeat_penalty, keep_alive, ... — never reach the wire)
        // and Instructions (the deleted client never mapped it either).
    };

    private async Task<ChatMessage> NormalizeMessageAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        List<AIContent>? rebuilt = null;

        foreach (var content in message.Contents)
        {
            AIContent replacement = content;

            if (content is FunctionResultContent result)
            {
                // AOT-mandatory (package-internal serialization path) AND restores the raw-string
                // tool-result wire shape (role:"tool" + plain content).
                replacement = new TextContent(FunctionResultToString(result.Result));
            }
            else if (content is UriContent uriContent && IsImageUri(uriContent))
            {
                replacement = await FetchImageContentAsync(uriContent, cancellationToken).ConfigureAwait(false);
            }

            if (!ReferenceEquals(replacement, content))
            {
                rebuilt ??= [];
                rebuilt.Add(replacement);
            }
            else if (rebuilt is not null)
            {
                rebuilt.Add(content);
            }
        }

        return rebuilt is null
            ? message
            : new ChatMessage(message.Role, rebuilt) { AuthorName = message.AuthorName };
    }

    private static bool IsImageUri(UriContent uriContent)
    {
        var mediaType = uriContent.MediaType;
        // image/* is the explicit case. "application/octet-stream" is MEAI's inference FALLBACK
        // for unknown extensions — the inbound OpenAI parser emits exactly that for image_url
        // parts without a recognized extension (it passes a null media type and MEAI fills the
        // fallback), so excluding it would silently drop those images. null stays defensive.
        // Text/* and other typed media are never fetched (they are not images on the wire).
        return mediaType is null
            || mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------
    //  UriContent image resolution (router-side fetch, maintainer-approved policy)
    // ---------------------------------------------------------------------

    private async Task<DataContent> FetchImageContentAsync(UriContent uriContent, CancellationToken cancellationToken)
    {
        var uri = uriContent.Uri;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            // Non-http(s) schemes fail the request the same way (null status ⇒ transport
            // failure ⇒ failover), never as a terminal upstream error.
            throw new HttpRequestException(
                $"Image URI scheme '{uri.Scheme}' is not supported (only http/https).",
                null,
                (HttpStatusCode?)null);
        }

        try
        {
            return await FetchImageCoreAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Genuine request cancellation: surfaced to the caller unmodified.
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            // Already normalized (non-2xx fetch / over-cap): rethrow as-is.
            throw;
        }
        catch (Exception ex)
        {
            // A failed router-side fetch (transport fault, the 30 s client timeout's
            // TaskCanceledException, read errors, ...) is NOT an upstream-daemon response:
            // null status ⇒ the middleware classifies transport failure ⇒ failover.
            throw new HttpRequestException($"Failed to fetch image content from {uri}: {ex.Message}", ex, (HttpStatusCode?)null);
        }
    }

    private async Task<DataContent> FetchImageCoreAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _imageFetchClient
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Image fetch from {uri} failed with HTTP {(int)response.StatusCode}.",
                null,
                (HttpStatusCode?)null);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var accumulated = new MemoryStream();
        var buffer = new byte[ImageFetchReadBufferSize];
        var total = 0L;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxImageBytes)
            {
                throw new HttpRequestException(
                    $"Image content from {uri} exceeds the {MaxImageBytes / (1024 * 1024)} MiB limit.",
                    null,
                    (HttpStatusCode?)null);
            }

            accumulated.Write(buffer, 0, read);
        }

        // The response Content-Type is preserved (fallback image/png, matching the
        // ProtocolConverterJsonHelpers convention for inbound base64 images without a type).
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
        return new DataContent(accumulated.ToArray(), mediaType);
    }

    private static string FunctionResultToString(object? result) => result switch
    {
        null => string.Empty,
        string s => s,
        _ => SerializeToJsonString(result),
    };

    private static string SerializeToJsonString(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            // Shared AOT-safe JSON value walker (no reflection, no dynamic) — task #112: now
            // lives in ProtocolConverterJsonHelpers (moved with the deleted OpenAiHttpChatClient).
            ProtocolConverterJsonHelpers.WriteJson(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static HttpClient CreateImageFetchClient()
    {
        // One shared, factory-owned-style fetch client per standalone client: standard pooling
        // policy + a 30 s budget (long generations use the per-request token; image fetches must not).
        return new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }
}
