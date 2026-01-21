using System.Buffers.Text;
using System.Text.Json;

namespace RazorSharp.Server.Roslyn;

/// <summary>
/// Parses LSP messages from a stream.
/// Manages its own buffer internally and parses JSON directly from bytes.
/// </summary>
public class LspMessageParser
{
    byte[] _buffer = new byte[65536];
    int _length;
    int _contentLength = -1;

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

        var newBuffer = new byte[newSize];
        Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _length);
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
    /// </summary>
    public bool TryParseMessage(out JsonDocument? message)
    {
        message = null;

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
                    if (Utf8Parser.TryParse(valueSpan, out int contentLength, out _))
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

        // Parse JSON - must copy because JsonDocument holds a reference to the backing array
        // and our _buffer is reused for subsequent messages
        var jsonBytes = new byte[_contentLength];
        Buffer.BlockCopy(_buffer, 0, jsonBytes, 0, _contentLength);
        message = JsonDocument.Parse(jsonBytes);

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
}
