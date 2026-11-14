using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace OllamaRouter.Services.Clients;

/// <summary>
/// Delegating <see cref="IChatClient"/> over the first-party Microsoft.Extensions.AI.OpenAI
/// adapter (task #112, replacing the previous 1,035-line hand-rolled client — same public name
/// and namespace as the deleted <c>OpenAiHttpChatClient</c>). It owns exactly one per-request
/// normalization and then delegates <c>GetResponseAsync</c>/<c>GetStreamingResponseAsync</c>
/// UNCHANGED to the native client (<c>OpenAIClient.GetChatClient(model).AsIChatClient()</c> over
/// the factory-owned <c>HttpClient</c> pipeline), whose built-in mapping handles the standard
/// <see cref="ChatOptions"/> → <c>chat/completions</c> wire mapping, the SSE stream reads, and
/// the response → MEAI mapping:
/// <list type="number">
/// <item><b>Model guard</b> (parity with the old client, pre-I/O): a blank
/// <c>ChatOptions.ModelId</c> throws <see cref="InvalidOperationException"/> BEFORE any HTTP
/// request — the middleware classifies pre-first-byte non-HTTP exceptions as uncommitted
/// (failover), whereas the provider's 400 for an empty model would be a terminal upstream error
/// (different classification). The pooled client is constructed with a private placeholder model
/// that is NEVER sent: the middleware always sets <c>requestOptions.ModelId</c> per request, and
/// the adapter's per-request mapping overrides the client-level model (verified on the wire).</item>
/// </list>
/// <para>
/// <b>Options narrowing (maintainer decision, mirroring task #111):</b> only the STANDARD
/// <see cref="ChatOptions"/> properties are mapped (natively). <see cref="ChatOptions.AdditionalProperties"/>
/// is DROPPED by the adapter (it maps standard properties only): Ollama-protocol request options
/// the parser stores there (<c>num_ctx</c>, <c>repeat_penalty</c>, raw <c>tool_choice</c> /
/// <c>reasoning_effort</c> values, …) never reach the OpenAI wire. Only tools that ARE
/// <c>AIFunction</c>/<c>AIFunctionDeclaration</c> (as produced by the OpenAI inbound parser) are
/// mapped — arbitrary <c>AITool</c> implementations are silently dropped by the native mapper.
/// </para>
/// <para>
/// v1 wire deltas accepted with the native mapping (documented in the README "Converter dispatch
/// limitations" note): <c>max_tokens</c> → <c>max_completion_tokens</c>; <c>top_k</c> dropped
/// (no native OpenAI mapping); <c>tool_choice</c> now explicitly expressed
/// (auto/none/required/function — previously only "required" was ever emitted); a single stop
/// sequence serializes as a 1-element array; streamed tool-call NAMES are taken from the first
/// fragment (arguments still accumulate across fragments). Two further native narrowings were
/// empirically verified and maintainer-accepted (documented + unit-tested, adapter stays strictly
/// delegating): a non-streaming response with an empty or missing <c>choices</c> array surfaces
/// the SDK's <see cref="ArgumentOutOfRangeException"/> (the deleted client used to map that shape
/// to a message-less response), and a final SSE <c>data:</c> line at EOF WITHOUT the blank-line
/// terminator is not dispatched (strict SSE parsing; the deleted client dispatched pending data
/// defensively).
/// </para>
/// <para>
/// <b>Error semantics:</b> every downstream failure surfaces as the native
/// <see cref="System.ClientModel.ClientResultException"/> (non-2xx before the first byte:
/// <c>Status</c> = HTTP code + full body on <c>GetRawResponse()</c>; transport failure:
/// <c>Status == 0</c>, null raw response — the exact analogue of the deleted
/// <c>OpenAiHttpException.StatusCode == null</c> "endpoint unreachable" case; mid-stream break
/// after a 200: <see cref="System.IO.IOException"/>). Cancellation keeps surfacing
/// <see cref="OperationCanceledException"/> unmodified. The routing middleware classifies all of
/// these directly (no router-specific wrapper type).
/// </para>
/// <para>
/// <b>Non-owning disposal by construction:</b> <see cref="Dispose"/> is a no-op — the adapter
/// never owns the transport (verified: disposing the M.E.AI adapter <c>IChatClient</c> is
/// harmless to the pooled client), so an MEAI consumer disposing a pooled client mid-fleet never
/// tears down shared resources; the <see cref="OpenAiChatClientFactory"/> alone disposes the
/// native client + <c>HttpClient</c> on <c>DropClient</c>/factory disposal.
/// </para>
/// </summary>
public sealed class OpenAiChatClient : IChatClient, IDisposable
{
    private readonly IChatClient _chatClient;

    /// <summary>
    /// Factory path: the adapter is ALWAYS non-owning — the <see cref="OpenAiChatClientFactory"/>
    /// owns the full handler/client lifecycle, so an MEAI consumer disposing a pooled client
    /// mid-fleet never tears down shared resources.
    /// </summary>
    internal OpenAiChatClient(IChatClient nativeChatClient)
    {
        ArgumentNullException.ThrowIfNull(nativeChatClient);
        _chatClient = nativeChatClient;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        GuardModelId(options);

        return await _chatClient
            .GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        GuardModelId(options);

        await foreach (var update in _chatClient
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    /// The ADAPTER is the client consumers hold, so <c>IChatClient</c>-compatible lookups return
    /// <c>this</c>; everything else (notably <c>ChatClientMetadata</c> — provider "OpenAI", the
    /// endpoint base address, the placeholder model id) delegates to the native adapter.
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
    /// No-op by construction (see the class summary): pooled instances are disposed
    /// deterministically by the <see cref="OpenAiChatClientFactory"/> alone.
    /// </summary>
    public void Dispose()
    {
        // Non-owning: the factory disposes the native client + HttpClient (verified safe —
        // disposing the M.E.AI adapter IChatClient never tears down the pooled transport).
    }

    /// <summary>
    /// Parity with the deleted client, enforced BEFORE any network I/O (see the class summary).
    /// </summary>
    private static void GuardModelId(ChatOptions? options)
    {
        if (string.IsNullOrWhiteSpace(options?.ModelId))
        {
            throw new InvalidOperationException(
                "No model id available: set ChatOptions.ModelId on the request options.");
        }
    }
}
