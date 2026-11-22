using OllamaRouter.Endpoints.RouterApi.ApiKeys;
using OllamaRouter.Options;
using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Config;

/// <summary>
/// Response payload of <c>GET /api/router/config</c> (task #19): the full live router
/// configuration — the effective fleet targets (configuration order; the Cloud
/// pseudo-target is never listed), cloud overflow, dynamic context, recording, failure
/// resilience and pricing. Contains NO credential fields (the product has no API keys).
/// </summary>
public sealed record RouterConfigResponse(
    [property: JsonPropertyName("targets")] IReadOnlyList<RouterTargetConfigDto> Targets,
    [property: JsonPropertyName("cloud")] RouterCloudConfigDto Cloud,
    [property: JsonPropertyName("dynamicContext")] RouterDynamicContextConfigDto DynamicContext,
    [property: JsonPropertyName("recording")] RouterRecordingConfigDto Recording,
    [property: JsonPropertyName("resilience")] RouterResilienceConfigDto Resilience,
    [property: JsonPropertyName("pricing")] RouterPricingConfigDto Pricing,
    [property: JsonPropertyName("apiSecurity")] ApiSecurityConfigDto ApiSecurity);

/// <summary>
/// One fleet target row of <c>GET /api/router/config</c> / the target CRUD responses
/// (task #19): the identity, the administrative enablement, the free-form hardware hint
/// (null when unset; display-only, no v1 routing consumer) and the parallel budget. Task #61
/// adds the protocol family <c>type</c> (lowercase enum name), the informational
/// <c>provider</c> label (null when unset), the declared <c>models</c> roster (null when empty)
/// and <c>apiKeyConfigured</c> — a BOOL flag; the raw or wrapped key is NEVER returned by any
/// endpoint, log or telemetry. Task #84 appends <c>powerWatts</c> (number|null — the node
/// power-draw override for the electricity-cost leg; null = unset ⇒ the resolution chain
/// <c>Pricing:DefaultNodePowerWatts</c> → 150 W applies). Task #143 appends the daemon-side
/// hardware probe ssh credential <c>sshUser</c> (string|null — null = the target is not
/// ssh-probeable; the App UI uses it to flag probeable rows) and <c>sshPort</c> (number|null,
/// null = the default port 22).
/// </summary>
public sealed record RouterTargetConfigDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("hardwareHint")] string? HardwareHint,
    [property: JsonPropertyName("maxParallel")] int MaxParallel,
    [property: JsonPropertyName("type")] string Type = "ollama",
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("models")] IReadOnlyList<Endpoints.RouterApi.Targets.TargetModelDto>? Models = null,
    [property: JsonPropertyName("apiKeyConfigured")] bool ApiKeyConfigured = false,
    // Task #84: node power-draw override (watts; null = unset). Appended at the END to avoid
    // silently shifting the positional arguments at every construction site.
    [property: JsonPropertyName("powerWatts")] double? PowerWatts = null,
    // Task #143: the ssh credential exposed so the UI can know which rows are ssh-probeable
    // by the daemon-side explicit hardware probe endpoint (null = unset; the probe host is
    // derived from the target url). Appended at the END for positional-compatibility.
    [property: JsonPropertyName("sshUser")] string? SshUser = null,
    [property: JsonPropertyName("sshPort")] int? SshPort = null,
    // Task #153: the PERSISTED max VRAM override in MB (null = unset; the fleet endpoint's
    // EFFECTIVE value falls back to the hardware profile, so without this field an override
    // is invisible on the Fleet tab card). Read-only on the wire; appended at the END for
    // positional-compatibility.
    [property: JsonPropertyName("maxVramMB")] int? MaxVramMB = null);

/// <summary>
/// The cloud section of <c>GET /api/router/config</c> (task #19). Contains EXACTLY
/// <c>enabled</c>, <c>defaultModel</c>, <c>preferredRelayTarget</c> and
/// <c>overflowConfigured</c> (<c>CloudOverflowOptions</c> has no key material — the cloud
/// relay depends on the fleet node's ollama.com sign-in; no credential fields exist to
/// mask). <c>overflowConfigured</c> is <see cref="OllamaRouterOptions.IsCloudOverflowConfigured"/>.
/// </summary>
public sealed record RouterCloudConfigDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("defaultModel")] string? DefaultModel,
    [property: JsonPropertyName("preferredRelayTarget")] string PreferredRelayTarget,
    [property: JsonPropertyName("overflowConfigured")] bool OverflowConfigured);

/// <summary>
/// The dynamicContext section (task #19), mirroring the real
/// <see cref="DynamicContextOptions"/> fields (there is no bucketStepTokens knob: 32,768
/// exists only as the read-only <c>DynamicContextOptions.DefaultBaselineTokens</c>
/// constant).
/// </summary>
public sealed record RouterDynamicContextConfigDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("denseClampEnabled")] bool DenseClampEnabled,
    [property: JsonPropertyName("autoShrinkCooldownTurns")] int AutoShrinkCooldownTurns,
    [property: JsonPropertyName("autoShrinkIdleTimeoutSeconds")] int AutoShrinkIdleTimeoutSeconds);

/// <summary>
/// The recording section of <c>GET /api/router/config</c> (task #19). Mirrors
/// <see cref="RecordingOptions"/> minus <c>TokenMetricsCsv</c> (intentionally NOT exposed
/// — out of scope); when the section is unset the defaults
/// (enabled:false, outputDirectory:null, maxFiles:500) are reported.
/// </summary>
public sealed record RouterRecordingConfigDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("outputDirectory")] string? OutputDirectory,
    [property: JsonPropertyName("maxFiles")] int MaxFiles = 500);

/// <summary>
/// The resilience section of <c>GET /api/router/config</c> (task #19): the per-target
/// failure-streak threshold (0 = streak skipping disabled) and the retry-streak token
/// tolerance (0 = exact match required).
/// </summary>
public sealed record RouterResilienceConfigDto(
    [property: JsonPropertyName("maxConsecutiveFailures")] int MaxConsecutiveFailures = 3,
    [property: JsonPropertyName("consecutiveFailuresTokenTolerance")] int ConsecutiveFailuresTokenTolerance = 4000);

/// <summary>
/// The pricing section of <c>GET /api/router/config</c> (task #19): the per-million-token
/// rates (null = rate not configured) and the display currency. Task #84 appends
/// <c>electricityPricePerKwh</c> (number|null; null = unset ⇒ the 0.20/kWh default applies) and
/// <c>defaultNodePowerWatts</c> (number|null; null = unset ⇒ the 150 W default applies) for the
/// actual-savings electricity-cost leg.
/// </summary>
public sealed record RouterPricingConfigDto(
    [property: JsonPropertyName("promptPricePerMillion")] double? PromptPricePerMillion,
    [property: JsonPropertyName("completionPricePerMillion")] double? CompletionPricePerMillion,
    [property: JsonPropertyName("pricePerMillion")] double? PricePerMillion,
    [property: JsonPropertyName("currency")] string Currency = "$",
    // Task #83: the cached prompt-token rate (null = not differentiated). Appended at the END
    // (NOT before the defaulted Currency) to avoid silently shifting the positional currency
    // argument at every construction site.
    [property: JsonPropertyName("cachedPromptPricePerMillion")] double? CachedPromptPricePerMillion = null,
    // Task #84: electricity price per kWh (null = unset ⇒ 0.20 effective) and the global
    // default node power draw (null = unset ⇒ 150 W effective). Appended at the END for
    // positional-compatibility.
    [property: JsonPropertyName("electricityPricePerKwh")] double? ElectricityPricePerKwh = null,
    [property: JsonPropertyName("defaultNodePowerWatts")] double? DefaultNodePowerWatts = null);

/// <summary>
/// Request body of <c>PUT /api/router/config</c> (task #19): a partial per-section update
/// — any subset of cloud / dynamicContext / recording / resilience / pricing (all five
/// null/absent → 400 "no configuration sections provided").
/// </summary>
public sealed record UpdateRouterConfigRequest(
    [property: JsonPropertyName("cloud")] UpdateCloudConfigRequest? Cloud = null,
    [property: JsonPropertyName("dynamicContext")] RouterDynamicContextConfigDto? DynamicContext = null,
    [property: JsonPropertyName("recording")] RouterRecordingConfigDto? Recording = null,
    [property: JsonPropertyName("resilience")] RouterResilienceConfigDto? Resilience = null,
    [property: JsonPropertyName("pricing")] RouterPricingConfigDto? Pricing = null);

/// <summary>
/// The cloud section of a <c>PUT /api/router/config</c> body (task #19): <c>enabled</c> is
/// always applied; <c>defaultModel</c> / <c>preferredRelayTarget</c> null/absent = no
/// change, <c>""</c> = clear (defaultModel → null, preferredRelayTarget → "Local"). There
/// is NO apiKey field: the router's own API-key system (task #50) is managed exclusively
/// via the <c>/api/router/api-keys</c> endpoints and the <c>ApiSecurity</c> config section —
/// key material is never part of the config endpoints' wire format.
/// </summary>
public sealed record UpdateCloudConfigRequest(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("defaultModel")] string? DefaultModel = null,
    [property: JsonPropertyName("preferredRelayTarget")] string? PreferredRelayTarget = null);

/// <summary>
/// Response of <c>PUT /api/router/config</c> (task #19): success flag + message, and on
/// success the resulting FULL configuration echoed (the exact GET shape, re-read from the
/// now-updated live options). <c>config</c> is omitted on failure.
/// </summary>
public sealed record UpdateRouterConfigResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("config")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouterConfigResponse? Config = null);
