using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;

namespace OllamaRouter.Converters;

/// <summary>
/// Renders protocol frames (Ollama NDJSON / OpenAI SSE) with a hand-rolled
/// <see cref="Utf8JsonWriter"/> over a single rented <see cref="ArrayPool{T}"/> buffer.
/// The <see cref="RentedFrameBuffer"/> is intentionally non-growing: when a frame outgrows its
/// buffer the render throws and the caller re-rents a larger one (bounded 4x growth loop), so a
/// frame always costs at most a handful of pooled array resizes and zero intermediate allocations.
/// </summary>
internal static class RentedFrameWriter
{
    /// <summary>Renders one frame and flushes it to the pipe (real-time delivery).</summary>
    public static async ValueTask WriteToPipeAsync(
        PipeWriter pipeWriter,
        int sizeHint,
        FrameRenderDelegate render,
        CancellationToken cancellationToken)
    {
        var (frame, buffer) = await RenderFrameAsync(sizeHint, render, cancellationToken).ConfigureAwait(false);
        try
        {
            await pipeWriter.WriteAsync(frame.Written, cancellationToken).ConfigureAwait(false);
            await pipeWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Renders one frame to a plain stream (non-streaming responses; no pipe flush).</summary>
    public static async ValueTask WriteToStreamAsync(
        Stream stream,
        int sizeHint,
        FrameRenderDelegate render,
        CancellationToken cancellationToken)
    {
        var (frame, buffer) = await RenderFrameAsync(sizeHint, render, cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame.Written, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ValueTask<(RentedFrameBuffer Frame, byte[] Buffer)> RenderFrameAsync(
        int sizeHint,
        FrameRenderDelegate render,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(sizeHint, 128));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var frame = new RentedFrameBuffer(buffer);
                render(frame, new Utf8JsonWriter(frame));
                return new ValueTask<(RentedFrameBuffer, byte[])>((frame, buffer));
            }
            catch (InvalidOperationException)
            {
                // Frame outgrew the rented buffer: bounded 4x growth retry (capped to protect
                // against runaway growth from a pathological render).
                if (sizeHint > 64 * 1024 * 1024)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw new OutOfMemoryException("Protocol frame exceeded the 64 MB rent cap.");
                }

                ArrayPool<byte>.Shared.Return(buffer);
                sizeHint *= 4;
                buffer = ArrayPool<byte>.Shared.Rent(sizeHint);
            }
        }
    }
}

/// <summary>Renders one compact frame: <paramref name="json"/> for the JSON payload,
/// <paramref name="frame"/> for surrounding non-JSON wire bytes (SSE prefix, newlines).</summary>
internal delegate void FrameRenderDelegate(RentedFrameBuffer frame, Utf8JsonWriter json);

/// <summary>
/// <see cref="IBufferWriter{TByte}"/> over one rented <see cref="ArrayPool{T}"/> buffer
/// (single-shot, non-growing; overflow surfaces as <see cref="InvalidOperationException"/>).
/// </summary>
internal sealed class RentedFrameBuffer : IBufferWriter<byte>
{
    private readonly byte[] _buffer;

    public int Count { get; private set; }

    public RentedFrameBuffer(byte[] buffer) => _buffer = buffer;

    public ReadOnlyMemory<byte> Written => _buffer.AsMemory(0, Count);

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (_buffer.Length - Count < Math.Max(sizeHint, 1))
        {
            throw new InvalidOperationException("Rented frame buffer is too small.");
        }

        return _buffer.AsMemory(Count);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        var span = _buffer.AsSpan(Count);
        if (span.Length < Math.Max(sizeHint, 1))
        {
            throw new InvalidOperationException("Rented frame buffer is too small.");
        }

        return span;
    }

    public void Advance(int count)
    {
        if (Count + count > _buffer.Length)
        {
            throw new InvalidOperationException("Rented frame buffer is too small.");
        }

        Count += count;
    }

    public void AppendByte(byte value)
    {
        var span = GetSpan(1);
        span[0] = value;
        Advance(1);
    }

    public void AppendBytes(ReadOnlySpan<byte> value)
    {
        var span = GetSpan(value.Length);
        value.CopyTo(span);
        Advance(value.Length);
    }
}
