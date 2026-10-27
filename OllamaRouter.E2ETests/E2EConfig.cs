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

    /// <summary>
    /// When true (OLLAMAROUTER_E2E_MESH=1), the live dual-router mesh scenario group runs
    /// against the real 3-node fleet (two physical routers on :11434, daemons on :11435).
    /// </summary>
    public bool MeshEnabled { get; init; }

    /// <summary>
    /// Base URL of the SECOND router in the live mesh (OLLAMAROUTER_E2E_MESH_URL_B).
    /// Router A is <see cref="RouterUrl"/>.
    /// </summary>
    public string RouterUrlB { get; init; } = "";

    /// <summary>
    /// Comma-separated target names expected in the fleet (OLLAMAROUTER_E2E_MESH_TARGETS).
    /// Used to validate the pin scenarios against the real node names.
    /// </summary>
    public string[] ExpectedTargets { get; init; } = [];

    public static E2EConfig FromEnvironment()
    {
        var expectedTargets = Environment.GetEnvironmentVariable("OLLAMAROUTER_E2E_MESH_TARGETS") is { Length: > 0 } targets
            ? targets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return new E2EConfig
        {
            RouterUrl = Environment.GetEnvironmentVariable("OLLAMAROUTER_E2E_URL") is { Length: > 0 } url
                ? url
                : "http://localhost:11434",
            ModelName = Environment.GetEnvironmentVariable("OLLAMAROUTER_E2E_MODEL") is { Length: > 0 } model
                ? model
                : "Qwen3.8-27B:latest",
            MeshEnabled = Environment.GetEnvironmentVariable("OLLAMAROUTER_E2E_MESH") == "1",
            RouterUrlB = Environment.GetEnvironmentVariable("OLLAMAROUTER_E2E_MESH_URL_B") is { Length: > 0 } urlB
                ? urlB
                : "",
            ExpectedTargets = expectedTargets
        };
    }
}
