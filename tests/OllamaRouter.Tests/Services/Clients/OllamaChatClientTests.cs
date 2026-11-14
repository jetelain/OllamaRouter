using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Clients;
using OllamaSharp.Models.Exceptions;

namespace OllamaRouter.Tests.Services.Clients;

/// <summary>
/// Task #111: <see cref="OllamaChatClient"/> — the delegating <c>IChatClient</c> over the native
/// OllamaSharp 5.5.2 <c>OllamaApiClient</c>. Covers the request normalizations (model guard,
/// options narrowing, tool-result stringification, UriContent image fetch policy), the native
/// wire mapping (roles, standard options only, tools without tool_choice, thinking in BOTH
/// directions, images, tool_calls round-trip with synthetic ids), the NDJSON streaming read
/// (content/thinking deltas, done-frame finish reason + UsageContent), the non-streaming
/// mapping, the native error semantics (400 → OllamaException family, other statuses →
/// HttpRequestException with StatusCode, transport → null status), and the non-owning disposal
/// contract. Everything runs against in-process fake handlers (no network).
/// </summary>
public class OllamaChatClientTests
{
    private const string DaemonAddress = "http://daemon:11434";

    private sealed class FakeOllamaHandler(HttpResponseMessage? response = null, Exception? throwException = null)
        : HttpMessageHandler
    {
        private readonly List<string> _requestBodies = [];

        public List<string> RequestBodies => _requestBodies;

        public List<string> RequestPaths { get; } = [];

        public int SendCount => RequestPaths.Count;

        /// <summary>True once this handler (and only this handler) was disposed.</summary>
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            if (request.Content is not null)
            {
                _requestBodies.Add(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }

            if (throwException is not null)
            {
                throw throwException;
            }

            var effective = response ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "application/json"),
            };

            return Task.FromResult(effective);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static HttpResponseMessage Ndjson(params string[] frames) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join("\n", frames), Encoding.UTF8, "application/x-ndjson"),
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static OllamaChatClient CreateClient(FakeOllamaHandler handler, string? defaultModelId = null, string baseAddress = DaemonAddress) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(baseAddress) }, defaultModelId);

    private static async Task<JsonElement> ParseBodyAsync(FakeOllamaHandler handler)
    {
        using var doc = JsonDocument.Parse(handler.RequestBodies.Single());
        return doc.RootElement.Clone();
    }

    // ------------------------------------------------------------------
    //  Streaming: content, thinking, finish reason, usage
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStreamingResponse_YieldsTextUpdatesThenDoneUpdateWithFinishAndUsage()
    {
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"llama3","message":{"role":"assistant","content":"Hel"},"done":false}""",
            """{"model":"llama3","message":{"role":"assistant","content":"lo"},"done":false}""",
            """{"model":"llama3","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":10,"eval_count":4}"""));

        using var client = CreateClient(handler);
        var updates = new List<ChatResponseUpdate>(
            await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
                new ChatOptions { ModelId = "llama3" }).ToListAsync());

        // Two text updates, then the native done-frame update carrying finish reason AND
        // UsageContent in ONE update (the encoders are update-shape-agnostic).
        Assert.Equal(3, updates.Count);
        Assert.Equal("Hel", updates[0].Contents.OfType<TextContent>().Single().Text);
        Assert.Equal("lo", updates[1].Contents.OfType<TextContent>().Single().Text);

        var doneUpdate = updates[2];
        Assert.Equal(ChatFinishReason.Stop, doneUpdate.FinishReason);
        var usage = doneUpdate.Contents.OfType<UsageContent>().Single().Details;
        Assert.Equal(10, usage.InputTokenCount);
        Assert.Equal(4, usage.OutputTokenCount);
        Assert.Equal(14, usage.TotalTokenCount);

        // Outbound wire: /api/chat, stream=true, model + user message.
        Assert.Equal("/api/chat", handler.RequestPaths.Single());
        var body = await ParseBodyAsync(handler);
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal("llama3", body.GetProperty("model").GetString());
        var messages = body.GetProperty("messages");
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("hi", messages[0].GetProperty("content").GetString());
        Assert.False(body.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public async Task GetStreamingResponse_OptionsAlwaysPresentAndEmptyWhenNothingIsSet()
    {
        // Known native wire artifact (task #111): the mapper initializes RequestOptions, so the
        // wire always carries "options":{} (and the inherited "CustomHeaders":{}).
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}"""));

        using var client = CreateClient(handler);
        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m" }).ToListAsync();

        var body = await ParseBodyAsync(handler);
        var options = body.GetProperty("options");
        Assert.Equal(JsonValueKind.Object, options.ValueKind);
        Assert.Equal(0, options.EnumerateObject().Count());
        Assert.Equal(0, body.GetProperty("CustomHeaders").EnumerateObject().Count());
    }

    [Theory]
    [InlineData("stop", "stop")]
    [InlineData("length", "length")]
    [InlineData("tool_calls", "tool_calls")]
    [InlineData("custom_reason", "custom_reason")]
    public async Task GetStreamingResponse_DoneReasonMapsToFinishReason(string doneReason, string expectedValue)
    {
        var handler = new FakeOllamaHandler(Ndjson(
            $$"""{"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"{{doneReason}}","prompt_eval_count":1,"eval_count":1}"""));

        using var client = CreateClient(handler);
        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m" }).ToListAsync();

        var finish = updates.Single(u => u.FinishReason is not null).FinishReason;
        Assert.True(string.Equals(finish.ToString(), expectedValue, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetStreamingResponse_ThinkingFrame_YieldsTextReasoningContent_ExcludedFromText()
    {
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":"A","thinking":"let me think"},"done":false}""",
            """{"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}"""));

        using var client = CreateClient(handler);
        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m" }).ToListAsync();

        var thinkingUpdate = updates.Single(u => u.Contents.OfType<TextReasoningContent>().Any());
        Assert.Equal("let me think", thinkingUpdate.Contents.OfType<TextReasoningContent>().Single().Text);
        Assert.Equal("A", thinkingUpdate.Contents.OfType<TextContent>().Single().Text);
        // ChatResponseUpdate.Text EXCLUDES reasoning (MEAI 10.9.0) — no encoder leak.
        Assert.Equal("A", thinkingUpdate.Text);
    }

    [Fact]
    public async Task GetStreamingResponse_ThinkingOnlyFrame_YieldsTextReasoningContent()
    {
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","thinking":"hmm"},"done":false}""",
            """{"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}"""));

        using var client = CreateClient(handler);
        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m" }).ToListAsync();

        var thinking = updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Single();
        Assert.Equal("hmm", thinking.Text);
        Assert.Equal(string.Empty, updates[0].Text);
    }

    [Fact]
    public async Task GetResponseAsync_NonStreamedThinking_MapsToTextReasoningContent()
    {
        var handler = new FakeOllamaHandler(Json(
            """{"model":"m","message":{"role":"assistant","content":"answer","thinking":"reasoning"},"done":true,"done_reason":"stop","prompt_eval_count":3,"eval_count":2}"""));

        using var client = CreateClient(handler);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m" });

        Assert.Equal("answer", response.Messages[0].Contents.OfType<TextContent>().Single().Text);
        Assert.Equal("reasoning", response.Messages[0].Contents.OfType<TextReasoningContent>().Single().Text);
    }

    [Fact]
    public async Task GetStreamingResponse_RequestThinkingContent_FlowsToWireThinking()
    {
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}"""));

        using var client = CreateClient(handler);
        var assistant = new ChatMessage(ChatRole.Assistant, [
            new TextReasoningContent("because it reasons"),
            new TextContent("the answer"),
        ]);

        await client.GetStreamingResponseAsync([
            new ChatMessage(ChatRole.User, "why?"),
            assistant,
        ], new ChatOptions { ModelId = "m" }).ToListAsync();

        var messages = (await ParseBodyAsync(handler)).GetProperty("messages");
        Assert.Equal("the answer", messages[1].GetProperty("content").GetString());
        // Request-side thinking preservation (the 5.5.2 pin).
        Assert.Equal("because it reasons", messages[1].GetProperty("thinking").GetString());
    }

    [Fact]
    public async Task GetStreamingResponse_ReasoningOptions_MapToThink()
    {
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}"""));

        using var client = CreateClient(handler);
        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions
            {
                ModelId = "m",
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            }).ToListAsync();

        var body = await ParseBodyAsync(handler);
        Assert.Equal("high", body.GetProperty("think").GetString());
    }

    [Fact]
    public async Task GetStreamingResponse_UsageZerosWhenDaemonOmitsCounts()
    {
        // Delta (task #111): Usage is ALWAYS present on done frames (0/0 when the daemon omits
        // the counts) instead of being absent.
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}"""));

        using var client = CreateClient(handler);
        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m" }).ToListAsync();

        var usage = updates.Single(u => u.Contents.OfType<UsageContent>().Any()).Contents.OfType<UsageContent>().Single().Details;
        Assert.Equal(0, usage.InputTokenCount);
        Assert.Equal(0, usage.OutputTokenCount);
        Assert.Equal(0, usage.TotalTokenCount);
        // CachedInputTokenCount is no longer surfaced (5.5.2 done-frame DTO has no
        // prompt_eval_cached_count) — even when present on the wire.
        var cachedHandler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":1000,"prompt_eval_cached_count":800,"eval_count":100}"""));
        using var cachedClient = CreateClient(cachedHandler);
        var cachedUpdates = await cachedClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m" }).ToListAsync();
        var cachedUsage = cachedUpdates.Single(u => u.Contents.OfType<UsageContent>().Any()).Contents.OfType<UsageContent>().Single().Details;
        Assert.Equal(1000, cachedUsage.InputTokenCount);
        Assert.Null(cachedUsage.CachedInputTokenCount);
    }

    // ------------------------------------------------------------------
    //  Options narrowing + tools
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetResponseAsync_MapsStandardOptionsOnly()
    {
        var handler = new FakeOllamaHandler(Json(
            """{"model":"qwen2.5","message":{"role":"assistant","content":"Hi there"},"done":true,"done_reason":"length","prompt_eval_count":5,"eval_count":2}"""));

        using var client = CreateClient(handler);
        var response = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "you are terse"),
                new ChatMessage(ChatRole.User, "hi"),
            ],
            new ChatOptions
            {
                ModelId = "qwen2.5",
                Temperature = 0.5f,
                TopP = 0.9f,
                TopK = 20,
                MaxOutputTokens = 128,
                Seed = 42,
                StopSequences = ["\n"],
            });

        Assert.False((await ParseBodyAsync(handler)).GetProperty("stream").GetBoolean());
        var messages = (await ParseBodyAsync(handler)).GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        var options = (await ParseBodyAsync(handler)).GetProperty("options");
        Assert.Equal(0.5, options.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, options.GetProperty("top_p").GetDouble(), precision: 10);
        Assert.Equal(20, options.GetProperty("top_k").GetInt32());
        Assert.Equal(128, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(42, options.GetProperty("seed").GetInt64());
        // Native mapping: stop is always an array (the old client sent a bare string for one).
        Assert.Equal(JsonValueKind.Array, options.GetProperty("stop").ValueKind);
        Assert.Equal("\n", options.GetProperty("stop")[0].GetString());

        // Non-streaming response mapping: text, finish reason, ALWAYS-present usage.
        Assert.Equal("Hi there", response.Messages[0].Contents.OfType<TextContent>().Single().Text);
        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
        Assert.NotNull(response.Usage);
        Assert.Equal(5, response.Usage!.InputTokenCount);
        Assert.Equal(2, response.Usage!.OutputTokenCount);
        Assert.Equal(7, response.Usage!.TotalTokenCount);
    }

    [Fact]
    public async Task GetStreamingResponse_AdditionalPropertiesNeverReachTheWire()
    {
        // Functional narrowing (maintainer decision): num_ctx / repeat_penalty / keep_alive in
        // ChatOptions.AdditionalProperties are DROPPED — neither the adapter pass-through nor
        // the native mapper's ~35-name whitelist may forward them.
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}"""));

        using var client = CreateClient(handler);
        var options = new ChatOptions
        {
            ModelId = "m",
            Temperature = 0.3f,
        };
        options.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["num_ctx"] = JsonSerializer.SerializeToElement(8192),
            ["repeat_penalty"] = JsonSerializer.SerializeToElement(1.2),
            ["keep_alive"] = JsonSerializer.SerializeToElement("10m"),
        };
        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options).ToListAsync();

        var raw = handler.RequestBodies.Single();
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(0.3, body.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.DoesNotContain("num_ctx", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("repeat_penalty", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("keep_alive", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStreamingResponse_FunctionToolsSerialized_WithoutToolChoice()
    {
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true}"""));

        using var client = CreateClient(handler);
        var options = new ChatOptions
        {
            ModelId = "m",
            // Tool constraints are DROPPED by design (5.5.2 has no tool_choice member) — set a
            // mode to prove it never reaches the wire.
            ToolMode = new RequiredChatToolMode("add"),
            Tools =
            [
                AIFunctionFactory.Create((int a, int b) => a + b,
                    new AIFunctionFactoryOptions { Name = "add", Description = "adds" }),
            ],
        };

        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "1+2")], options).ToListAsync();

        var body = await ParseBodyAsync(handler);
        var tools = body.GetProperty("tools");
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal("add", tools[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("adds", tools[0].GetProperty("function").GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Object, tools[0].GetProperty("function").GetProperty("parameters").ValueKind);
        Assert.False(body.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public async Task GetStreamingResponse_FunctionCallContent_GoesOutAsToolCallsWithObjectArguments()
    {
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true}"""));

        using var client = CreateClient(handler);
        var assistant = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("call-1", "get_weather", new Dictionary<string, object?>
            {
                ["city"] = "Berlin",
                ["days"] = 3,
            }),
        ]);

        await client.GetStreamingResponseAsync([
            new ChatMessage(ChatRole.User, "weather?"),
            assistant,
        ], new ChatOptions { ModelId = "m" }).ToListAsync();

        var messages = (await ParseBodyAsync(handler)).GetProperty("messages");
        var toolCalls = messages[1].GetProperty("tool_calls");
        Assert.Equal("get_weather", toolCalls[0].GetProperty("function").GetProperty("name").GetString());
        var arguments = toolCalls[0].GetProperty("function").GetProperty("arguments");
        Assert.Equal(JsonValueKind.Object, arguments.ValueKind);
        Assert.Equal("Berlin", arguments.GetProperty("city").GetString());
        Assert.Equal(3, arguments.GetProperty("days").GetInt32());
    }

    [Fact]
    public async Task GetStreamingResponse_FunctionResultContent_StringResult_GoesOutAsRawString()
    {
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true}"""));

        using var client = CreateClient(handler);
        await client.GetStreamingResponseAsync([
            new ChatMessage(ChatRole.User, "weather?"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "get_weather", new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "{\"temp\":21}")]),
        ], new ChatOptions { ModelId = "m" }).ToListAsync();

        var messages = (await ParseBodyAsync(handler)).GetProperty("messages");
        // role:"tool" with the RAW result string (no {"CallId":...,"Result":...} wrapper —
        // the adapter pre-converts, matching the old client's wire shape).
        var toolMessage = messages.EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal("{\"temp\":21}", toolMessage.GetProperty("content").GetString());
        Assert.False(toolMessage.TryGetProperty("CallId", out _));
    }

    [Fact]
    public async Task GetStreamingResponse_FunctionResultContent_NonStringResult_GoesOutAsJsonString()
    {
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true}"""));

        using var client = CreateClient(handler);
        await client.GetStreamingResponseAsync([
            new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent("call-1", new Dictionary<string, object?> { ["temp"] = 21 })]),
        ], new ChatOptions { ModelId = "m" }).ToListAsync();

        var messages = (await ParseBodyAsync(handler)).GetProperty("messages");
        Assert.Equal("{\"temp\":21}", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetStreamingResponse_EmptyFunctionResult_MessageDroppedByNativeMapper()
    {
        // Documented delta (task #111): an empty tool result yields an empty TextContent, which
        // the native mapper drops (the old client emitted an empty tool message).
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true}"""));

        using var client = CreateClient(handler);
        await client.GetStreamingResponseAsync([
            new ChatMessage(ChatRole.User, "weather?"),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", string.Empty)]),
        ], new ChatOptions { ModelId = "m" }).ToListAsync();

        var messages = (await ParseBodyAsync(handler)).GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength()); // the user message only — the empty tool message is gone
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task GetStreamingResponse_InboundToolCalls_MappedWithSyntheticIds()
    {
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":"","tool_calls":[{"id":null,"function":{"index":null,"name":"get_weather","arguments":{"city":"Paris"}}}]},"done":false}""",
            """{"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"tool_calls"}"""));

        using var client = CreateClient(handler);
        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "weather?")],
            new ChatOptions { ModelId = "m" }).ToListAsync();

        var call = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal("get_weather", call.Name);
        // Ollama conveys no server-side call id: a synthetic one is generated on read.
        Assert.Equal(8, call.CallId.Length);
        // In AOT context mode, object-typed argument values deserialize as JsonElement.
        var city = call.Arguments["city"] switch
        {
            string s => s,
            JsonElement je => je.ToString(),
            var other => Convert.ToString(other),
        };
        Assert.Equal("Paris", city);
    }

    // ------------------------------------------------------------------
    //  Images: DataContent (native) + UriContent (router fetch)
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStreamingResponse_DataContentImage_GoesIntoImagesArray()
    {
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}"""));

        using var client = CreateClient(handler);
        var imageBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        await client.GetStreamingResponseAsync([
            new ChatMessage(ChatRole.User, [
                new TextContent("what is this?"),
                new DataContent(imageBytes, "image/png"),
            ]),
        ], new ChatOptions { ModelId = "m" }).ToListAsync();

        var messages = (await ParseBodyAsync(handler)).GetProperty("messages");
        var images = messages[0].GetProperty("images");
        Assert.Equal(Convert.ToBase64String(imageBytes), images[0].GetString());
        Assert.DoesNotContain("data:", images[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStreamingResponse_DataUriDataContentImage_DecodedIntoImagesArray()
    {
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}"""));

        using var client = CreateClient(handler);
        var imageBytes = new byte[] { 1, 2, 3, 4 };
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(imageBytes);
        await client.GetStreamingResponseAsync([
            new ChatMessage(ChatRole.User, [new DataContent(dataUri, "image/png")]),
        ], new ChatOptions { ModelId = "m" }).ToListAsync();

        var messages = (await ParseBodyAsync(handler)).GetProperty("messages");
        Assert.Equal(Convert.ToBase64String(imageBytes), messages[0].GetProperty("images")[0].GetString());
    }

    private sealed class FakeImageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeImageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public List<Uri> FetchedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            FetchedUris.Add(request.RequestUri!);
            return Task.FromResult(_responder(request));
        }
    }

    private static OllamaChatClient CreateClientWithImageHandler(
        FakeOllamaHandler daemonHandler,
        FakeImageHandler imageHandler)
    {
        var httpClient = new HttpClient(daemonHandler) { BaseAddress = new Uri(DaemonAddress) };
        var apiClient = new RouterOllamaApiClient(httpClient, OllamaSharpWireContext.Default);
        var imageClient = new HttpClient(imageHandler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        return new OllamaChatClient(apiClient, imageClient, ownsNativeClient: true, ownsImageFetchClient: true);
    }

    [Fact]
    public async Task GetStreamingResponse_UriContentImage_FetchedAndEmbeddedWithMediaTypePreserved()
    {
        var payload = new byte[] { 255, 216, 255, 217, 0, 1 };
        var daemonHandler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}"""));
        var imageHandler = new FakeImageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });

        using var client = CreateClientWithImageHandler(daemonHandler, imageHandler);
        var user = new ChatMessage(ChatRole.User, [
            new TextContent("look"),
            new UriContent(new Uri("http://images.local/cat.jpg")),
        ]);

        await client.GetStreamingResponseAsync([user], new ChatOptions { ModelId = "m" }).ToListAsync();

        Assert.Single(imageHandler.FetchedUris);
        var messages = (await ParseBodyAsync(daemonHandler)).GetProperty("messages");
        Assert.Equal(Convert.ToBase64String(payload), messages[0].GetProperty("images")[0].GetString());
    }

    [Fact]
    public async Task GetStreamingResponse_UriContentNullMediaType_Fetched()
    {
        var daemonHandler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}"""));
        var imageHandler = new FakeImageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("img", Encoding.UTF8, "image/jpeg"),
        });

        using var client = CreateClientWithImageHandler(daemonHandler, imageHandler);
        // A URI without a recognizable extension gets MEAI's application/octet-stream inference
        // fallback (what the inbound OpenAI parser's image_url mapping produces for
        // non-standard image extensions) → the router fetches it.
        var user = new ChatMessage(ChatRole.User, [
            new TextContent("look"),
            new UriContent(new Uri("http://images.local/unknown"), mediaType: null),
        ]);

        await client.GetStreamingResponseAsync([user], new ChatOptions { ModelId = "m" }).ToListAsync();

        Assert.Single(imageHandler.FetchedUris);
        var messages = (await ParseBodyAsync(daemonHandler)).GetProperty("messages");
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("img")), messages[0].GetProperty("images")[0].GetString());
    }

    [Fact]
    public async Task GetStreamingResponse_UriContentNonImageMediaType_NotFetchedNotMapped()
    {
        var daemonHandler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}"""));
        var imageHandler = new FakeImageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("txt", Encoding.UTF8),
        });

        using var client = CreateClientWithImageHandler(daemonHandler, imageHandler);
        var user = new ChatMessage(ChatRole.User, [
            new TextContent("see this"),
            new UriContent(new Uri("http://images.local/doc.txt"), mediaType: "text/plain"),
        ]);

        await client.GetStreamingResponseAsync([user], new ChatOptions { ModelId = "m" }).ToListAsync();

        Assert.Empty(imageHandler.FetchedUris);
        var messages = (await ParseBodyAsync(daemonHandler)).GetProperty("messages");
        Assert.False(messages[0].TryGetProperty("images", out _));
    }

    [Fact]
    public async Task GetStreamingResponse_UriContentNonHttpScheme_ThrowsNullStatusHttpRequestException()
    {
        var daemonHandler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true}"""));
        var imageHandler = new FakeImageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("x", Encoding.UTF8),
        });

        using var client = CreateClientWithImageHandler(daemonHandler, imageHandler);
        var user = new ChatMessage(ChatRole.User, [
            new TextContent("look"),
            new UriContent(new Uri("file:///etc/passwd")),
        ]);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStreamingResponseAsync(
            [user], new ChatOptions { ModelId = "m" }).ToListAsync().AsTask());

        // A scheme violation is NOT an upstream response: null status ⇒ transport failure ⇒
        // failover (and the daemon was never contacted).
        Assert.Null(ex.StatusCode);
        Assert.Contains("scheme", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(daemonHandler.RequestPaths);
        Assert.Empty(imageHandler.FetchedUris);
    }

    [Fact]
    public async Task GetStreamingResponse_UriContentFetch404_ThrowsNullStatusHttpRequestException()
    {
        var daemonHandler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true}"""));
        var imageHandler = new FakeImageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("nope", Encoding.UTF8),
        });

        using var client = CreateClientWithImageHandler(daemonHandler, imageHandler);
        var user = new ChatMessage(ChatRole.User, [
            new TextContent("look"),
            new UriContent(new Uri("http://images.local/missing.jpg")),
        ]);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStreamingResponseAsync(
            [user], new ChatOptions { ModelId = "m" }).ToListAsync().AsTask());

        Assert.Null(ex.StatusCode); // deliberately stripped — not an upstream-daemon 404
        Assert.Empty(daemonHandler.RequestPaths);
    }

    [Fact]
    public async Task GetStreamingResponse_UriContentOverCap_ThrowsNullStatusHttpRequestException()
    {
        var daemonHandler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true}"""));
        var oversize = new byte[25 * 1024 * 1024 + 1];
        var imageHandler = new FakeImageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(oversize),
        });

        using var client = CreateClientWithImageHandler(daemonHandler, imageHandler);
        var user = new ChatMessage(ChatRole.User, [
            new TextContent("look"),
            new UriContent(new Uri("http://images.local/huge.jpg")),
        ]);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStreamingResponseAsync(
            [user], new ChatOptions { ModelId = "m" }).ToListAsync().AsTask());

        Assert.Null(ex.StatusCode);
        Assert.Empty(daemonHandler.RequestPaths);
    }

    [Fact]
    public async Task GetStreamingResponse_UriContentFetchTransportFailure_ThrowsNullStatusHttpRequestException()
    {
        var daemonHandler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true}"""));
        var imageHandler = new FakeImageHandler(_ => throw new IOException("socket reset"));

        using var client = CreateClientWithImageHandler(daemonHandler, imageHandler);
        var user = new ChatMessage(ChatRole.User, [
            new TextContent("look"),
            new UriContent(new Uri("http://images.local/down.jpg")),
        ]);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStreamingResponseAsync(
            [user], new ChatOptions { ModelId = "m" }).ToListAsync().AsTask());

        Assert.Null(ex.StatusCode);
        Assert.Empty(daemonHandler.RequestPaths);
    }

    [Fact]
    public async Task GetStreamingResponse_UriContentFetchCancelled_SurfacesOperationCanceled()
    {
        var daemonHandler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":""},"done":true}"""));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var imageHandler = new FakeImageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("img", Encoding.UTF8),
        });

        using var client = CreateClientWithImageHandler(daemonHandler, imageHandler);
        var user = new ChatMessage(ChatRole.User, [
            new TextContent("look"),
            new UriContent(new Uri("http://images.local/slow.jpg")),
        ]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStreamingResponseAsync(
            [user], new ChatOptions { ModelId = "m" }, cts.Token).ToListAsync().AsTask());
    }

    // ------------------------------------------------------------------
    //  Model guard, base address, service, cancellation
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetResponseAsync_MissingModelId_ThrowsBeforeAnyRequest()
    {
        var handler = new FakeOllamaHandler();
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], null));
        Assert.Empty(handler.RequestPaths);
    }

    [Fact]
    public async Task GetResponseAsync_DefaultModelId_FallsBackToSelectedModel()
    {
        var handler = new FakeOllamaHandler(Json(
            """{"model":"llama3","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}"""));

        using var client = CreateClient(handler, defaultModelId: "llama3");
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null);

        Assert.Equal("llama3", (await ParseBodyAsync(handler)).GetProperty("model").GetString());
    }

    [Fact]
    public async Task GetStreamingResponse_SlashTerminatedPrefixedBase_ResolvesApiChatOnce()
    {
        // The public constructor uses the caller's HttpClient BaseAddress verbatim: with a
        // slash-terminated prefix (what the factory always produces) "api/chat" composes to
        // /ollama/api/chat (never /ollama/ollama/api/chat).
        var handler = new FakeOllamaHandler(Ndjson(
            """{"model":"m","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}"""));

        using var client = new OllamaChatClient(new HttpClient(handler) { BaseAddress = new Uri("http://daemon:11434/ollama/") });
        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m" }).ToListAsync();

        Assert.Equal("/ollama/api/chat", handler.RequestPaths.Single());
    }

    [Fact]
    public void GetService_ReturnsAdapterForIChatClientAndMetadataFromNativeClient()
    {
        using var client = CreateClient(new FakeOllamaHandler());

        // The adapter is the client consumers must hold — never bypass its normalizations.
        Assert.Same(client, client.GetService(typeof(IChatClient)));

        var metadata = Assert.IsType<ChatClientMetadata>(client.GetService(typeof(ChatClientMetadata)));
        Assert.Equal("ollama", metadata.ProviderName);
        Assert.Equal(new Uri(DaemonAddress), metadata.ProviderUri);
    }

    [Fact]
    public async Task GetResponseAsync_Cancellation_SurfacesUnmodified()
    {
        // A cancelled token aborts the request at the transport (mimicked here — a plain
        // HttpMessageHandler test double performs no token checks itself) and the
        // OperationCanceledException must surface unmodified: the middleware classifies it as a
        // client abort (no retry, no streak).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new CancellationAwareHandler();

        using var client = new OllamaChatClient(new HttpClient(handler) { BaseAddress = new Uri(DaemonAddress) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "m" }, cts.Token));
    }

    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    // ------------------------------------------------------------------
    //  Error semantics (RouterOllamaApiClient)
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStreamingResponse_Daemon400_ThrowsOllamaExceptionWithParsedError()
    {
        var handler = new FakeOllamaHandler(Json("""{"error":"model 'ghost' not found"}""", HttpStatusCode.BadRequest));

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<OllamaException>(() => client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "ghost" }).ToListAsync().AsTask());

        Assert.Equal("Ollama endpoint error (HTTP 400): model 'ghost' not found", ex.Message);
    }

    [Fact]
    public async Task GetStreamingResponse_Daemon400ToolsUnsupported_ThrowsModelDoesNotSupportToolsException()
    {
        var handler = new FakeOllamaHandler(Json("""{"error":"model 'tiny' does not support tools"}""", HttpStatusCode.BadRequest));

        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<ModelDoesNotSupportToolsException>(() => client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "tiny" }).ToListAsync().AsTask());
    }

    [Fact]
    public async Task GetStreamingResponse_Daemon400UnparseableBody_UsesRawSnippet()
    {
        var handler = new FakeOllamaHandler(Json("<html>bad gateway</html>", HttpStatusCode.BadRequest));

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<OllamaException>(() => client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "m" }).ToListAsync().AsTask());

        Assert.Contains("<html>bad gateway</html>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStreamingResponse_Daemon404_ThrowsHttpRequestExceptionWithStatusAndBody()
    {
        var handler = new FakeOllamaHandler(Json("""{"error":"model 'nope' not found"}""", HttpStatusCode.NotFound));

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "nope" }).ToListAsync().AsTask());

        // The body snippet IS surfaced (stock 5.5.2 loses it — the subclass restores parity).
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("Ollama endpoint error (HTTP 404): model 'nope' not found", ex.Message);
    }

    [Fact]
    public async Task GetResponseAsync_Daemon500NonJsonBody_ThrowsHttpRequestExceptionWithSnippet()
    {
        var handler = new FakeOllamaHandler(Json("<html>Internal Server Error</html>", HttpStatusCode.InternalServerError));

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "m" }));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("<html>Internal Server Error</html>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResponseAsync_TransportFailure_ThrowsNullStatusHttpRequestException()
    {
        var handler = new FakeOllamaHandler(throwException: new HttpRequestException("connection refused"));

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "m" }));

        Assert.Null(ex.StatusCode);
    }

    // ------------------------------------------------------------------
    //  Non-owning disposal contract
    // ------------------------------------------------------------------

    [Fact]
    public void Dispose_DoesNotDisposeTheCallerSuppliedHandler()
    {
        // Pooled / caller-owned transport: disposing the client (or the native client under it)
        // must NEVER tear down the shared handler.
        var handler = new FakeOllamaHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(DaemonAddress) };

        var client = new OllamaChatClient(httpClient);
        client.Dispose();

        Assert.False(handler.Disposed);
    }
}
