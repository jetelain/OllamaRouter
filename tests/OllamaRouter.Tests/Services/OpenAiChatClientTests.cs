using System.Net;
using System.ClientModel;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OllamaRouter.Services.Clients;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// Mock-HTTP test suite for the native OpenAI-compatible <see cref="IChatClient"/> (task #112 —
/// the Microsoft.Extensions.AI.OpenAI adapter over the OpenAI SDK 2.13.0, pooled by
/// <see cref="OpenAiChatClientFactory"/>; replacing the deleted hand-rolled
/// <c>OpenAiHttpChatClient</c>). The <see cref="FakeOpenAiHandler"/> captures every request (URL,
/// body, auth header) and replays canned responses: a JSON string for the non-streaming / error
/// paths and a controllable SSE <see cref="Stream"/> for the streaming path (scripted frames,
/// optional hang-until-cancelled). Streaming is detected on the request BODY
/// (<c>"stream":true</c>) — the SDK sends <c>Accept: application/json</c> for both verbs.
/// </summary>
public class OpenAiChatClientTests
{
    private static readonly Uri BaseAddress = new("http://fake-host:8000/v1");

    private static readonly Uri RootAddress = new("http://fake-host:8000");

    private const string TestKey = "test-key";

    private static readonly JsonNode BaseCompletion = JsonNode.Parse("""
        {
          "id": "cmpl-1", "created": 1700000000, "model": "llama3",
          "choices": [ { "index": 0, "message": { "role": "assistant", "content": "Hello from the fake." }, "finish_reason": "stop" } ],
          "usage": { "prompt_tokens": 12, "completion_tokens": 34, "total_tokens": 46,
                     "prompt_tokens_details": { "cached_tokens": 8 },
                     "completion_tokens_details": { "reasoning_tokens": 20 } }
        }
        """)!;

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
            public required Uri Url { get; init; }
            public required string Body { get; init; }
            public required AuthenticationHeaderValue? Authorization { get; init; }
        }

        public List<CapturedRequest> CapturedRequests { get; } = [];

        /// <summary>Body returned for non-streaming requests (default: a plain completion).</summary>
        public string Json { get; set; } = BaseCompletion.ToJsonString();

        /// <summary>Raw SSE returned for streaming requests (default: one text frame).</summary>
        public string Stream { get; set; } = "data: {\"id\":\"s1\",\"created\":1700000000,\"model\":\"llama3\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

        /// <summary>Optional scripted stream overriding <see cref="Stream"/> content (cancellation tests).</summary>
        public Func<Stream>? ScriptedStream { get; set; }

        /// <summary>When set, every request fails at the transport level with this exception.</summary>
        public Exception? TransportException { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            CapturedRequests.Add(new CapturedRequest
            {
                Url = request.RequestUri ?? throw new InvalidOperationException("missing request URI"),
                Body = body,
                Authorization = request.Headers.Authorization,
            });

            if (TransportException is { } transport)
            {
                throw transport;
            }

            if (body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                var stream = ScriptedStream is { } factory
                    ? factory()
                    : new MemoryStream(Encoding.UTF8.GetBytes(Stream));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static IChatClient CreateClient(
        FakeOpenAiHandler handler,
        string? apiKey = TestKey,
        Uri? baseAddress = null)
    {
        // The factory pools per (base address, key); tests use one endpoint per handler, so the
        // pool trivially has a single entry (leaked per test — process-scoped, no cleanup needed).
        return new OpenAiChatClientFactory(_ => handler)
            .GetClient(new OpenAiTargetEndpoint("target", baseAddress ?? BaseAddress, apiKey));
    }

    // -----------------------------------------------------------------
    // Request body mapping (native standard-ChatOptions mapping)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Request_NonStreaming_BodyCarriesMappedStandardOptions()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);

        await client.GetResponseAsync(
            new[]
            {
                new ChatMessage(ChatRole.System, "be brief"),
                new ChatMessage(ChatRole.User, "hi"),
            },
            new ChatOptions
            {
                ModelId = "llama3",
                Temperature = 0.5f,
                TopP = 0.75f,
                Seed = 42,
                FrequencyPenalty = 0.5f,
                PresencePenalty = 0.25f,
            });

        var body = JsonNode.Parse(handler.CapturedRequests.Single().Body)!;
        // Per-request model override: the factory's placeholder client-level model is NEVER sent.
        Assert.Equal("llama3", body["model"]!.GetValue<string>());
        Assert.Equal(2, body["messages"]!.AsArray().Count);
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("be brief", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("user", body["messages"]![1]!["role"]!.GetValue<string>());
        Assert.Equal("hi", body["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal(0.5f, body["temperature"]!.GetValue<float>());
        Assert.Equal(0.75f, body["top_p"]!.GetValue<float>());
        Assert.Equal(42, body["seed"]!.GetValue<int>());
        Assert.Equal(0.5f, body["frequency_penalty"]!.GetValue<float>());
        Assert.Equal(0.25f, body["presence_penalty"]!.GetValue<float>());
        // Non-streaming requests carry no stream flag; options without native mapping are DROPPED.
        Assert.Null(body["stream"]);
        Assert.Null(body["top_k"]);
    }

    [Fact]
    public async Task Request_Body_MaxOutputTokens_MapsToMaxCompletionTokens()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3", MaxOutputTokens = 512 });

        var body = JsonNode.Parse(handler.CapturedRequests.Single().Body)!;
        // Task #112 wire delta: the native mapping routes MaxOutputTokenCount to the newer
        // field; the legacy "max_tokens" key no longer appears.
        Assert.Equal(512, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(body["max_tokens"]);
    }

    [Fact]
    public async Task Request_Body_StopSequences_Single_EmittedAsOneElementArray()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3", StopSequences = ["\n"] });

        var stop = JsonNode.Parse(handler.CapturedRequests.Single().Body)!["stop"]!;
        // Task #112 wire delta: even a single stop sequence is a 1-element array on the wire.
        Assert.Single(stop.AsArray());
        Assert.Equal("\n", stop[0]!.GetValue<string>());
    }

    [Fact]
    public async Task Request_Body_StopSequences_Multiple_EmittedAsArray()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3", StopSequences = ["\n", "END"] });

        var stop = JsonNode.Parse(handler.CapturedRequests.Single().Body)!["stop"]!;
        Assert.Equal(2, stop.AsArray().Count);
        Assert.Equal("\n", stop[0]!.GetValue<string>());
        Assert.Equal("END", stop[1]!.GetValue<string>());
    }

    [Fact]
    public async Task Request_Body_ReasoningEffort_Mapped()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions
            {
                ModelId = "llama3",
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            });

        var body = JsonNode.Parse(handler.CapturedRequests.Single().Body)!;
        Assert.Equal("high", body["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task Request_Body_ResponseFormatJson_Mapped()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3", ResponseFormat = ChatResponseFormat.Json });

        var body = JsonNode.Parse(handler.CapturedRequests.Single().Body)!;
        Assert.Equal("json_object", body["response_format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Request_Body_Tools_MappedWithSchema()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);
        var function = AIFunctionFactory.Create((string city) => "Paris is the capital.", "lookup", "Looks things up.");

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3", Tools = [function] });

        var body = JsonNode.Parse(handler.CapturedRequests.Single().Body)!;
        var tool = body["tools"]![0]!;
        Assert.Equal("function", tool["type"]!.GetValue<string>());
        Assert.Equal("lookup", tool["function"]!["name"]!.GetValue<string>());
        Assert.Equal("Looks things up.", tool["function"]!["description"]!.GetValue<string>());
        Assert.Equal("string", tool["function"]!["parameters"]!["properties"]!["city"]!["type"]!.GetValue<string>());
        var required = tool["function"]!["parameters"]!["required"]!.AsArray();
        Assert.Contains(required, n => n!.GetValue<string>() == "city");
    }

    [Theory]
    [InlineData(null, "\"auto\"")]
    [InlineData("Auto", "\"auto\"")]
    [InlineData("None", "\"none\"")]
    [InlineData("RequireAny", "\"required\"")]
    [InlineData("RequireSpecific", "{\"type\":\"function\",\"function\":{\"name\":\"lookup\"}}")]
    public async Task Request_Body_ToolChoice_MappedExplicitly(string? mode, string expectedWire)
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);
        var function = AIFunctionFactory.Create((string city) => "Paris is the capital.", "lookup", "Looks things up.");

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions
            {
                ModelId = "llama3",
                Tools = [function],
                ToolMode = mode switch
                {
                    null => null,
                    "Auto" => ChatToolMode.Auto,
                    "None" => ChatToolMode.None,
                    "RequireAny" => ChatToolMode.RequireAny,
                    _ => ChatToolMode.RequireSpecific("lookup"),
                },
            });

        var body = JsonNode.Parse(handler.CapturedRequests.Single().Body)!;
        // Task #112 wire delta: tool_choice is now EXPLICITLY expressed by the native mapping
        // (null/Auto -> "auto", None -> "none", RequireAny -> "required", RequireSpecific ->
        // the function-object form). Compare the exact wire shape (whitespace-insensitive).
        var expected = JsonNode.Parse(expectedWire)!;
        var actual = body["tool_choice"];
        Assert.NotNull(actual);
        Assert.Equal(expected.ToJsonString().Replace(" ", string.Empty), actual!.ToJsonString().Replace(" ", string.Empty));
    }

    [Fact]
    public async Task Request_Body_AssistantToolCallAndToolResult_Mapped()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);
        var functionCall = new FunctionCallContent(
            "call_1", "lookup", new Dictionary<string, object?> { ["city"] = "Paris" });
        var functionResult = new FunctionResultContent("call_1", "Paris is the capital.");

        await client.GetResponseAsync(
            new[]
            {
                new ChatMessage(ChatRole.User, new[] { (AIContent)new TextContent("where is the capital of france?") }),
                new ChatMessage(ChatRole.Assistant, new[] { (AIContent)functionCall }),
                new ChatMessage(ChatRole.Tool, new[] { (AIContent)functionResult }),
            },
            new ChatOptions { ModelId = "llama3" });

        var body = JsonNode.Parse(handler.CapturedRequests.Single().Body)!;
        var messages = body["messages"]!;
        var assistant = messages[1]!;
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        var toolCall = assistant["tool_calls"]![0]!;
        Assert.Equal("call_1", toolCall["id"]!.GetValue<string>());
        Assert.Equal("function", toolCall["type"]!.GetValue<string>());
        Assert.Equal("lookup", toolCall["function"]!["name"]!.GetValue<string>());
        // Tool arguments ride the wire as a JSON STRING (OpenAI requirement).
        var arguments = toolCall["function"]!["arguments"]!.GetValue<string>();
        using var argumentsJson = JsonDocument.Parse(arguments);
        Assert.Equal("Paris", argumentsJson.RootElement.GetProperty("city").GetString());

        var toolResult = messages[2]!;
        Assert.Equal("tool", toolResult["role"]!.GetValue<string>());
        Assert.Equal("call_1", toolResult["tool_call_id"]!.GetValue<string>());
        Assert.Equal("Paris is the capital.", toolResult["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Request_Body_UserImageContent_MapsToImageDataUri()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);
        var image = new DataContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "image/png");

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, new[] { (AIContent)new TextContent("describe"), (AIContent)image }) },
            new ChatOptions { ModelId = "llama3" });

        var body = JsonNode.Parse(handler.CapturedRequests.Single().Body)!;
        var parts = body["messages"]![0]!["content"]!;
        Assert.Equal(2, parts.AsArray().Count);
        Assert.Equal("describe", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("image_url", parts[1]!["type"]!.GetValue<string>());
        Assert.StartsWith("data:image/png;base64,", parts[1]!["image_url"]!["url"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_Body_AdditionalProperties_Dropped()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions
            {
                ModelId = "llama3",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["num_ctx"] = 4096,
                    ["repeat_penalty"] = 1.1,
                    ["raw_tool_choice"] = "first",
                },
            });

        var raw = handler.CapturedRequests.Single().Body;
        // Task #112 (maintainer decision, mirroring #111): the native adapter maps standard
        // properties ONLY — additional properties never reach the OpenAI wire.
        Assert.DoesNotContain("num_ctx", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("repeat_penalty", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("raw_tool_choice", raw, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // Auth + URL composition
    // -----------------------------------------------------------------

    [Fact]
    public async Task Auth_KeyedTarget_SendsBearerToken()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler, apiKey: "sk-secret");

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" });

        Assert.Equal("Bearer sk-secret", handler.CapturedRequests.Single().Authorization?.ToString());
    }

    [Fact]
    public async Task Auth_EmptyKey_SendsNoAuthorizationHeader()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler, apiKey: string.Empty);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" });

        // Keyless wire parity: the factory's strip handler removes the placeholder credential.
        Assert.Null(handler.CapturedRequests.Single().Authorization);
    }

    [Fact]
    public async Task Request_Url_UsesTargetBaseAddressPath()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" });

        Assert.Equal("http://fake-host:8000/v1/chat/completions", handler.CapturedRequests.Single().Url.AbsoluteUri);
    }

    [Fact]
    public async Task Request_Url_RootAddress_ComposesChatCompletions()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler, baseAddress: RootAddress);

        await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" });

        Assert.Equal("http://fake-host:8000/chat/completions", handler.CapturedRequests.Single().Url.AbsoluteUri);
    }

    // -----------------------------------------------------------------
    // Non-streaming response mapping
    // -----------------------------------------------------------------

    [Fact]
    public async Task Response_TextUsageAndMetadata_Mapped()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" });

        var message = Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal("Hello from the fake.", response.Text);
        Assert.Equal("cmpl-1", response.ResponseId);
        Assert.Equal("llama3", response.ModelId);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.NotNull(response.CreatedAt);

        var usage = response.Usage;
        Assert.NotNull(usage);
        Assert.Equal(12, usage.InputTokenCount);
        Assert.Equal(34, usage.OutputTokenCount);
        Assert.Equal(46, usage.TotalTokenCount);
        Assert.Equal(8, usage.CachedInputTokenCount);
        Assert.Equal(20, usage.ReasoningTokenCount);
    }

    [Fact]
    public async Task Response_EmptyChoices_NativeClientThrowsArgumentOutOfRange()
    {
        // Task #112 documented narrowing (native behavior, maintainer-accepted): the OpenAI SDK
        // does NOT tolerate provider-specific empty completions — an empty or missing
        // "choices" array surfaces ArgumentOutOfRangeException from the mapper (the deleted
        // hand-rolled client used to map that shape to a message-less response).
        var handler = new FakeOpenAiHandler
        {
            Json = """{"id":"empty-1","created":1700000000,"model":"llama3","choices":[]}""",
        };
        var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" }));
        // The request reached the wire (guard passed) and the native mapper exploded on the
        // empty array — assert the surface, not SDK-internal message text.
        Assert.Single(handler.CapturedRequests);
    }

    // -----------------------------------------------------------------
    // Streaming request/response mapping
    // -----------------------------------------------------------------

    [Fact]
    public async Task Stream_Body_StreamsAndRequestsUsage()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);

        await foreach (var _ in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" }))
        {
        }

        var body = JsonNode.Parse(handler.CapturedRequests.Single().Body)!;
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Stream_TextDeltas_EmittedInOrder()
    {
        var handler = new FakeOpenAiHandler
        {
            Stream = """
                data: {"id":"s1","model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"a"},"finish_reason":null}]}

                data: {"id":"s1","model":"m","choices":[{"index":0,"delta":{"content":"b"},"finish_reason":null}]}

                data: {"id":"s1","model":"m","choices":[{"index":0,"delta":{"content":"c"},"finish_reason":"stop"}]}

                data: [DONE]

                """,
        };
        var client = CreateClient(handler);

        var texts = new List<string>();
        ChatResponseUpdate? last = null;
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" }))
        {
            if (update.Contents.FirstOrDefault(c => c is TextContent) is TextContent text)
            {
                texts.Add(text.Text);
            }

            last = update;
        }

        Assert.Equal(new[] { "a", "b", "c" }, texts);
        Assert.Equal(ChatFinishReason.Stop, last!.FinishReason);
    }

    [Fact]
    public async Task Stream_NoDoneEof_Tolerated()
    {
        var handler = new FakeOpenAiHandler
        {
            Stream = """
                data: {"id":"s1","choices":[{"index":0,"delta":{"role":"assistant","content":"a"},"finish_reason":null}]}

                data: {"id":"s1","choices":[{"index":0,"delta":{"content":"b"},"finish_reason":"stop"}]}


                """,
        };
        var client = CreateClient(handler);

        var texts = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" }))
        {
            if (update.Contents.FirstOrDefault(c => c is TextContent) is TextContent text)
            {
                texts.Add(text.Text);
            }
        }

        Assert.Equal(new[] { "a", "b" }, texts);
    }

    [Fact]
    public async Task Stream_DoneThenTrailingJunk_Ignored()
    {
        var handler = new FakeOpenAiHandler
        {
            Stream = """
                data: {"id":"s1","choices":[{"index":0,"delta":{"role":"assistant","content":"kept"},"finish_reason":null}]}

                data: [DONE]

                data: {"id":"s1","choices":[{"index":0,"delta":{"content":"SHOULD_NOT_APPEAR"},"finish_reason":"stop"}]}

                """,
        };
        var client = CreateClient(handler);

        var texts = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" }))
        {
            if (update.Contents.FirstOrDefault(c => c is TextContent) is TextContent text)
            {
                texts.Add(text.Text);
            }
        }

        Assert.Equal(new[] { "kept" }, texts);
    }

    [Fact]
    public async Task Stream_CrlfAndCommentLines_Tolerated()
    {
        var handler = new FakeOpenAiHandler
        {
            Stream = ": keepalive\r\n" +
                     "event: message\r\n" +
                     "id: 42\r\n" +
                     "data: {\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"a\"},\"finish_reason\":null}]}\r\n" +
                     "\r\n" +
                     "data: {\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"b\"},\"finish_reason\":\"stop\"}]}\r\n" +
                     "\r\n",
        };
        var client = CreateClient(handler);

        var texts = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" }))
        {
            if (update.Contents.FirstOrDefault(c => c is TextContent) is TextContent text)
            {
                texts.Add(text.Text);
            }
        }

        Assert.Equal(new[] { "a", "b" }, texts);
    }

    [Fact]
    public async Task Stream_PartialLineAtEof_NotDispatched()
    {
        // Task #112 documented narrowing (native behavior, maintainer-accepted): the native SSE
        // parser is strict — a final "data:" line WITHOUT the blank-line terminator is not
        // dispatched (the deleted hand-rolled client dispatched pending data defensively).
        var handler = new FakeOpenAiHandler
        {
            Stream = "data: {\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"tail\"},\"finish_reason\":\"stop\"}]}",
        };
        var client = CreateClient(handler);

        var texts = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" }))
        {
            if (update.Contents.FirstOrDefault(c => c is TextContent) is TextContent text)
            {
                texts.Add(text.Text);
            }
        }

        Assert.Empty(texts);
    }

    [Fact]
    public async Task Stream_UsageChunk_EmitsUsageContent()
    {
        var handler = new FakeOpenAiHandler
        {
            Stream = """
                data: {"id":"s1","model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"Hi"},"finish_reason":null}]}

                data: {"id":"s1","model":"m","choices":[{"index":0,"delta":{"content":"!"},"finish_reason":"stop"}]}

                data: {"id":"s1","model":"m","choices":[],"usage":{"prompt_tokens":7,"completion_tokens":11,"total_tokens":18,"completion_tokens_details":{"reasoning_tokens":3},"prompt_tokens_details":{"cached_tokens":5}}}

                data: [DONE]

                """,
        };
        var client = CreateClient(handler);

        UsageContent? usage = null;
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" }))
        {
            if (update.Contents.FirstOrDefault(c => c is UsageContent) is UsageContent u)
            {
                usage = u;
            }
        }

        Assert.NotNull(usage);
        Assert.Equal(7, usage.Details.InputTokenCount);
        Assert.Equal(11, usage.Details.OutputTokenCount);
        Assert.Equal(18, usage.Details.TotalTokenCount);
        Assert.Equal(3, usage.Details.ReasoningTokenCount);
        Assert.Equal(5, usage.Details.CachedInputTokenCount);
    }

    [Fact]
    public async Task Stream_UsageMergedIntoFinalContentChunk_SingleUsageContent()
    {
        var handler = new FakeOpenAiHandler
        {
            Stream = """
                data: {"id":"s1","model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"Hi"},"finish_reason":null}]}

                data: {"id":"s1","model":"m","choices":[{"index":0,"delta":{"content":"!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":9,"total_tokens":14,"completion_tokens_details":{"reasoning_tokens":2}}}

                data: [DONE]

                """,
        };
        var client = CreateClient(handler);

        var usageChunks = new List<UsageContent>();
        var lastUpdate = (ChatResponseUpdate?)null;
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" }))
        {
            if (update.Contents.FirstOrDefault(c => c is UsageContent) is UsageContent u)
            {
                usageChunks.Add(u);
            }

            lastUpdate = update;
        }

        var usage = Assert.Single(usageChunks);
        Assert.Equal(5, usage.Details.InputTokenCount);
        Assert.Equal(9, usage.Details.OutputTokenCount);
        Assert.Equal(2, usage.Details.ReasoningTokenCount);
        // The usage rides the FINAL content chunk: no separate terminal update follows it.
        Assert.Contains(lastUpdate!.Contents, c => c is UsageContent);
    }

    [Fact]
    public async Task Stream_FragmentedToolCalls_AccumulateIntoSingleFunctionCall()
    {
        var handler = new FakeOpenAiHandler
        {
            Stream = """
                data: {"id":"s1","model":"m","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}

                data: {"id":"s1","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"ci"}}]},"finish_reason":null}]}

                data: {"id":"s1","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ty\":\"Pa"}}]},"finish_reason":null}]}

                data: {"id":"s1","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ris\"}"}}]},"finish_reason":"tool_calls"}]}

                data: [DONE]

                """,
        };
        var client = CreateClient(handler);

        var functionCalls = new List<FunctionCallContent>();
        ChatFinishReason? lastFinish = null;
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" }))
        {
            functionCalls.AddRange(update.Contents.OfType<FunctionCallContent>());
            lastFinish = update.FinishReason;
        }

        // Fragmented arguments accumulate natively; the final update carries ONE FunctionCallContent.
        var call = Assert.Single(functionCalls);
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("get_weather", call.Name);
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(call.Arguments));
        Assert.Equal("Paris", arguments.RootElement.GetProperty("city").GetString());
        Assert.Equal(ChatFinishReason.ToolCalls, lastFinish);
    }

    [Fact]
    public async Task Cancel_MidStream_OperationCanceledExceptionSurfacesUnmodified()
    {
        var handler = new FakeOpenAiHandler
        {
            ScriptedStream = () => new HangStream(Encoding.UTF8.GetBytes(
                "data: {\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":null}]}\n\n")),
        };
        var client = CreateClient(handler);

        using var cts = new CancellationTokenSource();
        var consume = ConsumeUntilCancelledAsync(client, cts.Token);
        await Task.Delay(2_000);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await consume);
        // The SDK's cancellation is not wrapped into a ClientResultException.
    }

    private static async Task ConsumeUntilCancelledAsync(IChatClient client, CancellationToken token)
    {
        await foreach (var _ in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" },
            token))
        {
        }
    }

    /// <summary>
    /// A <see cref="MemoryStream"/> that hangs on the socket after the FIRST read until
    /// <see cref="CancellationToken"/> fires (deterministic mid-stream cancel; the token completes
    /// the wait, and a 60 s backstop ends the stream so the suite can never hang past the gate).
    /// </summary>
    private sealed class HangStream(byte[] initial) : MemoryStream(initial)
    {
        private int _hangs;

        private async Task HangAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _hangs) != 1)
            {
                return;
            }

            // First read returned content; now hold the socket open until cancelled. A 60 s
            // backstop converts a missing cancellation into a LOUD test failure (never a hang).
            var backstop = Task.Delay(60_000);
            await Task.WhenAny(Task.Delay(Timeout.Infinite, token), backstop).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("cancellation backstop: the token was never cancelled");
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var result = await base.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            await HangAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            await HangAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    // -----------------------------------------------------------------
    // Native error semantics (ClientResultException)
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "key is locked", 401)]
    [InlineData(HttpStatusCode.NotFound, "model 'nope' not found", 404)]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit exceeded", 429)]
    [InlineData(HttpStatusCode.InternalServerError, "boom", 500)]
    public async Task Error_Non2xxBeforeFirstByte_ClientResultExceptionCarriesStatusAndBody(
        HttpStatusCode status, string detail, int expected)
    {
        var handler = new ErrorStatusHandler(status, detail);
        var client = new OpenAiChatClientFactory(_ => handler)
            .GetClient(new OpenAiTargetEndpoint("target", BaseAddress, TestKey));

        var ex = await Assert.ThrowsAsync<ClientResultException>(() => client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" }));

        Assert.Equal(expected, ex.Status);
        var raw = ex.GetRawResponse();
        Assert.NotNull(raw);
        Assert.Contains(detail, raw!.Content.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Replays a non-2xx OpenAI error envelope (the SDK throws before any body parse).</summary>
    private sealed class ErrorStatusHandler(HttpStatusCode status, string detail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string template = "{\"error\":{\"message\":\"%s\",\"type\":\"invalid_request_error\",\"param\":null,\"code\":null}}";
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(template.Replace("%s", detail), Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Error_TransportFailure_StatusZeroRawResponseNull()
    {
        var handler = new FakeOpenAiHandler { TransportException = new HttpRequestException("connection refused") };
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ClientResultException>(() => client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "llama3" }));

        // Transport-level failure analogue of the deleted status-less OpenAiHttpException:
        // Status == 0 + null raw response (the middleware fails over with a streak).
        Assert.Equal(0, ex.Status);
        Assert.Null(ex.GetRawResponse());
    }

    // -----------------------------------------------------------------
    // Model guard (parity with the deleted client, pre-I/O)
    // -----------------------------------------------------------------

    [Fact]
    public async Task ModelGuard_BlankModelId_ThrowsBeforeAnyNetworkCall()
    {
        var handler = new FakeOpenAiHandler();
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, options: null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }, new ChatOptions { ModelId = "   " }));
        Assert.Empty(handler.CapturedRequests);
    }

    // -----------------------------------------------------------------
    // Pooling factory semantics
    // -----------------------------------------------------------------

    [Fact]
    public async Task Factory_PoolKey_NormalizesUriAndKeyHash()
    {
        var handlerA = new FakeOpenAiHandler();
        var handlerB = new FakeOpenAiHandler();
        var factory = new OpenAiChatClientFactory(endpoint =>
            endpoint.BaseAddress.AbsolutePath.StartsWith("/v1", StringComparison.Ordinal) ? handlerA : handlerB);

        // Uri lowercases the host, so the case-cosmetic variant differs only in authority case
        // ("http://A:8000/v1/" vs "http://a:8000/v1") — exactly what the pool key must normalize.
        var endpointA = new OpenAiTargetEndpoint("A", new Uri("http://A:8000/v1/"), "key-1");
        var endpointAcosmetic = new OpenAiTargetEndpoint("A", new Uri("http://a:8000/v1"), "key-1");
        var endpointAdifferentKey = new OpenAiTargetEndpoint("A", new Uri("http://a:8000/v1"), "key-2");
        var endpointB = new OpenAiTargetEndpoint("B", new Uri("http://b:8000"), "key-1");

        var clientA = factory.GetClient(endpointA);
        var clientACosmetic = factory.GetClient(endpointAcosmetic);
        var clientADifferentKey = factory.GetClient(endpointAdifferentKey);
        var clientB = factory.GetClient(endpointB);

        Assert.Same(clientA, clientACosmetic);          // URI case / trailing-slash cosmetics share a pool slot.
        Assert.NotSame(clientA, clientADifferentKey);   // a key change yields a fresh client.
        Assert.NotSame(clientA, clientB);

        await clientA.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, new ChatOptions { ModelId = "m" });
        await clientADifferentKey.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, new ChatOptions { ModelId = "m" });
        await clientB.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, new ChatOptions { ModelId = "m" });
        // Both A-key-1 and A-key-2 entries route through the path-matched handlerA (two pooled
        // clients for the SAME url+path pair — the key change DID create a fresh entry).
        Assert.Collection(handlerA.CapturedRequests, _ => { }, _ => { });
        Assert.Single(handlerB.CapturedRequests);
    }

    [Fact]
    public void Factory_DropClient_DisposesInjectedHandler_ConsumerDisposeDoesNot()
    {
        var tracking = new TrackingHandler();
        var factory = new OpenAiChatClientFactory(_ => tracking);
        var endpoint = new OpenAiTargetEndpoint("t", new Uri("http://h:8000/v1"), "k");
        var pooled = factory.GetClient(endpoint);

        // Non-owning consumer contract: an MEAI consumer disposing a pooled client mid-fleet
        // must not tear down the shared handler (verified: the adapter dispose is a no-op and
        // the native pipeline never disposes the caller-supplied HttpClient).
        (pooled as IDisposable)?.Dispose();
        Assert.False(tracking.Disposed);

        factory.DropClient(endpoint);
        Assert.True(tracking.Disposed);
    }

    [Fact]
    public void Factory_Dispose_DisposesAllPooledHandlers()
    {
        var trackingA = new TrackingHandler();
        var trackingB = new TrackingHandler();
        var factory = new OpenAiChatClientFactory(endpoint =>
            string.Equals(endpoint.ApiKey, "k1", StringComparison.Ordinal) ? trackingA : trackingB);

        factory.GetClient(new OpenAiTargetEndpoint("a", new Uri("http://a:8000/v1"), "k1"));
        factory.GetClient(new OpenAiTargetEndpoint("b", new Uri("http://b:8000/v1"), "k2"));

        factory.Dispose();
        Assert.True(trackingA.Disposed);
        Assert.True(trackingB.Disposed);
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BaseCompletion.ToJsonString(), Encoding.UTF8, "application/json"),
            });

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
