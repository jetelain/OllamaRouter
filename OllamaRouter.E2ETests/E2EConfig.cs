namespace OllamaRouter.E2ETests;

/// <summary>
/// Configuration for the end-to-end test run, sourced from environment variables with
/// sensible defaults matching the OllamaRouter README setup instructions.
/// </summary>
internal sealed class E2EConfig
{
    /// <summary>
    /// Base URL of the OllamaRouter instance under test.
    /// </summary>
    public string RouterUrl { get; init; } = "http://localhost:11434";

    /// <summary>
    /// Name of the model expected to be available (aliased) on both the local and remote
    /// Ollama instances behind the router.
    /// </summary>
    public string ModelName { get; init; } = "Qwen3.8-27B:latest";

    public static E2EConfig FromEnvironment()
    {
        return new E2EConfig
        {
            RouterUrl = Environment.GetEnvironmentVariable("OLLAMAROUTER_E2E_URL") is { Length: > 0 } url
                ? url
                : "http://localhost:11434",
            ModelName = Environment.GetEnvironmentVariable("OLLAMAROUTER_E2E_MODEL") is { Length: > 0 } model
                ? model
                : "Qwen3.8-27B:latest"
        };
    }
}
