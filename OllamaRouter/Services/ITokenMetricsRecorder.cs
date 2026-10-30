using Pmad.AI.Tokens;

namespace OllamaRouter.Services;

/// <summary>
/// Optional recorder for token metrics: pairs the raw structural text metrics of an inference
/// request (<see cref="TokenMetrics"/>) with the actual token counts evaluated by the Ollama
/// instance, written as RFC 4180 CSV rows for calibrating the
/// <see cref="HeuristicTokenEstimator"/> model-family coefficients (regression / curve fitting
/// in Python, Excel, or R). A no-op when the CSV export is disabled.
/// </summary>
public interface ITokenMetricsRecorder
{
    /// <summary>True when CSV recording is configured and will write files.</summary>
    bool IsEnabled { get; }

    /// <summary>Best-effort, never throws. No-op when <see cref="IsEnabled"/> is false.</summary>
    void Record(TokenMetricsCsvRecord record);
}

/// <summary>
/// One aggregate data row of the token metrics CSV dataset: the structural metrics of the
/// request's context (final forwarded body) plus the actual token counts reported by Ollama
/// (empty/absent when the captured response tail carried no usage data).
/// </summary>
public sealed record TokenMetricsCsvRecord(
    DateTimeOffset Timestamp,
    Guid RequestId,
    string Model,
    string Target,
    string Endpoint,
    TokenMetrics AggregateMetrics, // Words, Symbols, NonAscii, CharCount
    int PriorContextTokenCount,
    int EstimatedPromptTokens,
    int? ActualPromptTokens,
    int? ActualResponseTokens,
    long ElapsedMilliseconds,
    int StatusCode,
    bool Success);
