using Microsoft.Extensions.Logging.Abstractions;

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
    /// <para>
    /// <b>Deprecated:</b> the per-model <see cref="ModelThresholds.MaxLocalTokens"/> and
    /// <see cref="ModelThresholds.MinRequiredVramMB"/> keys are superseded by the hardware
    /// suitability rules driven by the fleet <see cref="Targets"/> (<c>HardwareHint</c>/
    /// <c>MaxVramMB</c>) and are slated for removal after the transition release. They still
    /// work unchanged in this release.
    /// </para>
    /// </summary>
    public Dictionary<string, ModelThresholds> Models { get; set; } = new();

    /// <summary>
    /// Base address of the local Ollama instance (e.g. http://127.0.0.1:11435).
    /// <para>
    /// <b>Deprecated:</b> legacy two-tier configuration. When <see cref="Targets"/> is empty,
    /// a target named "Local" is synthesized from this URL; when <see cref="Targets"/> is
    /// configured, this property is only still consumed by the cloud-reclaim endpoints
    /// (full fleet-wide reclaim is a later release).
    /// </para>
    /// </summary>
    public string LocalUrl { get; set; } = "";

    /// <summary>
    /// Base address of the remote Ollama instance (e.g. http://aiserver.local:11434).
    /// <para>
    /// <b>Deprecated:</b> legacy two-tier configuration. When <see cref="Targets"/> is empty,
    /// a target named "Remote" is synthesized from this URL; when <see cref="Targets"/> is
    /// configured, this property is ignored by the proxy generation.
    /// </para>
    /// </summary>
    public string RemoteUrl { get; set; } = "";

    /// <summary>
    /// Named fleet targets of the router. Each entry produces one YARP route/cluster selectable
    /// via its <c>X-Ollama-Target</c> header value (the target <see cref="OllamaTargetOptions.Name"/>).
    /// <para>
    /// When this list is empty (the legacy configuration shape), the router falls back to
    /// synthesizing a "Local" and/or "Remote" target from <see cref="LocalUrl"/>/<see cref="RemoteUrl"/>
    /// (see <see cref="GetEffectiveTargets"/>), preserving the pre-fleet two-tier behavior.
    /// </para>
    /// </summary>
    public IList<OllamaTargetOptions> Targets { get; set; } = new List<OllamaTargetOptions>();

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

    /// <summary>
    /// Token estimation strategy: "Heuristic" (default, zero heap memory overhead) or "Tiktoken" (Microsoft.ML.Tokenizers cl100k_base).
    /// </summary>
    public string TokenEstimator { get; set; } = "Heuristic";

    /// <summary>
    /// Optional pricing configuration used to estimate savings from local/remote execution
    /// and the cost of cloud overflow. If not configured, cost estimates are not displayed.
    /// </summary>
    public PricingOptions? Pricing { get; set; }

    /// <summary>
    /// Number of consecutive failures on a target for adjacent requests within
    /// <see cref="ConsecutiveFailuresTokenTolerance"/> before switching to the next target.
    /// Defaults to 3. Set to 0 to disable automatic target switching.
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 3;

    /// <summary>
    /// Allowed difference in estimated prompt tokens between adjacent requests
    /// to consider them part of the same retry streak. Defaults to 4000.
    /// Set to 0 to require an exact match.
    /// </summary>
    public int ConsecutiveFailuresTokenTolerance { get; set; } = 4000;

    /// <summary>
    /// Optional recording mode, to investigate the difference between the estimated token count
    /// (used for routing) and the actual token count reported by the Ollama instances: each
    /// inference request is appended to a JSON Lines manifest and its full request body is saved
    /// as a pretty-printed payload file. Disabled by default.
    /// </summary>
    public RecordingOptions? Recording { get; set; }

    /// <summary>
    /// Configuration for the in-terminal live activity monitor.
    /// </summary>
    public TerminalMonitoringOptions TerminalMonitoring { get; set; } = new();

    /// <summary>
    /// Returns the effective fleet target list: the configured <see cref="Targets"/> in order
    /// (skipping invalid entries with a null/empty <c>Name</c> or <c>Url</c>, logging a warning
    /// for each skipped entry), or — when no targets are configured — a synthesized legacy list
    /// containing a "Local" target (from <see cref="LocalUrl"/>) and/or a "Remote" target (from
    /// <see cref="RemoteUrl"/>), each with <c>HardwareHint = "16gb"</c> and <c>MaxParallel = 1</>.
    /// Only non-empty URLs synthesize a target. The <c>Cloud</c> pseudo-target is NOT part of
    /// this list; it is always appended in code where needed.
    /// </summary>
    /// <param name="logger">
    /// Optional logger used to report skipped (invalid) <see cref="Targets"/> entries.
    /// </param>
    public IReadOnlyList<OllamaTargetOptions> GetEffectiveTargets(ILogger? logger = null)
    {
        if (Targets is { Count: > 0 })
        {
            var valid = new List<OllamaTargetOptions>(Targets.Count);
            foreach (var target in Targets)
            {
                if (string.IsNullOrWhiteSpace(target.Name) || string.IsNullOrWhiteSpace(target.Url))
                {
                    logger?.LogWarning(
                        "OllamaRouter:Targets entry was skipped because it has a missing Name or Url (Name: {Name}, Url: {Url}).",
                        target.Name,
                        target.Url);
                    continue;
                }

                valid.Add(target);
            }

            return valid;
        }

        // Legacy configuration: synthesize the well-known two-tier targets so that existing
        // configurations and X-Ollama-Target header values ("Local"/"Remote") keep working.
        var synthesized = new List<OllamaTargetOptions>(2);
        if (!string.IsNullOrWhiteSpace(LocalUrl))
        {
            synthesized.Add(new OllamaTargetOptions
            {
                Name = "Local",
                Url = LocalUrl,
                HardwareHint = "16gb",
                MaxParallel = 1,
                Enabled = true
            });
        }

        if (!string.IsNullOrWhiteSpace(RemoteUrl))
        {
            synthesized.Add(new OllamaTargetOptions
            {
                Name = "Remote",
                Url = RemoteUrl,
                HardwareHint = "16gb",
                MaxParallel = 1,
                Enabled = true
            });
        }

        return synthesized;
    }
}

/// <summary>
/// A single named fleet target of the router. One YARP route/cluster pair is generated per
/// effective target, and the request is directed to it via the <c>X-Ollama-Target</c> header
/// carrying the target <see cref="Name"/>.
/// </summary>
public sealed class OllamaTargetOptions
{
    /// <summary>
    /// Display/proxy name of the target; also the <c>X-Ollama-Target</c> header value used to
    /// select it (case-insensitive everywhere). Must be unique across the fleet.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Base address of the Ollama instance behind this target (e.g. http://192.168.1.25:11434).
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Coarse hardware class hint used to derive a default <c>MaxVramMB</c> when
    /// <see cref="MaxVramMB"/> is not set: "8gb" (8192 MB), "16gb" (16384 MB) or "96gb" (98304 MB),
    /// matched case-insensitively. Unknown or missing hints leave the effective VRAM unknown.
    /// </summary>
    public string? HardwareHint { get; set; }

    /// <summary>
    /// Explicit maximum VRAM (in MB) available on this target. Overrides the preset implied by
    /// <see cref="HardwareHint"/> when set.
    /// </summary>
    public int? MaxVramMB { get; set; }

    /// <summary>
    /// Maximum number of concurrent requests this target can serve. Defaults to 1.
    /// (Scheduling on this value arrives in a later release; it is only stored/normalized now.)
    /// </summary>
    public int MaxParallel { get; set; } = 1;

    /// <summary>
    /// Whether the target is active in the fleet. Defaults to true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Returns the effective maximum VRAM (in MB) of this target: the explicit
    /// <see cref="MaxVramMB"/> override, or the preset implied by <see cref="HardwareHint"/>
    /// ("8gb" → 8192, "16gb" → 16384, "96gb" → 98304, case-insensitive). Returns null when no
    /// override is set and the hint is missing or unknown ("unknown/no preset").
    /// </summary>
    public int? GetEffectiveMaxVramMB()
    {
        if (MaxVramMB.HasValue)
        {
            return MaxVramMB;
        }

        return HardwareHint?.Trim().ToLowerInvariant() switch
        {
            "8gb" => 8192,
            "16gb" => 16384,
            "96gb" => 98304,
            _ => null
        };
    }
}

/// <summary>
/// Configuration for the in-terminal live activity monitor.
/// </summary>
public sealed class TerminalMonitoringOptions
{
    /// <summary>
    /// Enables the in-terminal live activity monitor in the server console. Defaults to true.
    /// When false or when standard output is redirected, the console falls back to plain logging.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Refresh interval in seconds for the terminal UI. Defaults to 2.0 seconds.
    /// </summary>
    public double RefreshIntervalSeconds { get; set; } = 2.0;
}

/// <summary>
/// Configuration of the optional inference request recording mode.
/// </summary>
public sealed class RecordingOptions
{
    /// <summary>
    /// Enables the recording mode. Defaults to false (nothing is written to disk).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Directory where the records are stored: a "requests.jsonl" manifest plus one payload file
    /// per request containing the full request body. Defaults to the "inference-requests" folder
    /// in the local application data folder.
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// Maximum number of payload files to keep in the output directory; the oldest records are
    /// deleted once this count is exceeded (and the manifest is trimmed accordingly).
    /// Defaults to 500. Set to 0 to keep all files.
    /// </summary>
    public int MaxFiles { get; set; } = 500;
}

/// <summary>
/// Routing thresholds for a single model.
/// </summary>
public sealed class ModelThresholds
{
    /// <summary>
    /// Default value of <see cref="MaxLocalTokens"/> (30000).
    /// </summary>
    public const int DefaultMaxLocalTokens = 30000;

    /// <summary>
    /// Default value of <see cref="MinRequiredVramMB"/> (13500).
    /// </summary>
    public const int DefaultMinRequiredVramMB = 13500;

    /// <summary>
    /// Maximum number of tokens allowed in a request for this model to the local Ollama instance. Requests exceeding this limit will be routed to the remote instance.
    /// <remarks>
    /// Deprecated: superseded by the hardware suitability rules driven by the fleet targets
    /// (OllamaTargetOptions HardwareHint/MaxVramMB); slated for removal after the transition
    /// release. Still consumed by the local routing handler in this release.
    /// </remarks>
    /// </summary>
    public int MaxLocalTokens { get; set; } = DefaultMaxLocalTokens;

    /// <summary>
    /// Minimum amount of free VRAM (in MB) required to route a request for this model to the local Ollama instance.
    /// <remarks>
    /// Deprecated: superseded by the hardware suitability rules driven by the fleet targets
    /// (OllamaTargetOptions HardwareHint/MaxVramMB); slated for removal after the transition
    /// release. Still consumed by the local routing handler in this release.
    /// </remarks>
    /// </summary>
    public int MinRequiredVramMB { get; set; } = DefaultMinRequiredVramMB;

    /// <summary>
    /// Name of the equivalent model hosted on ollama.com cloud (e.g. "deepseek-v3.1:671b-cloud",
    /// "glm-4.6:cloud"), used as an overflow target when both Local and Remote are busy. The
    /// request is still forwarded to the Local instance, which is expected to be signed in to
    /// ollama.com and able to relay it to the cloud, but the "model" field of the request body is
    /// rewritten to this value. Leave null/empty to disable cloud overflow for this model.
    /// </summary>
    public string? CloudModel { get; set; }

    /// <summary>
    /// Explicit architecture pin: "Dense" or "Moe" (case-insensitive). Wins over all
    /// auto-detection for this model. "Dense" applies SmallDense/LargeDense rules from the
    /// parameter count (unknown count ⇒ LargeDense). Unknown values are ignored.
    /// </summary>
    public string? Architecture { get; set; }
}

/// <summary>
/// Pricing configuration used to estimate savings and cloud overflow costs.
/// </summary>
public sealed class PricingOptions
{
    /// <summary>
    /// Price per 1,000,000 prompt / input tokens (e.g. 0.20 for $0.20 / 1M tokens).
    /// </summary>
    public double? PromptPricePerMillion { get; set; }

    /// <summary>
    /// Alias for PromptPricePerMillion.
    /// </summary>
    public double? InputPricePerMillion
    {
        get => PromptPricePerMillion;
        set => PromptPricePerMillion = value;
    }

    /// <summary>
    /// Price per 1,000,000 completion / output tokens (e.g. 0.80 for $0.80 / 1M tokens).
    /// </summary>
    public double? CompletionPricePerMillion { get; set; }

    /// <summary>
    /// Alias for CompletionPricePerMillion.
    /// </summary>
    public double? OutputPricePerMillion
    {
        get => CompletionPricePerMillion;
        set => CompletionPricePerMillion = value;
    }

    /// <summary>
    /// Flat price per 1,000,000 tokens when input and output prices are not differentiated.
    /// </summary>
    public double? PricePerMillion { get; set; }

    /// <summary>
    /// Currency symbol or code to display (e.g. "$", "€", "USD"). Defaults to "$".
    /// </summary>
    public string Currency { get; set; } = "$";

    /// <summary>
    /// Returns true if at least one positive pricing rate is configured.
    /// </summary>
    public bool IsConfigured =>
        (PromptPricePerMillion.HasValue && PromptPricePerMillion.Value >= 0) ||
        (CompletionPricePerMillion.HasValue && CompletionPricePerMillion.Value >= 0) ||
        (PricePerMillion.HasValue && PricePerMillion.Value >= 0);
}
