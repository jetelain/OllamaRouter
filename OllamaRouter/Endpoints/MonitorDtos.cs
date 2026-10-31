using System.Text.Json.Serialization;
using OllamaRouter.Services;

namespace OllamaRouter.Endpoints;

/// <summary>
/// Request payload for <c>POST /targets</c>: a flat JSON object of target-name to enabled flag
/// (e.g. {"Local": true, "Remote": false, "Cloud": true}); keys are matched case-insensitively
/// against the fleet target names, so legacy lowercase clients keep working.
/// </summary>
public sealed class TargetsUpdateRequest
{
    [JsonExtensionData]
    public Dictionary<string, object>? Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Interprets the flat extension-data object as a target-name to enabled-flag dictionary.
    /// </summary>
    public IReadOnlyDictionary<string, bool> AsFlags() =>
        (Values ?? new Dictionary<string, object>())
            .Select(pair => (Key: pair.Key, Value: pair.Value))
            .Where(pair => pair.Value is bool || pair.Value is System.Text.Json.JsonElement)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value is bool b ? b : ((System.Text.Json.JsonElement)pair.Value!).GetBoolean(),
                StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Response payload for <c>POST /targets</c>: the same flat target-name to enabled flag object
/// that was applied.
/// </summary>
public sealed class TargetsUpdateResponse
{
    [JsonExtensionData]
    public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TargetsUpdateResponse() { }

    public TargetsUpdateResponse(IReadOnlyDictionary<string, bool> values)
    {
        foreach (var (key, value) in values)
        {
            Values[key] = value;
        }
    }
}

public sealed record ReclaimVramResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("unloadedModels")] IReadOnlyList<string> UnloadedModels,
    [property: JsonPropertyName("localDisabled")] bool LocalDisabled);

public sealed record MonitorCostStatus(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("estimatedSavings")] double EstimatedSavings,
    [property: JsonPropertyName("cloudOverflowCost")] double CloudOverflowCost,
    [property: JsonPropertyName("perTargetSavings")] IReadOnlyDictionary<string, double> PerTargetSavings);

/// <summary>
/// One row of the ordered multi-target fleet view (configured targets first, Cloud last):
/// identity, enablement, liveness (Online/Offline/Probing), the currently loaded model and
/// in-flight request count (execution tracker; Cloud always null/0), the hardware profile,
/// the current context bucket allocation (auto-shrink lease; Cloud always "baseline"), and
/// the lowercase location classification ("local"/"remote"/"cloud"; Cloud always "cloud").
/// </summary>
public sealed record MonitorFleetTargetItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("liveness")] string Liveness,
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("contextAllocation")] string ContextAllocation,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("url")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null,
    [property: JsonPropertyName("activeModel")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActiveModel = null,
    [property: JsonPropertyName("hardwareProfile")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HardwareProfile = null);

public sealed record MonitorStateResponse(
    [property: JsonPropertyName("cloudEnabled")] bool CloudEnabled,
    [property: JsonPropertyName("fleet")] IReadOnlyList<MonitorFleetTargetItem> Fleet,
    [property: JsonPropertyName("statistics")] MonitorStatisticsStatus Statistics,
    [property: JsonPropertyName("inProgress")] IReadOnlyList<MonitorInProgressItem> InProgress,
    [property: JsonPropertyName("requests")] IReadOnlyList<MonitorRecentRequestItem> Requests,
    [property: JsonPropertyName("cost")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MonitorCostStatus? Cost = null,
    [property: JsonPropertyName("overhead")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MonitorOverheadStatus? Overhead = null);

/// <summary>
/// Per-period usage statistics keyed by display target name, plus a reserved "total" key.
/// </summary>
public sealed record MonitorStatisticsStatus(
    [property: JsonPropertyName("today")] IReadOnlyDictionary<string, ActivityStatisticsTotals> Today,
    [property: JsonPropertyName("last7Days")] IReadOnlyDictionary<string, ActivityStatisticsTotals> Last7Days);

public sealed record MonitorInProgressItem(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("estimatedPromptTokens")] int EstimatedPromptTokens,
    [property: JsonPropertyName("elapsedMs")] double ElapsedMs);

public sealed record MonitorRecentRequestItem(
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("estimatedPromptTokens")] int EstimatedPromptTokens,
    [property: JsonPropertyName("actualPromptTokens")] int? ActualPromptTokens,
    [property: JsonPropertyName("actualResponseTokens")] int? ActualResponseTokens,
    [property: JsonPropertyName("elapsedMs")] double ElapsedMs,
    [property: JsonPropertyName("statusCode")] int? StatusCode,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("rawPromptTokens")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RawPromptTokens = null);

public sealed record MonitorOverheadRecommendation(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("observedFactor")] double ObservedFactor,
    [property: JsonPropertyName("sampleCount")] int SampleCount);

public sealed record MonitorOverheadStatus(
    [property: JsonPropertyName("configuredFactor")] double ConfiguredFactor,
    [property: JsonPropertyName("estimator")] string Estimator,
    [property: JsonPropertyName("overallObservedFactor")] double? OverallObservedFactor,
    [property: JsonPropertyName("sampleCount")] int SampleCount,
    [property: JsonPropertyName("models")] IReadOnlyList<MonitorOverheadRecommendation> Models);
