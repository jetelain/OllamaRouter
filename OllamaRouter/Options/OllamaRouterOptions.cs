using Microsoft.Extensions.Logging.Abstractions;

namespace OllamaRouter.Options;

/// <summary>
/// Configuration options for the Ollama router, bound to the "OllamaRouter" section of the
/// factory `appsettings.json` plus the optional user-owned `config/config.json` (next to the
/// executable; user values override factory defaults, standard sources override both).
/// </summary>
public sealed class OllamaRouterOptions
{
    public const string SectionName = "OllamaRouter";

    /// <summary>
    /// Per-model routing thresholds, keyed by model name (e.g. "llama3", "qwen3:8b").
    /// </summary>
    public Dictionary<string, ModelThresholds> Models { get; set; } = new();

    /// <summary>
    /// Base address of the local Ollama instance (e.g. http://127.0.0.1:11435).
    /// <para>
    /// <b>Deprecated:</b> legacy two-tier configuration. Use <see cref="Targets"/> instead.
    /// When <see cref="Targets"/> is empty, a target named "Local" is synthesized from this URL.
    /// </para>
    /// </summary>
    [Obsolete("Use Targets configuration instead.")]
    public string LocalUrl { get; set; } = "";

    /// <summary>
    /// Base address of the remote Ollama instance (e.g. http://aiserver.local:11434).
    /// <para>
    /// <b>Deprecated:</b> legacy two-tier configuration. Use <see cref="Targets"/> instead.
    /// When <see cref="Targets"/> is empty, a target named "Remote" is synthesized from this URL.
    /// </para>
    /// </summary>
    [Obsolete("Use Targets configuration instead.")]
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
    /// Directory for persisted state files (<c>targets.json</c>, <c>activity-statistics.json</c>).
    /// Defaults to null, which resolves to <c>%LOCALAPPDATA%\OllamaRouter\state</c> in production.
    /// </summary>
    public string? StateDirectory { get; set; }

    /// <summary>
    /// Multiplicative correction factor applied to the estimated token count to compensate for
    /// the systematic underestimation of the generic tokenizer compared to the actual tokenizer
    /// used by the targeted models (different vocabulary, chat template overhead, etc.).
    /// A value of 1.1 adds a 10% margin on top of the raw estimate. Defaults to 1.0 (no correction).
    /// </summary>
    public double TokenEstimationOverheadFactor { get; set; } = 1.0;

    /// <summary>
    /// Additive reasoning-fit margin (in tokens) added to the estimated prompt token count
    /// before qualifying a target for a request: a prompt of T estimated tokens needs
    /// <c>T + ContextFitMarginTokens ≤ CMax</c> (the target's pre-configured node context,
    /// falling back to the native GGUF context length, then the assumed 128k fallback) to be
    /// qualified. The margin reserves a typical high-reasoning/generation budget so the
    /// model's output tokens never overflow the node's context. Defaults to 8,192; for
    /// long-thinking agent models a larger value (e.g. 10,240) is recommended.
    /// Negative values are clamped to 0 by <see cref="Normalize"/>.
    /// This lives in the root <c>OllamaRouter</c> section (not under <c>DynamicContext</c>)
    /// because qualification happens even when <see cref="DynamicContextOptions.Enabled"/> is false.
    /// </summary>
    public int ContextFitMarginTokens { get; set; } = 8192;

    /// <summary>
    /// When true and all enabled fleet targets fail Stage-1 context qualification,
    /// the router dispatches the request to the online, capable target with the largest context
    /// capacity (Max CMax) rather than rejecting with a 503 error.
    /// </summary>
    public bool DispatchToLargestCapacityOnOverflow { get; set; } = true;

    /// <summary>
    /// When true (default), filters out models that are not native library models or local aliases
    /// (i.e. names containing a namespace slash such as "jetelain/*") from the fleet model list.
    /// This improves performance by skipping capability discovery for models not intended for fleet
    /// routing. Models explicitly declared in <see cref="Models"/> or configured for Cloud Overflow
    /// are never filtered out.
    /// </summary>
    public bool FilterNamespacedModels { get; set; } = true;

    /// <summary>
    /// Alias for <see cref="FilterNamespacedModels"/>.
    /// </summary>
    public bool? FilterNonNativeModels
    {
        get => FilterNamespacedModels;
        set
        {
            if (value.HasValue)
            {
                FilterNamespacedModels = value.Value;
            }
        }
    }

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
    /// Per-target failure streak threshold: a target whose uncommitted-transport and
    /// mid-stream failure streak exceeds this value is skipped as a routing candidate
    /// (by <see cref="Services.ITargetFailureStreakService"/>) until a success resets its
    /// streak. Defaults to 3. Set to 0 (or negative) to disable streak-based skipping.
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 3;

    /// <summary>
    /// Sliding TTL (in minutes) of the in-memory session-affinity store
    /// (<see cref="Services.ISessionAffinityService"/>): a session/agent lease pinned to a
    /// fleet target expires after this much inactivity (every hit re-arms the window).
    /// Defaults to 30. Set to 0 (or negative) to keep the safe default (the store normalizes
    /// non-positive values back to 30).
    /// </summary>
    public int SessionAffinityTtlMinutes { get; set; } = 30;

    /// <summary>
    /// Allowed difference in estimated prompt tokens between adjacent requests
    /// to consider them part of the same retry streak. Defaults to 4000.
    /// Set to 0 to require an exact match.
    /// </summary>
    /// <remarks>
    /// Deprecated: the streak-based failover (<see cref="Services.ITargetFailureStreakService"/>)
    /// no longer consults this value; it is kept for configuration binding only.
    /// </remarks>
    public int ConsecutiveFailuresTokenTolerance { get; set; } = 4000;

    /// <summary>
    /// Cloud Overflow (ollama.com relay) configuration: the static half of the cloud
    /// switch, the default cloud model name, and the preferred relay target used when
    /// the cloud-rewritten request must traverse a fleet node.
    /// </summary>
    public CloudOverflowOptions CloudOverflow { get; set; } = new();

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
    /// Configuration of the parallel fleet aggregation used by the unified catalog endpoints
    /// (GET /api/tags, /v1/models, /api/ps) and the model-directed /api/show endpoint.
    /// </summary>
    public AggregationOptions Aggregation { get; set; } = new();

    /// <summary>
    /// Dynamic context sizing configuration (Phase 2, MoE only after task #26): when
    /// enabled, MoE requests are sized on a two-bucket ladder (the node's pre-configured
    /// context as the optimal baseline bucket, the model's native context limit as the
    /// single extensible bucket) and <c>options.num_ctx</c> is injected accordingly; dense
    /// requests get clamp-only pass-through (an oversized static client <c>num_ctx</c> is
    /// clamped DOWN to the node's pre-configured context, never raised). Disabled by
    /// default.
    /// </summary>
    public DynamicContextOptions DynamicContext { get; set; } = new();

    /// <summary>
    /// Returns the effective fleet target list: the configured <see cref="Targets"/> in order
    /// (skipping invalid entries with a null/empty <c>Name</c> or <c>Url</c>, logging a warning
    /// for each skipped entry), or — when no targets are configured — a synthesized legacy list
    /// containing a "Local" target (from <see cref="LocalUrl"/>) and/or a "Remote" target (from
    /// <see cref="RemoteUrl"/>), each with <c>MaxParallel = 1</>.
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
#pragma warning disable CS0618
        if (!string.IsNullOrWhiteSpace(LocalUrl))
        {
            synthesized.Add(new OllamaTargetOptions
            {
                Name = "Local",
                Url = LocalUrl,
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
                MaxParallel = 1,
                Enabled = true
            });
        }
#pragma warning restore CS0618

        return synthesized;
    }

    /// <summary>
    /// Returns true when Cloud Overflow is statically enabled (<see cref="CloudOverflowOptions.Enabled"/>)
    /// and has at least one cloud model configured (either <see cref="CloudOverflowOptions.DefaultModel"/>
    /// is set or at least one model in <see cref="Models"/> has an explicit <see cref="ModelThresholds.CloudModel"/>).
    /// </summary>
    public bool IsCloudOverflowConfigured() =>
        CloudOverflow.Enabled &&
        (!string.IsNullOrEmpty(CloudOverflow.DefaultModel) ||
         (Models is { Count: > 0 } && Models.Values.Any(m => !string.IsNullOrEmpty(m.CloudModel))));

    /// <summary>
    /// Normalizes the root section after config binding: clamps a negative
    /// <see cref="ContextFitMarginTokens"/> to 0. Never throws.
    /// </summary>
    public void Normalize()
    {
        if (ContextFitMarginTokens < 0)
        {
            ContextFitMarginTokens = 0;
        }
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
    /// Optional base address of the OllamaRouter instance running on this node (e.g. <c>http://192.168.1.25:11434</c>).
    /// Used for router-to-router management operations such as hardware discovery (<c>/api/router/hardware</c>).
    /// When not set, falls back by convention to the same host on port 11434 only if <see cref="Url"/> is on port 11435.
    /// </summary>
    public string? RouterUrl { get; set; }

    /// <summary>
    /// Explicit maximum VRAM (in MB) available on this target. When not set, the effective
    /// VRAM is resolved dynamically from <see cref="Services.Hardware.IFleetHardwareService"/>.
    /// </summary>
    public int? MaxVramMB { get; set; }

    /// <summary>
    /// Static processing-speed hint: relative generation-throughput multiplier vs the fleet
    /// baseline (1.0 = fleet average / unset). Only values > 1.0 earn a Stage-2 SpeedBonus
    /// (the fastest-configured qualified node earns the full +150); non-finite values and
    /// values <= 1.0 collapse to an effective 1.0 (zero bonus) via <see cref="GetEffectiveSpeedFactor"/>.
    /// </summary>
    public double SpeedFactor { get; set; } = 1.0;

    /// <summary>
    /// The single read site for <see cref="SpeedFactor"/> clamping: non-finite values (NaN,
    /// +/-Infinity) or values <= 1.0 are treated as exactly 1.0 (no speed bonus); values > 1.0
    /// pass through unchanged. No Normalize() mutation - clamping is at read time only.
    /// </summary>
    public double GetEffectiveSpeedFactor() =>
        !double.IsFinite(SpeedFactor) || SpeedFactor <= 1.0 ? 1.0 : SpeedFactor;

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
    /// <see cref="MaxVramMB"/> override, or the <see cref="Services.Hardware.NodeHardwareProfile.TotalFastMemoryMB"/>
    /// from <paramref name="fleetHardware"/> if available. Returns null when no override is set
    /// and no profile is found.
    /// </summary>
    public int? GetEffectiveMaxVramMB(Services.Hardware.IFleetHardwareService? fleetHardware = null)
    {
        if (MaxVramMB.HasValue)
        {
            return MaxVramMB;
        }

        if (fleetHardware is not null)
        {
            return fleetHardware.GetEffectiveMaxVramMB(this);
        }

        return null;
    }

    /// <summary>
    /// Resolves the base URL of the OllamaRouter instance running on this target node.
    /// <para>
    /// Router-to-router management operations (such as hardware discovery via <c>/api/router/hardware</c>)
    /// must reach the OllamaRouter instance rather than the physical Ollama daemon.
    /// Priority:
    /// <list type="number">
    /// <item>Explicit <see cref="RouterUrl"/> if provided.</item>
    /// <item>Convention-based fallback: if <see cref="Url"/> is on port 11435 (the daemon port),
    /// the router is expected on port 11434 on the same host and scheme.</item>
    /// <item>Returns <c>null</c> if neither applies (e.g. standard Ollama daemon on port 11434 without a router).</item>
    /// </list>
    /// </para>
    /// </summary>
    public string? ResolveRouterUrl()
    {
        if (!string.IsNullOrWhiteSpace(RouterUrl))
        {
            return RouterUrl.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(Url) &&
            Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
            uri.Port == 11435)
        {
            var builder = new UriBuilder(uri) { Port = 11434, Path = string.Empty, Query = string.Empty };
            return builder.Uri.ToString().TrimEnd('/');
        }

        return null;
    }
}

/// <summary>
/// Configuration of the parallel fleet aggregation endpoints.
/// </summary>
public sealed class AggregationOptions
{
    /// <summary>
    /// Maximum time (in milliseconds) a single target may hold up the aggregated response
    /// (GET /api/tags, /v1/models, /api/ps and the /api/show fetch). When a target misses the
    /// budget, its last cached catalog is served instead (stale-while-revalidate), so an
    /// unresponsive daemon can never hang the IDE beyond this value. Defaults to 500.
    /// </summary>
    public int PerTargetTimeoutMs { get; set; } = 500;
}

/// <summary>
/// Dynamic context sizing configuration (MoE-only two-bucket ladder after task #26): MoE requests pick between
/// EXACTLY two buckets — the node's pre-configured context (the "optimal" baseline bucket)
/// and the model's native context limit (the single "extensible" bucket, optionally capped
/// by <c>Models:{model}:MaxContextTokens</c>) — and <c>options.num_ctx</c> is injected
/// accordingly; dense requests get clamp-only pass-through (see
/// <see cref="DenseClampEnabled"/>). Base sizing is stateless per request; the Auto-Shrink
/// hysteresis options below are consumed by the Auto-Shrink Manager (<c>IContextAutoShrinkManager</c>),
/// which retains an expanded bucket across cooldown/idle windows before injecting a shrink turn.
/// </summary>
public sealed class DynamicContextOptions
{
    public const string MoeProfileName = "Moe";

    /// <summary>Default baseline context fallback (32,768 tokens) when node has no pre-configured context.</summary>
    public const int DefaultBaselineTokens = 32_768;

    /// <summary>
    /// Enables dynamic context sizing. When false, the payload is NEVER rewritten (pass-through)
    /// and no sizing capability lookups are performed by the router middleware. Defaults to false.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// When true (default), a request landing on a target for a DENSE model rewrites
    /// <c>options.num_ctx</c> ONLY when the client's static value exceeds the node's
    /// pre-configured context, clamping it DOWN to that pre-configured value (one-time KV
    /// reallocation; prevents OOM from e.g. static 131,072 requests on 49,152 nodes). When
    /// the client value is absent or ≤ the pre-configured context the payload is untouched
    /// (no injection, no reallocation, no lease). Dense values are never raised, never
    /// bucketed, never leased. Set to false to make dense requests pure pass-through.
    /// </summary>
    public bool DenseClampEnabled { get; set; } = true;

    /// <summary>
    /// Cooldown (in turns) applied by the Auto-Shrink Manager after a context spike before the
    /// context may shrink back to the baseline bucket: while an expanded bucket is active, up to
    /// this many consecutive normal-size turns are RETAINED (the KV prefix cache is preserved)
    /// before a shrink turn is injected. Consumed by the Auto-Shrink Manager
    /// (<c>IContextAutoShrinkManager</c>). Defaults to 3.
    /// </summary>
    public int AutoShrinkCooldownTurns { get; set; } = 3;

    /// <summary>
    /// Idle timeout (in seconds) the Auto-Shrink Manager waits before shrinking an active
    /// session's context: while an expanded bucket is active, the lease is retained until this
    /// much idle time has elapsed (detected at the next request - no background timer). Consumed
    /// by the Auto-Shrink Manager (<c>IContextAutoShrinkManager</c>). Defaults to 60.
    /// </summary>
    public int AutoShrinkIdleTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Normalizes the section after config binding. Clamps non-positive cooldown and timeout values.
    /// </summary>
    public void Normalize() => Normalize(logger: null);

    /// <summary>
    /// Normalizes the section after config binding, with optional startup logging.
    /// Clamps non-positive cooldown and timeout values.
    /// </summary>
    public void Normalize(ILogger? logger)
    {
        if (AutoShrinkCooldownTurns < 0)
        {
            AutoShrinkCooldownTurns = 3;
        }

        if (AutoShrinkIdleTimeoutSeconds < 0)
        {
            AutoShrinkIdleTimeoutSeconds = 60;
        }
    }
}

/// <summary>
/// Cloud Overflow (ollama.com relay) configuration. Runtime availability is the AND of
/// <see cref="Enabled"/> here and the per-target Cloud toggle in targets.json
/// (<see cref="Services.ITargetAvailabilityService"/>); the model to rewrite to is taken from
/// the per-model <see cref="ModelThresholds.CloudModel"/> when set, falling back to
/// <see cref="DefaultModel"/>.
/// </summary>
public sealed class CloudOverflowOptions
{
    /// <summary>
    /// Enables the Cloud Overflow terminal fallback (the static half of the switch; the
    /// targets.json Cloud toggle must also be on). Defaults to true so existing deployments
    /// keep today's behavior.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default cloud model name used when the requested model has no per-model
    /// <see cref="ModelThresholds.CloudModel"/> configured. Null/empty means "no default":
    /// cloud overflow is only available for models with an explicit CloudModel.
    /// </summary>
    public string? DefaultModel { get; set; }

    /// <summary>
    /// Name of the fleet target preferred to relay the cloud-rewritten request to
    /// ollama.com (case-insensitive). The resolver's fallback chain (first online+enabled
    /// target, first enabled target, last-resort "Local") applies when the preferred relay is
    /// unknown, disabled, or offline. Defaults to "Local" (human-approved deviation from the
    /// plan's "desktop" default) so existing deployments keep today's relay selection
    /// byte-for-byte.
    /// </summary>
    public string PreferredRelayTarget { get; set; } = "Local";
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

    /// <summary>
    /// Optional operator cap (in tokens) on this model's effective capacity / MoE extensible
    /// limit across ALL nodes (e.g. a VRAM-tuned or Q8-lossless ceiling): the sizing engine
    /// and the Stage-1 MoE qualification use <c>min(native context ?? 128k, MaxContextTokens
    /// ?? ∞)</c>. Null (default) means "no cap". Non-positive values are ignored. This cap
    /// does NOT modify the per-target <c>TargetModelCapability.CMax</c> record — the
    /// MoE-vs-dense split lives in the sizing/qualification comparison, not in the capability.
    /// </summary>
    public int? MaxContextTokens { get; set; }
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
