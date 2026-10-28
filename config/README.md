# User configuration (`config/config.json`)

OllamaRouter uses a **two-layer configuration model** to keep user settings safe across application updates:

1. **`appsettings.json`** (next to the executable) — *read-only factory defaults*, shipped with the app. It is never modified at runtime and must not be used for personal or fleet-specific values.
2. **`config/config.json`** (this directory, next to the executable) — the *optional user-owned layer*. All user-changeable settings belong here.

## Key facts

- **Optional.** The file does not exist on a fresh install. The router boots with factory defaults; the file is created lazily — either by the Setup API (dynamic fleet configuration) or manually. Absence is a normal, silent state.
- **Survives updates.** Because the file lives in `config/` beside the executable — not among the files a publish/upgrade replaces — deleting or updating the application never touches your settings.
- **Reloadable.** The provider is wired with `reloadOnChange: true`, so downstream `IOptionsMonitor` consumers observe file changes at runtime.
- **Fail-open on corruption.** If `config/config.json` exists but contains invalid JSON, the router logs a prominent error at startup, **ignores the file, and continues booting with factory defaults** — a botched hand-edit or a truncated write can never brick the router. Fix or delete the file to apply user settings.

## Key format

Standard .NET configuration keys for the `OllamaRouter` section — the same JSONC shape as `appsettings.json` (comments and trailing commas are tolerated). Only include the keys you want to override; everything else falls through to the factory defaults.

```jsonc
{
  "OllamaRouter": {
    // Overrides any factory default key, e.g.:
    "TokenEstimationOverheadFactor": 1.11,
    "MaxConsecutiveFailures": 3,
    "Targets": [
      { "Name": "desktop", "Url": "http://127.0.0.1:11435", "HardwareHint": "16gb" },
      { "Name": "laptop", "Url": "http://192.168.1.20:11435", "HardwareHint": "8gb" }
    ]
  }
}
```

## Override precedence

Values later in the chain win:

```
appsettings.json  <  config/config.json  <  user secrets  <  environment variables  <  command-line arguments
```

So the user file overrides the factory defaults, while standard deployment/ops overrides (env vars, CLI args) still take precedence over the user file.
