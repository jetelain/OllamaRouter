using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Sessions;

/// <summary>
/// One row of <c>GET /api/router/monitor/sessions</c> (task #85): the session ID (first-seen
/// casing), the session-wide accumulated counters (sums over the per-target buckets — input
/// tokens are CACHED INCLUSIVE; cached are reported separately as a subset) and the cost
/// figures computed at READ time from the currently configured pricing (4-decimal rounding,
/// never clamped; Cloud targets contribute their token value to <see cref="CloudValue"/> but
/// no electricity/savings). <see cref="ActualCloudCost"/> is the Cloud pseudo-target's OWN
/// token value — the actual money paid to cloud (<c>0.0</c> when the Cloud target was never
/// used; distinct from the all-cloud <see cref="CloudValue"/> baseline, which values EVERY
/// token at cloud rates). <see cref="Cost"/> is the session's total actual spend,
/// <c>round(actualCloudCost + electricityCost, 4)</c> (cloud spend + local power) — NOT the
/// savings-vs-baseline figure. The cost fields are null-omitted when no token price rate is
/// configured (<c>Pricing.IsConfigured</c> is false — the monitor hides its whole cost section
/// in that case).
/// </summary>
public sealed record RouterSessionUsageItem(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("requests")] int Requests,
    [property: JsonPropertyName("inputTokens")] int InputTokens,
    [property: JsonPropertyName("cachedInputTokens")] int CachedInputTokens,
    [property: JsonPropertyName("outputTokens")] int OutputTokens,
    [property: JsonPropertyName("elapsedMs")] double ElapsedMs,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("cloudValue")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? CloudValue,
    [property: JsonPropertyName("electricityCost")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ElectricityCost,
    [property: JsonPropertyName("savings")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Savings,
    [property: JsonPropertyName("actualCloudCost")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ActualCloudCost,
    [property: JsonPropertyName("cost")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Cost);

/// <summary>
/// Response of <c>GET /api/router/monitor/sessions</c> (task #85): the stored per-session
/// aggregates, MOST-RECENTLY-ACTIVE FIRST (the store's LRU order — reads never refresh
/// recency). <see cref="Currency"/> is the configured <c>Pricing:Currency</c> symbol and is
/// null-omitted when no token price rate is configured (all cost fields are then absent too).
/// An empty store yields <c>count: 0</c> and an empty <c>sessions</c> array.
/// </summary>
public sealed record RouterSessionUsageListResponse(
    [property: JsonPropertyName("currency")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Currency,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("sessions")] IReadOnlyList<RouterSessionUsageItem> Sessions);

/// <summary>
/// One row of the <c>perTarget</c> breakdown of
/// <c>GET /api/router/monitor/sessions/{sessionId}</c> (task #85): the target's own
/// accumulated counters plus its cost figures. The Cloud pseudo-target row (when present)
/// carries <c>electricityCost: 0</c> and <c>savings: 0</c> (it has no node electricity and
/// its token value is a cost against the all-cloud baseline, not a saving).
/// </summary>
public sealed record RouterSessionUsageTargetItem(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("requests")] int Requests,
    [property: JsonPropertyName("inputTokens")] int InputTokens,
    [property: JsonPropertyName("cachedInputTokens")] int CachedInputTokens,
    [property: JsonPropertyName("outputTokens")] int OutputTokens,
    [property: JsonPropertyName("elapsedMs")] double ElapsedMs,
    [property: JsonPropertyName("cloudValue")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? CloudValue,
    [property: JsonPropertyName("electricityCost")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ElectricityCost,
    [property: JsonPropertyName("savings")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Savings);

/// <summary>
/// Response of <c>GET /api/router/monitor/sessions/{sessionId}</c> (task #85): the session's
/// totals at the top level (same fields as <see cref="RouterSessionUsageItem"/>, including the
/// session-level <c>actualCloudCost</c> — the Cloud target's own token value / actual cloud
/// spend — and <c>cost</c> — the total actual spend,
/// <c>round(actualCloudCost + electricityCost, 4)</c>) plus the per-target breakdown —
/// configured fleet order, Cloud last, any historical (since-renamed/removed) targets appended
/// after. Unknown session ⇒ 404 <c>OllamaErrorResponse</c>.
/// </summary>
public sealed record RouterSessionUsageDetailResponse(
    [property: JsonPropertyName("currency")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Currency,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("requests")] int Requests,
    [property: JsonPropertyName("inputTokens")] int InputTokens,
    [property: JsonPropertyName("cachedInputTokens")] int CachedInputTokens,
    [property: JsonPropertyName("outputTokens")] int OutputTokens,
    [property: JsonPropertyName("elapsedMs")] double ElapsedMs,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("cloudValue")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? CloudValue,
    [property: JsonPropertyName("electricityCost")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ElectricityCost,
    [property: JsonPropertyName("savings")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Savings,
    [property: JsonPropertyName("actualCloudCost")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ActualCloudCost,
    [property: JsonPropertyName("cost")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Cost,
    [property: JsonPropertyName("perTarget")] IReadOnlyList<RouterSessionUsageTargetItem> PerTarget);
