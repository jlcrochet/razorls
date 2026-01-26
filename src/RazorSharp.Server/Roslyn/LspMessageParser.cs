using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;

namespace RazorSharp.Server.Roslyn;

/// <summary>
/// Parses LSP messages from a stream.
/// Manages its own buffer internally and parses JSON directly from bytes.
/// Uses ArrayPool to reduce GC pressure from buffer allocations.
/// </summary>
public class LspMessageParser : IDisposable
{
    static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

    byte[] _buffer;
    int _length;
    int _contentLength = -1;
    bool _disposed;

    public LspMessageParser()
    {
        _buffer = Pool.Rent(65536);
    }

    // "Content-Length:" as bytes for zero-allocation header parsing
    static ReadOnlySpan<byte> ContentLengthHeader => "Content-Length:"u8;
    static ReadOnlySpan<byte> HeaderTerminator => "\r\n\r\n"u8;
    static ReadOnlySpan<byte> LineTerminator => "\r\n"u8;

    /// <summary>
    /// Gets a buffer to read data into directly.
    /// Call <see cref="Advance"/> after reading to update the length.
    /// </summary>
    public Memory<byte> GetBuffer(int minSize = 4096)
    {
        // Ensure there's at least minSize bytes available for reading into
        var available = _buffer.Length - _length;
        if (available < minSize)
        {
            Grow(minSize);
        }
        return _buffer.AsMemory(_length);
    }

    void Grow(int minSize)
    {
        var newSize = _buffer.Length;
        var required = _length + minSize;
        while (newSize < required) newSize *= 2;

        var newBuffer = Pool.Rent(newSize);
        Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _length);

        // Return old buffer to pool
        Pool.Return(_buffer);

        _buffer = newBuffer;
    }

    /// <summary>
    /// Advances the buffer position after data has been read into it.
    /// </summary>
    public void Advance(int count)
    {
        _length += count;
    }

    /// <summary>
    /// Tries to parse a complete LSP message from the buffer.
    /// Returns true if a complete message was parsed, false if more data is needed.
    /// The returned PooledJsonDocument MUST be disposed to return the buffer to the pool.
    /// </summary>
    public bool TryParseMessage(out PooledJsonDocument message)
    {
        message = default;

        // If we don't have content length yet, look for headers
        if (_contentLength < 0)
        {
            var span = _buffer.AsSpan(0, _length);
            var headerEnd = span.IndexOf(HeaderTerminator);
            if (headerEnd < 0) return false;

            // Parse headers directly from bytes (LSP headers are ASCII)
            var headers = span.Slice(0, headerEnd);
            while (headers.Length > 0)
            {
                var lineEnd = headers.IndexOf(LineTerminator);
                var line = lineEnd < 0 ? headers : headers.Slice(0, lineEnd);

                if (line.StartsWith(ContentLengthHeader))
                {
                    var valueSpan = line.Slice(ContentLengthHeader.Length).Trim((byte)' ');
                    if (Utf8Parser.TryParse(valueSpan, out int contentLength, out _) && contentLength >= 0)
                    {
                        _contentLength = contentLength;
                        break;
                    }
                }

                if (lineEnd < 0) break;
                headers = headers.Slice(lineEnd + 2);
            }

            if (_contentLength < 0) return false;

            // Remove headers from buffer
            var contentStart = headerEnd + 4;
            var remainingLength = _length - contentStart;
            if (remainingLength > 0)
            {
                Buffer.BlockCopy(_buffer, contentStart, _buffer, 0, remainingLength);
            }
            _length = remainingLength;
        }

        // Check if we have complete content
        if (_length < _contentLength) return false;

        // Parse JSON using a pooled buffer - JsonDocument holds a reference to the backing array
        // so we wrap it in PooledJsonDocument which returns the buffer on dispose
        var jsonBytes = Pool.Rent(_contentLength);
        Buffer.BlockCopy(_buffer, 0, jsonBytes, 0, _contentLength);
        var doc = JsonDocument.Parse(jsonBytes.AsMemory(0, _contentLength));
        message = new PooledJsonDocument(doc, jsonBytes);

        // Remove processed content from buffer
        var restLength = _length - _contentLength;
        if (restLength > 0)
        {
            Buffer.BlockCopy(_buffer, _contentLength, _buffer, 0, restLength);
        }
        _length = restLength;
        _contentLength = -1;

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Pool.Return(_buffer);
    }
}

/// <summary>
/// Wraps a JsonDocument with a pooled byte array backing buffer.
/// Disposing this struct returns the buffer to the pool and disposes the document.
/// </summary>
public readonly struct PooledJsonDocument : IDisposable
{
    static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

    public JsonDocument? Document { get; }
    readonly byte[]? _rentedBuffer;

    public PooledJsonDocument(JsonDocument document, byte[] rentedBuffer)
    {
        Document = document;
        _rentedBuffer = rentedBuffer;
    }

    public void Dispose()
    {
        Document?.Dispose();
        if (_rentedBuffer != null)
        {
            Pool.Return(_rentedBuffer);
        }
    }
}
