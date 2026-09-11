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

    /// <summary>
    /// Address the router itself listens on (e.g. http://localhost:11434, or http://0.0.0.0:11434
    /// to accept connections from other machines). Defaults to the standard Ollama port on
    /// localhost only.
    /// </summary>
    public string BindAddress { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Multiplicative correction factor applied to the estimated token count to compensate for
    /// the systematic underestimation of the generic tokenizer compared to the actual tokenizer
    /// used by the targeted models (different vocabulary, chat template overhead, etc.).
    /// A value of 1.1 adds a 10% margin on top of the raw estimate. Defaults to 1.0 (no correction).
    /// </summary>
    public double TokenEstimationOverheadFactor { get; set; } = 1.0;
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
