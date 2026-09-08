namespace OllamaRouter.Options;

/// <summary>
/// Configuration options for the Ollama router, bound to the "OllamaRouter" section of appsettings.json.
/// </summary>
public sealed class OllamaRouterOptions
{
    public const string SectionName = "OllamaRouter";

    /// <summary>
    /// Per-model routing thresholds, keyed by model name (e.g. "llama3", "qwen3:8b").
    /// Models without an entry in this dictionary are always routed to the remote instance.
    /// </summary>
    public Dictionary<string, ModelThresholds> Models { get; set; } = new();

    /// <summary>
    /// Base address of the local Ollama instance (e.g. http://127.0.0.1:11435).
    /// </summary>
    public string LocalUrl { get; set; } = "";

    /// <summary>
    /// Base address of the remote Ollama instance (e.g. http://aiserver.local:11434).
    /// </summary>
    public string RemoteUrl { get; set; } = "";
}

/// <summary>
/// Routing thresholds for a single model.
/// </summary>
public sealed class ModelThresholds
{
    /// <summary>
    /// Maximum number of tokens allowed in a request for this model to the local Ollama instance. Requests exceeding this limit will be routed to the remote instance.
    /// </summary>
    public int MaxLocalTokens { get; set; } = 30000;

    /// <summary>
    /// Minimum amount of free VRAM (in MB) required to route a request for this model to the local Ollama instance.
    /// </summary>
    public int MinRequiredVramMB { get; set; } = 13500;
}
