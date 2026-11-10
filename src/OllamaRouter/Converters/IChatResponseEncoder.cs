using System.IO.Pipelines;
using Microsoft.Extensions.AI;

namespace OllamaRouter.Converters;

/// <summary>
/// Encodes canonical Microsoft.Extensions.AI responses into a protocol-specific wire format
/// (Ollama NDJSON or OpenAI SSE). Encoders hold at most a lazily cached per-stream response id
/// (OpenAI); they are NOT safe for concurrent streams on one instance — the dispatch path
/// must scope one instance per active stream.
/// </summary>
public interface IChatResponseEncoder
{
    /// <summary>The wire content type ("application/x-ndjson" or "text/event-stream").</summary>
    string ContentType { get; }

    /// <summary>
    /// Writes a single streaming chunk frame to the pipe, flushing after the frame for real-time delivery.
    /// </summary>
    ValueTask WriteStreamChunkAsync(
        PipeWriter writer,
        ChatResponseUpdate update,
        string modelName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the terminal frame(s) of a stream, flushing after the frame(s).
    /// <paramref name="finishReason"/> of <c>null</paramref> is treated as
    /// <see cref="ChatFinishReason.Stop"/>.
    /// </summary>
    ValueTask WriteStreamEndAsync(
        PipeWriter writer,
        string modelName,
        UsageDetails? usage,
        TimeSpan duration,
        ChatFinishReason? finishReason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the single non-streaming response object to <paramref name="responseStream"/>.
    /// </summary>
    ValueTask WriteNonStreamingResponseAsync(
        Stream responseStream,
        ChatResponse completion,
        string modelName,
        TimeSpan duration,
        CancellationToken cancellationToken);
}
