using System.Buffers;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using OllamaRouter.Converters;
using OllamaRouter.Converters.Dtos;
using OllamaRouter.Serialization;

namespace OllamaRouter.Services.Clients;

/// <summary>
/// Lightweight Native-AOT-safe <see cref="IChatClient"/> for OpenAI-compatible
/// <c>/chat/completions</c> endpoints (task #60). Stateless per request except for the configured
/// base address / default model / API key; thread-safe for concurrent use per the
/// <see cref="IChatClient"/> contract.
/// <para>
/// The implementation targets the <c>Microsoft.Extensions.AI.Abstractions</c> 10.9.0 surface
/// (<c>GetResponseAsync</c>/<c>GetStreamingResponseAsync</c>/<c>GetService</c>/<c>Dispose</c>) and
/// keeps every AOT guarantee intact: ALL JSON (de)serialization flows through the source-generated
/// <see cref="OllamaRouterJsonSerializerContext"/> (inbound deserialization) and
/// <see cref="WireJsonSerializerContext"/> (compact outbound request bodies) — typed
/// <see cref="JsonTypeInfo{T}"/> only, never the reflection-based generic overloads — and tool
/// arguments/schemas go through the explicit <see cref="WriteJson"/> walker (no reflection, no
/// <c>dynamic</c>, no <c>Activator</c>).
/// </para>
/// <para>
/// Streaming follows the OpenAI SSE contract with a pooled-buffer, line-oriented reader
/// (no string materialization of the payload), tolerating LF/CRLF/CR, comments, <c>event:</c>/
/// <c>id:</c>/<c>retry:</c> lines, and <c>[DONE]</c>. Tool calls are accumulated across chunk
/// fragments and emitted as <see cref="FunctionCallContent"/>s on the
/// <c>finish_reason == "tool_calls"</c> update. Token usage travels the canonical MEAI route:
/// a <see cref="UsageContent"/> carried in an update's contents is aggregated by
/// <c>ChatResponseExtensions.ToChatResponseAsync</c> into <c>ChatResponse.Usage</c>
/// (ChatResponseUpdate has no Usage property in 10.9.0).
/// </para>
/// </summary>
public sealed class OpenAiHttpChatClient : IChatClient
{
    private const int SseReadBufferSize = 4096;
    private const int MaxErrorBodyBytes = 16 * 1024;
    private const int ErrorBodySnippetLength = 200;

    // Compact (non-indented) source-generated context for the outbound wire bodies + the small
    // SerializeToElement targets (plain strings / stop-sequence arrays / content-part arrays).
    private static readonly WireJsonSerializerContext s_wire = WireJsonSerializerContext.Default;


    private static readonly OllamaRouterJsonSerializerContext s_context = OllamaRouterJsonSerializerContext.Default;
    private static readonly byte[] s_doneToken = Encoding.ASCII.GetBytes("[DONE]");
    private static readonly byte[] s_dataPrefix = Encoding.ASCII.GetBytes("data:");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string? _defaultModelId;
    private readonly string? _apiKey;
    private readonly string _providerName;
    private int _disposed;

    /// <summary>
    /// Public constructor (tests / direct use): this client OWNS <paramref name="httpClient"/> and
    /// disposes it in <see cref="Dispose"/>.
    /// </summary>
    public OpenAiHttpChatClient(
        HttpClient httpClient,
        string? defaultModelId = null,
        string? apiKey = null,
        string providerName = "openai-compatible")
        : this(httpClient, ownsHttpClient: true, defaultModelId, apiKey, providerName)
    {
    }

    /// <summary>
    /// Factory path: pooled instances do NOT own the <see cref="HttpClient"/> — the
    /// <see cref="OpenAiChatClientFactory"/> owns the full handler/client lifecycle so a
    /// MEAI consumer disposing a pooled client mid-fleet never tears down shared connection pools.
    /// </summary>
    internal OpenAiHttpChatClient(
        HttpClient httpClient,
        bool ownsHttpClient,
        string? defaultModelId,
        string? apiKey,
        string providerName)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _defaultModelId = defaultModelId;
        _apiKey = apiKey;
        _providerName = string.IsNullOrWhiteSpace(providerName) ? "openai-compatible" : providerName;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var dto = BuildRequestDto(messages, options, stream: false);
        using var response = await SendAsync(BuildHttpRequest(dto, streaming: false), cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateHttpExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer
            .DeserializeAsync(bodyStream, s_context.OpenAiChatCompletionResponseDto, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OpenAiHttpException(
                "OpenAI-compatible endpoint returned an empty completion response.", response.StatusCode, null, null);

        return MapNonStreamingResponse(payload);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var dto = BuildRequestDto(messages, options, stream: true);
        using var response = await SendAsync(BuildHttpRequest(dto, streaming: true), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateHttpExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        // The response stream is await-using-scoped: aborting the enumeration releases the socket
        // immediately (no background tasks are ever started, so a cancelled enumeration leaves no
        // unobserved task exceptions and no leaked waits).
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var toolCalls = new Dictionary<int, AccumulatedToolCall>();
        var eventData = new List<byte>();
        UsageDetails? pendingUsage = null;
        var usageEmitted = false;

        await foreach (var line in ReadSseLinesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            // Blank line = event boundary: dispatch any accumulated data payload.
            if (line.Length == 0)
            {
                if (eventData.Count == 0)
                {
                    continue;
                }

                var boundaryChunk = DeserializeChunk(eventData);
                eventData.Clear();
                var boundaryUpdate = ProcessChunk(boundaryChunk, ref toolCalls, ref pendingUsage, ref usageEmitted);
                if (boundaryUpdate is not null)
                {
                    yield return boundaryUpdate;
                }

                continue;
            }

            // data: prefix -> payload after ONE optional space; everything else
            // (": keepalive" comments, event:, id:, retry:) is ignored.
            if (!line.StartsWith(s_dataPrefix))
            {
                continue;
            }

            var payload = line.AsSpan(s_dataPrefix.Length);
            if (payload.Length > 0 && payload[0] == (byte)' ')
            {
                payload = payload.Slice(1);
            }

            if (payload.SequenceEqual(s_doneToken))
            {
                // [DONE] terminates enumeration cleanly; any bytes after it are ignored.
                break;
            }

            // Multi-line events: join data lines with '\n' (a JSON parser tolerates the
            // whitespace; OpenAI itself always sends one self-contained object per line).
            if (eventData.Count > 0)
            {
                eventData.Add((byte)'\n');
            }

            eventData.AddRange(payload);
        }

        // Defensive EOF: some servers omit the final blank line / [DONE].
        if (eventData.Count > 0)
        {
            var chunk = DeserializeChunk(eventData);
            eventData.Clear();
            var update = ProcessChunk(chunk, ref toolCalls, ref pendingUsage, ref usageEmitted);
            if (update is not null)
            {
                yield return update;
            }
        }

        // Flush any tool calls the provider never terminated with finish_reason:"tool_calls".
        if (toolCalls.Count > 0)
        {
            var update = new ChatResponseUpdate();
            EmitAccumulatedToolCalls(toolCalls, update);
            toolCalls.Clear();
            yield return update;
        }

        // Terminal usage update (R3 transport): MEAI's ToChatResponseAsync aggregates the
        // UsageContent into ChatResponse.Usage. Emitted only when the usage did not already
        // ride on a content-bearing update.
        if (pendingUsage is not null && !usageEmitted)
        {
            yield return new ChatResponseUpdate(null, [new UsageContent(pendingUsage)]);
        }
    }

    /// <summary>
    /// AOT-safe service lookup (the documented MEAI <c>GetService</c> pattern — no reflection beyond
    /// the <see cref="Type"/> comparison): <c>ChatClientMetadata</c> (the only metadata exposure in
    /// 10.9.0) and the client itself for <c>IChatClient</c>; anything else is <c>null</c>.
    /// </summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata(
                _providerName,
                _httpClient.BaseAddress ?? new Uri("http://localhost"),
                _defaultModelId ?? string.Empty);
        }

        if (serviceType == typeof(IChatClient))
        {
            return this;
        }

        return null;
    }

    /// <summary>
    /// Disposes the underlying <see cref="HttpClient"/> ONLY when this client owns it (public
    /// constructor / non-pooled use). Pooled instances are disposed deterministically by the
    /// <see cref="OpenAiChatClientFactory"/> (DropClient / factory disposal).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    // ---------------------------------------------------------------------
    // Request construction (canonical -> wire)
    // ---------------------------------------------------------------------

    private OpenAiChatCompletionRequestDto BuildRequestDto(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        // Model id resolution is enforced BEFORE any network I/O.
        var modelId = options?.ModelId ?? _defaultModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException(
                "No model id available: set ChatOptions.ModelId or configure a default model id on the OpenAiHttpChatClient.");
        }

        var dto = new OpenAiChatCompletionRequestDto { Model = modelId };
        if (stream)
        {
            dto.Stream = true;
            dto.StreamOptions = new OpenAiStreamOptionsDto { IncludeUsage = true };
        }

        foreach (var message in messages)
        {
            dto.Messages.Add(MapRequestMessage(message));
        }

        if (options is null)
        {
            return dto;
        }

        dto.Temperature = options.Temperature;
        dto.TopP = options.TopP;
        dto.TopK = options.TopK;
        // max_tokens (not max_completion_tokens) for cross-provider compatibility — vLLM /
        // llama.cpp / Groq all accept max_tokens; OpenAI's deprecation warning is acceptable.
        dto.MaxTokens = options.MaxOutputTokens;
        dto.Seed = options.Seed;
        dto.FrequencyPenalty = options.FrequencyPenalty;
        dto.PresencePenalty = options.PresencePenalty;

        if (options.StopSequences is { Count: 1 })
        {
            // The OpenAI wire contract: a single stop sequence is a plain string, n > 1 an array.
            dto.Stop = JsonSerializer.SerializeToElement(options.StopSequences[0] ?? string.Empty, s_wire.String);
        }
        else if (options.StopSequences is { Count: > 1 })
        {
            var sequences = new List<string>(options.StopSequences.Count);
            foreach (var sequence in options.StopSequences)
            {
                sequences.Add(sequence ?? string.Empty);
            }

            dto.Stop = JsonSerializer.SerializeToElement(sequences, s_wire.ListString);
        }

        if (options.Reasoning?.Effort is { } effort && effort != ReasoningEffort.None)
        {
            dto.ReasoningEffort = effort.ToString().ToLowerInvariant();
        }

        // ToolMode == None ⇒ tools are omitted entirely; Auto/null ⇒ provider default (omitted);
        // Required ⇒ "tool_choice":"required" (v1: RequireSpecific(name) also collapses to "required").
        if (options.Tools is { Count: > 0 } && options.ToolMode is not NoneChatToolMode)
        {
            var tools = new List<OpenAiRequestToolDto>();
            foreach (var tool in options.Tools)
            {
                if (tool is null)
                {
                    continue;
                }

                var function = tool.GetService<AIFunction>();
                if (function is null)
                {
                    continue; // hosted / MCP / unknown tool subtypes are skipped in v1.
                }

                var functionDto = new OpenAiRequestToolFunctionDto
                {
                    Name = string.IsNullOrEmpty(tool.Name) ? string.Empty : tool.Name,
                    Description = tool.Description,
                };

                // AIFunction.JsonSchema IS the parameters schema object in MEAI 10.9.0.
                if (function.JsonSchema.ValueKind == JsonValueKind.Object)
                {
                    functionDto.Parameters = function.JsonSchema;
                }

                tools.Add(new OpenAiRequestToolDto { Type = "function", Function = functionDto });
            }

            if (tools.Count > 0)
            {
                dto.Tools = tools;
                if (options.ToolMode is RequiredChatToolMode)
                {
                    dto.ToolChoice = "required";
                }
            }
        }

        if (options.ResponseFormat is ChatResponseFormatJson)
        {
            // v1: only {"type":"json_object"} (schema-bearing response_format is out of scope).
            dto.ResponseFormat = new OpenAiResponseFormatDto { Type = "json_object" };
        }

        return dto;
    }

    private static OpenAiRequestMessageDto MapRequestMessage(ChatMessage message)
    {
        var dto = new OpenAiRequestMessageDto { Role = MapRole(message.Role) };
        if (!string.IsNullOrEmpty(message.AuthorName))
        {
            dto.Name = message.AuthorName;
        }

        var parts = new List<OpenAiContentPartDto>();
        var reasoning = new StringBuilder();
        var toolCalls = new List<OpenAiRequestToolCallDto>();
        string? toolCallId = null;

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    parts.Add(new OpenAiContentPartDto { Type = "text", Text = text.Text ?? string.Empty });
                    break;

                case DataContent { MediaType: string mediaType } data
                    when mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                    // In-memory image → base64 data-URI (http(s) UriContent / other MIME / hosted
                    // content / error content is intentionally ignored in v1).
                    parts.Add(new OpenAiContentPartDto
                    {
                        Type = "image_url",
                        ImageUrl = new OpenAiImageUrlDto
                        {
                            Url = $"data:{mediaType};base64,{Convert.ToBase64String(data.Data.Span)}",
                        },
                    });
                    break;

                case FunctionCallContent call:
                    toolCalls.Add(new OpenAiRequestToolCallDto
                    {
                        Id = string.IsNullOrEmpty(call.CallId) ? string.Empty : call.CallId,
                        Type = "function",
                        Function = new OpenAiRequestToolCallFunctionDto
                        {
                            Name = call.Name ?? string.Empty,
                            // OpenAI requires arguments as a JSON STRING.
                            Arguments = SerializeToJsonString(call.Arguments),
                        },
                    });
                    break;

                case FunctionResultContent result:
                    // Force role "tool" + tool_call_id; OpenAI requires the result as a string.
                    toolCallId = result.CallId ?? string.Empty;
                    parts.Add(new OpenAiContentPartDto { Type = "text", Text = FunctionResultToString(result.Result) });
                    break;

                case TextReasoningContent reasoningContent:
                    reasoning.Append(reasoningContent.Text ?? string.Empty);
                    break;

                default:
                    // Everything else (non-http(s) UriContent, audio, hosted, ErrorContent, ...) is ignored in v1.
                    break;
            }
        }

        if (toolCallId is not null)
        {
            dto.Role = "tool";
            dto.ToolCallId = toolCallId;
        }

        if (reasoning.Length > 0)
        {
            dto.ReasoningContent = reasoning.ToString();
        }

        if (toolCalls.Count > 0)
        {
            dto.ToolCalls = toolCalls;
        }

        if (parts.Count == 1 && string.Equals(parts[0].Type, "text", StringComparison.Ordinal))
        {
            // Single text and no other parts → content is a plain JSON string.
            dto.Content = JsonSerializer.SerializeToElement(parts[0].Text ?? string.Empty, s_wire.String);
            }
            else if (parts.Count > 0)
            {
            dto.Content = JsonSerializer.SerializeToElement(parts, s_wire.ListOpenAiContentPartDto);
            }

        return dto;
    }

    private static string MapRole(ChatRole role)
    {
        var value = role.Value?.ToLowerInvariant() ?? string.Empty;
        return value switch
        {
            "system" or "user" or "assistant" or "tool" or "developer" => value,
            // Defensive fallback for unknown/custom roles: never throw, never leak an alias.
            _ => "user",
        };
    }

    private HttpRequestMessage BuildHttpRequest(OpenAiChatCompletionRequestDto dto, bool streaming)
    {
        // The configured BaseAddress may already carry a provider path prefix
        // (e.g. http://host:8000/v1); relative resolution handles both root-mounted and prefixed bases.
        var baseAddress = _httpClient.BaseAddress
            ?? throw new InvalidOperationException(
                "The underlying HttpClient must have its BaseAddress set to the OpenAI-compatible endpoint.");

        // Path-prefixed bases (e.g. http://host:8000/v1) must end with '/' so the relative
        // resolution APPENDS "chat/completions" instead of dropping the last segment
        // (standard URI resolution would otherwise yield http://host:8000/chat/completions).
        var baseUri = baseAddress.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseAddress
            : new Uri(baseAddress.AbsoluteUri + "/");

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "chat/completions"))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(dto, s_wire.OpenAiChatCompletionRequestDto)),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        }

        if (streaming)
        {
            request.Headers.Accept.ParseAdd("text/event-stream");
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is never wrapped: it surfaces to the caller unmodified.
            throw;
        }
        catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Transport-level failure (connect / DNS / read): no status code — the failover
            // logic treats StatusCode == null as "endpoint unreachable".
            throw new OpenAiHttpException(
                "Failed to reach the OpenAI-compatible endpoint: " + ex.Message,
                statusCode: null,
                errorType: null,
                errorCode: null,
                innerException: ex);
        }
    }

    // ---------------------------------------------------------------------
    // Error model (shared by both verbs)
    // ---------------------------------------------------------------------

    private static async Task<OpenAiHttpException> CreateHttpExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Read the error body bounded to 16 KiB (best effort — a body read failure degrades
        // to the raw-snippet-less message rather than masking the HTTP error).
        var rawBody = string.Empty;
        try
        {
            using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[MaxErrorBodyBytes];
            var read = await bodyStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                rawBody = Encoding.UTF8.GetString(buffer, 0, read);
            }
        }
        catch (Exception)
        {
            // best effort — keep rawBody empty.
        }

        string? payloadMessage = null;
        string? errorType = null;
        string? errorCode = null;
        try
        {
            var errorDto = JsonSerializer.Deserialize(rawBody, s_context.OpenAiErrorResponseDto);
            payloadMessage = errorDto?.Error?.Message;
            errorType = errorDto?.Error?.Type;
            errorCode = errorDto?.Error?.Code;
        }
        catch (JsonException)
        {
            // Unparseable/empty error body: fall back to the raw snippet below.
        }

        var status = (int)response.StatusCode;
        var detail = !string.IsNullOrWhiteSpace(payloadMessage)
            ? payloadMessage!
            : (rawBody.Length > ErrorBodySnippetLength ? rawBody[..ErrorBodySnippetLength] : rawBody);

        var message = status switch
        {
            401 or 403 => $"OpenAI-compatible endpoint authentication failed (HTTP {status}): {detail}",
            429 => $"OpenAI-compatible endpoint rate limited (HTTP {status}): {detail}",
            503 => $"OpenAI-compatible endpoint unavailable/overloaded (HTTP {status}): {detail}",
            _ => $"OpenAI-compatible endpoint error (HTTP {status}, type={errorType ?? "unknown"}): {detail}",
        };

        return new OpenAiHttpException(message, response.StatusCode, errorType, errorCode);
    }

    // ---------------------------------------------------------------------
    // Response / chunk mapping (wire -> canonical)
    // ---------------------------------------------------------------------

    private static ChatResponse MapNonStreamingResponse(OpenAiChatCompletionResponseDto payload)
    {
        // Empty/missing choices with 200 → a ChatResponse WITHOUT an assistant message
        // (provider-specific empty completions must not throw).
        var chatResponse = new ChatResponse
        {
            ResponseId = payload.Id,
            ModelId = payload.Model,
            Usage = MapUsage(payload.Usage),
            RawRepresentation = payload,
        };

        if (payload.Created is { } created)
        {
            chatResponse.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(created);
        }

        if (payload.Choices is { Count: > 0 })
        {
            var firstChoice = payload.Choices[0];
            chatResponse.FinishReason = MapFinishReason(firstChoice.FinishReason);
            if (firstChoice.Message is { } message)
            {
                chatResponse.Messages.Add(MapAssistantMessage(message));
            }
        }

        return chatResponse;
    }

    private static ChatMessage MapAssistantMessage(OpenAiResponseMessageDto message)
    {
        var contents = new List<AIContent>();

        if (!string.IsNullOrEmpty(message.ReasoningContent))
        {
            contents.Add(new TextReasoningContent(message.ReasoningContent));
        }

        if (message.Content is JsonElement content)
        {
            switch (content.ValueKind)
            {
                case JsonValueKind.String:
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        contents.Add(new TextContent(text));
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var part in content.EnumerateArray())
                    {
                        // v1: text parts only; other part types (images, audio) are ignored.
                        if (part.ValueKind == JsonValueKind.Object &&
                            part.TryGetProperty("type", out var type) &&
                            type.ValueKind == JsonValueKind.String &&
                            string.Equals(type.GetString(), "text", StringComparison.Ordinal) &&
                            part.TryGetProperty("text", out var partText) &&
                            partText.ValueKind == JsonValueKind.String)
                        {
                            contents.Add(new TextContent(partText.GetString() ?? string.Empty));
                        }
                    }

                    break;
            }
        }

        if (message.ToolCalls is not null)
        {
            foreach (var call in message.ToolCalls)
            {
                contents.Add(new FunctionCallContent(
                    call.Id ?? string.Empty,
                    call.Function?.Name ?? string.Empty,
                    ParseToolArguments(call.Function?.Arguments)));
            }
        }

        return contents.Count > 0
            ? new ChatMessage(ChatRole.Assistant, contents)
            : new ChatMessage(ChatRole.Assistant, Array.Empty<AIContent>());
    }

    private static OpenAiStreamChunkDto DeserializeChunk(List<byte> eventData)
    {
        // Deserialized DIRECTLY from the raw payload bytes — no intermediate string materialization.
        return JsonSerializer.Deserialize(CollectionsMarshal.AsSpan(eventData), s_context.OpenAiStreamChunkDto) ?? new OpenAiStreamChunkDto();
    }

    private static ChatResponseUpdate? ProcessChunk(
        OpenAiStreamChunkDto chunk,
        ref Dictionary<int, AccumulatedToolCall> toolCalls,
        ref UsageDetails? pendingUsage,
        ref bool usageEmitted)
    {
        var update = new ChatResponseUpdate();
        var hasContent = false;

        if (!string.IsNullOrEmpty(chunk.Id))
        {
            update.ResponseId = chunk.Id;
        }

        if (!string.IsNullOrEmpty(chunk.Model))
        {
            update.ModelId = chunk.Model;
        }

        if (chunk.Created is { } created)
        {
            update.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(created);
        }

        if (chunk.Usage is { } usage)
        {
            pendingUsage = MapUsage(usage);
        }

        if (chunk.Choices is not null)
        {
            foreach (var choice in chunk.Choices)
            {
                var delta = choice.Delta;
                if (delta is not null)
                {
                    if (!string.IsNullOrEmpty(delta.Role) && update.Role is null)
                    {
                        update.Role = new ChatRole(delta.Role);
                    }

                    if (!string.IsNullOrEmpty(delta.Content))
                    {
                        update.Contents.Add(new TextContent(delta.Content));
                        hasContent = true;
                    }

                    if (!string.IsNullOrEmpty(delta.ReasoningContent))
                    {
                        update.Contents.Add(new TextReasoningContent(delta.ReasoningContent));
                        hasContent = true;
                    }

                    // OpenAI splits tool calls across chunks: id/function.name arrive once
                    // (typically chunk 0), function.arguments arrives as string fragments.
                    if (delta.ToolCalls is not null)
                    {
                        foreach (var toolCall in delta.ToolCalls)
                        {
                            var index = toolCall.Index ?? 0;
                            if (!toolCalls.TryGetValue(index, out var accumulated))
                            {
                                accumulated = new AccumulatedToolCall
                                {
                                    Id = toolCall.Id,
                                    Name = toolCall.Function?.Name ?? string.Empty,
                                };
                                toolCalls[index] = accumulated;
                            }
                            else
                            {
                                if (!string.IsNullOrEmpty(toolCall.Id))
                                {
                                    accumulated.Id = toolCall.Id;
                                }

                                if (!string.IsNullOrEmpty(toolCall.Function?.Name))
                                {
                                    accumulated.Name += toolCall.Function!.Name;
                                }
                            }

                            if (!string.IsNullOrEmpty(toolCall.Function?.Arguments))
                            {
                                accumulated.Arguments.Append(toolCall.Function!.Arguments);
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(choice.FinishReason))
                {
                    update.FinishReason = MapFinishReason(choice.FinishReason);
                    hasContent = true;

                    if (string.Equals(choice.FinishReason, "tool_calls", StringComparison.Ordinal))
                    {
                        EmitAccumulatedToolCalls(toolCalls, update);
                        toolCalls.Clear();
                    }
                }
            }
        }

        // Usage merged into a content-bearing terminal chunk rides on THIS update, so the
        // MEAI aggregator still picks it up without a separate frame.
        if (chunk.Usage is not null && hasContent && pendingUsage is not null)
        {
            update.Contents.Add(new UsageContent(pendingUsage));
            usageEmitted = true;
        }

        return hasContent ? update : null;
    }

    private static void EmitAccumulatedToolCalls(Dictionary<int, AccumulatedToolCall> toolCalls, ChatResponseUpdate update)
    {
        foreach (var pair in toolCalls.OrderBy(p => p.Key))
        {
            update.Contents.Add(new FunctionCallContent(
                pair.Value.Id ?? string.Empty,
                pair.Value.Name,
                ParseToolArguments(pair.Value.Arguments.ToString())));
        }
    }

    /// <summary>
    /// Parses an OpenAI tool-call arguments JSON <b>string</b> into the canonical value dictionary
    /// via the source-generated <c>Dictionary&lt;string, JsonElement&gt;</c> target registered by task #59.
    /// Empty/whitespace arguments → empty dictionary. Malformed JSON → <see cref="JsonException"/>
    /// (a broken provider stream is a protocol error and propagates).
    /// </summary>
    internal static IDictionary<string, object?> ParseToolArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new Dictionary<string, object?>();
        }

        var raw = JsonSerializer.Deserialize(arguments, s_context.DictionaryStringJsonElement)
            ?? new Dictionary<string, JsonElement>();
        return ProtocolConverterJsonHelpers.DictionaryToJsonDictionary(raw);
    }

    private static UsageDetails? MapUsage(OpenAiUsageDto? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = usage.PromptTokens,
            OutputTokenCount = usage.CompletionTokens,
            TotalTokenCount = usage.TotalTokens ?? (usage.PromptTokens.HasValue || usage.CompletionTokens.HasValue
                ? (usage.PromptTokens ?? 0) + (usage.CompletionTokens ?? 0)
                : null),
            ReasoningTokenCount = usage.CompletionTokensDetails?.ReasoningTokens,
        };
    }

    private static ChatFinishReason? MapFinishReason(string? reason) => reason switch
    {
        "stop" => ChatFinishReason.Stop,
        "length" => ChatFinishReason.Length,
        "tool_calls" => ChatFinishReason.ToolCalls,
        "content_filter" => ChatFinishReason.ContentFilter,
        // null / unknown provider-specific literals → null (the encoder layer treats null as Stop).
        _ => null,
    };

    // ---------------------------------------------------------------------
    // SSE reader (pooled buffer, no string materialization)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Yields raw SSE lines from the response stream: a pooled 4 KiB read buffer plus a growable
    /// line accumulator. Splits on <c>\n</c> (tolerating <c>\r</c> — i.e. LF/CRLF/CR). On stream
    /// EOF a pending partial line is still yielded (defensive: some servers omit the final newline).
    /// </summary>
    private static async IAsyncEnumerable<byte[]> ReadSseLinesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(SseReadBufferSize);
        var line = new List<byte>(256);
        try
        {
            int bytesRead;
            while ((bytesRead = await stream
                .ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < bytesRead; i++)
                {
                    var b = buffer[i];
                    if (b == (byte)'\n' || b == (byte)'\r')
                    {
                        // CRLF naturally yields the content line at '\r' and the empty-line
                        // event boundary at the following '\n' (an empty boundary is a no-op
                        // in the event assembler, which only dispatches pending data).
                        yield return line.ToArray();
                        line.Clear();
                    }
                    else
                    {
                        line.Add(b);
                    }
                }
            }

            if (line.Count > 0)
            {
                yield return line.ToArray();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---------------------------------------------------------------------
    // Reflection-free JSON value walker (tool arguments / function results)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Serializes tool-call arguments (an <c>IDictionary&lt;string, object?&gt;</c>) to the JSON
    /// <b>string</b> required on the OpenAI wire. Mirrors the value rules of
    /// <see cref="ProtocolConverterJsonHelpers"/>: no reflection, no <c>IConvertible</c> typing.
    /// </summary>
    private static string SerializeToJsonString(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteJson(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Explicit, allocation-conscious JSON value walker supporting the value shapes the canonical
    /// layer can actually carry (<see cref="ProtocolConverterJsonHelpers"/> output rules):
    /// string / long / int / double / bool / JsonElement / Dictionary / list / null.
    /// Unrecognized shapes degrade to <c>null</c> rather than throwing mid-request.
    /// </summary>
    internal static void WriteJson(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;

            case string s:
                writer.WriteStringValue(s);
                break;

            case long l:
                writer.WriteNumberValue(l);
                break;

            case int i:
                writer.WriteNumberValue(i);
                break;

            case double d:
                writer.WriteNumberValue(d);
                break;

            case bool b:
                writer.WriteBooleanValue(b);
                break;

            case JsonElement element:
                element.WriteTo(writer);
                break;

            case IDictionary<string, object?> dictionary:
                writer.WriteStartObject();
                foreach (var pair in dictionary)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteJson(writer, pair.Value);
                }

                writer.WriteEndObject();
                break;

            case IEnumerable enumerable:
                // Catches IList<object?> and any non-string enumerable of values
                // (string is handled above; JsonElement implements IEnumerable but is too).
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    WriteJson(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// Function-result → OpenAI wire string: strings pass through as-is; any other scalar /
    /// dictionary / list value is JSON-serialized with the <see cref="WriteJson"/> walker
    /// (OpenAI requires a string for tool-result content).
    /// </summary>
    private static string FunctionResultToString(object? result)
    {
        return result switch
        {
            null => string.Empty,
            string s => s,
            _ => SerializeToJsonString(result),
        };
    }

    /// <summary>Mutator state for one streamed tool call, keyed by the wire <c>tool_calls[i].index</c>.</summary>
    private sealed class AccumulatedToolCall
    {
        public string? Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public StringBuilder Arguments { get; } = new();
    }
}
