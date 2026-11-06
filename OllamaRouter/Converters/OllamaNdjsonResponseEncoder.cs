using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OllamaRouter.Converters;

/// <summary>
/// Encodes canonical responses as Ollama NDJSON (<c>/api/chat</c> / <c>/api/generate</c> wire shape).
/// Frames are rendered with a hand-rolled <see cref="Utf8JsonWriter"/> over a pooled
/// <see cref="ArrayPool{T}"/> buffer (compact single-line JSON — the source-generated serializer
/// context is indented and therefore unsuitable for the wire). Stateless — safe to share across
/// concurrent streams.
/// </summary>
public sealed class OllamaNdjsonResponseEncoder : IChatResponseEncoder
{
    private const int FixedFrameOverhead = 192;

    public string ContentType => "application/x-ndjson";

    public ValueTask WriteStreamChunkAsync(
        PipeWriter writer,
        ChatResponseUpdate update,
        string modelName,
        CancellationToken cancellationToken)
    {
        var content = update.Text ?? string.Empty;
        return RentedFrameWriter.WriteToPipeAsync(writer, EstimateSize(modelName, content), (frame, json) =>
        {
            RenderFrame(json, modelName, content, done: false, null, null, null, null);
            json.Flush();
            frame.AppendByte((byte)'\n');
        }, cancellationToken);
    }

    public ValueTask WriteStreamEndAsync(
        PipeWriter writer,
        string modelName,
        UsageDetails? usage,
        TimeSpan duration,
        ChatFinishReason? finishReason,
        CancellationToken cancellationToken)
    {
        var doneReason = ProtocolConverterJsonHelpers.ToWireFinishReason(finishReason);
        var totalDurationNs = ToNanoseconds(duration);
        long? promptEvalCount = usage?.InputTokenCount;
        long? evalCount = usage?.OutputTokenCount;

        return RentedFrameWriter.WriteToPipeAsync(writer, EstimateSize(modelName, string.Empty), (frame, json) =>
        {
            RenderFrame(json, modelName, string.Empty, done: true, doneReason, totalDurationNs, promptEvalCount, evalCount);
            json.Flush();
            frame.AppendByte((byte)'\n');
        }, cancellationToken);
    }

    public ValueTask WriteNonStreamingResponseAsync(
        Stream responseStream,
        ChatResponse completion,
        string modelName,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var content = ProtocolConverterJsonHelpers.ExtractAssistantText(completion);
        var doneReason = ProtocolConverterJsonHelpers.ToWireFinishReason(completion.FinishReason);
        var totalDurationNs = ToNanoseconds(duration);
        long? promptEvalCount = completion.Usage?.InputTokenCount;
        long? evalCount = completion.Usage?.OutputTokenCount;

        return RentedFrameWriter.WriteToStreamAsync(responseStream, EstimateSize(modelName, content), (frame, json) =>
        {
            RenderFrame(json, modelName, content, done: true, doneReason, totalDurationNs, promptEvalCount, evalCount);
            json.Flush();
            // Single NDJSON object; trailing newline written for consistency.
            frame.AppendByte((byte)'\n');
        }, cancellationToken);
    }

    /// <summary>
    /// Exact field order: model, created_at, message, done[, done_reason, total_duration,
    /// prompt_eval_count, eval_count]; null-valued count fields are omitted.
    /// </summary>
    private static void RenderFrame(
        Utf8JsonWriter json,
        string modelName,
        string content,
        bool done,
        string? doneReason,
        long? totalDurationNs,
        long? promptEvalCount,
        long? evalCount)
    {
        json.WriteStartObject();
        json.WriteString("model", modelName);
        // Explicit RFC 3339 UTC rendering (the runtime's built-in DateTimeOffset format is not wire-stable).
        json.WriteString("created_at", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture));
        json.WriteStartObject("message");
        json.WriteString("role", "assistant");
        json.WriteString("content", content);
        json.WriteEndObject();
        json.WriteBoolean("done", done);

        if (done)
        {
            json.WriteString("done_reason", doneReason ?? "stop");

            // Ollama durations are int64 nanoseconds.
            json.WriteNumber("total_duration", totalDurationNs ?? 0L);

            // Count fields are emitted only when the individual count is present.
            if (promptEvalCount is long promptCount)
            {
                json.WriteNumber("prompt_eval_count", promptCount);
            }

            if (evalCount is long eval)
            {
                json.WriteNumber("eval_count", eval);
            }
        }

        json.WriteEndObject();
    }

    /// <summary>
    /// Initial pooled-buffer hint (fixed frame overhead + escape headroom); the growth loop in
    /// <see cref="RentedFrameWriter"/> covers any larger frame.
    /// </summary>
    private static int EstimateSize(string modelName, string content)
        => Math.Max(FixedFrameOverhead, FixedFrameOverhead + 4 * (modelName.Length + content.Length));

    private static long ToNanoseconds(TimeSpan duration) => (long)(duration.Ticks * 100);
}
