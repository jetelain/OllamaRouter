using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;

namespace OllamaRouter.E2ETests.Scenarios;

/// <summary>
/// Scenarios exercising the native Ollama API surfaced by OllamaRouter, using OllamaSharp.
/// </summary>
internal static class OllamaNativeScenarios
{
    public static Scenario ListTags() => new("Ollama native /api/tags returns models", async config =>
    {
        using var client = new OllamaApiClient(new Uri(config.RouterUrl));

        var models = await client.ListLocalModelsAsync();

        return models.Any()
            ? null
            : "Expected at least one model in the merged /api/tags response, but none were returned.";
    });

    public static Scenario ListRunningModels() => new("Ollama native /api/ps returns a valid (possibly empty) list", async config =>
    {
        using var client = new OllamaApiClient(new Uri(config.RouterUrl));

        // /api/ps is expected to succeed even when no model is currently loaded.
        var runningModels = await client.ListRunningModelsAsync();

        return runningModels is null
            ? "Expected a non-null response from the merged /api/ps endpoint."
            : null;
    });

    public static Scenario Chat() => new("Ollama native /api/chat returns a completion", async config =>
    {
        using var client = new OllamaApiClient(new Uri(config.RouterUrl))
        {
            SelectedModel = config.ModelName
        };

        var request = new ChatRequest
        {
            Model = config.ModelName,
            Messages =
            [
                new Message(ChatRole.User, "Reply with exactly one word: OK")
            ],
            Stream = false
        };

        string? lastResponse = null;
        await foreach (var chunk in client.ChatAsync(request))
        {
            if (chunk?.Message?.Content is { Length: > 0 } content)
            {
                lastResponse = content;
            }
        }

        return string.IsNullOrWhiteSpace(lastResponse)
            ? "Expected a non-empty completion from /api/chat, but got none."
            : null;
    });
}
