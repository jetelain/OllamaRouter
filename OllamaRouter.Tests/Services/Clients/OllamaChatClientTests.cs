using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Moq;
using OllamaRouter.Services.Clients;

namespace OllamaRouter.Tests.Services.Clients;

/// <summary>
/// Task #61: <see cref="OllamaChatClient"/> — the NativeAOT-safe <c>IChatClient</c> over an
/// Ollama daemon's <c>POST {base}/api/chat</c>. Covers the canonical → wire request mapping
/// (roles, text, tool calls, scalar options), the NDJSON streaming read (per-chunk updates +
/// trailing finish-reason/usage updates), the non-streaming response mapping, and the error
/// model (<c>OllamaHttpException</c> with/without a status code).
/// </summary>
public class OllamaChatClientTests
{
    private sealed class FakeOllamaHandler(HttpResponseMessage? response = null, Exception? throwException = null)
        : HttpMessageHandler
    {
        private List<string> _requestBodies = [];

        public List<string> RequestBodies => _requestBodies;

        public List<string> RequestPaths { get; } = [];
        public List<string?> RequestContentTypes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            RequestContentTypes.Add(request.Content?.Headers.ContentType?.ToString());
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
                Content = new StringContent("", Encoding.UTF8, "application/json")
            };
            if (effective.Content is null)
            {
                effective.Content = new StringContent("", Encoding.UTF8, "application/json");
            }

            return Task.FromResult(effective);
        }
    }

    private static OllamaChatClient CreateClient(FakeOllamaHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://daemon:11434") });

    [Fact]
    public async Task GetStreamingResponse_YieldsTextChunksThenFinishAndUsage()
    {
        const string payload =
            """
            {"model":"llama3","message":{"role":"assistant","content":"Hel"},"done":false}
            {"model":"llama3","message":{"role":"assistant","content":"lo"},"done":false}
            {"model":"llama3","done":true,"done_reason":"stop","prompt_eval_count":10,"eval_count":4}
            """;
        var handler = new FakeOllamaHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/x-ndjson")
            });

        using var client = CreateClient(handler);
        var updates = new List<ChatResponseUpdate>(
            await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
                new ChatOptions { ModelId = "llama3" }).ToListAsync());

        // Two text updates, then the finish-reason update, then the trailing usage update.
        Assert.Equal(4, updates.Count);
        var texts = updates.Take(2).Select(u => u.Contents.OfType<TextContent>().Single().Text).ToList();
        Assert.Equal(new[] { "Hel", "lo" }, texts);

        var finishUpdate = updates[2];
        Assert.Equal(ChatFinishReason.Stop, finishUpdate.FinishReason);

        var usageUpdate = updates[3];
        var usage = usageUpdate.Contents.OfType<UsageContent>().Single().Details;
        Assert.Equal(10, usage.InputTokenCount);
        Assert.Equal(4, usage.OutputTokenCount);
        Assert.Equal(14, usage.TotalTokenCount);

        // Outbound wire: stream=true, /api/chat, model + message round-trip.
        Assert.Equal("/api/chat", handler.RequestPaths.Single());
        using var doc = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("llama3", doc.RootElement.GetProperty("model").GetString());
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("hi", messages[0].GetProperty("content").GetString());
        // Unset optional fields are OMITTED from the wire body (never "options":null /
        // "tools":null / "tool_choice":null — the daemon and strict intermediaries expect
        // absence, not JSON null).
        Assert.False(doc.RootElement.TryGetProperty("options", out _));
        Assert.False(doc.RootElement.TryGetProperty("tools", out _));
        Assert.False(doc.RootElement.TryGetProperty("tool_choice", out _));
        Assert.False(doc.RootElement.TryGetProperty("keep_alive", out _));
    }

    [Fact]
    public async Task GetStreamingResponse_KeepAlivePassthroughFlowsIntoOptionsDictionary()
    {
        const string payload = """
            {"model":"m","message":{"role":"assistant","content":"ok"},"done":true}
            """;
        var handler = new FakeOllamaHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/x-ndjson")
            });

        using var client = CreateClient(handler);
        var options = new ChatOptions { ModelId = "m" };
        options.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["keep_alive"] = JsonSerializer.SerializeToElement("10m"),
        };
        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options).ToListAsync();

        // keep_alive round-trips through the options{} pass-through (the top-level field does
        // not exist on the Ollama wire shape).
        using var doc = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.Equal("10m", doc.RootElement.GetProperty("options").GetProperty("keep_alive").GetString());
    }

    [Fact]
    public async Task GetStreamingResponse_PrefixedBaseAddress_ResolvesApiChatOnce()
    {
        const string payload = """
            {"model":"m","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}
            """;
        var handler = new FakeOllamaHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/x-ndjson")
            });

        using var client = new OllamaChatClient(new HttpClient(handler) { BaseAddress = new Uri("http://daemon:11434/ollama") });
        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "m" }).ToListAsync();

        // A path-prefixed base composes to /ollama/api/chat (never /ollama/ollama/api/chat).
        Assert.Equal("/ollama/api/chat", handler.RequestPaths.Single());
    }

    [Fact]
    public async Task GetResponseAsync_NonStreaming_MapsTextUsageAndFinish()
    {
        const string payload = """
            {"model":"qwen2.5","message":{"role":"assistant","content":"Hi there"},"done":true,"done_reason":"length","prompt_eval_count":5,"eval_count":2}
            """;
        var handler = new FakeOllamaHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/x-ndjson")
            });

        using var client = CreateClient(handler);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "qwen2.5" });

        Assert.Equal("Hi there", response.Messages[0].Contents.OfType<TextContent>().Single().Text);
        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
        Assert.Equal(5, response.Usage?.InputTokenCount);
        Assert.Equal(2, response.Usage?.OutputTokenCount);
        Assert.Equal(7, response.Usage?.TotalTokenCount);

        using var doc = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task GetResponseAsync_MapsScalarOptionsAndSystemPrompt()
    {
        var handler = new FakeOllamaHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"model":"m","message":{"role":"assistant","content":"x"},"done":true}
                    """, Encoding.UTF8, "application/x-ndjson")
            });

        using var client = CreateClient(handler);
        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "you are terse"),
                new ChatMessage(ChatRole.User, "hi")
            ],
            new ChatOptions
            {
                ModelId = "m",
                Temperature = 0.5f,
                TopP = 0.9f,
                TopK = 20,
                MaxOutputTokens = 128,
                Seed = 42,
                StopSequences = ["\n"]
            });

        using var doc = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.Equal("system", doc.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        var options = doc.RootElement.GetProperty("options");
        Assert.Equal((double)0.5f, options.GetProperty("temperature").GetDouble());
        Assert.Equal((double)0.9f, options.GetProperty("top_p").GetDouble(), precision: 10);
        Assert.Equal(20, options.GetProperty("top_k").GetInt32());
        Assert.Equal(128, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(42, options.GetProperty("seed").GetInt64());
        Assert.Equal("\n", options.GetProperty("stop").GetString());
    }

    [Fact]
    public async Task GetStreamingResponse_MapsFunctionCallContentToToolCalls()
    {
        var handler = new FakeOllamaHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"model":"m","done":true}
                    """, Encoding.UTF8, "application/x-ndjson")
            });

        using var client = CreateClient(handler);
        var assistant = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("call-1", "get_weather", new Dictionary<string, object?>
            {
                ["city"] = "Berlin",
                ["days"] = 3
            })
        ]);

        await client.GetStreamingResponseAsync(
            [
                new ChatMessage(ChatRole.User, "weather?"),
                assistant,
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "{\"temp\":21}")])
            ],
            new ChatOptions { ModelId = "m" }).ToListAsync();

        using var doc = JsonDocument.Parse(handler.RequestBodies.Single());
        var messages = doc.RootElement.GetProperty("messages");
        // Assistant message: the tool_calls array with a JSON OBJECT argument.
        var toolCalls = messages[1].GetProperty("tool_calls");
        Assert.Equal("get_weather", toolCalls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, toolCalls[0].GetProperty("function").GetProperty("arguments").ValueKind);
        Assert.Equal("Berlin", toolCalls[0].GetProperty("function").GetProperty("arguments").GetProperty("city").GetString());
        // Tool result message: role "tool" with the stringified result content.
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("{\"temp\":21}", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetStreamingResponse_MapsFunctionToolsAndRequiredMode()
    {
        var handler = new FakeOllamaHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"model":"m","done":true}
                    """, Encoding.UTF8, "application/x-ndjson")
            });

        using var client = CreateClient(handler);
        var options = new ChatOptions
        {
            ModelId = "m",
            ToolMode = new RequiredChatToolMode("add"),
            Tools =
            [
                AIFunctionFactory.Create((int a, int b) => a + b,
                    new AIFunctionFactoryOptions { Name = "add", Description = "adds" })
            ]
        };

        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "1+2")], options).ToListAsync();

        using var doc = JsonDocument.Parse(handler.RequestBodies.Single());
        var tools = doc.RootElement.GetProperty("tools");
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal("add", tools[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("required", doc.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task GetStreamingResponse_UpstreamHttpError_ThrowsOllamaHttpExceptionWithStatus()
    {
        var handler = new FakeOllamaHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""
                    {"error":"model 'ghost' not found"}
                    """, Encoding.UTF8, "application/json")
            });

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<OllamaHttpException>(() => client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "ghost" }).ToListAsync());

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("model 'ghost' not found", ex.Message);
    }

    [Fact]
    public async Task GetResponseAsync_TransportFailure_ThrowsOllamaHttpExceptionWithoutStatus()
    {
        var handler = new FakeOllamaHandler(
            throwException: new HttpRequestException("connection refused"));

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<OllamaHttpException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "m" }));

        Assert.Null(ex.StatusCode);
    }

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
    public async Task GetStreamingResponse_Cancellation_SurfacesUnmodified()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new FakeOllamaHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"model":"m","done":true}
                    """, Encoding.UTF8, "application/x-ndjson")
            });

        using var client = CreateClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "m" }, cts.Token).ToListAsync());
    }
}
