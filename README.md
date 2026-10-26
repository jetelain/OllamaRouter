# OllamaRouter

OllamaRouter is a lightweight reverse proxy that sits in front of two [Ollama](https://ollama.com) instances and transparently routes each request to the most appropriate one:

- A **local** Ollama instance, running on a machine with a powerful GPU but **limited VRAM** (an NVIDIA card, or an Apple Silicon Mac with unified memory).
- A **remote** Ollama instance (e.g. a server), with a lot of **unified RAM or VRAM** but a **less powerful GPU**.

The goal is to get the best of both worlds: use the fast local GPU whenever the request can fit in its available VRAM, and fall back to the remote server for everything else (larger contexts, models that don't fit locally, etc.).

OllamaRouter is more than a simple two-tier proxy, it also provides:

- **Cloud overflow** (optional, per model). When both instances are busy, requests can **overflow to [ollama.com cloud](https://ollama.com)** models, so a slow/unavailable Local or Remote instance never fully blocks incoming requests. See [Cloud overflow](#cloud-overflow).
- **Target failover on consecutive errors**. When client retries repeatedly fail on a target (e.g. context limit HTTP 400 or timeouts), requests automatically switch to the next target in the chain. See [Target failover on consecutive errors](#target-failover-on-consecutive-errors).
- **Routing transparency & client target pinning**. Emits telemetry headers (`X-OllamaRouter-Target`, `X-OllamaRouter-Model`, `X-OllamaRouter-Estimated-Tokens`) on all completions, and allows clients to explicitly override routing via an incoming `X-OllamaRouter-Target` header. See [Routing transparency & target pinning](#routing-transparency--client-target-pinning).
- **A built-in monitoring page**. A lightweight, dependency-free page at `/monitor` shows which targets are busy, which requests are in progress and what has been processed recently, and lets you enable/disable the **Local**, **Remote** and **Cloud** targets at runtime — switching the router between operating modes without restarting it. See [Monitoring](#monitoring).

## How it works

From the user's point of view, there is a single endpoint: the router listens on the standard Ollama port (`http://localhost:11434`) and transparently forwards each request to the local or the remote instance — or, optionally, to an [ollama.com cloud](https://ollama.com) model — depending on the model requested and the size of the prompt (a monitoring page is also available under `/monitor`, see [Monitoring](#monitoring)).

```mermaid
flowchart TD
    client["Client (any Ollama or OpenAI client)"]
    client -->|"http :11434"| router{"🔀 OllamaRouter<br/>“best machine for this request?”"}

    router -->|"⚡ context fits in the GPU — fastest answers"| local
    router -->|"🧠 big context, or model not available locally"| remote
    router -->|"☁️ both instances busy — cloud fallback (optional)"| cloud
    router -.-> monitor

    local["🖥️ Local Ollama (fast answers: powerful GPU, limited VRAM)"]
    remote["📡 Remote Ollama (heavy models: big RAM/VRAM, slower GPU)"]
    cloud["☁️ ollama.com cloud model (requests are never fully blocked)"]
    monitor["📊 /monitor — see what's going on, pause any target"]

    style local fill:#d3edff
    style remote fill:#cef8e9
    style cloud fill:#eddcff
    style client fill:#fff
    style router fill:#fff
    style monitor fill:#fff3cd
```

For every incoming request, OllamaRouter decides between `Local`, `Remote` and `Cloud` and forwards the request accordingly using [YARP](https://github.com/microsoft/reverse-proxy) (`Cloud` is physically routed to the **Local** instance, see [Cloud overflow](#cloud-overflow) below).

### Endpoints that are inspected and routed dynamically

Requests to the following endpoints are inspected to extract the prompt and the model name:

- `POST /api/chat`
- `POST /api/generate`
- `POST /v1/chat/completions`
- `POST /v1/completions`

For these requests, the routing decision is orchestrated by `RoutingDecisionService` using a priority-ordered chain of target handlers (`IRoutingTargetHandler`: **Local** → **Remote** → **Cloud**):

1. **Available target dispatch:** The router evaluates targets in order and dispatches the request to the first target that is **available** (enabled and not busy) and **capable** of handling it (`CanProcessAsync`):
   - **Local (`LocalRoutingTargetHandler`)**: Eligible if the model is configured under `OllamaRouter:Models`, exists in the local catalog, the estimated token count fits within `MaxLocalTokens`, and the model is either already loaded in memory (checked via `/api/ps`) or sufficient free GPU VRAM is available (checked against `MinRequiredVramMB` via `nvidia-smi`, or on macOS via an estimate of the unified memory usable by the GPU, see [GPU memory on macOS](#gpu-memory-on-macos)). If a model is only available locally, Local handles it exclusively.
   - **Remote (`RemoteRoutingTargetHandler`)**: Accepts requests for models that are available remotely (unconfigured models, models exceeding local token limits, or models that don't fit local VRAM naturally fall through to Remote).
   - **Cloud (`CloudRoutingTargetHandler`, overflow)**: Acts as an overflow target. If Local and Remote are unavailable or cannot handle the request, Cloud accepts it if enabled and a `CloudModel` is configured for the requested model (see [Cloud overflow](#cloud-overflow)).
2. **Busy fallback:** If all eligible targets are currently busy:
   - The router falls back to the first capable **enabled non-overflow target** (Local or Remote), allowing requests to queue behind a busy primary instance.
   - If no primary target can handle the request, it checks enabled overflow targets (Cloud).
   - If no enabled target is capable of handling the request, OllamaRouter rejects the request with HTTP `503 Service Unavailable`.

If the request body cannot be parsed for any reason, the request falls back to the default enabled instance (Remote if enabled, otherwise Local).

> **Note.** Each target (**Local**, **Remote**, **Cloud**) can also be disabled individually, at any time, from the [monitoring page](#monitoring). If a target is disabled, it will **never** handle any request. A **busy** target can still accept queued requests as a fallback when all eligible instances are in use, but a **disabled** target is completely excluded from routing. Disabling **Cloud** disables cloud overflow for all models. This lets you switch the router between operating modes (Local only, Remote only, Local + Cloud, Remote + Cloud, …) without restarting it.

### Cloud overflow

When a model's configuration includes a `CloudModel` entry (e.g. `deepseek-v3.1:671b-cloud` or `glm-4.6:cloud`), OllamaRouter can overflow to [ollama.com cloud](https://ollama.com) whenever using **Remote** would mean waiting behind an already-busy request:

- The request is still physically forwarded to the **Local** instance (same YARP cluster), because it is expected to be **signed in to ollama.com** (`ollama signin`) and therefore able to relay chat/generate requests to the cloud on its own.
- Before forwarding, the `model` field of the request body is rewritten to the configured `CloudModel` name, so the local Ollama process knows which cloud-hosted model to relay to.
- "Busy" is a simple, already-in-use signal: an instance is considered busy as soon as it currently has at least one chat/generate request in flight (see `IActivityMonitorService.IsBusy`).
- This is meant as a last resort for a model that has no directly usable ollama.com cloud equivalent (e.g. Qwen3.8 isn't offered as an ollama.com cloud model): configure a comparable/cheaper cloud model instead of leaving requests queued behind a busy Remote instance.
- Cloud overflow is entirely opt-in per model: leave `CloudModel` unset (the default) to keep the original Local/Remote-only behavior.

> **Note.** Unlike Local/Remote, OllamaRouter does not talk directly to ollama.com: it relies on the Local Ollama instance's own cloud relay and credentials. No API key or cloud URL needs to be configured in OllamaRouter itself.

### Target failover on consecutive errors

When errors or timeouts arise (such as HTTP 400 Bad Request because a prompt exceeds an instance's context size, or gateway timeouts), LLM clients typically retry the request repeatedly until success. If the chosen target cannot process the request, these retries can take an extremely long time.

To alleviate this, OllamaRouter correlates requests using their **estimated prompt token count** and tracks consecutive failures across **adjacent requests**:

- If adjacent requests within the token tolerance margin fail **3 times** on the same target (configurable via `MaxConsecutiveFailures`, defaulting to `3`), the router dynamically skips that target and routes subsequent retries to the next target in the chain (**Local** → **Remote** → **Cloud**).
- For example, if a large 91.3k-token request fails 3 times on **Remote** (e.g. returning 400 errors or timing out):
  ```
  02:36:08   📡 Remote   Qwen3.8-27B:latest   91.3k / -   -   307.3s   400
  02:42:08   📡 Remote   Qwen3.8-27B:latest   91.3k / -   -   358.1s   400
  02:43:59   📡 Remote   Qwen3.8-27B:latest   91.3k / -   -   332.4s   400
  ```
  The next retry will be dispatched to **Cloud** if enabled. If Cloud is disabled or has no `CloudModel` configured, it keeps the Remote target as fallback.
- Similarly, if a request fails 3 times on **Local**, it fails over to **Remote**; if Remote also fails 3 times, it fails over to **Cloud**.
- **Tolerance margin**: Because AI agents frequently append conversation turns, retry notes, or error context on retries (causing prompt token size to slightly increase on each attempt), adjacency allows a configurable margin of tolerance via `ConsecutiveFailuresTokenTolerance` (defaulting to `4000` tokens). Requests that increase or vary within this margin remain part of the same failure streak. Set to `0` to require an exact token count.
- **Interrupted request detection**: When an OpenAI-compatible caller aborts or times out mid-stream (e.g. client-side timeout during large prompt evaluation), the response may have already committed HTTP 200 headers. OllamaRouter inspects client disconnects (`RequestAborted`), YARP forwarder errors, and stream completion sentinels (`data: [DONE]` for SSE, `"done": true` for NDJSON). Interrupted streams are automatically detected, marked as failures (`HTTP 499` Client Closed Request or `HTTP 502`), and counted toward consecutive failures so cloud overflow can unstuck the agent.
- Any fully completed successful response (`HTTP status < 400`) clears the failure count.

### Routing transparency & client target pinning

AI agent frameworks, orchestrators (such as [NightCodeForge](https://github.com/jetelain/NightCodeForge), OpenCode, Aider, Zed AI), and telemetry pipelines benefit from knowing which physical backend processed each inference request, or being able to force a specific destination when necessary:

1. **Routing transparency headers:** OllamaRouter emits headers on all chat and completion responses (`/api/chat`, `/api/generate`, `/v1/chat/completions`, `/v1/completions`):
   - `X-OllamaRouter-Target`: The physical routing destination (`Local`, `Remote`, or `Cloud`).
   - `X-OllamaRouter-Model`: The model name extracted from the request.
   - `X-OllamaRouter-Estimated-Tokens`: The estimated prompt tokens used for routing decisions.

   Headers are registered via `OnStarting` so they stream immediately on chunked NDJSON and SSE connections:
   ```http
   HTTP/1.1 200 OK
   Content-Type: application/x-ndjson
   X-OllamaRouter-Target: Local
   X-OllamaRouter-Model: qwen2.5-coder:32b
   X-OllamaRouter-Estimated-Tokens: 1420
   ```

2. **Client target pinning (inbound header):** Clients can explicitly pin or prefer a target by supplying the `X-OllamaRouter-Target` request header:
   ```http
   POST /v1/chat/completions HTTP/1.1
   Host: localhost:11434
   X-OllamaRouter-Target: Cloud
   Content-Type: application/json
   ```
   - **Zero-VRAM Cloud Diagnostics:** Force execution to `Cloud` so large failure transcripts or debug analysis consume zero local VRAM and bypass the local dev queue.
   - **Air-Gapped / Untrusted Triage:** Force execution to `Local` to guarantee no external network transmission for untrusted or sensitive code.
   - **JIT Model Preload:** Force `Local` on empty `POST /api/generate` requests to ensure weights are resident in GPU memory.

   If the requested target is enabled in `targetAvailability`, OllamaRouter honors the pin immediately. If the requested target is currently disabled, it logs a warning and falls back to dynamic routing.


### Other endpoints

- All other requests (model pull/push/delete, etc.) are routed to **Local** by default.
- `GET /api/tags` and `GET /v1/models` return a **merged** list of models available on both instances (local models take priority in case of a name conflict), including configured `CloudModel` overflow options so client model pickers auto-discover cloud models.
- `GET /api/ps` returns a **merged** list of models currently loaded/running on both instances.
- `POST /api/models/unload` unloads running models from the local instance without disabling the `Local` routing target.
- `POST /monitor/reclaim-vram` unloads running models and accepts an optional query parameter `disableTarget` (defaults to `true` to disable Local for gaming/editing, or pass `?disableTarget=false` for non-destructive unloading).
- Any other request is proxied as a catch-all to the instance selected by the routing decision.

## Setup Ollama instances

OllamaRouter expects both instances to expose the **same model names**, so that a request can be transparently routed to either one. The steps below set up a quantized model locally (to fit in limited VRAM) and a less-quantized (or full-precision) version of the same model remotely (to take advantage of the larger unified RAM/VRAM), both aliased under the same model name.

### Local Ollama

Set the following environment variables:
- `OLLAMA_HOST` to `127.0.0.1:11435`
- `OLLAMA_KV_CACHE_TYPE` to `q8_0`
- `OLLAMA_FLASH_ATTENTION` to `1`

Then install (or restart) Ollama for the changes to take effect.

> **macOS.** The Ollama app does not read your shell environment. Set the variables with `launchctl setenv` (e.g. `launchctl setenv OLLAMA_HOST 127.0.0.1:11435`), then quit and restart Ollama from the menu bar. These values are lost on reboot: either set them again (or from a LaunchAgent), or prevent Ollama from starting at login and let OllamaRouter start it (see [Running](#running)), as it passes `OLLAMA_HOST` itself.
>
> If Ollama is left on its default port, it keeps listening on `127.0.0.1:11434` alongside the router, which listens on all interfaces on the same port: macOS allows both without any error, so local clients would bypass the router and the router would fail to reach the local instance on `11435`.

Pull the model you want to use locally, e.g.:
```powershell
ollama pull jetelain/Qwen3.8-27B:iq3
```

Create an alias for this model, so it matches the name used on the remote instance (feel free to tune the parameters), e.g.:
```powershell
Invoke-RestMethod -Uri 'http://localhost:11435/api/create' -Method Post -ContentType 'application/json' -Body '{
  "model": "Qwen3.8-27B",
  "from": "jetelain/Qwen3.8-27B:iq3",
  "parameters": {
    "num_ctx": 49152
  }
}'
```

> With `num_ctx` set to `49152`, the corresponding `Models:Qwen3.8-27B:MaxLocalTokens` setting should be around `36864` (75% of `num_ctx`, see [Configuration](#configuration)).

### Remote Ollama

See [this guide](https://github.com/jetelain/wiki/blob/main/ai/server.md) for a more comprehensive walkthrough of setting up a remote Ollama instance.

Set the following environment variable:
- `OLLAMA_HOST` to `0.0.0.0:11434`

Then install (or restart) Ollama for the change to take effect.

Pull the model you want to use, e.g.:
```bash
ollama pull jetelain/Qwen3.8-27B:latest
```

Create an alias for this model using the exact same name as on the local instance (feel free to tune the parameters), e.g.:
```bash
curl http://localhost:11434/api/create -d '{
  "model": "Qwen3.8-27B",
  "from": "jetelain/Qwen3.8-27B",
  "parameters": {
    "num_ctx": 196608
  }
}'
```

> The remote instance's larger `num_ctx` (`196608`) is only reached once the request no longer fits locally, so it does not need to follow the same 75% rule: the remote instance has no comparable `MaxLocalTokens` cap.

## Configuration

Configuration is provided through the `OllamaRouter` section of `appsettings.json` (or any other standard .NET configuration source, such as environment variables):

```json
{
  "OllamaRouter": {
	"Models": {
	  "Qwen3.8-27B": {
		  "MaxLocalTokens": 36864,
		  "MinRequiredVramMB": 13500,
		  "CloudModel": "deepseek-v3.1:671b-cloud"
		},
	  "llama3": {
		"MaxLocalTokens": 24576,
		"MinRequiredVramMB": 4096
	  }
	},
	"LocalUrl": "http://127.0.0.1:11435",
	"RemoteUrl": "http://aiserver.local:11434"
	}
}
```

> **Note on model names.** Configuration keys **must not include the tag suffix** (`:latest`, `:8b`, …): the .NET configuration binder treats `:` as a path separator, so a key such as `"Qwen3.8-27B:latest"` does not bind to the dictionary. Clients also frequently send the implicit `:latest` tag even when the model is stored without one, so the router strips the tag part before looking up the configuration (see `RoutingDecisionService.NormalizeModelName`). Use a single entry per base model name; all tag variants will share the same thresholds.

| Setting            | Description                                                                                     |
|--------------------|---------------------------------------------------------------------------------------------------|
| `Models`           | Dictionary of model names (base name, no tag) to their routing thresholds. **Any model not listed here is always routed to the remote instance.**                          |
| `Models:*:MaxLocalTokens`   | Maximum estimated token count that the local instance is allowed to handle for this model. Keep margin to leave room for model output, 75% of the local model's configured `num_ctx` should be fine. |
| `Models:*:MinRequiredVramMB`| Minimum amount of free VRAM (in MB) required on the local GPU to route a request for this model there. It should match VRAM usage of the model with a little margin.           |
| `Models:*:CloudModel`| Optional name of the equivalent model hosted on [ollama.com cloud](https://ollama.com) (e.g. `glm-5.3-flash:cloud`). When set, enables overflow to **Cloud** for this model whenever Remote is busy (see [Cloud overflow](#cloud-overflow)). Leave unset to disable cloud overflow for this model. |
| `LocalUrl`         | Base URL of the local Ollama instance.                                                            |
| `RemoteUrl`        | Base URL of the remote Ollama instance.                                                           |
| `BindAddress`      | Address the router itself listens on. Defaults to `http://localhost:11434`. Set it to e.g. `http://0.0.0.0:11434` to accept connections from other machines. |
| `TokenEstimator`   | Token estimation strategy: `"Heuristic"` (default, fast, zero heap memory overhead, saves ~24 MB RAM by omitting the BPE dictionary) or `"Tiktoken"` (uses Microsoft.ML.Tokenizers `cl100k_base`). |
| `TokenEstimationOverheadFactor` | Multiplicative correction applied to the estimated token count, to compensate for the systematic underestimation of the generic tokenizer versus the actual tokenizer and chat template of targeted models. Defaults to `1.0` (no correction); see [recommendations below](#token-estimation-overhead-recommendations). |
| `MaxConsecutiveFailures` | Number of consecutive failures on a target for adjacent requests within `ConsecutiveFailuresTokenTolerance` before switching to the next target in the chain (`Local` → `Remote` → `Cloud`). Defaults to `3`. Set to `0` to disable automatic target switching. |
| `ConsecutiveFailuresTokenTolerance` | Maximum difference in estimated prompt tokens between adjacent requests to consider them part of the same retry streak. Defaults to `4000`. Set to `0` to require an exact match. |
| `Recording:Enabled` | Enables the optional [inference request recording](#inference-request-recording-optional), saving each request's metadata and full request body for investigation of estimated vs actual token counts. Defaults to `false`. |
| `Recording:OutputDirectory` | Directory where the records are stored: a `requests.jsonl` manifest plus one pretty-printed payload file per request. Defaults to the `inference-requests` folder in the local application data folder (`%LOCALAPPDATA%\OllamaRouter\inference-requests` on Windows, `~/Library/Application Support/OllamaRouter/inference-requests` on macOS). |
| `Recording:MaxFiles` | Maximum number of payload files to keep in the output directory; the oldest records are deleted once this count is exceeded (and the manifest is trimmed to match). Defaults to `500`. Set to `0` to keep all files. |
| `Pricing:PromptPricePerMillion` | Optional price per 1,000,000 prompt / input tokens. Used to calculate 7-day estimated savings and cloud overflow cost. Alias: `InputPricePerMillion`. |
| `Pricing:CompletionPricePerMillion` | Optional price per 1,000,000 completion / output tokens. Used to calculate 7-day estimated savings and cloud overflow cost. Alias: `OutputPricePerMillion`. |
| `Pricing:PricePerMillion` | Optional flat price per 1,000,000 tokens when prompt and completion rates are not differentiated. |
| `Pricing:Currency` | Currency symbol or label to display in the monitoring UI (e.g. `$`, `€`, `USD`). Defaults to `$`. |
| `TerminalMonitoring:Enabled` | Enables the in-terminal live activity monitor in the server console (zero VRAM consumption). Defaults to `true`. Can also be disabled via `--no-tui`. |
| `TerminalMonitoring:RefreshIntervalSeconds` | Refresh interval in seconds for the in-terminal live monitor. Defaults to `2.0`. |

### Token estimation overhead recommendations

OllamaRouter estimates prompt token count prior to routing so it can check against `MaxLocalTokens` without having to download full tokenizer files for every model. Because generic tokenizers (`Tiktoken` or `Heuristic`) differ from the exact vocabulary and chat templates used by targeted models, set `TokenEstimationOverheadFactor` according to your primary model family:

| Model Family | Vocabulary Size | Recommended (`Tiktoken`) | Recommended (`Heuristic`) | Notes |
| :--- | :---: | :---: | :---: | :--- |
| **Qwen 3.8** (e.g. `Qwen3.8-27B`) | 248k | **1.09** | **1.11** | Qwen 3.8 expands token embeddings to 248,320. 1.09 is empirically validated for high-context workloads. |

> **Tip.** The monitoring page (`/monitor`) dynamically measures the ratio between raw estimated tokens and Ollama's actual reported prompt tokens for every completed request, providing you with a tailored recommendation for your exact models and prompts.
>
> If your prompts are predominantly non-English (e.g. French, German, Chinese) or contain heavy code indentation, add an extra `+0.05` margin to the overhead factor to prevent unexpected overflows.

### GPU memory on macOS

On macOS, there is no `nvidia-smi`: the free VRAM is estimated from the Apple Silicon unified memory (`AppleUnifiedMemoryVramProvider`), using `sysctl` and `vm_stat`:

- The GPU can only wire part of the RAM: `iogpu.wired_limit_mb` when it has been set (`sudo sysctl iogpu.wired_limit_mb=<MB>`), otherwise the macOS default, about 2/3 of the RAM up to 36 GB and 3/4 above.
- The reported value is the smallest of the remaining GPU budget (that limit minus the memory already wired, including models already loaded) and the memory the OS can hand out without swapping (free, inactive and speculative pages).

This value varies with the memory used by the other applications, so `MinRequiredVramMB` should be set according to the Mac: on a 24 GB Mac, about 12 to 14 GB is reported.

### Inference request recording (optional)

Because the optimal overhead factor depends on your exact models and prompt mix, OllamaRouter can record every inference request so the difference between the **estimated** token count (used for routing) and the **actual** token count (reported by Ollama) can be investigated per request. This recording mode is **disabled by default**.

Enable it in `appsettings.json`:

```json
"Recording": {
  "Enabled": true
}
```

Each recorded request then produces, in the output directory (default `%LOCALAPPDATA%\OllamaRouter\inference-requests\` on Windows, `~/Library/Application Support/OllamaRouter/inference-requests/` on macOS):

- one line in `requests.jsonl` with the routing metadata: `timestamp`, `target`, `model`, `endpoint`, `estimatedPromptTokens` (after applying `TokenEstimationOverheadFactor`), `rawPromptTokens` (before the factor), `actualPromptTokens` / `actualResponseTokens` as reported by the Ollama instance, `elapsedMilliseconds`, `statusCode`, `success`, and the name of the payload `file`;
- a payload file named `&lt;utc-timestamp&gt;_&lt;sequence&gt;_&lt;model&gt;.json` (e.g. `20260925-153012-123_000042_Qwen3.8-27B.json`) containing the **full request body**, pretty-printed and ready to open in an editor. File names begin with a sortable UTC timestamp followed by a per-process sequence, so files list in chronological order and remain unique even for rapid successive requests.

With both numbers and the exact payload of each request, you can tokenize locally with the model's actual tokenizer and pinpoint what drives the gap (chat template overhead, non-Latin scripts, code indentation, …) before tuning `TokenEstimationOverheadFactor`.

The output directory is capped at `Recording:MaxFiles` payload files (default `500`); when the cap is exceeded, the oldest records are deleted and the manifest is rewritten so it only references still-existing payloads. The recorder is best-effort: an I/O failure only produces a log entry and never affects routing.

## Installation

### Manual (Windows)

Download the latest [`OllamaRouter-win-x64.zip`](../../releases) from the [releases page](../../releases), extract it into a folder of your choice and run `OllamaRouter.exe`. The executable is self-contained: no .NET installation is required.

### macOS

No macOS release is published yet: run it from source, or publish a self-contained native executable (requires the Xcode Command Line Tools, `xcode-select --install`):

```bash
dotnet publish OllamaRouter/OllamaRouter.csproj -c Release -r osx-arm64 -o OllamaRouter-osx-arm64
```

### From source

```powershell
dotnet run --project OllamaRouter\OllamaRouter.csproj
```

## Running

After installation, start the router by launching `OllamaRouter.exe`, or from source:

```powershell
dotnet run --project OllamaRouter\OllamaRouter.csproj
```

By default, the router listens on `http://localhost:11434`, the standard Ollama port, so it can be used as a drop-in replacement for a direct Ollama endpoint in your existing tools and clients. On startup, a link to the [monitoring UI](#monitoring) is printed to the console.

On startup, the router also makes sure the local Ollama is running, and starts it from its default location if it is not:

- **Windows:** `%LOCALAPPDATA%\Programs\Ollama\ollama app.exe`.
- **macOS:** `/Applications/Ollama.app` (or `~/Applications/Ollama.app`), started in the background with `OLLAMA_HOST` set to the host and port of `LocalUrl`. If Ollama is already running, it is left as is: make sure it already listens on `LocalUrl` (see [Local Ollama](#local-ollama)).
- On other platforms, Ollama is expected to be managed by the OS (e.g. systemd) or started manually.

## Monitoring

OllamaRouter exposes a very lightweight, dependency-free monitoring page at `/monitor` (e.g. `http://localhost:11434/monitor`), backed by a JSON endpoint at `/monitor/api`. 

The page shows:

- Whether each instance (**Local**/**Remote**, and **Cloud** when at least one model has a `CloudModel` configured, see [Cloud overflow](#cloud-overflow)) is currently **busy** processing a chat/generate request.
- An **Enabled** checkbox per instance, to enable/disable each target at runtime. Toggling it immediately changes the routing behavior: a disabled target is completely excluded from routing and will never receive requests (unlike a busy target, which can still queue requests if all eligible targets are busy). The new state is persisted to a dedicated JSON file (`%LOCALAPPDATA%\OllamaRouter\targets.json` on Windows, `~/Library/Application Support/OllamaRouter/targets.json` on macOS), so it is restored on the next startup. The state file is written on a best-effort ("failsafe") basis: if it cannot be written, the change still takes effect in memory and a warning is logged — `appsettings.json` is never modified at runtime.
- The requests currently **in progress**, with target instance, model, estimated input tokens and running time.
- A history of the most recent completed requests (last 50), with model, estimated vs. actual input tokens, actual output tokens, elapsed time and HTTP status. Failed requests are highlighted.
- Aggregated **token statistics** per target (and overall total): number of requests, actual input tokens and actual output tokens, both for the **current day** and for the **last 7 days**. Only successful requests with actual token counts are counted. These statistics are persisted to a dedicated JSON file (`%LOCALAPPDATA%\OllamaRouter\activity-statistics.json` on Windows, `~/Library/Application Support/OllamaRouter/activity-statistics.json` on macOS) so they survive restarts, and are written on a best-effort basis (throttled to at most one write per 30 seconds, on day rollover, and on application shutdown).
- A **target usage ratio progress bar** visually breaking down the share of tokens processed by each target (**Local**, **Remote**, and **Cloud**) over the **last 7 days**.
- **Estimated savings and cloud overflow cost** based on the **last 7 days**, displayed whenever `Pricing` is configured in the settings. It calculates the money saved by serving requests locally and remotely instead of paying cloud API rates, as well as the cost incurred from cloud overflow. If pricing is not configured, this section is completely hidden.
- A **live token estimation overhead recommendation** card at the bottom of the page, computed from recent requests by comparing raw estimator token counts against the actual prompt tokens reported by Ollama. It displays the overall observed factor, per-model factors, and the exact recommended value to put in `appsettings.json`.

The page auto-refreshes every 2 seconds by polling `/monitor/api`. A clickable link to this page is printed to the console when the router starts (see [Running](#running)).

![Monitor screenshot](docs/monitor.png)

### In-terminal activity monitor (TUI)

To monitor traffic without keeping a GPU-accelerated web browser running (which consumes valuable VRAM needed for local LLMs and gaming), OllamaRouter includes a built-in **Terminal UI** that runs directly in the server console.

It displays the same live information as the monitoring page:
- Instance states (**Local**, **Remote**, **Cloud**: Idle, Busy, Offline, Disabled);
- Token statistics (**Today** and **Last 7 days**: requests, input tokens, output tokens, totals);
- Target usage ratio breakdown progress bar and legend (last 7 days);
- Estimated savings and cloud overflow costs (when `Pricing` is configured);
- In-progress requests (target, model, estimated input tokens, elapsed time);
- Recent completed requests with status highlighting.

The terminal UI is completely read-only (actions and token estimation overhead recommendations are omitted) and keeps a direct link to open the full-featured web monitoring page whenever runtime configuration changes or deep model tuning are needed.

It starts automatically when running interactively. If standard output is redirected (e.g. running as a background service or in CI), it automatically falls back to standard log output. You can also explicitly disable it by passing `--no-tui`:

```powershell
OllamaRouter.exe --no-tui
```

> Put `--no-tui` last when combining it with configuration overrides (e.g. `--OllamaRouter:BindAddress=http://127.0.0.1:18999/ --no-tui`): otherwise the next argument is read as its value and ignored.

## Requirements

- .NET 9 / .NET 10 SDK
- Two reachable Ollama instances (local and remote)
- To report the free VRAM of the local machine:
  - Windows / Linux: `nvidia-smi` (returns 0 if not available, which effectively disables local routing based on VRAM)
  - macOS (Apple Silicon): nothing to install, `sysctl` and `vm_stat` are used (see [GPU memory on macOS](#gpu-memory-on-macos))

## Project structure

- `Middleware/OllamaRoutingMiddleware.cs` – inspects intercepted requests, emits routing telemetry headers, enforces target pinning, and sets the `X-Ollama-Target` header used by YARP for routing.
- `Services/RoutingDecisionService.cs` – orchestrates the chain of target handlers to make the routing decision (first available capable target, with queued fallback when busy).
- `Services/IRoutingTargetHandler.cs` – abstraction for routing destinations (`Local`, `Remote`, `Cloud`) defining availability, overflow status, and request capability.
- `Services/LocalRoutingTargetHandler.cs` – target handler for the local Ollama instance (model configuration, token limits, loaded models, GPU VRAM).
- `Services/RemoteRoutingTargetHandler.cs` – target handler for the remote server.
- `Services/NvidiaSmiVramProvider.cs` / `Services/AppleUnifiedMemoryVramProvider.cs` – free VRAM of the local GPU, via `nvidia-smi` (Windows / Linux) or estimated from the unified memory (macOS).
- `Services/OllamaProcessLauncher.cs` – starts the local Ollama on startup if it is not running (Windows and macOS).
- `Services/CloudRoutingTargetHandler.cs` – overflow target handler relaying to ollama.com cloud models when primary instances are busy.
- `Services/RoutingContext.cs` – encapsulates request parameters, token estimates, thresholds, and shared catalog checks for a routing decision.
- `Services/OllamaModelCatalogClient.cs` – fetches and merges model catalogs (`/api/tags`, `/v1/models`, `/api/ps`) from both instances.
- `Parsing/OllamaRequestParser.cs` – extracts the prompt, model name, and JSON Schema format from request bodies.
- `ReverseProxy/OllamaReverseProxyConfig.cs` – builds the YARP routes/clusters in code.
- `Endpoints/OllamaEndpointsExtensions.cs` – hybrid endpoints (`/api/tags`, `/api/ps`, `/v1/models`) that short-circuit YARP to return merged results including configured cloud models.
- `Endpoints/MonitorEndpointsExtensions.cs` – lightweight monitoring UI (`/monitor`, `/monitor/api`), target toggles (`POST /targets`), and non-destructive VRAM reclaim (`POST /api/models/unload`, `POST /monitor/reclaim-vram`).
- `Services/ActivityMonitorService.cs` – in-memory bookkeeping of busy state, in-progress requests and recent request history used by the monitoring UI.
- `Services/ActivityStatisticsService.cs` – per-target counters (requests, actual input/output tokens) with current-day and last-7-days aggregation, persisted on a best-effort basis to a dedicated JSON file in the application data folder (`%LOCALAPPDATA%\OllamaRouter\activity-statistics.json` on Windows, `~/Library/Application Support/OllamaRouter/activity-statistics.json` on macOS) and exposed via `/monitor/api`.
- `Services/TargetAvailabilityService.cs` – in-memory state of the Local/Remote/Cloud enable/disable flags, loaded at startup from and persisted on a best-effort basis to a dedicated JSON state file in the application data folder (`%LOCALAPPDATA%\OllamaRouter\targets.json` on Windows, `~/Library/Application Support/OllamaRouter/targets.json` on macOS), toggled via `POST /targets`.
- `Services/InferenceRequestRecorder.cs` – optional, best-effort recorder of inference requests (a `requests.jsonl` metadata manifest plus one pretty-printed payload file per request with the full request body) used to investigate estimated vs actual token counts; disabled by default, records are stored in the application data folder (`%LOCALAPPDATA%\OllamaRouter\inference-requests\` on Windows, `~/Library/Application Support/OllamaRouter/inference-requests/` on macOS).

## Tests

Unit tests are located in `OllamaRouter.Tests` and cover the request parser, the routing decision policy, the model catalog merge logic, the middleware, and the YARP configuration.

```powershell
dotnet test
```

### End-to-end tests

`OllamaRouter.E2ETests` is a standalone console executable that exercises a **running** OllamaRouter instance (and its underlying local/remote Ollama instances) through both the native Ollama API (using [OllamaSharp](https://github.com/awaescher/OllamaSharp)) and the OpenAI-compatible API (using `Microsoft.Extensions.AI.OpenAI`). It is not a substitute for the unit tests above: it performs real HTTP calls against a live deployment and is meant to be run manually, or as a smoke test after deployment/configuration changes.

It checks that:
- `GET /api/tags` returns a non-empty merged list of models (native Ollama API).
- `GET /api/ps` returns a valid response (native Ollama API).
- `POST /api/chat` returns a completion (native Ollama API).
- `GET /v1/models` returns a non-empty merged list of models (OpenAI-compatible API).
- `POST /v1/chat/completions` returns a completion (OpenAI-compatible API).

Before running it, make sure OllamaRouter is running (see [Running](#running)) and that both the local and remote Ollama instances are up and have the expected model pulled and aliased (see [Setup Ollama instances](#setup-ollama-instances)).

```powershell
dotnet run --project OllamaRouter.E2ETests\OllamaRouter.E2ETests.csproj
```

The executable prints a `PASS`/`FAIL` line per scenario and exits with a non-zero code if any scenario fails, so it can be wired into a CI pipeline or a post-deployment check. It can be configured through the following environment variables:

| Variable                   | Description                                                        | Default                   |
|----------------------------|----------------------------------------------------------------------|----------------------------|
| `OLLAMAROUTER_E2E_URL`     | Base URL of the OllamaRouter instance under test.                    | `http://localhost:11434`  |
| `OLLAMAROUTER_E2E_MODEL`   | Name of the model aliased on both the local and remote instances.    | `Qwen3.8-27B:latest`      |

## License

OllamaRouter is released under the [MIT License](LICENSE).

> **Disclaimer.** OllamaRouter is an independent, open-source project and is **not affiliated with, endorsed by, or associated with Ollama Inc.** in any way. "Ollama" and the related trademarks are the property of Ollama Inc.
