using System.Text.Json.Serialization;

namespace OllamaRouter.Services;

/// <summary>
/// Optional recorder for inference requests, used to investigate the difference between the
/// estimated prompt token count (used for routing decisions) and the actual token count reported
/// by the Ollama instances. Each recorded request produces a line in a JSON Lines manifest
/// (<see cref="InferenceRequestRecord"/>) plus a payload file holding the full request body.
/// A no-op when the recording mode is disabled.
/// </summary>
public interface IInferenceRequestRecorder
{
    /// <summary>
    /// Records the routing metadata of a completed inference request together with the full
    /// request body. Best-effort only: I/O errors are logged, never thrown.
    /// </summary>
    void Record(ActivityLogEntry entry, string endpoint, string requestBody);
}

/// <summary>
/// One line of the recording manifest: the metadata of a completed inference request (see
/// <see cref="ActivityLogEntry"/>) plus the name of the payload file holding its full request
/// body.
/// </summary>
public sealed record InferenceRequestRecord(
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("estimatedPromptTokens")] int EstimatedPromptTokens,
    [property: JsonPropertyName("rawPromptTokens")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RawPromptTokens,
    [property: JsonPropertyName("actualPromptTokens")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ActualPromptTokens,
    [property: JsonPropertyName("actualResponseTokens")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ActualResponseTokens,
    [property: JsonPropertyName("elapsedMilliseconds")] long ElapsedMilliseconds,
    [property: JsonPropertyName("statusCode")] int StatusCode,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("file")] string File);
