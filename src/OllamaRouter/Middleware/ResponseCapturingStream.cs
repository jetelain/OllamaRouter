using System.Text;

namespace OllamaRouter.Middleware;

/// <summary>
/// A pass-through wrapper around the response body stream that also keeps the last chunk of
/// bytes written, so the router can parse the trailing NDJSON line (which carries token counts
/// such as <c>eval_count</c>) without buffering or delaying the response sent to the client.
/// </summary>
internal sealed class ResponseCapturingStream(Stream inner) : Stream
{
    private const int MaxTailLength = 4096;
    private readonly object _gate = new();
    private readonly byte[] _buffer = new byte[MaxTailLength];
    private int _head = 0;
    private int _count = 0;
    private bool _hasWritten;

    /// <summary>
    /// True once at least one byte has been written through the stream. The middleware uses
    /// this as its commit guard for the uncommitted-retry failover loop: once the response has
    /// started being written, the request can no longer be safely re-dispatched to another
    /// target. It complements <see cref="Microsoft.AspNetCore.Http.HttpResponse.HasStarted"/>
    /// (which is not observable in the <c>DefaultHttpContext</c> test host) and also tracks
    /// streaming writes on the production path, where YARP's <c>DisableBuffering()</c> swaps
    /// the response body after this stream is installed.
    /// </summary>
    public bool HasWritten => _hasWritten;

    public string CapturedTail
    {
        get
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    return string.Empty;
                }

                if (_count < MaxTailLength)
                {
                    return Encoding.UTF8.GetString(_buffer, 0, _count);
                }

                Span<byte> ordered = stackalloc byte[MaxTailLength];
                int firstPart = MaxTailLength - _head;
                _buffer.AsSpan(_head, firstPart).CopyTo(ordered);
                if (_head > 0)
                {
                    _buffer.AsSpan(0, _head).CopyTo(ordered.Slice(firstPart));
                }

                return Encoding.UTF8.GetString(ordered);
            }
        }
    }

    private void Capture(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            _hasWritten = true;
            if (buffer.Length >= MaxTailLength)
            {
                buffer.Slice(buffer.Length - MaxTailLength).CopyTo(_buffer);
                _head = 0;
                _count = MaxTailLength;
                return;
            }

            int firstChunk = Math.Min(buffer.Length, MaxTailLength - _head);
            buffer.Slice(0, firstChunk).CopyTo(_buffer.AsSpan(_head));
            int secondChunk = buffer.Length - firstChunk;
            if (secondChunk > 0)
            {
                buffer.Slice(firstChunk, secondChunk).CopyTo(_buffer.AsSpan(0));
            }

            _head = (_head + buffer.Length) % MaxTailLength;
            _count = Math.Min(_count + buffer.Length, MaxTailLength);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Capture(buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Capture(buffer.AsSpan(offset, count));
        await inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Capture(buffer.Span);
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
