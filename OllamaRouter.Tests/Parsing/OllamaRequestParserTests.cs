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
}
