using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaRouter.Converters;

namespace OllamaRouter.Tests.Converters;

public partial class ProtocolConverterTests
{
    /// <summary>Tiny valid 1x1 transparent PNG.</summary>
    public const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    public static async ValueTask<(IList<ChatMessage> Messages, ChatOptions Options, string ModelName)> ParseAsync(
        IChatRequestConverter converter,
        string json)
    {
        var body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await converter.ParseChatRequestAsync(body, CancellationToken.None);
    }

    public static async Task<string> DrainPipeAsync(Pipe pipe)
    {
        var output = new StringBuilder();
        while (true)
        {
            var result = await pipe.Reader.ReadAsync(CancellationToken.None);
            foreach (var segment in result.Buffer)
            {
                output.Append(Encoding.UTF8.GetString(segment.Span));
            }

            if (result.IsCompleted)
            {
                break;
            }

            pipe.Reader.AdvanceTo(result.Buffer.End);
        }

        return output.ToString();
    }

    public static async Task<string> CaptureStreamAsync(
        Func<Stream, CancellationToken, ValueTask> write)
    {
        using var stream = new MemoryStream();
        await write(stream, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static async Task<string> CapturePipeAsync(
        Func<PipeWriter, ValueTask> write)
    {
        var pipe = new Pipe();
        var drain = DrainPipeAsync(pipe);
        await write(pipe.Writer);
        await pipe.Writer.CompleteAsync();
        return await drain;
    }


    private static string NormalizeTimestamp(string json)
        => System.Text.RegularExpressions.Regex.Replace(
            json,
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z",
            "TIMESTAMP");
}

public partial class ProtocolConverterTests
{
    public class ProtocolConverterJsonHelpersTests
    {
        [Theory]
        [InlineData(null, "stop")]
        [InlineData("stop", "stop")]
        [InlineData("length", "length")]
        [InlineData("tool_calls", "tool_calls")]
        [InlineData("content_filter", "content_filter")]
        [InlineData("MyCustomReason", "mycustomreason")]
        public void ToWireFinishReason_MapsToWireLiterals(string? reason, string expected)
        {
            ChatFinishReason? value = reason is null ? null : new ChatFinishReason(reason);
            Assert.Equal(expected, ProtocolConverterJsonHelpers.ToWireFinishReason(value));
        }

        [Fact]
        public void JsonToDictionary_ConvertsPrimitivesRecursively()
        {
            using var doc = JsonDocument.Parse("{\"a\":1,\"b\":1.5,\"c\":\"x\",\"d\":true,\"e\":null,\"f\":[1,\"two\"],\"g\":{\"h\":2}}");
            var dict = ProtocolConverterJsonHelpers.JsonToDictionary(doc.RootElement);

            Assert.Equal(7, dict.Count);
            Assert.Equal(1L, dict["a"]);
            Assert.Equal(1.5, (double)dict["b"]!);
            Assert.Equal("x", dict["c"]);
            Assert.Equal(true, dict["d"]);
            Assert.Null(dict["e"]);
            var array = Assert.IsType<List<object?>>(dict["f"]!);
            Assert.Equal(new object?[] { 1L, "two" }, array);
            var nested = Assert.IsType<Dictionary<string, object?>>(dict["g"]!);
            Assert.Equal(2L, nested["h"]);
        }

        [Fact]
        public void JsonToDictionary_NonObjectYieldsEmptyDictionary()
        {
            using var doc = JsonDocument.Parse("[1,2]");
            Assert.Empty(ProtocolConverterJsonHelpers.JsonToDictionary(doc.RootElement));
        }

        [Fact]
        public void Base64ToImageDataContent_DecodesToPngDataContent()
        {
            var content = ProtocolConverterJsonHelpers.Base64ToImageDataContent(TinyPngBase64);
            Assert.Equal("image/png", content.MediaType);
            Assert.Equal(Convert.FromBase64String(TinyPngBase64), content.Data.ToArray());
        }

        [Fact]
        public void Base64ToImageDataContent_InvalidBase64ThrowsFormatException()
        {
            Assert.Throws<FormatException>(() => ProtocolConverterJsonHelpers.Base64ToImageDataContent("not-base64!"));
        }

        [Fact]
        public void ExtractAssistantText_UsesLastAssistantMessageTextContents()
        {
            var response = new ChatResponse
            {
                Messages =
                [
                    new ChatMessage(ChatRole.Assistant, new AIContent[] { new TextContent("first "), new TextContent("answer") }),
                    new ChatMessage(ChatRole.User, "question"),
                ],
            };

            // The LAST assistant message is the first one here; its TextContent texts concatenate.
            Assert.Equal("first answer", ProtocolConverterJsonHelpers.ExtractAssistantText(response));

            var noAssistant = new ChatResponse { Messages = [new ChatMessage(ChatRole.User, "q")] };
            Assert.Equal(string.Empty, ProtocolConverterJsonHelpers.ExtractAssistantText(noAssistant));
        }
    }

    public class OllamaChatRequestParserTests
    {
        private readonly OllamaChatRequestParser _parser = new();

        [Fact]
        public async Task MultiTurnChat_RolesMapOneToOne_ContentsPreservedInOrder()
        {
            var (messages, _, modelName) = await ParseAsync(_parser, """
            {
              "model": "llama3",
              "messages": [
                {"role": "system", "content": "be terse"},
                {"role": "user", "content": "hi"},
                {"role": "assistant", "content": "hello"},
                {"role": "tool", "tool_name": "get_weather", "content": "sunny"}
              ]
            }
            """);

            Assert.Equal("llama3", modelName);
            Assert.Equal(4, messages.Count);
            Assert.Equal(ChatRole.System, messages[0].Role);
            Assert.Equal(ChatRole.User, messages[1].Role);
            Assert.Equal(ChatRole.Assistant, messages[2].Role);
            Assert.Equal(ChatRole.Tool, messages[3].Role);
            Assert.Equal("be terse", messages[0].Text);
            Assert.Equal("hi", messages[1].Text);
            Assert.Equal("hello", messages[2].Text);

            var result = Assert.IsType<FunctionResultContent>(Assert.Single(messages[3].Contents));
            Assert.Equal("get_weather", result.CallId);
            Assert.Equal("sunny", result.Result);
        }

        [Fact]
        public async Task UnknownRoleLiteral_BecomesCustomChatRole_WithoutThrowing()
        {
            var (messages, _, _) = await ParseAsync(_parser, """
            {"messages": [{"role": "narrator", "content": "once upon a time"}]}
            """);

            var message = Assert.Single(messages);
            Assert.Equal(new ChatRole("narrator"), message.Role);
        }

        [Fact]
        public async Task ChatImage_BecomesPngDataContent_TextFirst()
        {
            string body = """
            {"messages": [{"role": "user", "content": "look", "images": ["__IMG__"]}]}
            """.Replace("__IMG__", TinyPngBase64);
            var (messages, _, _) = await ParseAsync(_parser, body);

            var contents = Assert.Single(messages).Contents;
            Assert.Equal(2, contents.Count);
            Assert.Equal("look", Assert.IsType<TextContent>(contents[0]).Text);
            var image = Assert.IsType<DataContent>(contents[1]);
            Assert.Equal("image/png", image.MediaType);
            Assert.Equal(Convert.FromBase64String(TinyPngBase64), image.Data.ToArray());
        }

        [Fact]
        public async Task ChatToolCall_WithObjectArguments_BecomesFunctionCallContent()
        {
            var (messages, _, _) = await ParseAsync(_parser, """
            {
              "messages": [
                {"role": "assistant", "content": "", "tool_calls": [
                  {"function": {"name": "get_weather", "arguments": {"city": "Tokyo", "days": 3, "precise": 1.5, "ok": true}}}
                ]}
              ]
            }
            """);

            var message = Assert.Single(messages);
            Assert.Equal(ChatRole.Assistant, message.Role);
            var call = Assert.IsType<FunctionCallContent>(Assert.Single(message.Contents));
            Assert.Equal("get_weather", call.Name);
            Assert.False(string.IsNullOrEmpty(call.CallId)); // synthesized id
            Assert.Equal("Tokyo", call.Arguments!["city"]);
            Assert.Equal(3L, call.Arguments["days"]);
            Assert.Equal(1.5, (double)call.Arguments["precise"]!);
            Assert.Equal(true, call.Arguments["ok"]);
        }

        [Fact]
        public async Task Options_MapToCanonicalChatOptions_AndFullDictionaryPassesThrough()
        {
            var (_, options, _) = await ParseAsync(_parser, """
            {
              "model": "m",
              "options": {
                "temperature": 0.7,
                "top_p": 0.9,
                "top_k": 42,
                "num_predict": 128,
                "stop": ["\n", "END"],
                "seed": 7,
                "num_ctx": 4096,
                "repeat_penalty": 1.1,
                "stream": true,
                "keep_alive": "10m",
                "format": "json",
                "template": "{{ .Prompt }}"
              }
            }
            """);

            Assert.Equal(0.7f, options.Temperature);
            Assert.Equal(0.9f, options.TopP);
            Assert.Equal(42, options.TopK);
            Assert.Equal(128, options.MaxOutputTokens);
            Assert.Equal(new[] { "\n", "END" }, options.StopSequences);
            Assert.Equal(7L, options.Seed);

            // Entire options dictionary preserved (values are JsonElements) for downstream dispatch.
            var numCtx = Assert.IsType<JsonElement>(options.AdditionalProperties!["num_ctx"]);
            Assert.Equal(4096, numCtx.GetInt32());
            var keepAlive = Assert.IsType<JsonElement>(options.AdditionalProperties!["keep_alive"]);
            Assert.Equal("10m", keepAlive.GetString());
            Assert.Equal(12, options.AdditionalProperties!.Count);
        }

        [Fact]
        public async Task Generate_PromptAndSystem_YieldsSystemThenUser()
        {
            var (messages, _, modelName) = await ParseAsync(_parser, """
            {"model": "gen-model", "prompt": "write a poem", "system": "you are a poet"}
            """);

            Assert.Equal("gen-model", modelName);
            Assert.Equal(2, messages.Count);
            Assert.Equal(ChatRole.System, messages[0].Role);
            Assert.Equal("you are a poet", messages[0].Text);
            Assert.Equal(ChatRole.User, messages[1].Role);
            Assert.Equal("write a poem", messages[1].Text);
        }

        [Fact]
        public async Task Generate_SystemAsStringArray_YieldsMultipleSystemMessages()
        {
            var (messages, _, _) = await ParseAsync(_parser, """
            {"prompt": "p", "system": ["rule one", "rule two"]}
            """);

            Assert.Equal(3, messages.Count);
            Assert.Equal(ChatRole.System, messages[0].Role);
            Assert.Equal("rule one", messages[0].Text);
            Assert.Equal(ChatRole.System, messages[1].Role);
            Assert.Equal("rule two", messages[1].Text);
            Assert.Equal(ChatRole.User, messages[2].Role);
        }

        [Fact]
        public async Task Generate_Images_AppendedToUserMessage()
        {
            string body = """
            {"prompt": "describe", "images": ["__IMG__"]}
            """.Replace("__IMG__", TinyPngBase64);
            var (messages, _, _) = await ParseAsync(_parser, body);

            var contents = Assert.Single(messages).Contents;
            Assert.Equal(2, contents.Count);
            Assert.Equal("describe", Assert.IsType<TextContent>(contents[0]).Text);
            Assert.IsType<DataContent>(contents[1]);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"model\":\"m\"}")]
        public async Task KeepAlivePings_ParseCleanly_WithZeroMessages(string body)
        {
            var (messages, _, modelName) = await ParseAsync(_parser, body);
            Assert.Empty(messages);
            Assert.Equal(body == "{}" ? string.Empty : "m", modelName);
        }

        [Fact]
        public async Task LegacyNameAlias_IsHonored()
        {
            var (_, _, modelName) = await ParseAsync(_parser, "{\"name\":\"legacy-model\",\"prompt\":\"x\"}");
            Assert.Equal("legacy-model", modelName);
        }

        [Fact]
        public async Task StopAsSingleString_BecomesSingleSequence()
        {
            var (_, options, _) = await ParseAsync(_parser, "{\"options\":{\"stop\":\"END\"}}");
            Assert.Equal(new[] { "END" }, options.StopSequences);
        }

        [Fact]
        public async Task MalformedJson_ThrowsJsonException()
        {
            await Assert.ThrowsAsync<JsonException>(async () => await ParseAsync(_parser, "not json"));
        }

        [Fact]
        public async Task EmptyBody_ThrowsJsonException()
        {
            await Assert.ThrowsAsync<JsonException>(async () => await ParseAsync(_parser, ""));
        }
    }

    public class OpenAiChatRequestParserTests
    {
        private readonly OpenAIChatRequestParser _parser = new();

        [Fact]
        public async Task MultiTurnChat_DeveloperMapsToSystem_ToolCarriesCallId()
        {
            var (messages, _, modelName) = await ParseAsync(_parser, """
            {
              "model": "gpt-x",
              "messages": [
                {"role": "developer", "name": "sys", "content": "be brief"},
                {"role": "user", "content": "weather in tokyo?"},
                {"role": "assistant", "content": null, "tool_calls": [
                  {"id": "call_1", "function": {"name": "get_weather", "arguments": "{\"city\":\"Tokyo\"}"}}
                ]},
                {"role": "tool", "tool_call_id": "call_1", "content": "sunny"}
              ]
            }
            """);

            Assert.Equal("gpt-x", modelName);
            Assert.Equal(4, messages.Count);

            var developer = messages[0];
            Assert.Equal(ChatRole.System, developer.Role);
            Assert.Equal("sys", developer.AuthorName);
            Assert.Equal("be brief", developer.Text);

            var assistant = messages[2];
            var call = Assert.IsType<FunctionCallContent>(Assert.Single(assistant.Contents));
            Assert.Equal("call_1", call.CallId);
            Assert.Equal("get_weather", call.Name);
            Assert.Equal("Tokyo", call.Arguments!["city"]);

            var tool = messages[3];
            Assert.Equal(ChatRole.Tool, tool.Role);
            var result = Assert.IsType<FunctionResultContent>(Assert.Single(tool.Contents));
            Assert.Equal("call_1", result.CallId);
            Assert.Equal("sunny", result.Result);
        }

        [Fact]
        public async Task AssistantToolCall_InvalidArgumentString_YieldsEmptyDictionary_WithoutThrowing()
        {
            var (messages, _, _) = await ParseAsync(_parser, """
            {"messages": [
              {"role": "assistant", "tool_calls": [
                {"id": "c1", "function": {"name": "f", "arguments": "not-json"}},
                {"id": "c2", "function": {"name": "g", "arguments": ""}},
                {"id": "c3", "function": {"name": "h"}}
              ]}
            ]}
            """);

            var calls = Assert.Single(messages).Contents.OfType<FunctionCallContent>().ToList();
            Assert.Equal(3, calls.Count);
            Assert.Empty(calls[0].Arguments!);
            Assert.Empty(calls[1].Arguments!);
            Assert.Empty(calls[2].Arguments!);
        }

        [Fact]
        public async Task MultimodalContentParts_MapToTextDataAndUriContents()
        {
            string body = """
            {"messages": [
              {"role": "user", "content": [
                {"type": "text", "text": "what is this?"},
                {"type": "image_url", "image_url": {"url": "data:image/png;base64,__IMG__"}},
                {"type": "image_url", "image_url": {"url": "https://example.com/cat.png"}}
              ]}
            ]}
            """.Replace("__IMG__", TinyPngBase64);
            var (messages, _, _) = await ParseAsync(_parser, body);

            var contents = Assert.Single(messages).Contents;
            Assert.Equal(3, contents.Count);
            Assert.Equal("what is this?", Assert.IsType<TextContent>(contents[0]).Text);

            var data = Assert.IsType<DataContent>(contents[1]);
            Assert.Equal("image/png", data.MediaType);
            Assert.Equal(Convert.FromBase64String(TinyPngBase64), data.Data.ToArray());

            var uri = Assert.IsType<UriContent>(contents[2]);
            Assert.Equal(new Uri("https://example.com/cat.png"), uri.Uri);
        }

        [Fact]
        public async Task ReasoningContent_BecomesTextReasoningContent()
        {
            var (messages, _, _) = await ParseAsync(_parser, """
            {"messages": [
              {"role": "assistant", "content": "the answer is 42", "reasoning_content": "thinking..."}
            ]}
            """);

            var contents = Assert.Single(messages).Contents;
            Assert.Equal(2, contents.Count);
            Assert.Equal("thinking...", Assert.IsType<TextReasoningContent>(contents[0]).Text);
            Assert.Equal("the answer is 42", Assert.IsType<TextContent>(contents[1]).Text);
        }

        [Fact]
        public async Task Options_MapToCanonicalChatOptions()
        {
            var (_, options, _) = await ParseAsync(_parser, """
            {
              "temperature": 0.2,
              "top_p": 0.8,
              "max_completion_tokens": 100,
              "max_tokens": 50,
              "stop": "END",
              "seed": 99,
              "frequency_penalty": 0.5,
              "presence_penalty": 0.25,
              "top_k": 7,
              "response_format": {"type": "json_object"}
            }
            """);

            Assert.Equal(0.2f, options.Temperature);
            Assert.Equal(0.8f, options.TopP);
            Assert.Equal(100, options.MaxOutputTokens); // max_completion_tokens preferred
            Assert.Equal(new[] { "END" }, options.StopSequences);
            Assert.Equal(99L, options.Seed);
            Assert.Equal(0.5f, options.FrequencyPenalty);
            Assert.Equal(0.25f, options.PresencePenalty);
            Assert.Equal(7, options.TopK);
            Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat);
        }

        [Fact]
        public async Task MaxTokens_Alone_FallsBackToMaxOutputTokens_AndStopArraySupported()
        {
            var (_, options, _) = await ParseAsync(_parser, """
            {"max_tokens": 50, "stop": ["a", "b"], "response_format": {"type": "text"}}
            """);

            Assert.Equal(50, options.MaxOutputTokens);
            Assert.Equal(new[] { "a", "b" }, options.StopSequences);
            Assert.IsType<ChatResponseFormatText>(options.ResponseFormat);
        }

        [Fact]
        public async Task LegacyCompletionsPrompt_YieldsSingleUserMessage()
        {
            var (messages, _, modelName) = await ParseAsync(_parser, """
            {"model": "gpt-4", "prompt": "legacy prompt", "n": 2, "stream": true, "user": "u1"}
            """);

            Assert.Equal("gpt-4", modelName);
            var message = Assert.Single(messages);
            Assert.Equal(ChatRole.User, message.Role);
            Assert.Equal("legacy prompt", message.Text);
            }

            [Fact]
            public async Task MalformedJson_ThrowsJsonException()
            {
            await Assert.ThrowsAsync<JsonException>(async () => await ParseAsync(_parser, "not json"));
            }
            }

            public class OllamaNdjsonResponseEncoderTests
    {
        private readonly OllamaNdjsonResponseEncoder _encoder = new();

        [Fact]
        public void ContentType_IsNdjson() => Assert.Equal("application/x-ndjson", _encoder.ContentType);

        [Fact]
        public async Task StreamChunk_ProducesExactFrameShape()
        {
            var json = await CapturePipeAsync(writer => _encoder.WriteStreamChunkAsync(
                writer, new ChatResponseUpdate(ChatRole.Assistant, "hi there"), "m1", CancellationToken.None));

            var expected = """
                {"model":"m1","created_at":"TIMESTAMP","message":{"role":"assistant","content":"hi there"},"done":false}
                """ + "\n";
            Assert.Equal(expected, NormalizeTimestamp(json));
        }

        [Fact]
        public async Task StreamChunk_EscapesSpecialContent_RoundTrippingByteIdentically()
        {
            const string content = "He said \"hi\"\n\té🚀\\back";
            var raw = await CapturePipeAsync(writer => _encoder.WriteStreamChunkAsync(
                writer, new ChatResponseUpdate(null, content), "m", CancellationToken.None));

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            Assert.Equal(content, root.GetProperty("message").GetProperty("content").GetString());
        }

        [Theory]
        [InlineData(null, "stop")]
        [InlineData("stop", "stop")]
        [InlineData("length", "length")]
        [InlineData("tool_calls", "tool_calls")]
        public async Task StreamEnd_TerminalFrame_CarriesDoneReasonAndCounts(string? reason, string expectedWire)
        {
            var finishReason = reason is null ? (ChatFinishReason?)null : new ChatFinishReason(reason);
            var usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 };
            var duration = TimeSpan.FromTicks(1234560); // 123.456 ms

            var raw = await CapturePipeAsync(writer => _encoder.WriteStreamEndAsync(
                writer, "m1", usage, duration, finishReason, CancellationToken.None));

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("done").GetBoolean());
            Assert.Equal(expectedWire, root.GetProperty("done_reason").GetString());
            Assert.Equal("m1", root.GetProperty("model").GetString());
            Assert.Equal((duration.Ticks * 100), root.GetProperty("total_duration").GetInt64());
            Assert.Equal(10, root.GetProperty("prompt_eval_count").GetInt32());
            Assert.Equal(5, root.GetProperty("eval_count").GetInt32());
        }

        [Fact]
        public async Task StreamEnd_NullUsage_OmitsCountFields()
        {
            var raw = await CapturePipeAsync(writer => _encoder.WriteStreamEndAsync(
                writer, "m", null, TimeSpan.FromSeconds(1), null, CancellationToken.None));

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("done").GetBoolean());
            Assert.Equal("stop", root.GetProperty("done_reason").GetString());
            Assert.False(root.TryGetProperty("prompt_eval_count", out _));
            Assert.False(root.TryGetProperty("eval_count", out _));
        }

        [Fact]
        public async Task NonStreaming_ProducesSingleObjectWithLastAssistantText()
        {
            var completion = new ChatResponse
            {
                Messages =
                [
                    new ChatMessage(ChatRole.User, "q"),
                    new ChatMessage(ChatRole.Assistant, "full answer"),
                ],
                Usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 34 },
                FinishReason = ChatFinishReason.Length,
                ModelId = "ignored",
            };

            var duration = TimeSpan.FromMilliseconds(50);
            var raw = await CaptureStreamAsync(async (stream, ct) =>
                await _encoder.WriteNonStreamingResponseAsync(stream, completion, "m1", duration, ct));

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            Assert.Equal("m1", root.GetProperty("model").GetString());
            Assert.True(root.GetProperty("done").GetBoolean());
            Assert.Equal("length", root.GetProperty("done_reason").GetString());
            Assert.Equal("full answer", root.GetProperty("message").GetProperty("content").GetString());
            Assert.Equal(duration.Ticks * 100, root.GetProperty("total_duration").GetInt64());
            Assert.Equal(12, root.GetProperty("prompt_eval_count").GetInt32());
            Assert.Equal(34, root.GetProperty("eval_count").GetInt32());
            Assert.EndsWith("\n", raw);
        }

        [Fact]
        public async Task PipeRoundTrip_ThreeChunksPlusEnd_EveryLineIsValidNdjson()
        {
            var pipe = new Pipe();
            var drain = DrainPipeAsync(pipe);

            await _encoder.WriteStreamChunkAsync(pipe.Writer, new ChatResponseUpdate(ChatRole.Assistant, "a"), "m", CancellationToken.None);
            await _encoder.WriteStreamChunkAsync(pipe.Writer, new ChatResponseUpdate(null, "b"), "m", CancellationToken.None);
            await _encoder.WriteStreamChunkAsync(pipe.Writer, new ChatResponseUpdate(null, "c"), "m", CancellationToken.None);
            await _encoder.WriteStreamEndAsync(
                pipe.Writer, "m", new UsageDetails { InputTokenCount = 1, OutputTokenCount = 3 }, TimeSpan.FromSeconds(2), ChatFinishReason.Stop, CancellationToken.None);
            await pipe.Writer.CompleteAsync();

            var text = await drain;
            var lines = text.TrimEnd('\n').Split('\n');
            Assert.Equal(4, lines.Length);

            for (var i = 0; i < lines.Length; i++)
            {
                using var doc = JsonDocument.Parse(lines[i]);
                var root = doc.RootElement;
                Assert.Equal("m", root.GetProperty("model").GetString());
                Assert.Equal("assistant", root.GetProperty("message").GetProperty("role").GetString());
                Assert.True(root.TryGetProperty("created_at", out _));
            }

            Assert.Equal("a", JsonDocument.Parse(lines[0]).RootElement.GetProperty("message").GetProperty("content").GetString());
            Assert.Equal("b", JsonDocument.Parse(lines[1]).RootElement.GetProperty("message").GetProperty("content").GetString());
            Assert.Equal("c", JsonDocument.Parse(lines[2]).RootElement.GetProperty("message").GetProperty("content").GetString());

            var terminal = JsonDocument.Parse(lines[3]).RootElement;
            Assert.True(terminal.GetProperty("done").GetBoolean());
            Assert.Equal("stop", terminal.GetProperty("done_reason").GetString());
        }
    }

    public class OpenAiSseResponseEncoderTests
    {
        private static readonly System.Text.RegularExpressions.Regex IdPattern =
            new(@"^chatcmpl-[0-9a-f]{24}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        [Fact]
        public void ContentType_IsSse() => Assert.Equal("text/event-stream", new OpenAiSseResponseEncoder().ContentType);

        [Fact]
        public async Task StreamChunk_ProducesExactSseFrameShape()
        {
            var encoder = new OpenAiSseResponseEncoder();
            var raw = await CapturePipeAsync(writer => encoder.WriteStreamChunkAsync(
                writer, new ChatResponseUpdate(ChatRole.Assistant, "hello"), "gpt-x", CancellationToken.None));

            Assert.StartsWith("data: ", raw);
            Assert.EndsWith("\n\n", raw);
            var json = raw["data: ".Length..^2];

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Matches(IdPattern, root.GetProperty("id").GetString()!);
            Assert.Equal("chat.completion.chunk", root.GetProperty("object").GetString());
            Assert.Equal(JsonValueKind.Number, root.GetProperty("created").ValueKind);
            Assert.True(root.GetProperty("created").GetInt64() > 0);
            Assert.Equal("gpt-x", root.GetProperty("model").GetString());
            var choice = root.GetProperty("choices")[0];
            Assert.Equal(0, choice.GetProperty("index").GetInt32());
            Assert.Equal("assistant", choice.GetProperty("delta").GetProperty("role").GetString());
            Assert.Equal("hello", choice.GetProperty("delta").GetProperty("content").GetString());
            Assert.Equal(JsonValueKind.Null, choice.GetProperty("finish_reason").ValueKind);
        }

        [Fact]
        public async Task StreamChunk_WithoutRoleOrText_YieldsEmptyDelta()
        {
            var encoder = new OpenAiSseResponseEncoder();
            var raw = await CapturePipeAsync(writer => encoder.WriteStreamChunkAsync(
                writer, new ChatResponseUpdate(), "m", CancellationToken.None));

            var json = raw["data: ".Length..^2];
            using var doc = JsonDocument.Parse(json);
            var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
            Assert.Equal(JsonValueKind.Object, delta.ValueKind);
            var properties = delta.EnumerateObject().ToList();
            Assert.Empty(properties);
        }

        [Fact]
        public async Task StreamChunk_IdIsStableAcrossAllFramesOfOneStream()
        {
            var encoder = new OpenAiSseResponseEncoder();
            var pipe = new Pipe();
            var drain = DrainPipeAsync(pipe);
            await encoder.WriteStreamChunkAsync(pipe.Writer, new ChatResponseUpdate(null, "a"), "m", CancellationToken.None);
            await encoder.WriteStreamChunkAsync(pipe.Writer, new ChatResponseUpdate(null, "b"), "m", CancellationToken.None);
            await encoder.WriteStreamEndAsync(pipe.Writer, "m", null, TimeSpan.Zero, null, CancellationToken.None);
            await pipe.Writer.CompleteAsync();
            var text = await drain;

            var frameIds = text
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Where(frame => frame.StartsWith("data: ") && !frame.StartsWith("data: [DONE]"))
                .Select(frame => JsonDocument.Parse(frame["data: ".Length..]).RootElement.GetProperty("id").GetString())
                .Distinct()
                .ToList();

            var singleId = Assert.Single(frameIds);
            Assert.Matches(IdPattern, singleId);
        }

        [Fact]
        public async Task StreamEnd_WithUsage_FinishChunkThenUsageChunkThenDone()
        {
            var encoder = new OpenAiSseResponseEncoder();
            var raw = await CapturePipeAsync(writer => encoder.WriteStreamEndAsync(
                writer, "m", new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }, TimeSpan.FromSeconds(1),
                ChatFinishReason.Length, CancellationToken.None));

            var frames = raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).ToList();
            Assert.Equal(3, frames.Count);

            // 1. Finish chunk: empty delta + finish_reason.
            var finish = JsonDocument.Parse(frames[0]["data: ".Length..]).RootElement;
            Assert.Equal("chat.completion.chunk", finish.GetProperty("object").GetString());
            var finishChoice = finish.GetProperty("choices")[0];
            Assert.Equal(JsonValueKind.Object, finishChoice.GetProperty("delta").ValueKind);
            Assert.Equal("length", finishChoice.GetProperty("finish_reason").GetString());

            // 2. Usage chunk: empty choices + token counts.
            var usage = JsonDocument.Parse(frames[1]["data: ".Length..]).RootElement;
            Assert.Equal(0, usage.GetProperty("choices").GetArrayLength());
            Assert.Equal(10, usage.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
            Assert.Equal(5, usage.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
            Assert.Equal(15, usage.GetProperty("usage").GetProperty("total_tokens").GetInt32());

            // 3. Terminator.
            Assert.Equal("data: [DONE]", frames[2]);
        }

        [Fact]
        public async Task StreamEnd_WithoutUsage_SkipsUsageChunk()
        {
            var encoder = new OpenAiSseResponseEncoder();
            var raw = await CapturePipeAsync(writer => encoder.WriteStreamEndAsync(
                writer, "m", null, TimeSpan.Zero, null, CancellationToken.None));

            var frames = raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).ToList();
            Assert.Equal(2, frames.Count);
            var finish = JsonDocument.Parse(frames[0]["data: ".Length..]).RootElement;
            Assert.Equal("stop", finish.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
            Assert.Equal("data: [DONE]", frames[1]);
        }

        [Fact]
        public async Task NonStreaming_ProducesChatCompletionObject()
        {
            var encoder = new OpenAiSseResponseEncoder();
            var completion = new ChatResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, "the full text")],
                Usage = new UsageDetails { InputTokenCount = 3, OutputTokenCount = 4 },
                FinishReason = ChatFinishReason.Stop,
            };

            var raw = await CaptureStreamAsync(async (stream, ct) =>
                await encoder.WriteNonStreamingResponseAsync(stream, completion, "gpt-x", TimeSpan.Zero, ct));

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            Assert.Matches(IdPattern, root.GetProperty("id").GetString()!);
            Assert.Equal("chat.completion", root.GetProperty("object").GetString());
            Assert.Equal("gpt-x", root.GetProperty("model").GetString());
            var choice = root.GetProperty("choices")[0];
            Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
            Assert.Equal("the full text", choice.GetProperty("message").GetProperty("content").GetString());
            Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
            Assert.Equal(3, root.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
            Assert.Equal(4, root.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
            Assert.Equal(7, root.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        }

        [Fact]
        public async Task NonStreaming_NullUsage_OmitsUsageObject()
        {
            var encoder = new OpenAiSseResponseEncoder();
            var completion = new ChatResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, "t")],
            };

            var raw = await CaptureStreamAsync(async (stream, ct) =>
                await encoder.WriteNonStreamingResponseAsync(stream, completion, "m", TimeSpan.Zero, ct));

            using var doc = JsonDocument.Parse(raw);
            Assert.False(doc.RootElement.TryGetProperty("usage", out _));
        }

        [Fact]
        public async Task SseRoundTrip_EverySegmentStartsWithData_AndAllPayloadsParse()
        {
            var encoder = new OpenAiSseResponseEncoder();
            var pipe = new Pipe();
            var drain = DrainPipeAsync(pipe);
            await encoder.WriteStreamChunkAsync(pipe.Writer, new ChatResponseUpdate(ChatRole.Assistant, "one "), "m", CancellationToken.None);
            await encoder.WriteStreamChunkAsync(pipe.Writer, new ChatResponseUpdate(null, "two"), "m", CancellationToken.None);
            await encoder.WriteStreamEndAsync(
                pipe.Writer, "m", new UsageDetails { InputTokenCount = 1, OutputTokenCount = 2 }, TimeSpan.Zero, null, CancellationToken.None);
            await pipe.Writer.CompleteAsync();
            var text = await drain;

            var segments = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).ToList();
            Assert.True(segments.Count >= 3);
            Assert.All(segments, segment => Assert.StartsWith("data: ", segment));
            Assert.Equal("data: [DONE]", segments[^1]);

            foreach (var segment in segments.SkipLast(1))
            {
                using var doc = JsonDocument.Parse(segment["data: ".Length..]);
                Assert.Matches(IdPattern, doc.RootElement.GetProperty("id").GetString()!);
            }
        }
    }
}
