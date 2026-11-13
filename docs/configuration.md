# Configuration

OllamaRouter uses a two-layer configuration model so user settings survive application updates:

- **`appsettings.json`** (next to the executable) holds **read-only factory defaults**. It ships with the app, is never modified at runtime, and is replaced by a software update.
- **`config/config.json`** (in a `config/` directory next to the executable) is the **optional user-owned layer** for all user-changeable settings. It is created lazily (by the Setup API, Setup Wizard, or manually), is absent on a fresh install, and — because it lives beside the executable, outside the files a publish/upgrade replaces — it **survives updates and deletions of the application**. The provider is wired with `reloadOnChange: true` so `IOptionsMonitor` consumers observe file changes at runtime. If the file exists but contains invalid JSON, the router **fails open**: it logs a prominent error, ignores the file, and boots with factory defaults (fix or delete the file to apply user settings). See [config/README.md](../config/README.md).

Configuration is provided through the `OllamaRouter` section of `config/config.json` (user values) or `appsettings.json` (factory defaults), or any other standard .NET configuration source such as environment variables. Effective precedence (later wins):

```text
appsettings.json  <  config/config.json  <  user secrets  <  environment variables  <  command-line arguments
```

Example `config/config.json`:

```json
{
  "OllamaRouter": {
    "Targets": [
      {
        "Name": "Local",
        "Url": "http://127.0.0.1:11435",
        "SpeedFactor": 2.0
      },
      {
        "Name": "Remote",
        "Url": "http://aiserver.local:11434"
      }
    ],
    "Models": {
      "Qwen3.8-27B": {
        "CloudModel": "deepseek-v3.1:671b-cloud"
      }
    }
  }
}
```

> **Note on model names.** Configuration keys **must not include the tag suffix** (`:latest`, `:8b`, …): the .NET configuration binder treats `:` as a path separator, so a key such as `"Qwen3.8-27B:latest"` does not bind to the dictionary. Clients also frequently send the implicit `:latest` tag even when the model is stored without one, so the router strips the tag part before looking up the configuration. Use a single entry per base model name; all tag variants share the same settings.

## Settings reference

| Setting | Description |
|---------|-------------|
| `Targets` | Array of named fleet targets (`Name`, `Url`, `RouterUrl`, `Type`, `ApiKey`, `Provider`, `Models`, `Location`, `MaxVramMB`, `MaxParallel`, `SpeedFactor`, `KeepAliveSeconds`, `PowerWatts`, `HardwareHint`, `Enabled`). See [Dual-Router Mesh](ollama-setup.md#dual-router-mesh-desktop--laptop). |
| `Targets:*:RouterUrl` | Optional base address of the OllamaRouter instance running on this node (e.g. `http://192.168.1.20:11434`). Used for router-to-router management operations (such as hardware discovery via `/api/router/hardware`). When omitted, falls back by convention to the same host on port `11434` only if `Url` uses port `11435`. |
| `Targets:*:Type` | Protocol family of the target: `"Ollama"` (default; standard Ollama daemon), `"OpenAI"` (OpenAI-compatible `/v1/chat/completions` & `/models` endpoint, with automatic protocol translation when inbound protocol differs), or `"OsNative"` (reserved for in-process OS providers; config-storable but not dispatched). |
| `Targets:*:ApiKey` | Optional upstream bearer API key for `"OpenAI"` targets. Stored encrypted/wrapped at rest (`v1:` + Base64 envelope via DPAPI on Windows, `0600` on Unix) in `config/config.json` and unwrapped in memory. Never echoed in logs, telemetry headers, or API responses (`GET /api/router/config` exposes `apiKeyConfigured` only). |
| `Targets:*:Provider` | Optional informational provider label for non-Ollama targets (e.g. `"openrouter"`, `"groq"`, `"openai"`), surfaced in metadata and monitoring. Defaults to unset. |
| `Targets:*:Models` | Optional declared model roster (`[{ "Name": "...", "ContextLength": 131072, "Quantization": "Q4_K_M" }]`) for non-Ollama targets. A non-empty list enforces **strict** Stage-1 catalog membership; an empty list (default) is **permissive** so a minimal `{Name, Url, ApiKey}` OpenAI target can serve any model name. Ignored for `"Ollama"` targets (whose live catalog comes from `/api/tags`). |
| `Targets:*:Location` | Optional explicit target-location override: `"local"`, `"remote"`, or `"cloud"` (case-insensitive). When set, it wins over the [URL-based classification](#target-location-classification). Defaults to unset (classified from `Url`). |
| `Targets:*:MaxVramMB` | Optional explicit maximum VRAM (in MB) available on this target. When omitted, VRAM is auto-discovered dynamically via `IFleetHardwareService`. |
| `Targets:*:MaxParallel` | Maximum concurrent requests the target can serve before incurring the `MaxParallelCliffPenalty` in Stage 2. Defaults to `1` (**live**). |
| `Targets:*:Enabled` | Whether the target is statically enabled in configuration. Defaults to `true` (**live**; also runtime-toggleable in `targets.json` via `/targets` or `/monitor`). |
| `Targets:*:HardwareHint` | Optional free-form hardware label (e.g. `"16gb"`, `"96gb"`) surfaced by the Setup API / Web UI. Informational only (does not affect routing). |
| `Targets:*:KeepAliveSeconds` | Optional per-node **preload keep-alive override** (seconds; `-1` = resident indefinitely) applied by `/api/router/models/{model}/preload` when the request omits `keepAliveSeconds`. When omitted, the [per-node class convention](#preload-keep-alive-policy) applies. `0` and values `≤ -2` are invalid (immediate unload is not a preload intent) and are normalized to `null` at startup. Surfaced live (with its `"explicit"` source) by `GET /api/router/fleet` (`preload.*`) and the monitor Keep-Alive column. |
| `Targets:*:SpeedFactor` | Optional per-target **static processing-speed hint**: relative generation-throughput multiplier vs the fleet baseline (`1.0` = fleet average / unset; only values `> 1.0` apply a bonus). At routing time the qualified candidate with the highest effective factor earns the full Stage-2 speed bonus (+150), and the others are scaled linearly between 0 and +150 — so a fast cold node (e.g. a local workstation) can be preferred over a slower node that happens to hold the model hot (+100), while Stage-1 hardware/context gates are untouched and a fast node that would evict another model still loses to the hot one (`150 − 80 = 70 < 100`, anti-thrash). Recommended heterogeneous setup: fast workstation `"SpeedFactor": 2.0`, servers default/omitted. Non-finite or `≤ 1.0` values are treated as `1.0` (zero bonus). Surfaced in `GET /api/router/fleet` (`hardware.speedFactor`) and the TUI HW column (`16gb,2.0x`). |
| `Targets:*:PowerWatts` | Optional per-target node power draw (in watts) for the electricity-cost leg of monitor/session savings (e.g. `400` for a desktop GPU node, `130` for a remote node). `0` marks a zero-consumption node; unset (default) resolves via `Targets:{n}:PowerWatts` → `Pricing:DefaultNodePowerWatts` → `150` W. Editable via the Setup API (`POST`/`PUT /api/router/targets`, `powerWatts`). |
| `Models` | Dictionary of model names (base name, no tag) to model-specific routing settings (`CloudModel`, `Architecture`, `MaxContextTokens`). |
| `Models:*:CloudModel` | Optional name of the equivalent model hosted on [ollama.com cloud](https://ollama.com) (e.g. `glm-5.3-flash:cloud`). When set, takes precedence over `CloudOverflow:DefaultModel` for this model whenever primary targets are busy or failing. |
| `Models:*:Architecture` | Optional explicit architecture pin: `"Dense"` or `"Moe"` (case-insensitive). Wins over auto-detection for this model. Defaults to unset (auto-detected). |
| `Models:*:MaxContextTokens` | Optional operator cap (in tokens) on a model's **extensible** context limit, applied across all nodes as `min(native context ?? 128k, MaxContextTokens)`. Affects MoE dynamic sizing and Stage-1 MoE qualification only (it does not change a node's pre-configured `CMax`). Defaults to unset (no cap). |
| `CloudOverflow:Enabled` | Enables the Cloud Overflow terminal fallback (ANDed with the runtime `Cloud` toggle in `targets.json`). Defaults to `true` (**live**). |
| `CloudOverflow:DefaultModel` | Global fallback cloud model name (e.g. `"glm-5.3-flash:cloud"` in factory `appsettings.json`) used when a model has no explicit `Models:{model}:CloudModel`. Leave null/empty to restrict cloud overflow to models with an explicit `Models:{model}:CloudModel` (**live**). |
| `CloudOverflow:PreferredRelayTarget` | Name of the fleet target preferred to relay cloud-rewritten requests to `ollama.com` (case-insensitive; defaults to `"Local"`). Falls back to the first online enabled fleet target when the preferred relay is disabled or offline (**live**). |
| `LocalUrl` | Base URL of the local Ollama instance (legacy; prefer configuring `Targets`). |
| `RemoteUrl` | Base URL of the remote Ollama instance (legacy; prefer configuring `Targets`). |
| `BindAddress` | Address the router itself listens on. Code default is `http://localhost:11434` (factory `appsettings.json` ships with `http://+:11434/`). Set it to e.g. `http://0.0.0.0:11434` or `http://127.0.0.1:11434` as needed (**startup-scoped**). |
| `StateDirectory` | Directory for persisted runtime state files (`targets.json`, `activity-statistics.json`, `session-usage.json`, `benchmarks.json`, `targets-speed.json`, `targets-hardware.json`, `recommended.json`). Defaults to `%LOCALAPPDATA%\OllamaRouter\state` on Windows. |
| `FilterNamespacedModels` | When `true` (default; alias: `FilterNonNativeModels`), filters out namespaced models (containing `/`, e.g. `jetelain/*`) from the merged fleet model catalog, avoiding slow capability discovery for backing weights not directly used in routing. Models explicitly declared in `Models` or `CloudOverflow` are always retained (**live**). |
| `TokenEstimator` | Token estimation strategy: `"Heuristic"` (default, fast, zero heap memory overhead, saves ~24 MB RAM by omitting the BPE dictionary) or `"Tiktoken"` (uses `Microsoft.ML.Tokenizers` `cl100k_base`) (**startup-scoped**). |
| `TokenEstimationOverheadFactor` | Multiplicative correction applied to the estimated token count to compensate for underestimation of the generic tokenizer vs. the actual tokenizer and chat template of targeted models. Code default is `1.0` (factory `appsettings.json` ships with `1.025`); see [recommendations below](#token-estimation-overhead-recommendations) (**live**). |
| `ContextFitMarginTokens` | Additive **reasoning-fit margin** (in tokens) added to the estimated prompt token count before qualifying a target: a prompt of T tokens is routed to a target only when `T + ContextFitMarginTokens ≤ CMax` (the target's [pre-configured node context](ollama-setup.md#local-ollama)). Reserves a generation/reasoning budget so output never overflows the node's context. Defaults to `8192`; for long-thinking agent models a larger value (e.g. `10240`) is recommended. Negative values are clamped to `0`. Applied even when `DynamicContext:Enabled` is `false` (**live**). |
| `PreloadDefaultKeepAliveSeconds` | Last-resort router-level **preload retention** (seconds) used by `/api/router/models/{model}/preload` for nodes the class convention cannot classify (unknown VRAM and no cloud signal). Defaults to `900` (15 min). `-1` (indefinite) and `0` (no extra retention beyond the daemon default) are valid; values `≤ -2` are clamped to `900` at startup. |
| `PreloadLoadTimeoutSeconds` | Timeout (in seconds) bounding only the preload **load call** (`POST /api/generate`, `stream=false`, empty prompt). A cold load of a large model regularly exceeds the 4 s bound of the shared catalog client, so the load call uses a dedicated client with this timeout (**live** — changes in `config/config.json` apply to the next load call without a restart). Defaults to `20` seconds; values `≤ 0` are clamped to `20` at startup. Raise it (e.g. `60`) for very large models over a slow network. |
| `DispatchToLargestCapacityOnOverflow` | When `true` (default) and all enabled fleet targets fail Stage-1 context qualification due to context overflow, the router dispatches the request to the online, capable target with the largest context capacity (`Max(CMax)`) with `options.num_ctx` clamped to that ceiling, rather than rejecting with HTTP `503`. Runs **before** Cloud Overflow is considered; Cloud Overflow is the terminal fallback when this option is `false`, no fleet target can serve the overflow, or every largest-capacity candidate has an active failure streak above `MaxConsecutiveFailures` (**live**). |
| `DynamicContext:Enabled` | Enables dynamic context sizing (MoE two-bucket ladder + dense clamp-only). Defaults to `false` (**live**). See [Dynamic context sizing (MoE only)](#dynamic-context-sizing-moe-only). |
| `DynamicContext:DenseClampEnabled` | When `true` (default), a request on a **dense** model rewrites `options.num_ctx` only when the client's static value exceeds the node's pre-configured context (or when an active role recommendation ceiling is lower than the node's pre-configured context), clamping it down (one-time KV reallocation; OOM protection). Set to `false` for pure dense pass-through. Dense values are never raised, bucketed, or leased (**live**). |
| `DynamicContext:AutoShrinkCooldownTurns` | Number of consecutive normal-size turns the Auto-Shrink Manager retains an expanded MoE context bucket (preserving the KV prefix cache) after a context spike before injecting a shrink back to baseline. Defaults to `3` (**live**). |
| `DynamicContext:AutoShrinkIdleTimeoutSeconds` | Idle timeout (in seconds) the Auto-Shrink Manager waits before shrinking an active expanded MoE context lease (evaluated on the next request). Defaults to `60` (**live**). |
| `Aggregation:PerTargetTimeoutMs` | Maximum time (in milliseconds) a single target may hold up parallel fleet catalog aggregation (`GET /api/tags`, `/v1/models`, `/api/ps`, `POST /api/show`). When a target exceeds this budget, its last cached catalog is served (stale-while-revalidate). Defaults to `500` (**live**). |
| `MaxConsecutiveFailures` | Per-target consecutive failure threshold (uncommitted transport failures and mid-stream drops) before tripping the circuit breaker and skipping the target as a routing candidate. Code default is `3` (factory `appsettings.json` ships with `2`). Set to `0` (or negative) to disable streak-based skipping (**live**). |
| `FailureStreakCooldownSeconds` | Circuit-breaker cooldown window (in seconds) that a tripped target (streak > `MaxConsecutiveFailures`) stays in `Cooldown` (Open) before transitioning to `HalfOpen` for a single trial request. Defaults to `60`. Set to `0` to disable the cooldown window (immediately `HalfOpen`) (**live**). |
| `ResetStreakOnLivenessRecovery` | When `true` (default), an `Offline/Unknown → Online` liveness transition automatically resets the target's failure streak so a recovered node rejoins routing immediately (**live**). |
| `ConsecutiveFailuresTokenTolerance` | *Deprecated (kept for config-binding compatibility only).* Previously controlled token-count tolerance between adjacent retries; failure streaks are now tracked per target via the circuit breaker above. Defaults to `4000`. |
| `SessionAffinityTtlMinutes` | Sliding TTL (in minutes) of the in-memory session-affinity store (`X-OllamaRouter-Session-Id` / `X-OllamaRouter-Agent-Id`). Every hit re-arms the window. Defaults to `30` (**live**). |
| `SessionUsageMaxSessions` | Maximum number of distinct `X-OllamaRouter-Session-Id` values kept in the per-session usage LRU (persisted to `session-usage.json` in `StateDirectory`). When a new session arrives beyond the cap, the least-recently-active stored session is evicted. Defaults to `1000` (**live** — changes take effect on the next recorded request). Non-positive values normalize back to `1000`. |
| `SessionReservationWindowSeconds` | Active-session reservation window (in seconds): a target that served a session within this window is treated as contended when a *different* session evaluates it in Stage 2 (incurring `OtherSessionReservationPenalty`), protecting the active session's hot KV cache across inter-turn tool calls or compiles. Defaults to `30`; set to `0` to disable (**live**). |
| `OtherSessionReservationPenalty` | Stage-2 score penalty applied to a candidate target reserved by another active session within `SessionReservationWindowSeconds` (calibrated at `120` so `100 hot − 120 = −20 < 0` loses to an idle cold node). Defaults to `120`; set to `0` to disable (**live**). |
| `MaxParallelCliffPenalty` | Additional Stage-2 concurrency penalty added on top of the per-in-flight penalty (`40 × InFlight`) when a target is at or above its `MaxParallel` cap (calibrated at `220` so a saturated node scores below an idle qualified candidate). Defaults to `220`; set to `0` to disable (**live**). |
| `Recording:Enabled` | Enables optional [inference request recording](#inference-request-recording-optional), saving each request's metadata and full request body. Defaults to `false` (**live**). |
| `Recording:OutputDirectory` | Directory for recorded requests (`requests.jsonl` + per-request JSON payloads). Defaults to `%LOCALAPPDATA%\OllamaRouter\inference-requests` on Windows (**live**). |
| `Recording:MaxFiles` | Maximum number of payload files to keep; oldest records are deleted once exceeded. Defaults to `500`. Set to `0` to keep all files (**live**). |
| `Recording:TokenMetricsCsv:Enabled` | Enables optional [token metrics CSV export](#token-metrics-csv-export-optional). Defaults to `false`. Independent of `Recording:Enabled` (**live**). |
| `Recording:TokenMetricsCsv:OutputDirectory` | Directory where CSV files are written. Defaults to `%LOCALAPPDATA%\OllamaRouter\token-metrics` on Windows (**live**). |
| `Recording:TokenMetricsCsv:MaxFiles` | Maximum number of per-request `*_metrics.csv` files to keep (the consolidated `token-metrics.csv` is never pruned). Defaults to `1000`. Set to `0` to keep all files (**live**). |
| `Recording:TokenMetricsCsv:GenerateIndividualFiles` | When `true` (default), each recorded request produces an individual CSV file (`{timestamp}_{sequence}_{model}_metrics.csv`) (**live**). |
| `Recording:TokenMetricsCsv:AppendConsolidatedFile` | When `true` (default), each recorded request appends one row to the consolidated `token-metrics.csv` dataset (**live**). |
| `Pricing:PromptPricePerMillion` | Optional price per 1,000,000 prompt / input tokens (alias: `InputPricePerMillion`). Used to calculate 7-day estimated savings and cloud overflow cost (**live**). |
| `Pricing:CompletionPricePerMillion` | Optional price per 1,000,000 completion / output tokens (alias: `OutputPricePerMillion`) (**live**). |
| `Pricing:CachedPromptPricePerMillion` | Optional price per 1,000,000 **cached** prompt / input tokens (alias: `CachedInputPricePerMillion`). When unset, cached tokens fall back to `PromptPricePerMillion` (then `PricePerMillion`) (**live**). |
| `Pricing:PricePerMillion` | Optional flat price per 1,000,000 tokens when prompt and completion rates are not differentiated (**live**). |
| `Pricing:Currency` | Currency symbol or label displayed in the monitoring UI (e.g. `$`, `€`, `USD`). Defaults to `$` (**live**). |
| `Pricing:ElectricityPricePerKwh` | Optional electricity price per kilowatt-hour (e.g. `0.20`) used for the node electricity-cost leg of monitor/session **actual savings**. Defaults to `0.20`/kWh when unset. Setting it alone does not show the cost section (a token price rate must also be configured) (**live**). |
| `Pricing:DefaultNodePowerWatts` | Optional global default node power draw (in watts) applied to every non-Cloud fleet target without its own `PowerWatts` override. Defaults to `150` W when unset (**live**). |
| `TerminalMonitoring:Enabled` | Enables the in-terminal live activity monitor (TUI) in the server console (zero VRAM consumption). Defaults to `true`. Can also be disabled via `--no-tui` (**startup-scoped**). |
| `TerminalMonitoring:RefreshIntervalSeconds` | Refresh interval in seconds for the in-terminal live monitor. Defaults to `2.0` (**live**). |
| `ApiSecurity:RequireKeyForAllEndpoints` | When `true`, extends API key enforcement to **every** endpoint (reads, `/monitor*`, proxy paths, runtime actions). Defaults to `false` (**live**). See [API key security](#api-key-security). |
| `ApiSecurity:DefaultKeyRotationHours` | Auto-rotation interval (in hours) for the auto-generated default key. Defaults to `24` (**live**). |

### Live vs. startup-scoped rebinding

Most behavioral settings are read via `IOptionsMonitor<OllamaRouterOptions>` and take effect **live** as soon as `config/config.json` is saved (or updated via the Setup API), without restarting the daemon. Target additions, updates, and removals via the Setup API (`POST`/`PUT`/`DELETE /api/router/targets`, `PUT /api/router/config`) also take effect immediately on the completion dispatch path (`/api/chat`, `/api/generate`, `/v1/chat/completions`, `/v1/completions`, `/api/embeddings`).

Only the following require a daemon restart:
- `BindAddress` (Kestrel listener binding)
- `TokenEstimator` (DI singleton selection)
- `TerminalMonitoring:Enabled` (startup TUI host toggle)
- YARP passthrough cluster routes (`pull`/`create`/`copy`/`delete` and catch-all pass-through when target URLs change outside the completion dispatch loop)

## Target location classification

Every fleet target is assigned a **location** — `Local`, `Remote`, or `Cloud` — which drives its monitor icon/color and location-scoped `X-OllamaRouter-Target` pins (`local`, `remote`, `cloud`). The location is derived **synchronously from the target's `Url`** (no DNS lookups, so it never hangs or blocks startup), with an optional per-target override via `Targets:*:Location` that always wins:

| Location | Classification rule (applied in order) |
|----------|------------------------------------------|
| **Local** | Loopback host: `localhost`, `127.0.0.1`, `::1`, `0.0.0.0`, or `::`. |
| **Remote** | LAN / private: IPv4 literals in `10.0.0.0/8`, `172.16.0.0/12`, or `192.168.0.0/16`; IPv6 ULA (`fc00::/7`) and link-local (`fe80::/10`) literals; hostnames ending in `.local` (mDNS); Tailscale addresses ending in `.ts.net`; and single-label hostnames (no dot, e.g. `aiserver`). |
| **Cloud** | Everything else: public IPv4/IPv6 literals (including CGNAT `100.64.0.0/10` and `169.254.0.0/16`), multi-label public hostnames, and any null / malformed / unparseable `Url`. The **Cloud** pseudo-target is always `Cloud` by definition. |

> **Override.** Set `"Location": "remote"` (or `"local"` / `"cloud"`, case-insensitive) on a target when its hostname should be treated differently from what its URL implies.

The location is exposed in `/monitor/api` fleet rows and `GET /api/router/fleet` (`targets[]`) as `"local"`, `"remote"`, or `"cloud"`.

## Preload keep-alive policy

When `POST /api/router/models/{model}/preload` omits `keepAliveSeconds`, the router resolves the effective `keep_alive` using the following convention (surfaced live on `GET /api/router/fleet` as `preload.keepAliveSeconds` + `preload.keepAliveSource`, and in the monitor Keep-Alive column):

| Rule | Effective `keep_alive` | `keepAliveSource` |
|---|---|---|
| Explicit `Targets:*:KeepAliveSeconds` override | Configured value verbatim (incl. `-1` on a desktop-class node) | `"explicit"` |
| Cloud class (`Location` override or URL classification) | `-1` (resident indefinitely) | `"class"` |
| Server class (effective VRAM ≥ 32,768 MB) | `-1` (resident indefinitely) | `"class"` |
| Desktop/laptop class (known VRAM below the server threshold) | `900` (15 minutes) | `"class"` |
| Unclassifiable (unknown VRAM, not cloud) | `PreloadDefaultKeepAliveSeconds` (default `900`) | `"default"` |

## Dynamic context sizing (MoE only)

When `DynamicContext:Enabled` is `true`, the router sizes the outgoing `options.num_ctx` per request. The system is **MoE-only**: dense models are never bucketed or expanded because a dense KV-cache refill is prohibitively slow.

- **MoE — two-bucket ladder:** A MoE request is sized between exactly two buckets:
  1. the node's **pre-configured context** (its Modelfile `num_ctx`, or active recommended baseline from `recommended.json`) — the *optimal* baseline bucket; and
  2. the model's **native context length** — the single *extensible* bucket, optionally capped across all nodes by `Models:{model}:MaxContextTokens` (`min(native, override)`).

  If the margin-adjusted requirement (`min(estimatedTokens + ContextFitMarginTokens, clientNumCtx)`) fits the baseline bucket, the baseline is injected; otherwise the extensible bucket is. Stage-1 qualification uses the same extensible limit, so a node pre-configured at 184,320 can elastically serve a 205k prompt by expanding to its 262,144 native limit instead of failing over. The **Auto-Shrink Manager** retains the expanded bucket across `DynamicContext:AutoShrinkCooldownTurns` (default `3`) normal turns (preserving the KV prefix cache) before shrinking back to the baseline on a 50%-token drop or after `DynamicContext:AutoShrinkIdleTimeoutSeconds` (default `60` s) of inactivity.

- **Dense — clamp-only pass-through:** For a dense model, the router rewrites `options.num_ctx` when the client's static value **exceeds** the node's effective ceiling (clamping it **down** for OOM protection), or **injects** the active role recommendation ceiling (`recommended.json`) when the client omits `num_ctx` and the recommended ceiling is strictly below the node's Modelfile window (`X-OllamaRouter-Context-Mode: RecommendedCeiling`). Otherwise, the payload is left byte-identical. Set `DynamicContext:DenseClampEnabled` to `false` for pure dense pass-through.

## Token estimation overhead recommendations

OllamaRouter estimates prompt token count prior to routing so it can qualify target context capacity (`CMax` / `ContextFitMarginTokens`) without downloading full tokenizer files for every model. Set `TokenEstimationOverheadFactor` according to your primary model family:

| Model Family | Vocabulary Size | Recommended (`Tiktoken`) | Recommended (`Heuristic`) | Notes |
| :--- | :---: | :---: | :---: | :--- |
| **Qwen 3.8** (e.g. `Qwen3.8-27B`) | 248k | **1.09** | **1.11** | Qwen 3.8 expands token embeddings to 248,320. `1.09` is empirically validated for high-context workloads. |

> **Tip.** The monitoring page (`/monitor`) dynamically measures the ratio between raw estimated tokens and Ollama's actual reported prompt tokens for every completed request and displays a tailored recommendation. If your prompts are predominantly non-English or contain heavy code indentation, add an extra `+0.05` margin.

## Inference request recording (optional)

OllamaRouter can record every inference request so the difference between the **estimated** token count and the **actual** token count (reported by Ollama) can be investigated per request. Disabled by default.

Enable it in `config/config.json`:

```json
"Recording": {
  "Enabled": true
}
```

Each recorded request produces, in `Recording:OutputDirectory` (default `%LOCALAPPDATA%\OllamaRouter\inference-requests\` on Windows):

- one line in `requests.jsonl` with routing metadata (`timestamp`, `target`, `model`, `endpoint`, `estimatedPromptTokens`, `rawPromptTokens`, `actualPromptTokens`, `actualResponseTokens`, `elapsedMilliseconds`, `statusCode`, `success`, `file`);
- a pretty-printed payload file named `<utc-timestamp>_<sequence>_<model>.json` containing the full request body.

The output directory is capped at `Recording:MaxFiles` payload files (default `500`); oldest records are deleted and the manifest is trimmed to match. Recording is best-effort and never affects routing.

## Token metrics CSV export (optional)

To calibrate the heuristic estimator's model-family coefficients, OllamaRouter can export an empirical training dataset pairing prompt structural text metrics (`Words`, `Symbols`, `NonAscii`, `CharCount`) with actual token counts reported by Ollama. Disabled by default and independent of `Recording:Enabled`:

```json
"Recording": {
  "TokenMetricsCsv": {
    "Enabled": true,
    "MaxFiles": 1000,
    "GenerateIndividualFiles": true,
    "AppendConsolidatedFile": true
  }
}
```

Writes to `Recording:TokenMetricsCsv:OutputDirectory` (default `%LOCALAPPDATA%\OllamaRouter\token-metrics\` on Windows):

- **Per-request files**: `{yyyyMMdd-HHmmss-fff}_{sequence}_{model}_metrics.csv` (header + single data row, capped at `MaxFiles`).
- **Consolidated dataset**: `token-metrics.csv` (never pruned), ready to load in Pandas / Excel / R.

Fixed RFC 4180 column order (CRLF, quote-escaped):
`timestamp_utc,request_id,model,target,endpoint,words,symbols,non_ascii,char_count,prior_context_tokens,estimated_prompt_tokens,actual_prompt_tokens,actual_response_tokens,elapsed_ms,status_code,success`.

## API key security

Config-affecting Setup API endpoints require a valid API key by default (`401` responses include `WWW-Authenticate: Bearer` and a JSON `error` body, never echoing key material). Keys are accepted via `Authorization: Bearer <key>` or `X-Api-Key: <key>` (`Bearer` wins if both are present).

### Default protected routes

| Method | Route |
|---|---|
| `GET`, `PUT` | `/api/router/config` |
| `POST` | `/api/router/targets` |
| `PUT`, `DELETE` | `/api/router/targets/{name}` |
| `PUT` | `/api/router/fleet/hardware/{targetName}` |
| `POST` | `/api/router/benchmark` |
| `DELETE` | `/api/router/benchmark/results` |
| `POST` | `/api/router/recommendations/generate` |
| `POST` | `/api/router/recommendations/apply` |
| `GET`, `POST` | `/api/router/api-keys` |
| `DELETE` | `/api/router/api-keys/{name}` |
| `POST` | `/api/router/api-keys/default/rotate` |
| `POST` | `/api/router/targets/{name}/reset-streak` |
| `POST` | `/api/router/fleet/reset-streaks` |
| `POST` | `/api/router/admin/shutdown` |

Runtime availability toggles (`POST /targets`), reads, `/monitor`, and inference/proxy paths stay open in default mode. Setting `ApiSecurity:RequireKeyForAllEndpoints: true` protects **all** endpoints:

```json
"ApiSecurity": {
  "RequireKeyForAllEndpoints": false,
  "DefaultKeyRotationHours": 24
}
```

### Key files (next to `config/config.json`)

- **`config/default-key.dat`** — auto-generated default key (32 random bytes, base64url, 43 chars). On Windows it is a **DPAPI-wrapped blob** (unwrapable only by the same Windows user); on Unix it is stored plain with `0600` permissions. On first start and on every rotation, the raw key is **logged at INFO**.
- **`config/api-keys.json`** — user keys stored as **hashes only** (name + SHA-256 + optional expiry), `0600` on Unix. Raw user keys are returned once at creation and never written to disk.

Missing or corrupt key files are regenerated on startup (fail-open).

### Auto-rotation & user keys

The default key auto-rotates every `ApiSecurity:DefaultKeyRotationHours` hours (default `24`) with **no grace period** — callers should re-read `default-key.dat` on a `401` and retry once. You can also rotate manually via `POST /api/router/api-keys/default/rotate`.

Create, list, and revoke custom user keys:

```bash
# Create a key valid for 168 hours (omit "validFor" for no expiry)
curl -X POST http://localhost:11434/api/router/api-keys \
  -H "Authorization: Bearer <default-or-user-key>" \
  -H "Content-Type: application/json" \
  -d '{ "name": "ci", "validFor": 168 }'

curl http://localhost:11434/api/router/api-keys -H "X-Api-Key: <key>"
curl -X DELETE http://localhost:11434/api/router/api-keys/ci -H "Authorization: Bearer <key>"
```

