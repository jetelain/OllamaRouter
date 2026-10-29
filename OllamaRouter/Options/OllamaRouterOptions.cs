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
    /// Directory for persisted state files (<c>targets.json</c>, <c>activity-statistics.json</c>).
    /// Defaults to null, which resolves to <c>%LOCALAPPDATA%\OllamaRouter\state</c> in production.
    /// </summary>
    public string? StateDirectory { get; set; }

    /// <summary>
    /// Directory for persisted config files (not yet used).
    /// Defaults to null, which resolves to <c>%LOCALAPPDATA%\OllamaRouter\config</c> in production.
    /// </summary>
    public string? ConfigDirectory { get; set; }

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
/// A quantized elastic context bucket profile — RETIRED by the two-bucket MoE ladder
/// (task #26). Kept bindable (and the <c>Profiles:*</c> config keys still bind) for
/// configuration compatibility, but the sizing engine no longer consults any profile
/// values: buckets now derive from the node's pre-configured context (optimal baseline)
/// and the model's native context limit (single extensible bucket).
/// </summary>
public sealed class DynamicContextProfile
{
    /// <summary>
    /// Creates a bucket profile from its three explicit bucket values (retired bucket
    /// triple; kept for configuration compatibility only).
    /// </summary>
    public DynamicContextProfile(int defaultContext, int? intermediateContext, int maxContext)
    {
        DefaultContext = defaultContext;
    #pragma warning disable CS0618 // Obsolete IntermediateContext is written for binding compatibility only.
        IntermediateContext = intermediateContext;
    #pragma warning restore CS0618
        MaxContext = maxContext;
    }

    /// <summary>
    /// Parameterless constructor for config binding of custom (operator-defined) profiles.
    /// </summary>
    public DynamicContextProfile()
    {
    }

    /// <summary>
    /// Baseline bucket (in tokens). RETIRED: the two-bucket ladder takes the baseline from
    /// the node's pre-configured context, never from a profile constant. Bound but ignored.
    /// </summary>
    public int DefaultContext { get; set; }

    /// <summary>
    /// Optional explicit intermediate bucket (in tokens). Obsolete (task #26): the
    /// two-bucket ladder has no intermediate bucket — the key still binds for
    /// configuration compatibility but is IGNORED by the sizing engine.
    /// </summary>
    [Obsolete("The two-bucket MoE ladder (task #26) has no intermediate bucket; this key is bound but ignored. The extensible bucket is the model's native context limit.")]
    public int? IntermediateContext { get; set; }

    /// <summary>
    /// Elastic ceiling bucket (in tokens). RETIRED: the two-bucket ladder takes the
    /// extensible limit from the model's native context length (optionally capped by
    /// <see cref="ModelThresholds.MaxContextTokens"/>), never from a profile constant.
    /// Bound but ignored.
    /// </summary>
    public int MaxContext { get; set; }
}

/// <summary>
/// Dynamic context sizing configuration (Phase 2, items 3-4 of the Multi-Target Zero-Config
/// Routing Plan; MoE-only two-bucket ladder after task #26): MoE requests pick between
/// EXACTLY two buckets — the node's pre-configured context (the "optimal" baseline bucket)
/// and the model's native context limit (the single "extensible" bucket, optionally capped
/// by <c>Models:{model}:MaxContextTokens</c>) — and <c>options.num_ctx</c> is injected
/// accordingly; dense requests get clamp-only pass-through (see
/// <see cref="DenseClampEnabled"/>). Base sizing is stateless per request; the Auto-Shrink
/// hysteresis options below are bound and stored here and CONSUMED in this release by the
/// Auto-Shrink Manager (<c>IContextAutoShrinkManager</c>, Phase 3 item 2), which retains an
/// expanded bucket across cooldown/idle windows before injecting a shrink turn.
/// </summary>
public sealed class DynamicContextOptions
{
    /// <summary>Default <see cref="BucketStepTokens"/> value (32,768; the grid is retired).</summary>
    public const int DefaultBucketStepTokens = 32_768;

    /// <summary>Built-in profile name for Mixture-of-Experts models (any VRAM class).</summary>
    public const string MoeProfileName = "Moe";

    /// <summary>
    /// RETIRED (task #26): built-in profile name for large dense models on desktop-class
    /// (16 GB) targets. Dense models are no longer profiled (clamp-only pass-through); a
    /// legacy config still declaring this profile binds but is ignored with a startup
    /// warning.
    /// </summary>
    [Obsolete("Dense models use clamp-only pass-through (task #26); dense profiles are retired. The key still binds but is ignored.")]
    public const string DenseDesktop16gbProfileName = "DenseDesktop16gb";

    /// <summary>
    /// RETIRED (task #26): built-in profile name for large dense models on server-class
    /// (96 GB) targets. Dense models are no longer profiled (clamp-only pass-through); a
    /// legacy config still declaring this profile binds but is ignored with a startup
    /// warning.
    /// </summary>
    [Obsolete("Dense models use clamp-only pass-through (task #26); dense profiles are retired. The key still binds but is ignored.")]
    public const string DenseServer96gbProfileName = "DenseServer96gb";

    /// <summary>
    /// RETIRED (task #26): built-in profile name for small dense models. Dense models are
    /// no longer profiled (clamp-only pass-through); a legacy config still declaring this
    /// profile binds but is ignored with a startup warning.
    /// </summary>
    [Obsolete("Dense models use clamp-only pass-through (task #26); dense profiles are retired. The key still binds but is ignored.")]
    public const string SmallDenseProfileName = "SmallDense";

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
    /// Grid step (in tokens) — OBSOLETE (task #26): the intermediate-bucket step grid was
    /// retired along with the profile table. The key still binds (legacy configs) but is
    /// IGNORED by the sizing engine; values ≤ 0 are still clamped to
    /// <see cref="DefaultBucketStepTokens"/> by <see cref="Normalize"/> for binding
    /// compatibility.
    /// </summary>
    [Obsolete("The two-bucket MoE ladder (task #26) removed the intermediate-bucket step grid; this key is bound but ignored.")]
    public int BucketStepTokens { get; set; } = DefaultBucketStepTokens;

    /// <summary>
    /// Cooldown (in turns) applied by the Auto-Shrink Manager after a context spike before the
    /// context may shrink back to the baseline bucket: while an expanded bucket is active, up to
    /// this many consecutive normal-size turns are RETAINED (the KV prefix cache is preserved)
    /// before a shrink turn is injected. Consumed in this release by the Auto-Shrink Manager
    /// (<c>IContextAutoShrinkManager</c>). Defaults to 3.
    /// </summary>
    public int AutoShrinkCooldownTurns { get; set; } = 3;

    /// <summary>
    /// Idle timeout (in seconds) the Auto-Shrink Manager waits before shrinking an active
    /// session's context: while an expanded bucket is active, the lease is retained until this
    /// much idle time has elapsed (detected at the next request - no background timer). Consumed
    /// in this release by the Auto-Shrink Manager (<c>IContextAutoShrinkManager</c>). Defaults to 60.
    /// </summary>
    public int AutoShrinkIdleTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Bucket profiles keyed by profile name (case-insensitive). RETIRED by the two-bucket
    /// MoE ladder (task #26): kept bindable for configuration compatibility (the
    /// <c>Profiles:*</c> keys still bind), but the sizing engine never consults profile
    /// values — buckets derive from the node's pre-configured context (baseline) and the
    /// model's native context limit (extensible). .NET config binding replaces the whole
    /// dictionary when any "Profiles:*" key is set, so a partial override drops the
    /// built-in — <see cref="Normalize"/> (invoked via PostConfigure/startup) restores the
    /// single built-in <see cref="MoeProfileName"/> entry.
    /// </summary>
    public Dictionary<string, DynamicContextProfile> Profiles { get; set; } = DefaultProfiles();

    /// <summary>
    /// The built-in profile names (case-insensitive) restored by <see cref="Normalize"/>.
    /// After task #26 only the <see cref="MoeProfileName"/> profile is built-in; the
    /// former dense profiles are no longer restored (legacy configs that still declare
    /// them bind but are ignored with a startup warning).
    /// </summary>
    public static IReadOnlyList<string> DefaultProfileNames { get; } =
        new[] { MoeProfileName };

    /// <summary>
    /// The legacy dense profile names retired by the two-bucket MoE ladder (task #26):
    /// used by <see cref="Normalize(ILogger?)"/> to detect and warn about legacy configs
    /// that still declare them.
    /// </summary>
    public static IReadOnlyList<string> LegacyRetiredProfileNames { get; } = BuildLegacyRetiredProfileNames();

    private static IReadOnlyList<string> BuildLegacyRetiredProfileNames()
    {
#pragma warning disable CS0618 // Legacy dense profile names are obsolete (referenced for the startup warning only).
        return new[]
        {
            DynamicContextOptions.DenseDesktop16gbProfileName,
            DynamicContextOptions.DenseServer96gbProfileName,
            DynamicContextOptions.SmallDenseProfileName
        };
#pragma warning restore CS0618
    }

    /// <summary>
    /// Looks up a profile by name (case-insensitive). Returns a stateless conservative
    /// fallback profile (baseline 32,768 / max 32,768) for unknown names. RETIRED bucket
    /// source (task #26): kept for configuration compatibility only — the sizing engine
    /// does not consult this lookup at request time.
    /// </summary>
    public DynamicContextProfile GetProfile(string profileName)
    {
        if (!string.IsNullOrWhiteSpace(profileName) &&
            Profiles is { } profiles &&
            profiles.TryGetValue(profileName, out var profile) &&
            profile is not null)
        {
            return profile;
        }

        return new DynamicContextProfile(32_768, null, 32_768);
    }

    /// <summary>
    /// Normalizes the section after config binding (no warnings): clamps a non-positive
    /// <see cref="BucketStepTokens"/> to <see cref="DefaultBucketStepTokens"/> and restores
    /// the missing or zero-valued built-in <see cref="MoeProfileName"/> profile. Never throws.
    /// </summary>
    public void Normalize()
    {
        Normalize(logger: null);
    }

    /// <summary>
    /// Normalizes the section after config binding, with optional startup logging:
    /// clamps a non-positive <see cref="BucketStepTokens"/> to
    /// <see cref="DefaultBucketStepTokens"/>, restores the missing or zero-valued built-in
    /// <see cref="MoeProfileName"/> profile, and — when <paramref name="logger"/> is
    /// provided — logs ONE warning naming any legacy dense profile
    /// (<see cref="LegacyRetiredProfileNames"/>) that a pre-task-#26 config still declares
    /// (they bind but are ignored by the two-bucket ladder; no behavior change). Never
    /// throws.
    /// </summary>
    public void Normalize(ILogger? logger)
    {
    #pragma warning disable CS0618 // BucketStepTokens is obsolete (bind-and-ignore, kept for compatibility).
        if (BucketStepTokens <= 0)
        {
            BucketStepTokens = DefaultBucketStepTokens;
        }
    #pragma warning restore CS0618

        Profiles ??= new Dictionary<string, DynamicContextProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in DefaultProfileNames)
        {
            if (!Profiles.TryGetValue(name, out var existing) || IsInvalid(existing))
            {
                Profiles[name] = GetDefaultProfile(name);
            }
        }

        if (logger is not null)
        {
            var legacy = Profiles.Keys
                .Where(name => LegacyRetiredProfileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (legacy.Count > 0)
            {
                logger.LogWarning(
                    "DynamicContext: legacy dense profile(s) '{Profiles}' are retired by the two-bucket MoE ladder (task #26) and will be ignored; dense models use clamp-only pass-through. Remove the legacy DenseDesktop16gb / DenseServer96gb / SmallDense profile keys to silence this warning.",
                    string.Join(", ", legacy));
            }
        }
    }

    private static bool IsInvalid(DynamicContextProfile? profile) =>
        profile is null || profile.DefaultContext <= 0 || profile.MaxContext <= 0;

    private static DynamicContextProfile GetDefaultProfile(string name) =>
    #pragma warning disable CS0618 // Obsolete IntermediateContext is written for binding compatibility only.
        // The bucket triple is RETIRED (task #26): the ladder no longer reads these
        // constants. The entry is restored so legacy 'GetProfile("Moe")' callers keep
        // a bound, non-null profile; values are historical (former Moe profile table).
        new DynamicContextProfile(184_320, 221_184, 262_144);
    #pragma warning restore CS0618

    private static Dictionary<string, DynamicContextProfile> DefaultProfiles() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [MoeProfileName] = GetDefaultProfile(MoeProfileName)
        };
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
    /// (OllamaTargetOptions HardwareHint/MaxVramMB) and the two-stage routing pipeline;
    /// slated for removal after the transition release.
    /// </remarks>
    /// </summary>
    public int MaxLocalTokens { get; set; } = DefaultMaxLocalTokens;

    /// <summary>
    /// Minimum amount of free VRAM (in MB) required to route a request for this model to the local Ollama instance.
    /// <remarks>
    /// Deprecated: superseded by the hardware suitability rules driven by the fleet targets
    /// (OllamaTargetOptions HardwareHint/MaxVramMB) and the two-stage routing pipeline;
    /// slated for removal after the transition release.
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
