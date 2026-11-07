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
/// (null when unset; display-only, no v1 routing consumer) and the parallel budget.
/// </summary>
public sealed record RouterTargetConfigDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("hardwareHint")] string? HardwareHint,
    [property: JsonPropertyName("maxParallel")] int MaxParallel);

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
/// rates (null = rate not configured) and the display currency.
/// </summary>
public sealed record RouterPricingConfigDto(
    [property: JsonPropertyName("promptPricePerMillion")] double? PromptPricePerMillion,
    [property: JsonPropertyName("completionPricePerMillion")] double? CompletionPricePerMillion,
    [property: JsonPropertyName("pricePerMillion")] double? PricePerMillion,
    [property: JsonPropertyName("currency")] string Currency = "$");

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
