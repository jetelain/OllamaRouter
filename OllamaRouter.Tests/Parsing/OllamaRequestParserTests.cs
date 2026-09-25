using OllamaRouter.Parsing;

namespace OllamaRouter.Tests.Parsing;

public class OllamaRequestParserTests
{
    [Fact]
    public void ExtractPrompt_ChatFormat_ReturnsLastMessageContent()
    {
        var body = """
        {
            "model": "llama3",
            "messages": [
                { "role": "user", "content": "Première question" },
                { "role": "assistant", "content": "Réponse" },
                { "role": "user", "content": "Dernière question" }
            ]
        }
        """;

        var prompt = OllamaRequestParser.ExtractPrompt(body);

        Assert.Equal("Dernière question", prompt);
    }

    [Fact]
    public void ExtractPrompt_GenerateFormat_ReturnsPrompt()
    {
        var body = """{ "model": "llama3", "prompt": "Explique la relativité" }""";

        var prompt = OllamaRequestParser.ExtractPrompt(body);

        Assert.Equal("Explique la relativité", prompt);
    }

    [Fact]
    public void ExtractPrompt_UnknownFormat_ReturnsEmptyString()
    {
        var body = """{ "model": "llama3" }""";

        var prompt = OllamaRequestParser.ExtractPrompt(body);

        Assert.Equal("", prompt);
    }

    [Fact]
    public void ExtractPrompt_InvalidJson_ReturnsEmptyString()
    {
        var prompt = OllamaRequestParser.ExtractPrompt("not-json");

        Assert.Equal("", prompt);
    }

    [Fact]
    public void ExtractContextText_ChatFormat_ConcatenatesAllMessages()
    {
        var body = """
        {
            "model": "llama3",
            "messages": [
                { "role": "user", "content": "Première question" },
                { "role": "assistant", "content": "Réponse" },
                { "role": "user", "content": "Dernière question" }
            ]
        }
        """;

        var context = OllamaRequestParser.ExtractContextText(body);

        Assert.Equal("Première question\nRéponse\nDernière question", context);
    }

    [Fact]
    public void ExtractContextText_ChatFormat_IncludesSystemPrompt()
    {
        var body = """
        {
            "model": "llama3",
            "system": "Tu es un assistant utile.",
            "messages": [
                { "role": "user", "content": "Bonjour" }
            ]
        }
        """;

        var context = OllamaRequestParser.ExtractContextText(body);

        Assert.Equal("Bonjour\nTu es un assistant utile.", context);
    }

    [Fact]
    public void ExtractContextText_GenerateFormat_ReturnsPromptWithSystem()
    {
        var body = """
        {
            "model": "llama3",
            "system": "Tu es un assistant utile.",
            "prompt": "Et maintenant ?"
        }
        """;

        var context = OllamaRequestParser.ExtractContextText(body);

        Assert.Equal("Et maintenant ?\nTu es un assistant utile.", context);
    }

    [Fact]
    public void ExtractContextText_UnknownFormat_ReturnsEmptyString()
    {
        var body = """{ "model": "llama3" }""";

        var context = OllamaRequestParser.ExtractContextText(body);

        Assert.Equal("", context);
    }

    [Fact]
    public void ExtractContextText_InvalidJson_ReturnsEmptyString()
    {
        var context = OllamaRequestParser.ExtractContextText("not-json");

        Assert.Equal("", context);
    }

    [Fact]
    public void ExtractContextText_MultiPartContent_ExtractsTextParts()
    {
        var body = """
        {
            "model": "llama3",
            "messages": [
                {
                    "role": "user",
                    "content": [
                        { "type": "text", "text": "Que vois-tu ?" },
                        { "type": "image_url", "image_url": { "url": "data:image/png;base64,AAAA" } }
                    ]
                }
            ]
        }
        """;

        var context = OllamaRequestParser.ExtractContextText(body);

        Assert.Equal("Que vois-tu ?", context);
    }

    [Fact]
    public void ExtractContextText_MultiPartContent_WithStringParts_ExtractsAllParts()
    {
        var body = """
        {
            "model": "llama3",
            "messages": [
                {
                    "role": "user",
                    "content": [ "Bonjour", "le monde" ]
                }
            ]
        }
        """;

        var context = OllamaRequestParser.ExtractContextText(body);

        Assert.Equal("Bonjour\nle monde", context);
    }

    [Fact]
    public void ExtractContextText_ToolCalls_IncludesToolCallPayload()
    {
        var body = """
        {
            "model": "llama3",
            "messages": [
                {
                    "role": "assistant",
                    "tool_calls": [
                        { "function": { "name": "get_weather", "arguments": "{\"city\":\"Paris\"}" } }
                    ]
                }
            ]
        }
        """;

        var context = OllamaRequestParser.ExtractContextText(body);

        Assert.Contains("get_weather", context);
        Assert.Contains("Paris", context);
    }

    [Fact]
    public void ExtractContextText_Tools_IncludesToolDefinitions()
    {
        var body = """
        {
            "model": "llama3",
            "messages": [
                { "role": "user", "content": "Quelle heure est-il ?" }
            ],
            "tools": [
                { "type": "function", "function": { "name": "get_time", "description": "Retourne l'heure courante." } }
            ]
        }
        """;

        var context = OllamaRequestParser.ExtractContextText(body);

        Assert.Contains("Quelle heure est-il ?", context);
        Assert.Contains("get_time", context);
        Assert.Contains("Retourne l'heure courante.", context);
    }

    [Fact]
    public void ExtractPriorContextTokenCount_GenerateFormat_ReturnsArrayLength()
    {
        var body = """
        {
            "model": "llama3",
            "prompt": "Continue",
            "context": [1, 2, 3, 4, 5]
        }
        """;

        var tokenCount = OllamaRequestParser.ExtractPriorContextTokenCount(body);

        Assert.Equal(5, tokenCount);
    }

    [Fact]
    public void ExtractPriorContextTokenCount_MissingContext_ReturnsZero()
    {
        var body = """{ "model": "llama3", "prompt": "Bonjour" }""";

        var tokenCount = OllamaRequestParser.ExtractPriorContextTokenCount(body);

        Assert.Equal(0, tokenCount);
    }

    [Fact]
    public void ExtractPriorContextTokenCount_InvalidJson_ReturnsZero()
    {
        var tokenCount = OllamaRequestParser.ExtractPriorContextTokenCount("not-json");

        Assert.Equal(0, tokenCount);
    }

    [Fact]
    public void ExtractModelName_ReturnsModel()
    {
        var body = """{ "model": "llama3", "prompt": "Bonjour" }""";

        var modelName = OllamaRequestParser.ExtractModelName(body);

        Assert.Equal("llama3", modelName);
    }

    [Fact]
    public void ExtractModelName_MissingModel_ReturnsEmptyString()
    {
        var body = """{ "prompt": "Bonjour" }""";

        var modelName = OllamaRequestParser.ExtractModelName(body);

        Assert.Equal("", modelName);
    }

    [Fact]
    public void ExtractModelName_InvalidJson_ReturnsEmptyString()
    {
        var modelName = OllamaRequestParser.ExtractModelName("not-json");

        Assert.Equal("", modelName);
    }

    [Fact]
    public void ParseCompletionRequest_ChatFormat_ExtractsAllFieldsInSinglePass()
    {
        var body = """
        {
            "model": "qwen2.5:7b",
            "messages": [
                { "role": "system", "content": "You are helpful." },
                { "role": "user", "content": "Hello!" }
            ],
            "context": [101, 102, 103]
        }
        """;

        var result = OllamaRequestParser.ParseCompletionRequest(body);

        Assert.Equal("qwen2.5:7b", result.ModelName);
        Assert.Equal(new[] { "You are helpful.", "Hello!" }, result.ContextParts);
        Assert.Equal(3, result.PriorContextTokenCount);
    }

    [Fact]
    public void ParseCompletionRequest_GenerateFormat_ExtractsAllFields()
    {
        var body = """
        {
            "model": "llama3:8b",
            "prompt": "Tell me a joke",
            "system": "Be funny"
        }
        """;

        var result = OllamaRequestParser.ParseCompletionRequest(body);

        Assert.Equal("llama3:8b", result.ModelName);
        Assert.Equal(new[] { "Tell me a joke", "Be funny" }, result.ContextParts);
        Assert.Equal(0, result.PriorContextTokenCount);
    }

    [Fact]
    public void ParseCompletionRequest_ReasoningOnMessage_IsIncludedInContextParts()
    {
        var body = """
        {
            "model": "qwen3.8-27b",
            "messages": [
                { "role": "user", "content": "Hello!" },
                { "role": "assistant", "content": "Hi there.", "reasoning": "Let me think about this." }
            ]
        }
        """;

        var result = OllamaRequestParser.ParseCompletionRequest(body);

        Assert.Equal(new[] { "Hello!", "Hi there.", "Let me think about this." }, result.ContextParts);
    }

    [Fact]
    public void ParseCompletionRequest_ReasoningAsContentObject_IsIncludedInContextParts()
    {
        var body = """
        {
            "model": "qwen3.8-27b",
            "messages": [
                { "role": "assistant", "content": "", "reasoning": { "content": "thinking as an object." } }
            ]
        }
        """;

        var result = OllamaRequestParser.ParseCompletionRequest(body);

        Assert.Equal(new[] { "thinking as an object." }, result.ContextParts);
    }

    [Fact]
    public void ParseCompletionRequest_NullReasoning_IsIgnored()
    {
        var body = """
        {
            "model": "qwen3.8-27b",
            "messages": [
                { "role": "assistant", "content": "Hi.", "reasoning": null }
            ]
        }
        """;

        var result = OllamaRequestParser.ParseCompletionRequest(body);

        Assert.Equal(new[] { "Hi." }, result.ContextParts);
    }

    [Fact]
    public void ParseCompletionRequest_InvalidJson_ReturnsEmptyInfo()
    {
        var result = OllamaRequestParser.ParseCompletionRequest("invalid-json");

        Assert.Equal("", result.ModelName);
        Assert.Empty(result.ContextParts);
        Assert.Equal(0, result.PriorContextTokenCount);
    }
}
