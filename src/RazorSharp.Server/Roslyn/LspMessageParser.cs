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
    const int MaxContentLength = 128 * 1024 * 1024;
    const int MaxHeaderBytes = 32 * 1024;

    readonly Action<string>? _onMalformedHeader;
    byte[] _buffer;
    int _length;
    int _contentLength = -1;
    bool _disposed;

    public LspMessageParser(Action<string>? onMalformedHeader = null)
    {
        _onMalformedHeader = onMalformedHeader;
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
        ThrowIfDisposed();

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
        ThrowIfDisposed();

        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (_length + count > _buffer.Length) throw new InvalidOperationException("Cannot advance past the end of the buffer.");
        _length += count;
    }

    /// <summary>
    /// Tries to parse a complete LSP message from the buffer.
    /// Returns true if a complete message was parsed, false if more data is needed.
    /// The returned PooledJsonDocument MUST be disposed to return the buffer to the pool.
    /// </summary>
    public bool TryParseMessage(out PooledJsonDocument message)
    {
        ThrowIfDisposed();

        message = default;
        var resyncAttempts = 0;

        while (true)
        {
            // If we don't have content length yet, look for headers
            if (_contentLength < 0)
            {
                var span = _buffer.AsSpan(0, _length);
                var headerEnd = span.IndexOf(HeaderTerminator);
                if (headerEnd < 0)
                {
                    if (_length > MaxHeaderBytes)
                    {
                        // Drop oversized/invalid headers to avoid unbounded buffer growth.
                        _length = 0;
                    }
                    return false;
                }

                // Parse headers directly from bytes (LSP headers are ASCII)
                var sawContentLengthHeader = false;
                var headers = span.Slice(0, headerEnd);
                while (headers.Length > 0)
                {
                    var lineEnd = headers.IndexOf(LineTerminator);
                    var line = lineEnd < 0 ? headers : headers.Slice(0, lineEnd);

                    if (StartsWithHeaderIgnoreCase(line, ContentLengthHeader))
                    {
                        sawContentLengthHeader = true;
                        var valueSpan = line.Slice(ContentLengthHeader.Length).Trim((byte)' ');
                        if (Utf8Parser.TryParse(valueSpan, out int contentLength, out _) && contentLength >= 0)
                        {
                            if (contentLength > MaxContentLength)
                            {
                                throw new InvalidOperationException($"LSP message too large: {contentLength} bytes (max {MaxContentLength}).");
                            }
                            _contentLength = contentLength;
                            break;
                        }
                    }

                    if (lineEnd < 0) break;
                    headers = headers.Slice(lineEnd + 2);
                }

                if (_contentLength < 0)
                {
                    _onMalformedHeader?.Invoke(
                        sawContentLengthHeader
                            ? "Invalid Content-Length header"
                            : "Missing Content-Length header");

                    resyncAttempts++;
                    if (resyncAttempts >= 16)
                    {
                        _length = 0;
                        _contentLength = -1;
                        return false;
                    }

                    // Try to resynchronize by finding the next plausible header start in the buffer.
                    // This handles cases where we have leftover bytes before the next header (e.g. after malformed frames).
                    var nextHeaderStart = _length > ContentLengthHeader.Length
                        ? IndexOfHeaderIgnoreCase(span.Slice(1), ContentLengthHeader)
                        : -1;

                    if (nextHeaderStart >= 0)
                    {
                        nextHeaderStart += 1;
                        var remainingAfterResync = _length - nextHeaderStart;
                        if (remainingAfterResync > 0)
                        {
                            Buffer.BlockCopy(_buffer, nextHeaderStart, _buffer, 0, remainingAfterResync);
                        }
                        _length = remainingAfterResync;
                        continue;
                    }

                    // Header block is complete but unusable; discard it so we don't get stuck.
                    var dropStart = headerEnd + 4;
                    var remainingAfterDrop = _length - dropStart;
                    if (remainingAfterDrop > 0)
                    {
                        Buffer.BlockCopy(_buffer, dropStart, _buffer, 0, remainingAfterDrop);
                    }
                    _length = remainingAfterDrop;
                    return false;
                }

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
            try
            {
                var doc = JsonDocument.Parse(jsonBytes.AsMemory(0, _contentLength));
                message = new PooledJsonDocument(doc, jsonBytes);
            }
            catch (JsonException)
            {
                Pool.Return(jsonBytes);
            }
            catch
            {
                Pool.Return(jsonBytes);
                ConsumeContent();
                throw;
            }

            ConsumeContent();
            return message.Document != null;
        }
    }

    private void ConsumeContent()
    {
        var restLength = _length - _contentLength;
        if (restLength > 0)
        {
            Buffer.BlockCopy(_buffer, _contentLength, _buffer, 0, restLength);
        }
        _length = restLength;
        _contentLength = -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Pool.Return(_buffer);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LspMessageParser));
        }
    }

    static bool StartsWithHeaderIgnoreCase(ReadOnlySpan<byte> line, ReadOnlySpan<byte> header)
    {
        if (line.Length < header.Length)
        {
            return false;
        }

        for (var i = 0; i < header.Length; i++)
        {
            var left = line[i];
            var right = header[i];

            if (left == right)
            {
                continue;
            }

            if ((uint)(left - 'A') <= 25)
            {
                left = (byte)(left + 32);
            }
            if ((uint)(right - 'A') <= 25)
            {
                right = (byte)(right + 32);
            }
            if (left != right)
            {
                return false;
            }
        }

        return true;
    }

    static int IndexOfHeaderIgnoreCase(ReadOnlySpan<byte> span, ReadOnlySpan<byte> header)
    {
        if (header.Length == 0)
        {
            return 0;
        }

        for (var i = 0; i <= span.Length - header.Length; i++)
        {
            if (StartsWithHeaderIgnoreCase(span.Slice(i), header))
            {
                return i;
            }
        }

        return -1;
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
