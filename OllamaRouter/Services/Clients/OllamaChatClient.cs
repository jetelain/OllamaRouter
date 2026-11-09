using System.Buffers;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaRouter.Converters.Dtos;
using OllamaRouter.Serialization;

namespace OllamaRouter.Services.Clients;

/// <summary>
/// Lightweight Native-AOT-safe <see cref="IChatClient"/> over an Ollama daemon's
/// <c>POST {base}/api/chat</c> endpoint (task #61) — the mirror image of
/// <see cref="OpenAiHttpChatClient"/> (task #60). It powers the CONVERTER dispatch path for
/// Ollama-protocol targets serving OpenAI-protocol clients (and vice versa).
/// <para>
/// ALL JSON (de)serialization flows through the source-generated
/// <see cref="OllamaRouterJsonSerializerContext"/> (NDJSON frames) and
/// <see cref="WireJsonSerializerContext"/> (compact outbound request bodies / scalar element
/// values) — typed <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/> only,
/// never the reflection-based generic overloads; tool arguments are rendered with the shared
/// <see cref="OpenAiHttpChatClient.WriteJson"/> value walker (no reflection, no <c>dynamic</c>).
/// </para>
/// <para>
/// Streaming reads NDJSON frames (pooled line reader, no string materialization of the whole
/// payload): each <c>done:false</c> frame yields a <see cref="ChatResponseUpdate"/> with
/// <see cref="TextContent"/> (message.content delta) and <see cref="FunctionCallContent"/>
/// (message.tool_calls) contents; the <c>done:true</c> frame supplies the finish reason and the
/// <c>prompt_eval_count</c>/<c>eval_count</c> usage, emitted as a trailing
/// <see cref="UsageContent"/> (the canonical MEAI usage route — ChatResponseUpdate has no
/// Usage property in 10.9.0).
/// </para>
/// <para>
/// v1 limitations (documented): <see cref="UriContent"/> image parts are dropped (base64
/// <see cref="DataContent"/> images map to <c>images[]</c>); reasoning/thinking contents are
/// not mapped; Ollama conveys no server-side tool-call id (a synthetic id is generated on
/// read, mirroring the inbound <c>OllamaChatRequestParser</c>).
/// </para>
/// </summary>
public sealed class OllamaChatClient : IChatClient
{
    private const int NdjsonReadBufferSize = 4096;
    private const int MaxErrorBodyBytes = 16 * 1024;
    private const int ErrorBodySnippetLength = 200;

    private static readonly WireJsonSerializerContext s_wire = WireJsonSerializerContext.Default;
    private static readonly OllamaRouterJsonSerializerContext s_context = OllamaRouterJsonSerializerContext.Default;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string? _defaultModelId;
    private readonly string _providerName;
    private int _disposed;

    /// <summary>
    /// Public constructor (tests / direct use): this client OWNS <paramref name="httpClient"/>
    /// and disposes it in <see cref="Dispose"/>.
    /// </summary>
    public OllamaChatClient(
        HttpClient httpClient,
        string? defaultModelId = null,
        string providerName = "ollama")
        : this(httpClient, ownsHttpClient: true, defaultModelId, providerName)
    {
    }

    /// <summary>
    /// Factory path: pooled instances do NOT own the <see cref="HttpClient"/> — the
    /// <see cref="OllamaChatClientFactory"/> owns the full handler/client lifecycle so a
    /// MEAI consumer disposing a pooled client mid-fleet never tears down shared connection pools.
    /// </summary>
    internal OllamaChatClient(
        HttpClient httpClient,
        bool ownsHttpClient,
        string? defaultModelId,
        string providerName)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _defaultModelId = defaultModelId;
        _providerName = string.IsNullOrWhiteSpace(providerName) ? "ollama" : providerName;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var dto = BuildRequestDto(messages, options, stream: false);
        using var response = await SendAsync(BuildHttpRequest(dto), cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateHttpExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer
            .DeserializeAsync(bodyStream, s_context.OllamaChatStreamChunkDto, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OllamaHttpException(
                "Ollama endpoint returned an empty chat response.", null, null);

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
        using var response = await SendAsync(BuildHttpRequest(dto), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateHttpExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        // The response stream is await-using-scoped: aborting the enumeration releases the
        // socket immediately (no background tasks, so a cancelled enumeration leaks nothing).
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        ChatFinishReason? finishReason = null;
        UsageDetails? usage = null;

        await foreach (var line in ReadNdjsonLinesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var chunk = DeserializeChunk(line);
            if (chunk is null)
            {
                continue;
            }

            if (chunk.Done)
            {
                finishReason = MapDoneReason(chunk.DoneReason);
                usage = MapUsage(chunk);
                continue;
            }

            var update = MapStreamChunk(chunk);
            if (update is not null)
            {
                yield return update;
            }
        }

        // Terminal finish-reason update (null reason = Stop, the encoder's default).
        if (finishReason is not null)
        {
            yield return new ChatResponseUpdate() { FinishReason = finishReason };
        }

        // Terminal usage update (canonical MEAI route): ToChatResponseAsync aggregates the
        // UsageContent into ChatResponse.Usage.
        if (usage is not null)
        {
            yield return new ChatResponseUpdate(null, [new UsageContent(usage)]);
        }
    }

    /// <summary>
    /// AOT-safe service lookup (mirrors <see cref="OpenAiHttpChatClient.GetService"/>):
    /// <c>ChatClientMetadata</c> (the <paramref name="serviceKey"/> carries the target name)
    /// and the client itself for <c>IChatClient</c>; anything else is <c>null</c>.
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
    /// <see cref="OllamaChatClientFactory"/> (DropClient / factory disposal).
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
    //  Request construction (canonical -> wire)
    // ---------------------------------------------------------------------

    private OllamaChatRequestDto BuildRequestDto(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        // Model id resolution is enforced BEFORE any network I/O.
        var modelId = options?.ModelId ?? _defaultModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException(
                "No model id available: set ChatOptions.ModelId or configure a default model id on the OllamaChatClient.");
        }

        var dto = new OllamaChatRequestDto
        {
            Model = modelId,
            Stream = stream,
            Messages = [],
        };

        foreach (var message in messages)
        {
            dto.Messages.Add(MapRequestMessage(message));
        }

        dto.Options = BuildOptions(options);

        if (options is not null && options.Tools is { Count: > 0 } && options.ToolMode is not NoneChatToolMode)
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

        return dto;
    }

    /// <summary>
    /// Canonical options → the Ollama <c>options{}</c> dictionary: the canonical scalars
    /// (temperature / top_p / top_k / num_predict / seed / stop) plus a pass-through of every
    /// <see cref="JsonElement"/>-typed <see cref="ChatOptions.AdditionalProperties"/> entry
    /// (num_ctx, repeat_penalty, keep_alive, ... — the parsers store the raw wire values there).
    /// Non-<see cref="JsonElement"/> additional values are skipped (they cannot be re-rendered
    /// without reflection). Returns null when nothing is set (the field is then omitted).
    /// </summary>
    private static Dictionary<string, JsonElement>? BuildOptions(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        var result = new Dictionary<string, JsonElement>();

        void AddElement(string name, JsonElement value) => result[name] = value;

        if (options.Temperature is { } temperature)
        {
            AddElement("temperature", JsonSerializer.SerializeToElement((double)temperature, s_wire.Double));
        }

        if (options.TopP is { } topP)
        {
            AddElement("top_p", JsonSerializer.SerializeToElement((double)topP, s_wire.Double));
        }

        if (options.TopK is { } topK)
        {
            AddElement("top_k", JsonSerializer.SerializeToElement(topK, s_wire.Int32));
        }

        if (options.MaxOutputTokens is { } maxOutputTokens)
        {
            AddElement("num_predict", JsonSerializer.SerializeToElement(maxOutputTokens, s_wire.Int32));
        }

        if (options.Seed is { } seed)
        {
            AddElement("seed", JsonSerializer.SerializeToElement(seed, s_wire.Int64));
        }

        if (options.StopSequences is { Count: 1 })
        {
            AddElement("stop", JsonSerializer.SerializeToElement(options.StopSequences[0] ?? string.Empty, s_wire.String));
        }
        else if (options.StopSequences is { Count: > 1 })
        {
            var sequences = new List<string>(options.StopSequences.Count);
            foreach (var sequence in options.StopSequences)
            {
                sequences.Add(sequence ?? string.Empty);
            }

            AddElement("stop", JsonSerializer.SerializeToElement(sequences, s_wire.ListString));
        }

        if (options.AdditionalProperties is { } additional)
        {
            foreach (var pair in additional)
            {
                if (pair.Value is JsonElement element && !result.ContainsKey(pair.Key))
                {
                    result[pair.Key] = element;
                }
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static OllamaChatMessageDto MapRequestMessage(ChatMessage message)
    {
        var dto = new OllamaChatMessageDto { Role = MapRole(message.Role) };

        // Tool result message: Ollama conveys the result as a role:"tool" message with
        // tool_name + string content (the OpenAI wire uses role:"tool" + tool_call_id; the
        // inbound OllamaChatRequestParser maps that back to FunctionResultContent).
        if (message.Role == ChatRole.Tool)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent result)
                {
                    // v1 limitation: MEAI 10.x FunctionResultContent carries no function NAME
                    // (only CallId + result), so the Ollama tool message's name field is omitted.
                    dto.ToolName = null;
                    dto.Content = FunctionResultToString(result.Result);
                    break;
                }

                if (content is TextContent text)
                {
                    dto.Content = text.Text ?? string.Empty;
                    break;
                }
            }

            return dto;
        }

        var textBuilder = new StringBuilder();
        var toolCalls = new List<OllamaToolCallDto>();

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    if (textBuilder.Length > 0)
                    {
                        textBuilder.Append('\n');
                    }

                    textBuilder.Append(text.Text ?? string.Empty);
                    break;

                case DataContent { MediaType: string mediaType } data
                    when mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                    // In-memory image → base64 payload (Ollama conveys no MIME). UriContent /
                    // other MIME / hosted / error content is intentionally dropped in v1.
                    dto.Images ??= [];
                    dto.Images.Add(Convert.ToBase64String(data.Data.Span));
                    break;

                case FunctionCallContent call:
                    toolCalls.Add(new OllamaToolCallDto
                    {
                        Function = new OllamaToolCallFunctionDto
                        {
                            Name = call.Name ?? string.Empty,
                            // Ollama schema: arguments as a JSON OBJECT (not a string).
                            Arguments = JsonElementFromObject(call.Arguments),
                        },
                    });
                    break;

                default:
                    // Reasoning / thinking, audio, hosted, error content: not mapped in v1.
                    break;
            }
        }

        if (textBuilder.Length > 0)
        {
            dto.Content = textBuilder.ToString();
        }

        if (toolCalls.Count > 0)
        {
            dto.ToolCalls = toolCalls;
        }

        return dto;
    }

    private static JsonElement JsonElementFromObject(IDictionary<string, object?>? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            if (value is null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                OpenAiHttpChatClient.WriteJson(writer, value);
            }
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string MapRole(ChatRole role)
    {
        var value = role.Value?.ToLowerInvariant() ?? string.Empty;
        return value switch
        {
            "system" or "user" or "assistant" or "tool" => value,
            // Defensive fallback for unknown/custom roles: never throw (the daemon rejects
            // roles it cannot understand; the error then surfaces as a normal 400).
            _ => "user",
        };
    }

    private static string FunctionResultToString(object? result)
        => result switch
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
            OpenAiHttpChatClient.WriteJson(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private HttpRequestMessage BuildHttpRequest(OllamaChatRequestDto dto)
    {
        // The configured BaseAddress may already carry a path prefix; relative resolution
        // handles both root-mounted and prefixed bases (mirrors OpenAiHttpChatClient).
        var baseAddress = _httpClient.BaseAddress
            ?? throw new InvalidOperationException(
                "The underlying HttpClient must have its BaseAddress set to the Ollama daemon endpoint.");

        var baseUri = baseAddress.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseAddress
            : new Uri(baseAddress.AbsoluteUri + "/");

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/chat"))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(dto, s_wire.OllamaChatRequestDto)),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

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
            // Cancellation is never wrapped: it surfaces to the caller unmodified (the dispatch
            // loop classifies it as a client abort — no retry, no streak).
            throw;
        }
        catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Transport-level failure (connect / DNS / read): no status code — the dispatch
            // loop treats StatusCode == null as an uncommitted (failover-eligible) failure.
            throw new OllamaHttpException(
                "Failed to reach the Ollama endpoint: " + ex.Message,
                statusCode: null,
                innerException: ex);
        }
    }

    // ---------------------------------------------------------------------
    //  Error model
    // ---------------------------------------------------------------------

    private static async Task<OllamaHttpException> CreateHttpExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Read the error body bounded to 16 KiB (best effort — a body read failure degrades
        // to a snippet-less message rather than masking the HTTP error).
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
        try
        {
            if (rawBody.Length > 0)
            {
                using var document = JsonDocument.Parse(rawBody);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.String)
                {
                    payloadMessage = error.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Unparseable error body: fall back to the raw snippet below.
        }

        var detail = !string.IsNullOrWhiteSpace(payloadMessage)
            ? payloadMessage!
            : (rawBody.Length > ErrorBodySnippetLength ? rawBody[..ErrorBodySnippetLength] : rawBody);

        return new OllamaHttpException(
            $"Ollama endpoint error (HTTP {(int)response.StatusCode}): {detail}",
            response.StatusCode,
            null);
    }

    // ---------------------------------------------------------------------
    //  Response mapping (wire -> canonical)
    // ---------------------------------------------------------------------

    private static OllamaChatStreamChunkDto? DeserializeChunk(byte[] line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, s_context.OllamaChatStreamChunkDto);
        }
        catch (JsonException)
        {
            // Defensive: a malformed NDJSON frame in the middle of the stream degrades to a
            // skipped frame (the done frame still terminates the stream) rather than killing
            // the whole request.
            return null;
        }
    }

    private static ChatResponseUpdate? MapStreamChunk(OllamaChatStreamChunkDto chunk)
    {
        if (chunk.Message is not { } message)
        {
            return null;
        }

        var contents = MapMessageContents(message);
        return contents.Count == 0
            ? null
            : new ChatResponseUpdate(new ChatRole(message.Role ?? "assistant"), contents);
    }

    private static ChatResponse MapNonStreamingResponse(OllamaChatStreamChunkDto payload)
    {
        var chatResponse = new ChatResponse
        {
            ModelId = payload.Model,
            Usage = MapUsage(payload),
            RawRepresentation = payload,
        };

        if (payload.Done)
        {
            chatResponse.FinishReason = MapDoneReason(payload.DoneReason);
        }

        if (payload.Message is { } message)
        {
            var contents = MapMessageContents(message);
            chatResponse.Messages.Add(new ChatMessage(ChatRole.Assistant, contents));
        }

        return chatResponse;
    }

    private static List<AIContent> MapMessageContents(OllamaChatMessageDto message)
    {
        var contents = new List<AIContent>();

        if (!string.IsNullOrEmpty(message.Content))
        {
            contents.Add(new TextContent(message.Content));
        }

        if (message.Images is not null)
        {
            foreach (var image in message.Images)
            {
                contents.Add(new DataContent(Convert.FromBase64String(image), "image/png"));
            }
        }

        if (message.ToolCalls is not null)
        {
            foreach (var call in message.ToolCalls)
            {
                if (call.Function is null)
                {
                    continue;
                }

                // Ollama conveys no server-side call id: synthesize one (the inbound parser does the same).
                contents.Add(new FunctionCallContent(
                    Guid.NewGuid().ToString("N"),
                    call.Function.Name ?? string.Empty,
                    call.Function.Arguments is { ValueKind: JsonValueKind.Object } arguments
                        ? OllamaHttpChatClientJsonHelpers.JsonToDictionary(arguments)
                        : new Dictionary<string, object?>()));
            }
        }

        return contents;
    }

    private static UsageDetails? MapUsage(OllamaChatStreamChunkDto chunk)
    {
        if (chunk.PromptEvalCount is null && chunk.EvalCount is null && chunk.PromptEvalCachedCount is null)
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = chunk.PromptEvalCount,
            OutputTokenCount = chunk.EvalCount,
            CachedInputTokenCount = chunk.PromptEvalCachedCount,
            TotalTokenCount = chunk.PromptEvalCount.HasValue || chunk.EvalCount.HasValue
                ? (chunk.PromptEvalCount ?? 0) + (chunk.EvalCount ?? 0)
                : null,
        };
    }

    private static ChatFinishReason? MapDoneReason(string? reason) => reason switch
    {
        "stop" => ChatFinishReason.Stop,
        "length" => ChatFinishReason.Length,
        // null / unknown → null (the encoder layer treats null as Stop).
        _ => null,
    };

    // ---------------------------------------------------------------------
    //  NDJSON reader (pooled buffer, no string materialization)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Yields raw NDJSON lines from the response stream: a pooled 4 KiB read buffer plus a
    /// growable line accumulator. Splits on <c>\n</c> (tolerating <c>\r</c>). On stream EOF a
    /// pending partial line is still yielded (defensive: some servers omit the final newline).
    /// </summary>
    private static async IAsyncEnumerable<byte[]> ReadNdjsonLinesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(NdjsonReadBufferSize);
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
}

/// <summary>
/// Internal JSON value conversion shared by the Ollama client (tool-call arguments, the
/// <c>Dictionary&lt;string, object?&gt;</c> rule set of
/// <see cref="Converters.ProtocolConverterJsonHelpers"/> — no reflection, AOT-safe).
/// </summary>
internal static class OllamaHttpChatClientJsonHelpers
{
    /// <summary>
    /// Recursively converts a JSON object element into a plain value dictionary (string /
    /// long / double / bool / null / nested dictionary / list) without retaining the document.
    /// </summary>
    public static IDictionary<string, object?> JsonToDictionary(JsonElement element)
    {
        var dictionary = new Dictionary<string, object?>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            return dictionary;
        }

        foreach (var property in element.EnumerateObject())
        {
            dictionary[property.Name] = JsonToValue(property.Value);
        }

        return dictionary;
    }

    private static object? JsonToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out long integral) ? (object)integral : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => JsonToDictionary(element),
        JsonValueKind.Array => JsonToArray(element),
        _ => null,
    };

    private static List<object?> JsonToArray(JsonElement array)
    {
        var list = new List<object?>();
        foreach (var item in array.EnumerateArray())
        {
            list.Add(JsonToValue(item));
        }

        return list;
    }
}
