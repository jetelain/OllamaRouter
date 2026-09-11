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
    private readonly StringBuilder _tail = new();

    public string CapturedTail
    {
        get
        {
            lock (_tail)
            {
                return _tail.ToString();
            }
        }
    }

    private void Capture(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        var text = Encoding.UTF8.GetString(buffer);

        lock (_tail)
        {
            _tail.Append(text);
            if (_tail.Length > MaxTailLength)
            {
                _tail.Remove(0, _tail.Length - MaxTailLength);
            }
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
