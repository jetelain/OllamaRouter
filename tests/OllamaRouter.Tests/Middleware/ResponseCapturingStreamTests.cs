using System.Text;
using OllamaRouter.Middleware;

namespace OllamaRouter.Tests.Middleware;

public class ResponseCapturingStreamTests
{
    [Fact]
    public async Task WriteAsync_Bytes_ForwardsDataToInnerStream()
    {
        var inner = new MemoryStream();
        var sut = new ResponseCapturingStream(inner);
        var data = Encoding.UTF8.GetBytes("hello world");

        await sut.WriteAsync(data, 0, data.Length, CancellationToken.None);

        Assert.Equal("hello world", Encoding.UTF8.GetString(inner.ToArray()));
    }

    [Fact]
    public async Task WriteAsync_Memory_ForwardsDataToInnerStream()
    {
        var inner = new MemoryStream();
        var sut = new ResponseCapturingStream(inner);
        var data = Encoding.UTF8.GetBytes("hello memory");

        await sut.WriteAsync(new ReadOnlyMemory<byte>(data));

        Assert.Equal("hello memory", Encoding.UTF8.GetString(inner.ToArray()));
    }

    [Fact]
    public void Write_Synchronous_ForwardsDataToInnerStream()
    {
        var inner = new MemoryStream();
        var sut = new ResponseCapturingStream(inner);
        var data = Encoding.UTF8.GetBytes("sync write");

        sut.Write(data, 0, data.Length);

        Assert.Equal("sync write", Encoding.UTF8.GetString(inner.ToArray()));
    }

    [Fact]
    public async Task CapturedTail_ReturnsAllWrittenContent_WhenBelowLimit()
    {
        var sut = new ResponseCapturingStream(new MemoryStream());
        var data = Encoding.UTF8.GetBytes("{\"eval_count\":10}");

        await sut.WriteAsync(data, 0, data.Length, CancellationToken.None);

        Assert.Equal("{\"eval_count\":10}", sut.CapturedTail);
    }

    [Fact]
    public async Task CapturedTail_AccumulatesAcrossMultipleWrites()
    {
        var sut = new ResponseCapturingStream(new MemoryStream());

        foreach (var chunk in new[] { "{\"done\":false}\n", "{\"done\":true,\"eval_count\":42}" })
        {
            var bytes = Encoding.UTF8.GetBytes(chunk);
            await sut.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None);
        }

        Assert.Equal("{\"done\":false}\n{\"done\":true,\"eval_count\":42}", sut.CapturedTail);
    }

    [Fact]
    public async Task CapturedTail_IsTruncated_WhenContentExceedsMaxLength()
    {
        var sut = new ResponseCapturingStream(new MemoryStream());
        var largeChunk = new string('a', 5000);
        var bytes = Encoding.UTF8.GetBytes(largeChunk);

        await sut.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None);

        Assert.True(sut.CapturedTail.Length <= 4096);
        Assert.EndsWith("a", sut.CapturedTail);
    }

    [Fact]
    public void CanRead_CanSeek_AreFalse_AndCanWrite_IsTrue()
    {
        var sut = new ResponseCapturingStream(new MemoryStream());

        Assert.False(sut.CanRead);
        Assert.False(sut.CanSeek);
        Assert.True(sut.CanWrite);
    }

    [Fact]
    public void Read_ThrowsNotSupportedException()
    {
        var sut = new ResponseCapturingStream(new MemoryStream());

        Assert.Throws<NotSupportedException>(() => sut.Read(new byte[10], 0, 10));
    }
}
