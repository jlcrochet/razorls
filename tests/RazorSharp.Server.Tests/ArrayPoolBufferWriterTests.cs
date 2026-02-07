using RazorSharp.Server.Utilities;

namespace RazorSharp.Server.Tests;

public class ArrayPoolBufferWriterTests
{
    [Fact]
    public void GetMemory_AfterDispose_ThrowsObjectDisposedException()
    {
        var writer = new ArrayPoolBufferWriter();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.GetMemory());
    }

    [Fact]
    public void GetSpan_AfterDispose_ThrowsObjectDisposedException()
    {
        var writer = new ArrayPoolBufferWriter();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan());
    }

    [Fact]
    public void Advance_AfterDispose_ThrowsObjectDisposedException()
    {
        var writer = new ArrayPoolBufferWriter();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.Advance(1));
    }

    [Fact]
    public void Reset_AfterDispose_ThrowsObjectDisposedException()
    {
        var writer = new ArrayPoolBufferWriter();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.Reset());
    }

    [Fact]
    public void WrittenMemory_BasicUsage_ReturnsCorrectData()
    {
        using var writer = new ArrayPoolBufferWriter();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var span = writer.GetSpan(data.Length);
        data.CopyTo(span);
        writer.Advance(data.Length);

        Assert.Equal(data.Length, writer.WrittenCount);
        Assert.True(writer.WrittenMemory.Span.SequenceEqual(data));
    }
}
