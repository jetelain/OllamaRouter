using System.Text.Json.Serialization;

namespace OllamaRouter.Services.Monitoring;

/// <summary>
/// Aggregated usage for a routing target (or for all targets combined, when used as a total).
/// </summary>
public sealed record ActivityStatisticsTotals(
    [property: JsonPropertyName("requests")] int Requests,
    [property: JsonPropertyName("inputTokens")] int InputTokens,
    [property: JsonPropertyName("outputTokens")] int OutputTokens,
    // Total cached prompt tokens (a subset of InputTokens); appended at the END as an optional
    // last parameter to keep the ~74 positional construction sites source-compatible.
    [property: JsonPropertyName("cachedInputTokens")] int CachedInputTokens = 0,
    // Cumulative GPU time (milliseconds) accumulated from completed SUCCESSFUL requests
    // (task #84); appended at the END as an optional last parameter to keep the ~74
    // positional construction sites source-compatible. Exposed as `elapsedMs` in the
    // monitor statistics block (per target AND the `total` keys).
    [property: JsonPropertyName("elapsedMs")] double ElapsedMilliseconds = 0);
