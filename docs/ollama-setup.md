# Ollama Instance & Mesh Setup

## Setup Ollama instances

OllamaRouter routes requests across targets that expose the **same model name** (or a mapped virtual role alias). You can set up your fleet in two ways:

1. **Automated Setup Wizard & Role Recommendations (recommended for standard catalog models):** Pull native library models (e.g. `gemma4:e4b`, `qwen3.8:27b`, `qwen3.6:35b-a3b`) directly on your nodes and run the Setup Wizard (`ollamarouter setup`) or benchmark/recommendation API (`ollamarouter benchmark run`, `POST /api/router/recommendations/generate`). The router automatically enforces per-node context ceilings (`recommended.json`) without requiring separate custom Modelfile aliases per node.
2. **Custom Modelfile aliases (for mixed quantizations or custom `num_ctx`):** Run a more heavily quantized model locally (to fit in limited VRAM) and a less-quantized or full-precision version remotely (to take advantage of larger unified RAM/VRAM), both aliased under the same model name with explicit per-node `num_ctx`.

> **Note on namespaced models (`FilterNamespacedModels`).** By default (`FilterNamespacedModels: true`), OllamaRouter filters out models whose names contain a `/` namespace (such as `jetelain/Qwen3.8-27B:iq3`) from the merged fleet catalog so backing weights don't trigger redundant capability probes — only native library names (`qwen3.8:27b`), local aliases without a slash (`Qwen3.8-27B`), or models explicitly listed in `OllamaRouter:Models` are exposed for routing.

### Local Ollama

Set the following environment variables:

- `OLLAMA_HOST` to `127.0.0.1:11435`
- `OLLAMA_KV_CACHE_TYPE` to `q8_0`
- `OLLAMA_FLASH_ATTENTION` to `1`

Then install or restart Ollama for the changes to take effect.

> **macOS.** On macOS the daemon is launched by the `Ollama.app` menu bar app, so the environment must be set in the user's launchd session: run `launchctl setenv OLLAMA_HOST 127.0.0.1:11435` and restart Ollama. These values are lost on reboot — either re-set them from a LaunchAgent, or let OllamaRouter start Ollama itself, which passes `OLLAMA_HOST` at launch. Beware the default-port pitfall: an Ollama left on `127.0.0.1:11434` keeps listening alongside the router on `11434`, letting local clients bypass the router.

Pull the model you want to use locally, e.g.:

```powershell
ollama pull jetelain/Qwen3.8-27B:iq3
```

Create an alias for this model so it matches the name used on the remote instance:

```powershell
Invoke-RestMethod -Uri 'http://localhost:11435/api/create' -Method Post -ContentType 'application/json' -Body '{
  "model": "Qwen3.8-27B",
  "from": "jetelain/Qwen3.8-27B:iq3",
  "parameters": {
    "num_ctx": 49152
  }
}'
```

> **This target alias `num_ctx` is the source of truth for the router's context capacity.** Each target's maximum context (`CMax`) is the `num_ctx` pre-configured on its alias — the router does *not* invent a ceiling from the hardware. A dense request of `T` estimated tokens is qualified for a target only when `T + ContextFitMarginTokens ≤ CMax` (default margin: `8192`; see [Configuration](configuration.md)). Only when a node's alias sets **no** `num_ctx` does the router fall back to the model's native GGUF context length, then to an assumed `128,000` tokens. For **MoE** models, the pre-configured `num_ctx` is the *optimal baseline bucket*; when a request exceeds it, the router can elastically expand up to the model's native context limit (optionally capped by `Models:{model}:MaxContextTokens`).

### Remote Ollama

See [this guide](https://github.com/jetelain/wiki/blob/main/ai/server.md) for a more comprehensive walkthrough of setting up a remote Ollama instance.

Set the following environment variable:

- `OLLAMA_HOST` to `0.0.0.0:11434`

Then install or restart Ollama for the change to take effect.

Pull the model you want to use, e.g.:

```bash
ollama pull jetelain/Qwen3.8-27B:latest
```

Create an alias using the exact same name as on the local instance:

```bash
curl http://localhost:11434/api/create -d '{
  "model": "Qwen3.8-27B",
  "from": "jetelain/Qwen3.8-27B",
  "parameters": {
    "num_ctx": 196608
  }
}'
```

> The remote instance's larger `num_ctx` (`196608`) allows it to qualify for larger context workloads whenever a smaller node's context capacity is exceeded.

---

## Dual-Router Mesh (Desktop + Laptop)

OllamaRouter is designed to run as a **symmetrical mesh**: place one router on *each* node and let every router see every physical daemon. Because a router only ever forwards **directly to a physical daemon** (it never lists a peer router as a target), the mesh is loop-free by construction — there is no router→router hop to recurse on.

### Architecture

```text
                      ┌────────────────────┐            ┌────────────────────┐
   Local clients ────▶│  Desktop Router    │            │  Laptop Router     │ ◀──── Local clients
                      │   (bind :11434)    │            │   (bind :11434)    │
                      └─────────┬──────────┘            └─────────┬──────────┘
                                │  forwards ONLY to physical daemons on :11435
                                ▼                                  ▼
                 ┌───────────────────────────────┐  ┌───────────────────────────────┐
                 │ Desktop daemon :11435         │  │ Laptop daemon :11435          │
                 │ (OLLAMA_HOST=0.0.0.0:11435)   │  │ (OLLAMA_HOST=0.0.0.0:11435)   │
                 └───────────────────────────────┘  └───────────────────────────────┘
```

Both routers run the same `Targets` list (each node's local daemon + the peer's LAN daemon). A request landing on either router is routed by the shared scoring engine to the best **daemon**, so a laptop client and a desktop client get identical routing decisions regardless of which router they hit.

### Windows: moving the Ollama daemon to `OLLAMA_HOST=0.0.0.0:11435`

On Windows the Ollama GUI (`ollama app.exe`) binds **11434** automatically at user login, which is the port OllamaRouter wants to bind. Move the daemon to `11435` so the router can take `11434`:

1. Open **System environment variables** (not just *User*): `System Properties → Advanced → Environment Variables`.
2. Set `OLLAMA_HOST` = `0.0.0.0:11435` (a **System** variable so it applies to the daemon process).
3. Sign out / sign back in (or restart) and let Ollama relaunch on the new port.

`OLLAMA_HOST` must bind to `0.0.0.0` (not `127.0.0.1`) so the *peer* router can reach the daemon over the LAN. If a node only serves **local** clients through its own router, you can bind the *router* to `127.0.0.1:11434` (`BindAddress`) and keep the daemon on `0.0.0.0:11435` for the peer.

### LAN firewall: restrict the daemon to your private subnet

Binding the daemon to `0.0.0.0:11435` exposes it on every interface. Restrict inbound traffic to your private subnet (example for `192.168.1.0/24`):

```powershell
# Allow the LAN, block everything else, for the Ollama daemon port.
New-NetFirewallRule -DisplayName "Ollama daemon - LAN" -Direction Inbound -Protocol TCP -LocalPort 11435 -RemoteAddress 192.168.1.0/24 -Action Allow
New-NetFirewallRule -DisplayName "Ollama daemon - block others" -Direction Inbound -Protocol TCP -LocalPort 11435 -RemoteAddress Any -Action Block
```

### Router port-availability detection

Before starting `ollama app.exe`, the router probes the expected daemon port (`11435`, or `11434` as a fallback). If that port is already occupied by a manually managed daemon, the router logs a warning and **does not** spawn a second `ollama` process.

### Per-node `Targets` configuration

> **Loop-free invariant:** a router's `Targets` must **never** list a peer router's `:11434` — only physical daemons on `:11435`.

Desktop node (`config/config.json`):

```json
"Targets": [
  { "Name": "desktop", "Url": "http://127.0.0.1:11435", "SpeedFactor": 2.0 },
  { "Name": "laptop",  "Url": "http://192.168.1.20:11435" }
]
```

Laptop node (`config/config.json`):

```json
"Targets": [
  { "Name": "laptop",  "Url": "http://127.0.0.1:11435", "SpeedFactor": 2.0 },
  { "Name": "desktop", "Url": "http://192.168.1.10:11435" }
]
```

Swap `192.168.1.10` / `192.168.1.20` for each node's real LAN IP. When a peer target's `Url` uses port `:11435`, the router automatically infers `RouterUrl` on port `:11434` of the same host to query the peer router's `/api/router/hardware` endpoint for remote VRAM discovery (or set `RouterUrl` explicitly if the peer router binds a non-standard port). Other per-target settings (`MaxVramMB`, `KeepAliveSeconds`, `PowerWatts`, `Location`, etc.) apply as documented in [Configuration](configuration.md).

