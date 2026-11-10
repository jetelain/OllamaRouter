using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace OllamaRouter.E2ETests.Scenarios;

/// <summary>
/// Scenarios exercising the OpenAI-compatible API surfaced by OllamaRouter, using the OpenAI
/// SDK and Microsoft.Extensions.AI.OpenAI.
/// </summary>
internal static class OpenAiCompatibleScenarios
{
    public static Scenario ListModels() => new("OpenAI-compatible /v1/models returns models", async config =>
    {
        var openAiClient = CreateOpenAiClient(config);
        var modelClient = openAiClient.GetOpenAIModelClient();

        var models = await modelClient.GetModelsAsync();

        return models.Value is { Count: > 0 }
            ? null
            : "Expected at least one model in the merged /v1/models response, but none were returned.";
    });

    public static Scenario ChatCompletion() => new("OpenAI-compatible /v1/chat/completions returns a completion", async config =>
    {
        var openAiClient = CreateOpenAiClient(config);
        IChatClient chatClient = openAiClient.GetChatClient(config.ModelName).AsIChatClient();

        var response = await chatClient.GetResponseAsync("Reply with exactly one word: OK");

        return string.IsNullOrWhiteSpace(response.Text)
            ? "Expected a non-empty completion from /v1/chat/completions, but got none."
            : null;
    });

    private static OpenAIClient CreateOpenAiClient(E2EConfig config)
    {
        // OllamaRouter does not require an API key, but the OpenAI SDK requires a non-empty one.
        var credential = new ApiKeyCredential("not-required");
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri($"{config.RouterUrl.TrimEnd('/')}/v1")
        };

        return new OpenAIClient(credential, options);
    }
}
