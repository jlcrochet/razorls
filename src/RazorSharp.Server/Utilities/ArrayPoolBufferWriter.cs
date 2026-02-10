using System.Buffers;

namespace RazorSharp.Server.Utilities;

/// <summary>
/// A reusable IBufferWriter implementation backed by ArrayPool.
/// Call Reset() to clear written content while keeping the pooled buffer.
/// Dispose to return the buffer to the pool.
/// </summary>
public sealed class ArrayPoolBufferWriter : IBufferWriter<byte>, IDisposable
{
    const int DefaultInitialCapacity = 256;
    const int MaxRetainedBufferSize = 1024 * 1024;
    const int MaxBufferSize = 256 * 1024 * 1024;
    static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

    byte[] _buffer;
    int _written;
    bool _disposed;

    public ArrayPoolBufferWriter(int initialCapacity = DefaultInitialCapacity)
    {
        _buffer = Pool.Rent(initialCapacity);
    }

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
    public int WrittenCount => _written;

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (_written + count > _buffer.Length) throw new InvalidOperationException("Cannot advance past the end of the buffer.");
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    /// <summary>
    /// Resets the writer to the beginning, keeping the pooled buffer allocated.
    /// </summary>
    public void Reset()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ArrayPoolBufferWriter));
        }

        if (_buffer.Length > MaxRetainedBufferSize)
        {
            Pool.Return(_buffer);
            _buffer = Pool.Rent(DefaultInitialCapacity);
        }
        _written = 0;
    }

    void EnsureCapacity(int sizeHint)
    {
        if (sizeHint <= 0) sizeHint = 1;

        var available = _buffer.Length - _written;
        if (available >= sizeHint) return;

        // Grow the buffer
        var newSize = _buffer.Length;
        var required = _written + sizeHint;
        while (newSize < required) newSize *= 2;

        if (newSize > MaxBufferSize)
        {
            throw new InvalidOperationException($"Buffer growth would exceed maximum size of {MaxBufferSize} bytes (requested: {required}).");
        }

        var newBuffer = Pool.Rent(newSize);
        _buffer.AsSpan(0, _written).CopyTo(newBuffer);
        Pool.Return(_buffer);
        _buffer = newBuffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Pool.Return(_buffer);
    }
}
