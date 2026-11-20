using Microsoft.Extensions.Logging.Abstractions;

namespace OllamaRouter.Options;

/// <summary>
/// Configuration options for the Ollama router, bound to the "OllamaRouter" section of the
/// factory `appsettings.json` plus the optional user-owned `config/config.json` (next to the
/// executable; user values override factory defaults, standard sources override both).
/// <para>
/// <b>Live vs startup rebinding boundary.</b> All production consumers read this options type
/// through <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c>, so behavioral options are LIVE at
/// runtime (an on-disk <c>config/config.json</c> change rebinds on the next dispatch / loop
/// iteration, no restart required): <c>MaxConsecutiveFailures</c>, <c>FailureStreakCooldownSeconds</c>,
/// <c>ResetStreakOnLivenessRecovery</c>, <c>SessionAffinityTtlMinutes</c>,
/// <c>SessionUsageMaxSessions</c>,
/// <c>SessionReservationWindowSeconds</c>, <c>OtherSessionReservationPenalty</c>,
/// <c>MaxParallelCliffPenalty</c>, <c>ContextFitMarginTokens</c>, <c>DispatchToLargestCapacityOnOverflow</c>,
/// <c>FilterNamespacedModels</c>, <c>TokenEstimationOverheadFactor</c>, <c>Models:{model}.*</c>
/// thresholds, <c>CloudOverflow.*</c>, <c>Recording.*</c> (incl. <c>TokenMetricsCsv</c>),
/// <c>Aggregation.PerTargetTimeoutMs</c>, <c>PreloadLoadTimeoutSeconds</c>, <c>DynamicContext.*</c>,
/// <c>TerminalMonitoring.RefreshIntervalSeconds</c>, <c>ApiSecurity.*</c>, <c>Pricing.*</c> and per-EXISTING-target
/// attributes (<c>Enabled</c>, <c>MaxParallel</c>, <c>MaxVramMB</c>, <c>SpeedFactor</c>,
/// <c>Location</c>, <c>PowerWatts</c>).
/// </para>
/// <para>
/// The following remain STARTUP-SCOPED (a process restart is required for changes to take
/// effect): fleet membership for the YARP passthrough paths (YARP routes/clusters are built
/// once from the startup snapshot via <c>LoadFromMemory</c>; YARP dynamic configuration is a
/// separate feature), per-target <c>Url</c>/<c>RouterUrl</c> (cluster destinations are fixed at
/// startup), <see cref="BindAddress"/> (the Kestrel listener binds
/// at startup), <c>TokenEstimator</c> engine selection (a DI-time singleton) and
/// <c>TerminalMonitoring.Enabled</c> (startup-only TUI toggle).
/// </para>
/// <para>
/// <b>Setup-API exception (task #19).</b> The dynamic fleet configuration / Setup API
/// (<c>POST</c>/<c>PUT</c>/<c>DELETE /api/router/targets</c>, <c>PUT /api/router/config</c>, persisted via
/// <c>IRouterConfigPersistence</c> to <c>config/config.json</c>) makes fleet membership and target
/// <c>Url</c> changes effective WITHOUT a restart on the inference (completion) dispatch path: the
/// routing middleware resolves its candidate list live from
/// <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;.CurrentValue.GetEffectiveTargets()</c> per request, so a
/// runtime-added, updated or removed target is immediately (un)routable for
/// <c>POST /api/chat|/api/generate|/v1/chat/completions|/v1/completions|/api/embeddings</c>. The
/// YARP passthrough paths (pinned <c>pull</c>/<c>create</c>/<c>copy</c>/<c>delete</c> and the default-class
/// pass-through) keep the startup route/cluster table until the follow-up "YARP dynamic
/// topology" task (rebuild via <c>IInMemoryConfigProvider</c>).
/// </para>
/// </summary>
public sealed class OllamaRouterOptions
{
    public const string SectionName = "OllamaRouter";

    /// <summary>
    /// The <see cref="PreloadLoadTimeoutSeconds"/> default (20 s).
    /// </summary>
    public const int DefaultPreloadLoadTimeoutSeconds = 20;

    /// <summary>
    /// The <see cref="SessionReservationWindowSeconds"/> default (30 s): the inter-turn gap
    /// (tool calls, compiles, I/O waits) during which a session's target stays "reserved".
    /// </summary>
    public const int DefaultSessionReservationWindowSeconds = 30;

    /// <summary>
    /// The <see cref="FailureStreakCooldownSeconds"/> default (60 s): the circuit-breaker
    /// window a tripped target stays skipped (circuit Open) before it becomes eligible for
    /// a single half-open trial request.
    /// </summary>
    public const int DefaultFailureStreakCooldownSeconds = 60;

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
    /// Last-resort preload keep-alive default (in seconds) for the model preload endpoints
    /// (<c>POST /api/router/models/{*modelName}/preload</c>): used by
    /// <see cref="OllamaTargetOptions.GetEffectivePreloadKeepAliveSeconds(OllamaRouterOptions, Hardware.IFleetHardwareService?)"/>
    /// ONLY when neither an explicit per-node <see cref="OllamaTargetOptions.KeepAliveSeconds"/>
    /// override nor the class convention (cloud / server-class = -1 indefinitely, desktop/
    /// laptop-class = 900 seconds / 15 min) applies — i.e. an unclassifiable node (unknown
    /// VRAM, no cloud location signal). Defaults to 900 (15 min). Values <= -2 are clamped to
    /// 900 by <see cref="Normalize"/>; -1 (indefinite) and 0 ("no extra retention beyond the
    /// Ollama default" policy) and positive values are kept as-is.
    /// </summary>
    public int PreloadDefaultKeepAliveSeconds { get; set; } = 900;

    /// <summary>
    /// Timeout (in seconds) that bounds ONLY the preload load call (<c>POST /api/generate</c>,
    /// <c>stream=false</c>, empty prompt) of the model preload endpoints
    /// (<c>POST /api/router/models/{*modelName}/preload</c>): a cold load of a large model (e.g.
    /// a 27B model with a large <c>num_ctx</c> and <c>keep_alive -1</c>) regularly exceeds the
    /// 4 s bound of the shared catalog client, so the load call rides a dedicated HttpClient
    /// whose timeout is this value. Every OTHER catalog call (tags/show/ps, catalog seeding on
    /// target add) keeps the 4 s catalog client bound, so a stalled peer daemon never slows
    /// routing or catalog fan-out. LIVE: the dedicated client reads this value from
    /// <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c> at each load call, so a
    /// <c>config/config.json</c> change applies to the NEXT load call without a restart.
    /// Defaults to <see cref="DefaultPreloadLoadTimeoutSeconds"/> (20 s); values &lt;= 0 are
    /// normalized to the default by <see cref="Normalize"/>. A load that still misses the bound
    /// is reported as not accepted (fail-soft, <c>accepted=false</c>), never a 5xx.
    /// </summary>
    public int PreloadLoadTimeoutSeconds { get; set; } = DefaultPreloadLoadTimeoutSeconds;

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
    /// (by <see cref="Services.Monitoring.ITargetFailureStreakService"/>) until a success resets its
    /// streak. Defaults to 3. Set to 0 (or negative) to disable streak-based skipping.
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 3;

    /// <summary>
    /// Failure-streak circuit-breaker cooldown (in seconds, task #94): once a target's streak
    /// exceeds <see cref="MaxConsecutiveFailures"/> (circuit Open), the target is skipped for
    /// this bounded window; after it elapses the circuit is HalfOpen and exactly ONE trial
    /// request may reach the target (a success closes the circuit, a failure re-arms the
    /// cooldown). Default 60. A NEGATIVE value normalizes to 60 by <see cref="Normalize"/>;
    /// 0 DISABLES the cooldown window (a tripped target is immediately half-open — the
    /// single-trial lease still bounds concurrency). LIVE: the effective window is read from
    /// <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c> per routing decision.
    /// </summary>
    public int FailureStreakCooldownSeconds { get; set; } = DefaultFailureStreakCooldownSeconds;

    /// <summary>
    /// When true (default, task #94), an Offline/Unknown → Online liveness transition resets
    /// the target's failure streak (<c>ResetStreak</c>), clearing a stale streak the moment a
    /// re-probed node rejoins the fleet. Transition-ONLY by design: probes on an already-Online
    /// target never reset (the breaker exists for probe-healthy daemons whose model streams
    /// drop). LIVE: read from <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c> per liveness
    /// transition.
    /// </summary>
    public bool ResetStreakOnLivenessRecovery { get; set; } = true;

    /// <summary>
    /// Sliding TTL (in minutes) of the in-memory session-affinity store
    /// (<see cref="Services.Routing.ISessionAffinityService"/>): a session/agent lease pinned to a
    /// fleet target expires after this much inactivity (every hit re-arms the window).
    /// Defaults to 30. Set to 0 (or negative) to keep the safe default (the store normalizes
    /// non-positive values back to 30).
    /// </summary>
    public int SessionAffinityTtlMinutes { get; set; } = 30;

    /// <summary>
    /// In-memory-only bound of the per-session usage LRU
    /// (<see cref="Services.Monitoring.ISessionUsageStore"/>): the maximum number of distinct
    /// public session IDs kept in memory at once. When a NEW session arrives beyond the cap,
    /// the LEAST-recently-active stored session is evicted (requests are never persisted to
    /// disk). Defaults to 1000. Non-positive values normalize back to 1000 by
    /// <see cref="Normalize"/> (re-applied on every config reload). LIVE: the cap is read from
    /// <c>IOptionsMonitor</c> on every recorded request, so a config change takes effect on the
    /// next recorded completion (a shrunk cap evicts down to the new bound).
    /// </summary>
    public int SessionUsageMaxSessions { get; set; } = 1000;

    /// <summary>
    /// Active-session reservation window (in seconds, task #77): a fleet target that served a
    /// session within this window is treated as RESERVED/contended when a DIFFERENT session
    /// evaluates it in Stage 2 (that candidate takes the
    /// <see cref="OtherSessionReservationPenalty"/>, keeping a new workload off a target whose
    /// KV cache is still hot for the other session). Covers the typical inter-turn agent gap
    /// (tool calls, compiles, I/O waits) during which <c>InFlightRequests == 0</c> but the
    /// target's KV cache still belongs to the previous session. Defaults to 30 (covers a
    /// typical agent tool loop); set to 0 to disable the reservation window entirely (the
    /// contention penalty never applies). Negative values normalize back to 30 by
    /// <see cref="Normalize"/> (re-applied on every config reload). LIVE.
    /// </summary>
    public int SessionReservationWindowSeconds { get; set; } = DefaultSessionReservationWindowSeconds;

    /// <summary>
    /// Stage-2 contention penalty (task #77): the fixed penalty applied to a candidate that is
    /// reserved by ANOTHER active session within <see cref="SessionReservationWindowSeconds"/>
    /// (the request carries a non-blank session ID and the target holds a fresh lease for a
    /// different session; the request's own lease is exempt — sticky affinity is preserved on
    /// the session's own target). Calibrated at 120 to flip the sign of a hot-but-reserved
    /// target (100 hot − 120 = −20 &lt; the idle cold candidate's 0). Set to 0 to disable the
    /// term; negative values normalize back to 120 by <see cref="Normalize"/>. LIVE.
    /// </summary>
    public int OtherSessionReservationPenalty { get; set; } = 120;

    /// <summary>
    /// Stage-2 concurrency CLIFF term (task #77 recalibration): added to the per-in-flight
    /// concurrency penalty (40 per in-flight request) when the target is at or above its
    /// <c>MaxParallel</c> cap (a saturated leased node must no longer beat an idle qualified
    /// candidate: 150 + 100 − (1 × 40 + 220) = −10 &lt; 0). Set to 0 to disable the cliff term;
    /// negative values normalize back to 220 by <see cref="Normalize"/>. LIVE.
    /// </summary>
    public int MaxParallelCliffPenalty { get; set; } = 220;

    /// <summary>
    /// Allowed difference in estimated prompt tokens between adjacent requests
    /// to consider them part of the same retry streak. Defaults to 4000.
    /// Set to 0 to require an exact match.
    /// </summary>
    /// <remarks>
    /// Deprecated: the streak-based failover (<see cref="Services.Monitoring.ITargetFailureStreakService"/>)
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
    /// API key security configuration (task #50): config-affecting endpoints are
    /// key-protected by default; <see cref="ApiSecurityOptions.RequireKeyForAllEndpoints"/>
    /// extends enforcement to every endpoint, and <see cref="ApiSecurityOptions.DefaultKeyRotationHours"/>
    /// controls the auto-rotation interval of the auto-generated default key. Both values are
    /// LIVE (read from <c>IOptionsMonitor</c> per request / per rotation tick).
    /// </summary>
    public ApiSecurityOptions ApiSecurity { get; set; } = new();

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
    /// <see cref="ContextFitMarginTokens"/> to 0; a negative
    /// <see cref="SessionReservationWindowSeconds"/>/<see cref="OtherSessionReservationPenalty"/>
    /// /<see cref="MaxParallelCliffPenalty"/> resets to its default (0 stays 0 = disabled).
    /// Never throws.
    /// </summary>
    public void Normalize()
    {
        if (ContextFitMarginTokens < 0)
        {
            ContextFitMarginTokens = 0;
        }

        // Task #77: reservation window / contention / cliff tuning — a negative value is
        // invalid (reset to the default); 0 is meaningful (the term is disabled).
        if (SessionReservationWindowSeconds < 0)
        {
            SessionReservationWindowSeconds = DefaultSessionReservationWindowSeconds;
        }

        if (OtherSessionReservationPenalty < 0)
        {
            OtherSessionReservationPenalty = 120;
        }

        if (MaxParallelCliffPenalty < 0)
        {
            MaxParallelCliffPenalty = 220;
        }

        // Per-session usage store bound (task #85): a non-positive cap is invalid → the
        // 1000-session default (the store is in-memory-only, so the bound must stay positive).
        if (SessionUsageMaxSessions <= 0)
        {
            SessionUsageMaxSessions = 1000;
        }

        // Failure-streak circuit-breaker cooldown (task #94): a negative window is invalid →
        // the 60 s default; 0 is meaningful (the cooldown window is disabled — tripped targets
        // are immediately half-open, single trial still bounded).
        if (FailureStreakCooldownSeconds < 0)
        {
            FailureStreakCooldownSeconds = DefaultFailureStreakCooldownSeconds;
        }

        // Preload keep-alive normalization: the router-level last-resort default keeps -1
        // (indefinite), 0 (no extra retention) and positive values; <= -2 clamps to 900.
        // Per-node overrides: 0 ("immediate unload" is NOT a preload intent) and <= -2 are
        // invalid → reset to null so the class convention takes over at resolution time.
        if (PreloadDefaultKeepAliveSeconds <= -2)
        {
            PreloadDefaultKeepAliveSeconds = 900;
        }

        // Preload load-call timeout (task #75): bounds ONLY the bounded preload load POST; a
        // non-positive value is invalid → the 20 s default.
        if (PreloadLoadTimeoutSeconds <= 0)
        {
            PreloadLoadTimeoutSeconds = DefaultPreloadLoadTimeoutSeconds;
        }

        foreach (var target in Targets)
        {
            if (target.KeepAliveSeconds is { } keepAlive && (keepAlive == 0 || keepAlive <= -2))
            {
                target.KeepAliveSeconds = null;
            }

            // Task #84: a negative / non-finite power draw is invalid → null so the
            // resolution chain (Pricing:DefaultNodePowerWatts → 150 W) takes over; 0
            // (a zero-consumption node) stays 0.
            if (target.PowerWatts is { } powerWatts && (!double.IsFinite(powerWatts) || powerWatts < 0))
            {
                target.PowerWatts = null;
            }
        }

        // Task #84: the electricity price / default node power knobs are nullable (null =
        // unset ⇒ the effective defaults 0.20 / 150 W apply at read time); negative or
        // non-finite values are invalid → null (IsConfigured is deliberately UNCHANGED —
        // the electricity price alone never surfaces the cost section).
        if (Pricing is { } pricing)
        {
            if (pricing.ElectricityPricePerKwh is { } price && (!double.IsFinite(price) || price < 0))
            {
                pricing.ElectricityPricePerKwh = null;
            }

            if (pricing.DefaultNodePowerWatts is { } defaultWatts && (!double.IsFinite(defaultWatts) || defaultWatts < 0))
            {
                pricing.DefaultNodePowerWatts = null;
            }
        }

        if (Recording?.TokenMetricsCsv is { } tokenMetricsCsv && tokenMetricsCsv.MaxFiles < 0)
        {
            tokenMetricsCsv.MaxFiles = 0;
        }

        ApiSecurity.Normalize();
    }
}

/// <summary>
/// Protocol family of a fleet target (task #61). Determines the dispatch path of the
/// completion request pipeline (<see cref="Middleware.OllamaRoutingMiddleware"/>):
/// <see cref="Ollama"/> targets are same-protocol HTTP peers of the Ollama daemon (the
/// YARP fast path for same-protocol traffic, <c>/api/*</c> semantics everywhere),
/// <see cref="OpenAI"/> targets speak the OpenAI-compatible HTTP API
/// (<c>/v1/chat/completions</c>, <c>/models</c>) and are reached either through the
/// same-protocol YARP pass-through (OpenAI-protocol inbound) or through the converter
/// path (<c>IChatClient</c> + protocol encoders) when the inbound protocol differs.
/// <see cref="OsNative"/> is reserved for OS/in-process model providers (tasks #63/#64):
/// such targets store and round-trip configuration but are never dispatch candidates.
/// A config file omitting the <c>Type</c> field binds to <see cref="Ollama"/> (enum
/// default + binder), so existing configurations are byte-identical.
/// </summary>
public enum TargetType
{
    /// <summary>Standard Ollama daemon (default; existing behavior byte-identical).</summary>
    Ollama = 0,

    /// <summary>OpenAI-compatible HTTP API (<c>/v1/chat/completions</c>, <c>/models</c>).</summary>
    OpenAI = 1,

    /// <summary>OS/in-process provider (reserved; NOT routable until tasks #63/#64).</summary>
    OsNative = 2
}

/// <summary>
/// One declared model of a non-Ollama target's synthetic catalog (task #61): the model
/// <see cref="Name"/> is the membership key for Stage-1 routing qualification (strict
/// when the target's <see cref="OllamaTargetOptions.Models"/> list is non-empty);
/// <see cref="ContextLength"/> and <see cref="Quantization"/> are optional informational
/// hints consumed by the fleet aggregation of task #62. Entries with blank names are
/// skipped by every consumer.
/// </summary>
public sealed class TargetModelConfig
{
    /// <summary>Declared model name (the routing qualification key).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional context-length hint (tokens); consumed by task #62.</summary>
    public int? ContextLength { get; set; }

    /// <summary>Optional quantization hint (e.g. "Q4_K_M"); consumed by task #62.</summary>
    public string? Quantization { get; set; }
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
    /// Optional explicit location classification override (<c>"local"</c> / <c>"remote"</c> /
    /// <c>"cloud"</c>, case-insensitive) that wins over the URL-based classification of
    /// <see cref="Services.Routing.TargetLocationClassifier"/>. Use it when a hostname resolves to a
    /// private IP but should be treated differently (or vice versa). Null (default) means the
    /// location is derived from <see cref="Url"/>.
    /// </summary>
    public Services.Routing.TargetLocation? Location { get; set; }

    /// <summary>
    /// Optional base address of the OllamaRouter instance running on this node (e.g. <c>http://192.168.1.25:11434</c>).
    /// Used for router-to-router management operations such as hardware discovery (<c>/api/router/hardware</c>).
    /// When not set, falls back by convention to the same host on port 11434 only if <see cref="Url"/> is on port 11435.
    /// </summary>
    public string? RouterUrl { get; set; }

    /// <summary>
    /// The ssh user used by the daemon-side explicit hardware probe
    /// (<c>POST /api/router/fleet/hardware/{targetName}/probe</c>, task #143) to reach this
    /// target's host (the host is derived from <see cref="Url"/>). KEY-BASED authentication
    /// ONLY (the ssh user's installed private key — no password support, no identity-file
    /// handling); null/absent = the target cannot be ssh-probed. When present the value must
    /// match <c>^[A-Za-z0-9._-]{1,64}$</c> (validated at the API edge).
    /// </summary>
    public string? SshUser { get; set; }

    /// <summary>
    /// The ssh port for the daemon-side explicit hardware probe (task #143); null/absent
    /// (or out of range) = the default port 22 (see <see cref="GetEffectiveSshPort"/>).
    /// </summary>
    public int? SshPort { get; set; }

    /// <summary>
    /// The effective ssh port for the probe: the configured <see cref="SshPort"/> when it is a
    /// valid 1–65535 value, otherwise the default 22 (defensive read for hand-edited
    /// config.json files).
    /// </summary>
    public int GetEffectiveSshPort()
    {
        var port = SshPort;
        return port is not null && port >= 1 && port <= 65535 ? port.Value : 22;
    }

    /// <summary>
    /// Protocol family of this target (task #61). Defaults to <see cref="TargetType.Ollama"/>
    /// — a configuration file omitting the field (or binding the value "Ollama") keeps the
    /// exact pre-fleet behavior. <see cref="TargetType.OpenAI"/> enables the converter dispatch
    /// path, the <c>GET /models</c> liveness probe and the eviction-penalty suppression;
    /// <see cref="TargetType.OsNative"/> is config-storable but unroutable (tasks #63/#64).
    /// </summary>
    public TargetType Type { get; set; } = TargetType.Ollama;

    /// <summary>
    /// Upstream provider bearer key of this target (task #61): in-memory this always carries
    /// the UNWRAPPED plaintext; at rest (<c>config/config.json</c>) it is stored
    /// <c>IKeyProtector</c>-wrapped (see <see cref="TargetApiKeyUnwrapPostProcessor"/> for the
    /// <c>v1:</c> + Base64 envelope and the corrupt-blob degradation). Only meaningful for
    /// <see cref="TargetType.OpenAI"/> targets; the raw key appears in no log line, telemetry
    /// header or API response (the Setup API exposes <c>apiKeyConfigured</c> only).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Informational provider label of this target (task #61), e.g. "openrouter" or "groq":
    /// surfaced in <c>ChatClientMetadata</c> (the provider name of the target's
    /// <c>IChatClient</c>) and in monitoring; it has NO routing effect (location classification
    /// stays <see cref="Services.Routing.TargetLocationClassifier"/>-driven). Null (default) = unset.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Declared model roster for non-Ollama targets (task #61): the synthetic Stage-1 catalog.
    /// A non-empty list is STRICT (a model not listed disqualifies the target from the
    /// request); an empty list (the default) is PERMISSIVE (the catalog check short-circuits
    /// to true — see <see cref="Services.Catalog.NonOllamaCatalogPolicy"/>) so a minimal
    /// <c>{name, url, apiKey}</c> OpenAI target stays routable for every model name.
    /// Ignored for <see cref="TargetType.Ollama"/> targets (their live catalog comes from
    /// <c>/api/tags</c>).
    /// </summary>
    public List<TargetModelConfig> Models { get; set; } = [];

    /// <summary>
    /// Optional explicit maximum VRAM (in MB) available on this target. When not set, the effective
    /// VRAM is resolved dynamically from <see cref="Services.Hardware.IFleetHardwareService"/>.
    /// </summary>
    public int? MaxVramMB { get; set; }

    /// <summary>
    /// Explicit preload keep-alive retention (in seconds) for this node, consumed by the model
    /// preload endpoints (<c>POST /api/router/models/{*modelName}/preload</c>):
    /// <c>-1</c> = resident indefinitely (Ollama <c>keep_alive: -1</c>), a positive value =
    /// that many seconds, <c>null</c> (default) = derive from the documented class convention
    /// (see <see cref="GetEffectivePreloadKeepAliveSeconds(OllamaRouterOptions, Hardware.IFleetHardwareService?)"/>):
    /// cloud-class nodes → -1 (indefinite), server-class nodes (effective VRAM ≥
    /// <c>DynamicContextSizingEngine.ServerClassVramThresholdMB</c>, 32,768 MB) → -1,
    /// desktop/laptop-class nodes → 900 (15 min), unclassifiable → the router-level
    /// <see cref="OllamaRouterOptions.PreloadDefaultKeepAliveSeconds"/>. An explicit value
    /// (including -1 on a desktop-class node) is honored verbatim — the class convention
    /// applies only when this is unset. 0 and values <= -2 are invalid preload intents:
    /// <see cref="OllamaRouterOptions.Normalize"/> resets them to null (fall-through to the
    /// convention).
    /// </summary>
    public int? KeepAliveSeconds { get; set; }

    /// <summary>
    /// Static processing-speed hint: relative generation-throughput multiplier vs the fleet
    /// baseline (1.0 = fleet average / unset). Only values > 1.0 earn a Stage-2 SpeedBonus
    /// (the fastest-configured qualified node earns the full +150); non-finite values and
    /// values <= 1.0 collapse to an effective 1.0 (zero bonus) via <see cref="GetEffectiveSpeedFactor"/>.
    /// </summary>
    public double SpeedFactor { get; set; } = 1.0;

    /// <summary>
    /// Explicit node power-draw override (in watts) for the electricity-cost leg of the
    /// monitor's actual-savings math (task #84):
    /// <c>electricityCost = (elapsedHours) × (watts / 1000) × ElectricityPricePerKwh</c>.
    /// <c>0</c> (a zero-consumption node) is valid. Null (default) = resolved by
    /// <see cref="GetEffectivePowerWatts(PricingOptions?)"/> (target override →
    /// <c>Pricing:DefaultNodePowerWatts</c> → 150 W). Negative or non-finite values are
    /// normalized to null by <see cref="OllamaRouterOptions.Normalize"/>. LIVE.
    /// </summary>
    public double? PowerWatts { get; set; }

    /// <summary>
    /// The single read site for <see cref="SpeedFactor"/> clamping: non-finite values (NaN,
    /// +/-Infinity) or values <= 1.0 are treated as exactly 1.0 (no speed bonus); values > 1.0
    /// pass through unchanged. No Normalize() mutation - clamping is at read time only.
    /// </summary>
    public double GetEffectiveSpeedFactor() =>
        !double.IsFinite(SpeedFactor) || SpeedFactor <= 1.0 ? 1.0 : SpeedFactor;

    /// <summary>
    /// Resolves the effective node power draw (in watts) for this target's electricity-cost
    /// leg (task #84): the explicit <see cref="PowerWatts"/> override (finite and &gt;= 0,
    /// including 0) wins verbatim, otherwise the global
    /// <see cref="PricingOptions.DefaultNodePowerWatts"/> of <paramref name="pricing"/>
    /// (finite and &gt;= 0) and, finally, <see cref="PricingOptions.DefaultPowerWatts"/>
    /// (150 W). Fail-safe: never throws.
    /// </summary>
    public double GetEffectivePowerWatts(PricingOptions? pricing = null)
    {
        if (PowerWatts is { } watts && double.IsFinite(watts) && watts >= 0)
        {
            return watts;
        }

        return pricing is not null
            ? pricing.GetEffectiveDefaultNodePowerWatts()
            : PricingOptions.DefaultPowerWatts;
    }

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
    /// Stored, free-form hardware hint (e.g. "8gb", "16gb", "96gb") surfaced by the Setup API
    /// (<c>GET /api/router/config</c>, <c>POST /api/router/targets</c>) for Web-UI display and
    /// persisted to <c>config/config.json</c> under <c>OllamaRouter:Targets</c>.
    /// NO v1 routing consumer: location classification stays URL/VRAM-driven
    /// (<see cref="Services.Routing.TargetLocationClassifier"/>), so the value influences no routing
    /// decision. Null (default) means unset.
    /// </summary>
    public string? HardwareHint { get; set; }

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
    /// Resolves the effective preload keep-alive (in seconds) for this node:
    /// an explicit <see cref="KeepAliveSeconds"/> (<c>-1</c> or positive) wins verbatim (the
    /// operator may pin -1 on a desktop-class node), otherwise the documented class convention
    /// (see <see cref="Services.Preload.PreloadKeepAlivePolicy"/>): cloud class → -1 (indefinite),
    /// server class (effective VRAM ≥ <see cref="Services.ContextSizing.DynamicContextSizingEngine.ServerClassVramThresholdMB"/>)
    /// → -1, desktop/laptop class (known VRAM below the threshold) → 900 (15 min),
    /// unclassifiable (unknown VRAM, not cloud) → the router-level
    /// <see cref="OllamaRouterOptions.PreloadDefaultKeepAliveSeconds"/> of
    /// <paramref name="routerOptions"/>. Fail-safe: never throws (invalid explicit values
    /// normalized away by <see cref="OllamaRouterOptions.Normalize"/> fall through to the
    /// convention). Delegates to
    /// <see cref="Services.Preload.PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(OllamaTargetOptions, OllamaRouterOptions, Hardware.IFleetHardwareService?)"/>
    /// (the shared single source of truth also serialized by the fleet/monitor residency-
    /// visibility surfaces) so the int and the <c>keepAliveSource</c> discriminator can never
    /// drift.
    /// </summary>
    public int GetEffectivePreloadKeepAliveSeconds(
        OllamaRouterOptions routerOptions,
        Services.Hardware.IFleetHardwareService? fleetHardware = null) =>
        Services.Preload.PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(this, routerOptions, fleetHardware).Seconds;

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
/// (<see cref="Services.Routing.ITargetAvailabilityService"/>); the model to rewrite to is taken from
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

    /// <summary>
    /// Optional CSV export of the heuristic structural token metrics (words, symbols, non-ASCII
    /// characters, character count) paired with the actual token counts reported by the Ollama
    /// instances, for calibrating the <c>HeuristicTokenEstimator</c> model-family coefficients.
    /// A null (absent) section means the export is disabled, independently of the full JSON
    /// payload recording mode above.
    /// </summary>
    public TokenMetricsCsvOptions? TokenMetricsCsv { get; set; }
    }

    /// <summary>
    /// Configuration of the optional token metrics CSV export: each recorded completion request
    /// produces one aggregate data row (structural text metrics from the prompt vs the actual
    /// prompt/response token counts evaluated by Ollama), written to a per-request CSV file and/or
    /// appended to a consolidated dataset CSV ready for statistical regression and profile tuning.
    /// </summary>
    public sealed class TokenMetricsCsvOptions
    {
        /// <summary>
        /// Enables the token metrics CSV export. Defaults to false (zero disk I/O).
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Directory where the CSV files are written. Defaults to null, which resolves to the
        /// "token-metrics" folder in the local application data folder
        /// (<c>%LOCALAPPDATA%\OllamaRouter\token-metrics</c> on Windows).
        /// </summary>
        public string? OutputDirectory { get; set; }

        /// <summary>
        /// Maximum number of per-request CSV files to keep in the output directory; the oldest
        /// <c>*_metrics.csv</c> files are deleted once this count is exceeded (the consolidated
        /// <c>token-metrics.csv</c> is never pruned). Defaults to 1000. Set to 0 to keep all files.
        /// </summary>
        public int MaxFiles { get; set; } = 1000;

        /// <summary>
        /// When true (default), each recorded request produces an individual CSV file containing a
        /// header row plus the single aggregate data row.
        /// </summary>
        public bool GenerateIndividualFiles { get; set; } = true;

        /// <summary>
        /// When true (default), each recorded request appends one row to the consolidated
        /// <c>token-metrics.csv</c> dataset in the output directory (the header row is written only
        /// when the file does not exist or is empty).
        /// </summary>
        public bool AppendConsolidatedFile { get; set; } = true;
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
    /// Price per 1,000,000 cached prompt / input tokens (the prompt-cache-hit subset of
    /// <see cref="PromptPricePerMillion"/>). Cloud providers price cached input tokens cheaper
    /// than fresh input tokens (e.g. 0.05 for $0.05 / 1M cached tokens). Null (default) means
    /// the cached rate is NOT differentiated: cached tokens fall back to
    /// <c>PromptPricePerMillion ?? PricePerMillion</c>. Alias: <see cref="CachedInputPricePerMillion"/>.
    /// </summary>
    public double? CachedPromptPricePerMillion { get; set; }

    /// <summary>
    /// Alias for CachedPromptPricePerMillion.
    /// </summary>
    public double? CachedInputPricePerMillion
    {
        get => CachedPromptPricePerMillion;
        set => CachedPromptPricePerMillion = value;
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
    /// Effective electricity price default (0.20 per kWh in the configured display currency;
    /// e.g. 0.20 USD/kWh ⇒ one hour of a 130 W node costs 0.026). Task #84.
    /// </summary>
    public const double DefaultElectricityPricePerKwh = 0.20;

    /// <summary>
    /// Effective default node power draw (150 W) applied when neither a per-target
    /// <see cref="OllamaTargetOptions.PowerWatts"/> override nor a
    /// <see cref="DefaultNodePowerWatts"/> override applies. Task #84.
    /// </summary>
    public const double DefaultPowerWatts = 150.0;

    /// <summary>
    /// Electricity price per kilowatt-hour (e.g. 0.20 = $0.20/kWh in the display currency)
    /// used by the monitor's actual-savings math (task #84):
    /// <c>electricityCost = (elapsedSeconds / 3600) × (watts / 1000) × ElectricityPricePerKwh</c>.
    /// Null (default) = the <see cref="DefaultElectricityPricePerKwh"/> (0.20) applies via
    /// <see cref="GetEffectiveElectricityPricePerKwh"/>. Negative or non-finite values are
    /// normalized to null by <see cref="OllamaRouterOptions.Normalize"/>. Setting this ALONE
    /// does NOT surface the cost section — <see cref="IsConfigured"/> is deliberately
    /// unchanged (a token price rate must still be configured). LIVE.
    /// </summary>
    public double? ElectricityPricePerKwh { get; set; }

    /// <summary>
    /// Global default node power draw (in watts) applied to every non-Cloud fleet target
    /// WITHOUT its own <see cref="OllamaTargetOptions.PowerWatts"/> override (task #84).
    /// Null (default) = the <see cref="DefaultPowerWatts"/> (150 W) applies via
    /// <see cref="GetEffectiveDefaultNodePowerWatts"/>. Negative or non-finite values are
    /// normalized to null by <see cref="OllamaRouterOptions.Normalize"/>. LIVE.
    /// </summary>
    public double? DefaultNodePowerWatts { get; set; }

    /// <summary>
    /// Resolves the effective electricity price (display currency per kWh): the explicit
    /// <see cref="ElectricityPricePerKwh"/> (finite and &gt;= 0), otherwise the
    /// <see cref="DefaultElectricityPricePerKwh"/> (0.20). Fail-safe: never throws.
    /// </summary>
    public double GetEffectiveElectricityPricePerKwh() =>
        ElectricityPricePerKwh is { } price && double.IsFinite(price) && price >= 0
            ? price
            : DefaultElectricityPricePerKwh;

    /// <summary>
    /// Resolves the effective default node power draw (in watts): the explicit
    /// <see cref="DefaultNodePowerWatts"/> (finite and &gt;= 0), otherwise the
    /// <see cref="DefaultPowerWatts"/> (150 W). Fail-safe: never throws.
    /// </summary>
    public double GetEffectiveDefaultNodePowerWatts() =>
        DefaultNodePowerWatts is { } watts && double.IsFinite(watts) && watts >= 0
            ? watts
            : DefaultPowerWatts;

    /// <summary>
    /// Returns true if at least one positive pricing rate is configured.
    /// </summary>
    public bool IsConfigured =>
        (PromptPricePerMillion.HasValue && PromptPricePerMillion.Value >= 0) ||
        (CompletionPricePerMillion.HasValue && CompletionPricePerMillion.Value >= 0) ||
        (CachedPromptPricePerMillion.HasValue && CachedPromptPricePerMillion.Value >= 0) ||
        (PricePerMillion.HasValue && PricePerMillion.Value >= 0);
}

/// <summary>
/// API key security configuration (task #50): the router exposes an API-key system so
/// config-affecting endpoints (GET/PUT /api/router/config, POST/PUT/DELETE
/// /api/router/targets, PUT /api/router/fleet/hardware/{targetName},
/// POST /api/router/benchmark, DELETE /api/router/benchmark/results and the key
/// management endpoints) cannot be edited unauthenticated. An auto-generated DEFAULT key is
/// stored in config/default-key.dat (DPAPI-wrapped on Windows, 0600 on Unix) and is
/// auto-rotated every <see cref="DefaultKeyRotationHours"/> hours (no grace period); operators
/// can create additional, duration-limited user keys (hashes only, in
/// config/api-keys.json) via the /api/router/api-keys endpoints.
/// </summary>
public sealed class ApiSecurityOptions
{
    /// <summary>
    /// When true (default false), the API key is required on EVERY endpoint (reads, monitor
    /// pages, proxying, runtime actions) instead of only the default config-affecting route
    /// set. LIVE: the auth middleware reads this per request via
    /// IOptionsMonitor&lt;OllamaRouterOptions&gt;.CurrentValue, so a config/config.json
    /// edit rebinds without a restart.
    /// </summary>
    public bool RequireKeyForAllEndpoints { get; set; } = false;

    /// <summary>
    /// Auto-rotation interval (in hours) of the auto-generated default API key: when the
    /// stored key's generatedAt is older than this many hours the rotation service
    /// generates a new key, atomically replaces config/default-key.dat and logs the new
    /// raw key at INFO (callers must re-read the key after a 401; there is NO grace period
    /// for the old key). Defaults to 24 (hours). Values &lt;= 0 are clamped back to 24 by
    /// <see cref="Normalize"/>. LIVE: each rotation tick reads the current value.
    /// </summary>
    public int DefaultKeyRotationHours { get; set; } = 24;

    /// <summary>
    /// Normalizes the section after config binding: clamps non-positive
    /// <see cref="DefaultKeyRotationHours"/> back to the 24 h default. Never throws.
    /// </summary>
    public void Normalize()
    {
        if (DefaultKeyRotationHours <= 0)
        {
            DefaultKeyRotationHours = 24;
        }
    }
}
