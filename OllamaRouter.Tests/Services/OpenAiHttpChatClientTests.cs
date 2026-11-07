using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OllamaRouter.Services.Clients;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// Mock-HTTP test suite for the Native AOT OpenAI-compatible <see cref="IChatClient"/>
/// (task #60). The <see cref="FakeOpenAiHandler"/> captures every request (URL, method, headers,
/// raw body) and replays canned responses: JSON strings for the non-streaming / error paths and
/// controllable <see cref="Stream"/>s for the SSE streaming path (scripted chunks, optional
/// hang-until-cancelled).
/// </summary>
public class OpenAiHttpChatClientTests
{
    private static readonly Uri BaseAddress = new("http://fake-host:8000/v1");

    private static readonly Uri RootAddress = new("http://fake-host:8000");

    // -----------------------------------------------------------------
    // Test harness
    // -----------------------------------------------------------------

    /// <summary>
    /// Deterministic fake OpenAI-compatible endpoint: records every request and replays a canned
    /// response (JSON string, SSE stream, or a transport-level exception).
    /// </summary>
    private sealed class FakeOpenAiHandler : HttpMessageHandler
    {
        public sealed class CapturedRequest
        {
            public required HttpRequestMessage Request { get; init; }
            public required Uri Uri { get; init; }
            public required string Body { get; init; }
            public required AuthenticationHeaderValue? Authorization { get; init; }
            public required string Accept { get; init; }
            }

        public List<CapturedRequest> Requests { get; } = [];

        private readonly JsonPayload? _jsonPayload;
        private readonly Stream? _streamPayload;
        private readonly Exception? _transportFailure;

        public FakeOpenAiHandler(string jsonBody, HttpStatusCode status = HttpStatusCode.OK)
        {
            _jsonPayload = new JsonPayload(jsonBody, status);
        }

        public FakeOpenAiHandler(Stream stream)
        {
            _streamPayload = stream;
        }

        public FakeOpenAiHandler(Exception transportFailure)
        {
            _transportFailure = transportFailure;
        }

        private sealed record JsonPayload(string Body, HttpStatusCode Status);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest
            {
                Request = request,
                Uri = request.RequestUri ?? throw new InvalidOperationException("No request URI captured."),
                Body = body,
                Authorization = request.Headers.Authorization,
                Accept = string.Join(",", request.Headers.Accept.Select(a => a.ToString())),
                });

                if (_transportFailure is not null)
            {
                throw _transportFailure;
            }

            if (_jsonPayload is not null)
            {
                return new HttpResponseMessage(_jsonPayload.Status)
                {
                    Content = new StringContent(_jsonPayload.Body, Encoding.UTF8, "application/json"),
                };
            }

            if (_streamPayload is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(_streamPayload),
                };
            }

            throw new InvalidOperationException("No canned response configured.");
        }
    }

    /// <summary>
    /// SSE stream that replays scripted byte chunks and then HANGS until the request
    /// cancellation token fires (simulates a live socket with no more data — a real hung stream
    /// that only ends when the consumer abandons it).
    /// </summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly Queue<byte[]> _chunks;

        public ScriptedStream(IEnumerable<byte[]> chunks)
        {
            _chunks = new Queue<byte[]>(chunks);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_chunks.Count == 0)
            {
                // Hang until the caller cancels (Task.Delay throws TaskCanceledException, an
                // OperationCanceledException, when the token fires).
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0; // unreachable
            }

            var chunk = _chunks.Dequeue();
            var count = Math.Min(chunk.Length, buffer.Length);
            chunk.AsSpan(0, count).CopyTo(buffer.Span);
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrackingMessageHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
    }

    /// <summary>Test-only AITool exposing an <see cref="AIFunction"/> (the documented GetService pattern).</summary>
    private sealed class FunctionTool : AITool
    {
        private readonly AIFunction _function;

        public FunctionTool(AIFunction function)
        {
            _function = function;
        }

        public override string Name => "lookup";

        // The base AITool members (MEAI Abstractions 10.9.0) are nullable-OBLIVIOUS (the assembly
        // is compiled without nullable context); annotated overrides of oblivious members trip
        // CS8764 by design, so the (intentional) annotations are kept and the mismatch diagnostic
        // is suppressed for this class.
        #pragma warning disable CS8764 // Nullability mismatch: base members are nullable-oblivious.
        public override string? Description => "Looks things up.";

        public override object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(AIFunction) ? _function : base.GetService(serviceType, serviceKey);
        #pragma warning restore CS8764
        }

    private static OpenAiHttpChatClient CreateClient(
        FakeOpenAiHandler handler,
        string? apiKey = "test-key",
        string? defaultModel = "gpt-test",
        Uri? baseAddress = null)
        => new(new HttpClient(handler) { BaseAddress = baseAddress ?? BaseAddress }, defaultModel, apiKey);

    private static string SseEvent(string json) => $"data: {json}\n\n";

    private static Stream SseStream(string sseText) => new MemoryStream(Encoding.UTF8.GetBytes(sseText));

    // -----------------------------------------------------------------
    // 1-3. Request construction & auth
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetResponseAsync_SendsCanonicalWireRequest()
    {
        var handler = new FakeOpenAiHandler("""{"id":"r1","created":1700000000,"model":"gpt-test","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        var client = CreateClient(handler, apiKey: "test-key");

        var options = new ChatOptions
        {
            ModelId = "gpt-test",
            Temperature = 0.7f,
            TopP = 0.9f,
            TopK = 20,
            MaxOutputTokens = 512,
            Seed = 42,
            FrequencyPenalty = 0.1f,
            PresencePenalty = 0.2f,
            StopSequences = ["\n", "STOP"],
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "sys"), new ChatMessage(ChatRole.User, "hi")],
            options);

        var captured = Assert.Single(handler.Requests);
        // URL = base + chat/completions; the /v1 path-prefixed base address is honored.
        Assert.Equal("http://fake-host:8000/v1/chat/completions", captured.Uri.ToString());
        Assert.Equal(HttpMethod.Post, captured.Request.Method);
        Assert.Equal("test-key", captured.Authorization?.Parameter);
        Assert.Equal("Bearer", captured.Authorization?.Scheme);

        var body = Assert.IsType<JsonObject>(JsonNode.Parse(captured.Body));
        Assert.Equal("gpt-test", body["model"]!.GetValue<string>());
        Assert.Null(body["stream"]); // omitted for non-streaming
        Assert.Equal(0.7f, body["temperature"]!.GetValue<float>());
        Assert.Equal(0.9f, body["top_p"]!.GetValue<float>());
        Assert.Equal(20, body["top_k"]!.GetValue<int>());
        Assert.Equal(512, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(42, body["seed"]!.GetValue<long>());
        Assert.Equal(0.1f, body["frequency_penalty"]!.GetValue<float>());
        Assert.Equal(0.2f, body["presence_penalty"]!.GetValue<float>());
        var stop = Assert.IsType<JsonArray>(body["stop"]);
        Assert.Equal(["\n", "STOP"], stop.Select(n => n!.GetValue<string>()));

        var messages = Assert.IsType<JsonArray>(body["messages"]);
        var first = Assert.IsType<JsonObject>(messages[0]);
        Assert.Equal("system", first["role"]!.GetValue<string>());
        Assert.Equal("sys", first["content"]!.GetValue<string>()); // single text → plain JSON string
        var second = Assert.IsType<JsonObject>(messages[1]);
        Assert.Equal("user", second["role"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetResponseAsync_WithoutApiKey_SendsNoAuthorizationHeader(string? apiKey)
    {
        var handler = new FakeOpenAiHandler("""{"id":"r1","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        var client = CreateClient(handler, apiKey: apiKey);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var captured = Assert.Single(handler.Requests);
        Assert.Null(captured.Authorization);
    }

    [Fact]
    public async Task GetResponseAsync_MissingModelId_ThrowsInvalidOperationException_NoNetworkCall()
    {
        var handler = new FakeOpenAiHandler("{}");
        var client = CreateClient(handler, defaultModel: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Empty(handler.Requests); // the pre-flight throw happens BEFORE any network I/O.
    }

    // -----------------------------------------------------------------
    // 4-6. Non-streaming parsing
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetResponseAsync_ParsesTextCompletion_UsageAndMetadata()
    {
        var handler = new FakeOpenAiHandler("""
            {
                "id": "chatcmpl-1",
                "created": 1700000000,
                "model": "vllm-llama",
                "choices": [{ "index": 0, "message": { "role": "assistant", "content": "Hello there" }, "finish_reason": "stop" }],
                "usage": { "prompt_tokens": 12, "completion_tokens": 34, "total_tokens": 46, "completion_tokens_details": { "reasoning_tokens": 20 } }
            }
            """);
        var client = CreateClient(handler, apiKey: null);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("chatcmpl-1", response.ResponseId);
        Assert.Equal("vllm-llama", response.ModelId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), response.CreatedAt);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        var message = Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal("Hello there", Assert.Single(message.Contents.OfType<TextContent>()).Text);
        Assert.NotNull(response.Usage);
        Assert.Equal(12, response.Usage!.InputTokenCount);
        Assert.Equal(34, response.Usage.OutputTokenCount);
        Assert.Equal(46, response.Usage.TotalTokenCount);
        Assert.Equal(20, response.Usage.ReasoningTokenCount);
    }

    [Fact]
    public async Task GetResponseAsync_ParsesToolCalls_Into_FunctionCallContent()
    {
        var handler = new FakeOpenAiHandler("""
            {
                "id": "tc1",
                "model": "m",
                "choices": [{
                    "index": 0,
                    "message": {
                        "role": "assistant",
                        "content": null,
                        "tool_calls": [{ "id": "call_9", "type": "function", "function": { "name": "lookup", "arguments": "{\"city\":\"Paris\"}" } }]
                    },
                    "finish_reason": "tool_calls"
                }]
            }
            """);
        var client = CreateClient(handler, apiKey: null);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "weather?")]);

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        var call = Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_9", call.CallId);
        Assert.Equal("lookup", call.Name);
        Assert.Equal("Paris", call.Arguments!["city"]);
    }

    [Fact]
    public async Task GetResponseAsync_EmptyChoices_ReturnsResponseWithoutMessage()
    {
        var handler = new FakeOpenAiHandler("""{"id":"empty-1","model":"m","choices":[]}""");
        var client = CreateClient(handler, apiKey: null);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        // Provider-specific empty completions must not throw.
        Assert.Equal("empty-1", response.ResponseId);
        Assert.Empty(response.Messages);
    }

    // -----------------------------------------------------------------
    // 7. Error translation
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized,
        """{"error":{"message":"Invalid API key","type":"invalid_request_error","param":"api_key","code":"invalid_key"}}""",
        "OpenAI-compatible endpoint authentication failed (HTTP 401): Invalid API key",
        "invalid_request_error", "invalid_key")]
    [InlineData(HttpStatusCode.Forbidden,
        """{"error":{"message":"No access","type":"permission_error","param":null,"code":null}}""",
        "OpenAI-compatible endpoint authentication failed (HTTP 403): No access",
        "permission_error", null)]
    [InlineData(HttpStatusCode.TooManyRequests,
        """{"error":{"message":"Slow down","type":"rate_limit_error","param":null,"code":"rate_limited"}}""",
        "OpenAI-compatible endpoint rate limited (HTTP 429): Slow down",
        "rate_limit_error", "rate_limited")]
    [InlineData(HttpStatusCode.ServiceUnavailable,
        """{"error":{"message":"Overloaded","type":"server_error","param":null,"code":"overloaded"}}""",
        "OpenAI-compatible endpoint unavailable/overloaded (HTTP 503): Overloaded",
        "server_error", "overloaded")]
    [InlineData(HttpStatusCode.InternalServerError,
        """{"error":{"message":"boom","type":"server_error","param":null,"code":null}}""",
        "OpenAI-compatible endpoint error (HTTP 500, type=server_error): boom",
        "server_error", null)]
    public async Task ErrorResponse_TranslatedToOpenAiHttpException(HttpStatusCode status, string body, string expectedMessage, string? expectedType, string? expectedCode)
    {
        var handler = new FakeOpenAiHandler(body, status);
        var client = CreateClient(handler, apiKey: null);

        var ex = await Assert.ThrowsAsync<OpenAiHttpException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(expectedType, ex.ErrorType);
        Assert.Equal(expectedCode, ex.ErrorCode);
        Assert.Equal(expectedMessage, ex.Message);
    }

    [Fact]
    public async Task ErrorResponse_NonJsonBody_UsesRawSnippet()
    {
        var rawBody = new string('x', 250);
        var handler = new FakeOpenAiHandler(rawBody, HttpStatusCode.InternalServerError);
        var client = CreateClient(handler, apiKey: null);

        var ex = await Assert.ThrowsAsync<OpenAiHttpException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Null(ex.ErrorType);
        Assert.EndsWith(new string('x', 200), ex.Message); // bounded 200-char raw snippet
        Assert.StartsWith("OpenAI-compatible endpoint error (HTTP 500, type=unknown): ", ex.Message);
    }

    [Fact]
    public async Task TransportFailure_WrappedAsOpenAiHttpException_WithoutStatusCode()
    {
        var handler = new FakeOpenAiHandler(new HttpRequestException("DNS failure"));
        var client = CreateClient(handler, apiKey: null);

        var ex = await Assert.ThrowsAsync<OpenAiHttpException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Null(ex.StatusCode); // null ⇒ transport-level failure
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.StartsWith("Failed to reach the OpenAI-compatible endpoint: ", ex.Message);
    }

    // -----------------------------------------------------------------
    // 8-15. Streaming
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetStreamingResponseAsync_YieldsTextDeltas_InOrder_WithFinishReason()
    {
        var sse =
            SseEvent("""{"id":"cmpl-1","created":1700000001,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}]}""") +
            SseEvent("""{"id":"cmpl-1","model":"m","choices":[{"index":0,"delta":{"content":" world"},"finish_reason":null}]}""") +
            SseEvent("""{"id":"cmpl-1","model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""") +
            "data: [DONE]\n\n";
        var handler = new FakeOpenAiHandler(SseStream(sse));
        var client = CreateClient(handler, apiKey: null);

        var updates = (await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync()).ToList();

        var texts = updates.Where(u => u.Contents.OfType<TextContent>().Any()).Select(u => u.Contents.OfType<TextContent>().Single().Text).ToList();
        Assert.Equal(["Hello", " world"], texts);
        var terminal = updates[^1];
        Assert.Equal(ChatFinishReason.Stop, terminal.FinishReason);
        Assert.Equal("cmpl-1", terminal.ResponseId);
        Assert.Equal("m", terminal.ModelId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000001), updates[0].CreatedAt);
    }

    [Fact]
    public async Task Stream_DataDone_TerminatesEnumeration_And_BytesAfterDoneIgnored()
    {
        var sse =
            SseEvent("""{"id":"d1","choices":[{"index":0,"delta":{"content":"kept"},"finish_reason":null}]}""") +
            "data: [DONE]\n\n" +
            SseEvent("""{"id":"d2","choices":[{"index":0,"delta":{"content":"SHOULD_NOT_APPEAR"},"finish_reason":"stop"}]}""");
        var handler = new FakeOpenAiHandler(SseStream(sse));
        var client = CreateClient(handler, apiKey: null);

        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        var allText = string.Concat(updates.SelectMany(u => u.Contents.OfType<TextContent>().Select(t => t.Text)));
        Assert.Equal("kept", allText);
        Assert.DoesNotContain("SHOULD_NOT_APPEAR", allText);
    }

    [Fact]
    public async Task Stream_CrlfLineEndings_And_CommentEventIdLinesIgnored()
    {
        var sse = string.Join("",
            ": keepalive\r\n",
            "event: message\r\n",
            "id: 42\r\n",
            $"data: {"""{"id":"c1","choices":[{"index":0,"delta":{"content":"a"},"finish_reason":null}]}"""}\r\n",
            "\r\n",
            $"data: {"""{"id":"c1","choices":[{"index":0,"delta":{"content":"b"},"finish_reason":null}]}"""}\r\n",
            "\r\n");
        var handler = new FakeOpenAiHandler(SseStream(sse));
        var client = CreateClient(handler, apiKey: null);

        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        var texts = updates.SelectMany(u => u.Contents.OfType<TextContent>().Select(t => t.Text)).ToList();
        Assert.Equal(["a", "b"], texts);
    }

    [Fact]
    public async Task Stream_PartialLineAtEof_StillDispatched()
    {
        // Some servers omit the final blank line / newline: the pending data must still parse.
        var sse = "data: " + """{"id":"e1","choices":[{"index":0,"delta":{"content":"tail"},"finish_reason":"stop"}]}""";
        var handler = new FakeOpenAiHandler(SseStream(sse));
        var client = CreateClient(handler, apiKey: null);

        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        var texts = updates.SelectMany(u => u.Contents.OfType<TextContent>().Select(t => t.Text)).ToList();
        Assert.Equal(["tail"], texts);
        Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);
    }

    [Fact]
    public async Task Stream_UsageChunk_ProducesUsageContent_ThatAggregatesIntoChatResponseUsage()
    {
        var sse =
            SseEvent("""{"id":"u1","model":"m","choices":[{"index":0,"delta":{"content":"Hi"},"finish_reason":null}]}""") +
            SseEvent("""{"id":"u1","model":"m","choices":[],"usage":{"prompt_tokens":7,"completion_tokens":11,"total_tokens":18,"completion_tokens_details":{"reasoning_tokens":3}}}""") +
            "data: [DONE]\n\n";
        var handler = new FakeOpenAiHandler(SseStream(sse));
        var client = CreateClient(handler, apiKey: null);

        var updates = (await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync()).ToList();

        // The terminal usage frame carries EXACTLY ONE UsageContent (R3 transport).
        var usageUpdate = updates.Single(u => u.Contents.OfType<UsageContent>().Any());
        Assert.Single(usageUpdate.Contents);
        var details = Assert.IsType<UsageContent>(usageUpdate.Contents[0]).Details;
        Assert.Equal(7, details.InputTokenCount);
        Assert.Equal(11, details.OutputTokenCount);
        Assert.Equal(18, details.TotalTokenCount);
        Assert.Equal(3, details.ReasoningTokenCount);

        // And the REAL MEAI aggregator folds it into ChatResponse.Usage (validates the transport).
        var aggregated = await OpenAiHttpChatClientTestAsyncHelpers.ToAsyncEnumerable(updates).ToChatResponseAsync();
        Assert.NotNull(aggregated.Usage);
        Assert.Equal(7, aggregated.Usage!.InputTokenCount);
        Assert.Equal(11, aggregated.Usage.OutputTokenCount);
        Assert.Equal(18, aggregated.Usage.TotalTokenCount);
        Assert.Equal(3, aggregated.Usage.ReasoningTokenCount);
        }

        [Fact]
        public async Task Stream_UsageMergedIntoFinalContentChunk_RidesOnTerminalUpdate_WithoutDuplicateFrame()
        {
        // Servers that ignore stream_options.merge the usage into the final content chunk:
        // the UsageContent must ride on THAT update (no extra terminal frame) and the MEAI
        // aggregator must still fold it into ChatResponse.Usage exactly once.
        var sse =
            SseEvent("""{"id":"m1","model":"m","choices":[{"index":0,"delta":{"content":"Hi"},"finish_reason":null}]}""") +
            SseEvent("""{"id":"m1","model":"m","choices":[{"index":0,"delta":{"content":"!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":9,"total_tokens":14,"completion_tokens_details":{"reasoning_tokens":2}}}""") +
            "data: [DONE]\n\n";
        var handler = new FakeOpenAiHandler(SseStream(sse));
        var client = CreateClient(handler, apiKey: null);

        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        // Exactly one update carries a UsageContent — the terminal content/finish update itself.
        var usageUpdate = Assert.Single(updates.Where(u => u.Contents.OfType<UsageContent>().Any()));
        Assert.Equal(ChatFinishReason.Stop, usageUpdate.FinishReason);
        Assert.Contains(usageUpdate.Contents, c => c is TextContent);
        var details = Assert.IsType<UsageContent>(Assert.Single(usageUpdate.Contents, c => c is UsageContent)).Details;
        Assert.Equal(5, details.InputTokenCount);
        Assert.Equal(9, details.OutputTokenCount);
        Assert.Equal(14, details.TotalTokenCount);
        Assert.Equal(2, details.ReasoningTokenCount);

        // The REAL MEAI aggregator folds it into ChatResponse.Usage without doubling.
        var aggregated = await OpenAiHttpChatClientTestAsyncHelpers.ToAsyncEnumerable(updates).ToChatResponseAsync();
        Assert.NotNull(aggregated.Usage);
        Assert.Equal(5, aggregated.Usage!.InputTokenCount);
        Assert.Equal(9, aggregated.Usage.OutputTokenCount);
        Assert.Equal(14, aggregated.Usage.TotalTokenCount);
        Assert.Equal(2, aggregated.Usage.ReasoningTokenCount);
        }

    [Fact]
    public async Task Stream_RequestBody_ContainsStreamTrueAndStreamOptionsIncludeUsage_AndBearerHeader()
    {
        var handler = new FakeOpenAiHandler(SseStream("data: [DONE]\n\n"));
        var client = CreateClient(handler, apiKey: "stream-key");

        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        var captured = Assert.Single(handler.Requests);
        Assert.Equal("stream-key", captured.Authorization?.Parameter);
        Assert.Contains("text/event-stream", captured.Accept);
        var body = Assert.IsType<JsonObject>(JsonNode.Parse(captured.Body));
        Assert.True(body["stream"]!.GetValue<bool>());
        var streamOptions = Assert.IsType<JsonObject>(body["stream_options"]);
        Assert.True(streamOptions["include_usage"]!.GetValue<bool>());
        Assert.Equal("gpt-test", body["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stream_ToolCallArgumentFragments_AccumulateIntoSingleFunctionCallContent()
    {
        var sse =
            SseEvent("""{"id":"t1","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""") +
            SseEvent("""{"id":"t1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"ci"}}]},"finish_reason":null}]}""") +
            SseEvent("""{"id":"t1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ty\":\"Pa"}}]},"finish_reason":null}]}""") +
            SseEvent("""{"id":"t1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ris\"}"}}]},"finish_reason":"tool_calls"}]}""") +
            "data: [DONE]\n\n";
        var handler = new FakeOpenAiHandler(SseStream(sse));
        var client = CreateClient(handler, apiKey: null);

        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "weather")]).ToListAsync();

        var terminal = updates[^1];
        Assert.Equal(ChatFinishReason.ToolCalls, terminal.FinishReason);
        // ONE FunctionCallContent with the FULL accumulated + parsed argument dictionary.
        var call = Assert.Single(terminal.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Paris", call.Arguments!["city"]);
    }

    [Fact]
    public async Task Stream_CancellationMidStream_AbortsCleanly()
    {
        var cts = new CancellationTokenSource();
        var sse = SseEvent("""{"id":"x1","choices":[{"index":0,"delta":{"content":"first"},"finish_reason":null}]}""");
        var handler = new FakeOpenAiHandler(new ScriptedStream([Encoding.UTF8.GetBytes(sse)]));
        var client = CreateClient(handler, apiKey: null);

        var updates = client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            options: null,
            cts.Token);
        var enumerator = updates.GetAsyncEnumerator();
        OperationCanceledException? ocException = null;
        try
        {
            Assert.True(await enumerator.MoveNextAsync());

            // The stream now hangs with no more data: canceling the request token must surface
            // an OperationCanceledException (no unobserved task exceptions, no leaked waits).
            cts.Cancel();
            ocException = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        // The client threads the CONSUMER's token into the response-stream reads: the hang above
        // was unblocked by that exact token (no leaked waits on handler-internal tokens — note
        // HttpClient's own linked token source is disposed once SendAsync completes, so the
        // captured handler token cannot be asserted on afterwards).
        Assert.True(ocException is TaskCanceledException tce && tce.CancellationToken.Equals(cts.Token));
        }

    [Fact]
    public async Task Stream_NonSuccessStatus_ThrowsOpenAiHttpException()
    {
        var handler = new FakeOpenAiHandler(
            """{"error":{"message":"bad key","type":"invalid_request_error","param":"api_key","code":"bad_key"}}""",
            HttpStatusCode.Unauthorized);
        var client = CreateClient(handler, apiKey: null);

        var ex = await Assert.ThrowsAsync<OpenAiHttpException>(
            () => client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal("OpenAI-compatible endpoint authentication failed (HTTP 401): bad key", ex.Message);
    }

    // -----------------------------------------------------------------
    // 16-17. GetService / metadata / lifecycle
    // -----------------------------------------------------------------

    [Fact]
    public void GetService_ReturnsChatClientMetadata_And_Itself_For_IChatClient_And_Null_Unknown()
    {
        var client = new OpenAiHttpChatClient(
            new HttpClient(new TrackingMessageHandler()) { BaseAddress = new Uri("http://prov:1234/v1") },
            defaultModelId: "model-a",
            apiKey: "k",
            providerName: "my-provider");

        var metadata = Assert.IsType<ChatClientMetadata>(client.GetService(typeof(ChatClientMetadata)));
        Assert.Equal("my-provider", metadata.ProviderName);
        Assert.Equal(new Uri("http://prov:1234/v1"), metadata.ProviderUri);
        Assert.Equal("model-a", metadata.DefaultModelId);

        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Null(client.GetService(typeof(string)));
    }

    [Fact]
    public void Dispose_WithOwnedClient_DisposesHttpClient()
    {
        var tracking = new TrackingMessageHandler();
        var client = new OpenAiHttpChatClient(new HttpClient(tracking));

        client.Dispose();

        Assert.True(tracking.Disposed); // public ctor ⇒ client owns the HttpClient.
    }

    [Fact]
    public void Dispose_PooledClient_DoesNotTearDownSharedResources_DropClientDoes()
    {
        var tracking = new TrackingMessageHandler();
        var factory = new OpenAiChatClientFactory(_ => tracking);
        var endpoint = new OpenAiTargetEndpoint("pooled-target", new Uri("http://pooled:1/"));

        var client = factory.GetClient(endpoint);
        // The pooled client must surface the endpoint target name as its provider name.
        var metadata = Assert.IsType<ChatClientMetadata>(client.GetService(typeof(ChatClientMetadata)));
        Assert.Equal("pooled-target", metadata.ProviderName);

        // MEAI consumer disposing a pooled client mid-fleet must NOT tear down the pool...
        client.Dispose();
        Assert.False(tracking.Disposed);
        // ...and the factory keeps serving the SAME instance afterwards.
        Assert.Same(client, factory.GetClient(endpoint));

        // ...while DropClient releases the pooled resources deterministically.
        factory.DropClient(endpoint);
        Assert.True(tracking.Disposed);
        factory.Dispose();
    }

    // -----------------------------------------------------------------
    // 18-19. Factory pooling
    // -----------------------------------------------------------------

    [Fact]
    public void Factory_SameEndpoint_ReturnsSameInstance_DifferentApiKey_ReturnsNewInstance_DropClientRemoves()
    {
        var factory = new OpenAiChatClientFactory();
        var endpoint = new OpenAiTargetEndpoint("t1", new Uri("http://factory-host:1234/v1"));

        var first = factory.GetClient(endpoint);
        Assert.Same(first, factory.GetClient(endpoint)); // pooled: same instance.

        // An API-key change for the same URL yields a fresh client (key is part of the pool key).
        var withKey = endpoint with { ApiKey = "secret" };
        var keyed = factory.GetClient(withKey);
        Assert.NotSame(first, keyed);

        factory.DropClient(endpoint);
        var recreated = factory.GetClient(endpoint);
        Assert.NotSame(first, recreated);

        factory.Dispose();
    }

    [Fact]
    public void Factory_CaseAndTrailingSlashAndDefaultPort_DifferingUris_MapToSamePoolKey()
    {
        var factory = new OpenAiChatClientFactory();
        var upperCase = new OpenAiTargetEndpoint("t", new Uri("http://HOST:1234/v1/"));
        var lowerCase = new OpenAiTargetEndpoint("t", new Uri("http://host:1234/v1"));
        var explicitDefaultPort = new OpenAiTargetEndpoint("t", new Uri("http://host:80/root"));
        var implicitDefaultPort = new OpenAiTargetEndpoint("t", new Uri("http://host/root"));

        Assert.Same(factory.GetClient(upperCase), factory.GetClient(lowerCase));
        Assert.Same(factory.GetClient(explicitDefaultPort), factory.GetClient(implicitDefaultPort));
        Assert.NotSame(factory.GetClient(lowerCase), factory.GetClient(implicitDefaultPort)); // different path.

        factory.Dispose();
    }

    // -----------------------------------------------------------------
    // Bonus: tools / tool_choice / response_format / reasoning_effort mapping
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetResponseAsync_MapsToolsToolChoiceResponseFormatAndReasoningEffort()
    {
        var handler = new FakeOpenAiHandler("""{"id":"r1","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        var client = CreateClient(handler, apiKey: null);

        var function = AIFunctionFactory.Create((string city) => city);
        var options = new ChatOptions
        {
            ModelId = "m",
            Tools = [new FunctionTool(function)],
            ToolMode = ChatToolMode.RequireAny,
            ResponseFormat = ChatResponseFormat.Json,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
        };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        var body = Assert.IsType<JsonObject>(JsonNode.Parse(handler.Requests.Single().Body));
        var tools = Assert.IsType<JsonArray>(body["tools"]);
        var tool = Assert.IsType<JsonObject>(Assert.Single(tools));
        Assert.Equal("function", tool["type"]!.GetValue<string>());
        var toolFunction = Assert.IsType<JsonObject>(tool["function"]);
        Assert.Equal("lookup", toolFunction["name"]!.GetValue<string>());
        Assert.Equal("Looks things up.", toolFunction["description"]!.GetValue<string>());
        var parameters = Assert.IsType<JsonObject>(toolFunction["parameters"]);
        Assert.NotNull(parameters["properties"]!["city"]); // AIFunction.JsonSchema IS the parameters schema.
        Assert.Equal("required", body["tool_choice"]!.GetValue<string>());
        var responseFormat = Assert.IsType<JsonObject>(body["response_format"]);
        Assert.Equal("json_object", responseFormat["type"]!.GetValue<string>());
        Assert.Equal("high", body["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetResponseAsync_ToolModeNone_OmitsToolsEntirely()
    {
        var handler = new FakeOpenAiHandler("""{"id":"r1","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        var client = CreateClient(handler, apiKey: null);

        var options = new ChatOptions
        {
            ModelId = "m",
            Tools = [new FunctionTool(AIFunctionFactory.Create((string city) => city))],
            ToolMode = ChatToolMode.None,
        };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        var body = Assert.IsType<JsonObject>(JsonNode.Parse(handler.Requests.Single().Body));
        Assert.Null(body["tools"]);
        Assert.Null(body["tool_choice"]);
    }

}

public static class OpenAiHttpChatClientTestAsyncHelpers
{
    public static async Task<List<ChatResponseUpdate>> ToListAsync(this IAsyncEnumerable<ChatResponseUpdate> source)
    {
        var list = new List<ChatResponseUpdate>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    public static async IAsyncEnumerable<ChatResponseUpdate> ToAsyncEnumerable(
        this IEnumerable<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }
}
