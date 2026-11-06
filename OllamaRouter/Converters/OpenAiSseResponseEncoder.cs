using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OllamaRouter.Converters;

/// <summary>
/// Encodes canonical responses as OpenAI SSE (<c>/v1/chat/completions</c> wire shape:
/// <c>data: {json}</c> frames, finish chunk, optional usage chunk, <c>data: [DONE]</c>).
/// Frames are rendered with a hand-rolled <see cref="Utf8JsonWriter"/> over a pooled
/// <see cref="ArrayPool{T}"/> buffer (compact single-line JSON).
/// <para>
/// The per-stream <c>chatcmpl-</c> response id is lazily generated on the first write and reused
/// for every frame of that stream (OpenAI SDKs key the stream by <c>id</c>); the instance is
/// therefore NOT safe for concurrent streams — scope one instance per active stream.
/// </para>
/// </summary>
public sealed class OpenAiSseResponseEncoder : IChatResponseEncoder
{
    private const int FixedFrameOverhead = 200;
    private static readonly byte[] SseDone = Encoding.ASCII.GetBytes("data: [DONE]\n\n");
    private static readonly byte[] SseDataPrefix = Encoding.ASCII.GetBytes("data: ");

    private string? _responseId;

    public string ContentType => "text/event-stream";

    /// <summary>
    /// The lazily cached per-stream id (OpenAI SDKs key the stream by <c>id</c>); stable for the
    /// lifetime of this encoder instance.
    /// </summary>
    private string ResponseId => _responseId ??= "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];

    public ValueTask WriteStreamChunkAsync(
        PipeWriter writer,
        ChatResponseUpdate update,
        string modelName,
        CancellationToken cancellationToken)
    {
        var deltaRole = update.Role.HasValue ? update.Role.Value.ToString() : null;
        // ChatResponseUpdate.Text is non-nullable (empty string when there are no contents):
        // emit "content" only when the update actually carries content parts, so a bare update
        // renders the spec's empty delta {"delta":{}} (explicitly-empty text still emits "").
        var deltaContent = update.Contents.Count > 0 ? update.Text : null;

        return RentedFrameWriter.WriteToPipeAsync(writer, EstimateSize(modelName, deltaContent?.Length ?? 0), (frame, json) =>
        {
            frame.AppendBytes(SseDataPrefix);
            RenderChunkHeader(json, modelName);
            json.WriteStartArray("choices");
            json.WriteStartObject();
            json.WriteNumber("index", 0);
            json.WriteStartObject("delta");
            if (deltaRole is not null)
            {
                json.WriteString("role", deltaRole);
            }

            if (deltaContent is not null)
            {
                json.WriteString("content", deltaContent);
            }

            json.WriteEndObject();
            json.WriteNull("finish_reason");
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
            json.Flush();
            AppendSseFrameTail(frame);
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
        return WriteStreamEndAsync(writer, modelName, usage, finishReason, cancellationToken);
    }

    /// <summary>
    /// Terminal sequence: finish chunk (empty delta + finish_reason), optional usage chunk
    /// (empty choices), then <c>data: [DONE]\n\n</c>.
    /// </summary>
    private async ValueTask WriteStreamEndAsync(
        PipeWriter writer,
        string modelName,
        UsageDetails? usage,
        ChatFinishReason? finishReason,
        CancellationToken cancellationToken)
    {
        var finishReasonWire = ProtocolConverterJsonHelpers.ToWireFinishReason(finishReason);

        await RentedFrameWriter.WriteToPipeAsync(writer, EstimateSize(modelName), (frame, json) =>
        {
            frame.AppendBytes(SseDataPrefix);
            RenderChunkHeader(json, modelName);
            json.WriteStartArray("choices");
            json.WriteStartObject();
            json.WriteNumber("index", 0);
            json.WriteStartObject("delta");
            json.WriteEndObject();
            json.WriteString("finish_reason", finishReasonWire);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
            json.Flush();
            AppendSseFrameTail(frame);
        }, cancellationToken).ConfigureAwait(false);

        if (usage is not null)
        {
            await RentedFrameWriter.WriteToPipeAsync(writer, EstimateSize(modelName), (frame, json) =>
            {
                frame.AppendBytes(SseDataPrefix);
                RenderChunkHeader(json, modelName);
                json.WriteStartArray("choices");
                json.WriteEndArray();
                RenderUsage(json, usage);
                json.WriteEndObject();
                json.Flush();
                AppendSseFrameTail(frame);
            }, cancellationToken).ConfigureAwait(false);
        }

        await writer.WriteAsync(SseDone, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask WriteNonStreamingResponseAsync(
        Stream responseStream,
        ChatResponse completion,
        string modelName,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var content = ProtocolConverterJsonHelpers.ExtractAssistantText(completion);
        var finishReasonWire = ProtocolConverterJsonHelpers.ToWireFinishReason(completion.FinishReason);
        UsageDetails? usage = completion.Usage;

        return RentedFrameWriter.WriteToStreamAsync(responseStream, EstimateSize(modelName, content.Length), (frame, json) =>
        {
            // Single JSON object — NOT SSE — on a plain stream.
            json.WriteStartObject();
            json.WriteString("id", ResponseId);
            json.WriteString("object", "chat.completion");
            json.WriteNumber("created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            json.WriteString("model", modelName);
            json.WriteStartArray("choices");
            json.WriteStartObject();
            json.WriteNumber("index", 0);
            json.WriteStartObject("message");
            json.WriteString("role", "assistant");
            json.WriteString("content", content);
            json.WriteEndObject();
            json.WriteString("finish_reason", finishReasonWire);
            json.WriteEndObject();
            json.WriteEndArray();

            // usage object omitted when completion.Usage is null.
            if (usage is not null)
            {
                RenderUsage(json, usage);
            }

            json.WriteEndObject();
            json.Flush();
        }, cancellationToken);
    }

    /// <summary>
    /// id + object + created (unix seconds) + model — the common head of every SSE chunk frame.
    /// </summary>
    private void RenderChunkHeader(Utf8JsonWriter json, string modelName)
    {
        json.WriteStartObject();
        json.WriteString("id", ResponseId);
        json.WriteString("object", "chat.completion.chunk");
        json.WriteNumber("created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        json.WriteString("model", modelName);
    }

    /// <summary>
    /// "usage":{"prompt_tokens"?,"completion_tokens"?,"total_tokens"?} — individual fields are
    /// omitted when null; total_tokens is the sum of the present parts (omitted when none present).
    /// </summary>
    private static void RenderUsage(Utf8JsonWriter json, UsageDetails usage)
    {
        json.WriteStartObject("usage");
        long? total = null;

        if (usage.InputTokenCount is long input)
        {
            json.WriteNumber("prompt_tokens", input);
            total = input;
        }

        if (usage.OutputTokenCount is long output)
        {
            json.WriteNumber("completion_tokens", output);
            total = (total ?? 0) + output;
        }

        if (total is long sum)
        {
            json.WriteNumber("total_tokens", sum);
        }

        json.WriteEndObject();
    }

    private static void AppendSseFrameTail(RentedFrameBuffer frame)
    {
        frame.AppendByte((byte)'\n');
        frame.AppendByte((byte)'\n');
    }

    /// <summary>
    /// Initial pooled-buffer hint (fixed frame overhead + escape headroom + SSE wrapper); the
    /// growth loop in <see cref="RentedFrameWriter"/> covers any larger frame.
    /// </summary>
    private static int EstimateSize(string modelName, int contentLength = 0)
        => Math.Max(FixedFrameOverhead, FixedFrameOverhead + 8 + 4 * (modelName.Length + contentLength));
}
