using System.Text;
using System.Text.Json;
using RazorSharp.Server.Roslyn;

namespace RazorSharp.Server.Tests;

public class LspMessageParserTests
{
    private static void AppendToParser(LspMessageParser parser, string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        var buffer = parser.GetBuffer(bytes.Length);
        bytes.CopyTo(buffer);
        parser.Advance(bytes.Length);
    }

    [Fact]
    public void TryParseMessage_CompleteMessage_ReturnsTrue()
    {
        using var parser = new LspMessageParser();
        var message = """{"jsonrpc":"2.0","id":1,"result":null}""";
        var expectedLength = Encoding.UTF8.GetByteCount(message);
        var lspMessage = $"Content-Length: {expectedLength}\r\n\r\n{message}";
        AppendToParser(parser, lspMessage);

        // Debug: verify expected length
        Assert.Equal(38, expectedLength);

        var result = parser.TryParseMessage(out var pooledDoc);
        using (pooledDoc)
        {
            Assert.True(result);
            Assert.NotNull(pooledDoc.Document);
            Assert.Equal("2.0", pooledDoc.Document!.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal(1, pooledDoc.Document.RootElement.GetProperty("id").GetInt32());
        }
    }

    [Fact]
    public void TryParseMessage_IncompleteHeader_ReturnsFalse()
    {
        using var parser = new LspMessageParser();
        AppendToParser(parser, "Content-Length: 10\r\n");

        var result = parser.TryParseMessage(out var pooledDoc);
        using (pooledDoc)
        {
            Assert.False(result);
            Assert.Null(pooledDoc.Document);
        }
    }

    [Fact]
    public void TryParseMessage_IncompleteContent_ReturnsFalse()
    {
        using var parser = new LspMessageParser();
        AppendToParser(parser, "Content-Length: 100\r\n\r\n{\"partial\":");

        var result = parser.TryParseMessage(out var pooledDoc);
        using (pooledDoc)
        {
            Assert.False(result);
            Assert.Null(pooledDoc.Document);
        }
    }

    [Fact]
    public void TryParseMessage_MultipleMessages_ParsesSequentially()
    {
        using var parser = new LspMessageParser();
        var msg1 = """{"id":1}""";
        var msg2 = """{"id":2}""";
        var combined = $"Content-Length: {Encoding.UTF8.GetByteCount(msg1)}\r\n\r\n{msg1}" +
                       $"Content-Length: {Encoding.UTF8.GetByteCount(msg2)}\r\n\r\n{msg2}";
        AppendToParser(parser, combined);

        // First message
        var result1 = parser.TryParseMessage(out var pooledDoc1);
        using (pooledDoc1)
        {
            Assert.True(result1);
            Assert.NotNull(pooledDoc1.Document);
            Assert.Equal(1, pooledDoc1.Document!.RootElement.GetProperty("id").GetInt32());
        }

        // Second message
        var result2 = parser.TryParseMessage(out var pooledDoc2);
        using (pooledDoc2)
        {
            Assert.True(result2);
            Assert.NotNull(pooledDoc2.Document);
            Assert.Equal(2, pooledDoc2.Document!.RootElement.GetProperty("id").GetInt32());
        }

        // No more messages
        var result3 = parser.TryParseMessage(out var pooledDoc3);
        using (pooledDoc3)
        {
            Assert.False(result3);
            Assert.Null(pooledDoc3.Document);
        }
    }

    [Fact]
    public void TryParseMessage_IncrementalData_WorksCorrectly()
    {
        using var parser = new LspMessageParser();
        var message = """{"jsonrpc":"2.0","id":1}""";
        var lspMessage = $"Content-Length: {Encoding.UTF8.GetByteCount(message)}\r\n\r\n{message}";
        var bytes = Encoding.UTF8.GetBytes(lspMessage);

        // Write first half
        var firstHalfLength = bytes.Length / 2;
        var buffer1 = parser.GetBuffer(firstHalfLength);
        bytes.AsSpan(0, firstHalfLength).CopyTo(buffer1.Span);
        parser.Advance(firstHalfLength);
        var result1 = parser.TryParseMessage(out var pooledDoc1);
        using (pooledDoc1)
        {
            Assert.False(result1);
            Assert.Null(pooledDoc1.Document);
        }

        // Write second half
        var secondHalfLength = bytes.Length - firstHalfLength;
        var buffer2 = parser.GetBuffer(secondHalfLength);
        bytes.AsSpan(firstHalfLength).CopyTo(buffer2.Span);
        parser.Advance(secondHalfLength);

        var result2 = parser.TryParseMessage(out var pooledDoc2);
        using (pooledDoc2)
        {
            Assert.True(result2);
            Assert.NotNull(pooledDoc2.Document);
            Assert.Equal(1, pooledDoc2.Document!.RootElement.GetProperty("id").GetInt32());
        }
    }

    [Fact]
    public void TryParseMessage_LargeMessage_WorksCorrectly()
    {
        using var parser = new LspMessageParser();
        // Create a large JSON payload
        var largeContent = new string('x', 100000);
        var message = $$"""{"data":"{{largeContent}}"}""";
        var lspMessage = $"Content-Length: {Encoding.UTF8.GetByteCount(message)}\r\n\r\n{message}";
        AppendToParser(parser, lspMessage);

        var result = parser.TryParseMessage(out var pooledDoc);
        using (pooledDoc)
        {
            Assert.True(result);
            Assert.NotNull(pooledDoc.Document);
            Assert.Equal(largeContent, pooledDoc.Document!.RootElement.GetProperty("data").GetString());
        }
    }

    [Fact]
    public void TryParseMessage_MultipleHeaderLines_WorksCorrectly()
    {
        using var parser = new LspMessageParser();
        var message = """{"id":1}""";
        // Some LSP implementations send multiple headers
        var lspMessage = $"Content-Type: application/vscode-jsonrpc; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(message)}\r\n\r\n{message}";
        AppendToParser(parser, lspMessage);

        var result = parser.TryParseMessage(out var pooledDoc);
        using (pooledDoc)
        {
            Assert.True(result);
            Assert.NotNull(pooledDoc.Document);
        }
    }

}
