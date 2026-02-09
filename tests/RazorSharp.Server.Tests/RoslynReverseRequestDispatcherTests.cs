using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Protocol;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class RoslynReverseRequestDispatcherTests
{
    [Fact]
    public async Task HandleAsync_ForwardsWorkDoneProgressCreateToClient()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var requestResult = JsonSerializer.SerializeToElement(new { ok = true });
        var forwardedMethod = string.Empty;
        JsonElement? forwardedParams = null;
        var token = string.Empty;

        var dispatcher = new RoslynReverseRequestDispatcher(
            loggerFactory.CreateLogger<RoslynReverseRequestDispatcher>(),
            static p => p.HasValue ? "progress-token" : null,
            (method, @params, _) =>
            {
                forwardedMethod = method;
                forwardedParams = @params;
                token = "seen";
                return Task.FromResult<JsonElement?>(requestResult);
            },
            static _ => Task.CompletedTask,
            static (_, _) => Task.FromResult<JsonElement?>(null),
            static (_, _) => Task.FromResult<JsonElement?>(null),
            static () => JsonSerializer.SerializeToElement(new { }));

        var requestParams = JsonSerializer.SerializeToElement(new { token = "abc" });
        var result = await dispatcher.HandleAsync(LspMethods.WindowWorkDoneProgressCreate, requestParams, CancellationToken.None);

        Assert.Equal(LspMethods.WindowWorkDoneProgressCreate, forwardedMethod);
        Assert.True(forwardedParams.HasValue);
        Assert.Equal("abc", forwardedParams.Value.GetProperty("token").GetString());
        Assert.Equal("seen", token);
        Assert.True(result.HasValue);
        Assert.True(result.Value.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task HandleAsync_InvokesRazorUpdateHtmlAndReturnsEmptyObject()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var calls = 0;
        JsonElement capturedParams = default;

        var dispatcher = new RoslynReverseRequestDispatcher(
            loggerFactory.CreateLogger<RoslynReverseRequestDispatcher>(),
            static _ => null,
            static (_, _, _) => Task.FromResult<JsonElement?>(null),
            p =>
            {
                calls++;
                capturedParams = p;
                return Task.CompletedTask;
            },
            static (_, _) => Task.FromResult<JsonElement?>(null),
            static (_, _) => Task.FromResult<JsonElement?>(null),
            static () => JsonSerializer.SerializeToElement(new { }));

        var requestParams = JsonSerializer.SerializeToElement(new
        {
            textDocument = new { uri = "file:///test.razor" },
            checksum = "abc",
            text = "<div />"
        });

        var result = await dispatcher.HandleAsync(LspMethods.RazorUpdateHtml, requestParams, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("abc", capturedParams.GetProperty("checksum").GetString());
        Assert.True(result.HasValue);
        Assert.Equal(JsonValueKind.Object, result.Value.ValueKind);
        Assert.False(result.Value.EnumerateObject().Any());
    }

    [Fact]
    public async Task HandleAsync_InvokesFormattingHandlerForDocumentFormatting()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var calls = 0;
        var formattingResult = JsonSerializer.SerializeToElement(new[] { new { newText = "x" } });

        var dispatcher = new RoslynReverseRequestDispatcher(
            loggerFactory.CreateLogger<RoslynReverseRequestDispatcher>(),
            static _ => null,
            static (_, _, _) => Task.FromResult<JsonElement?>(null),
            static _ => Task.CompletedTask,
            (p, _) =>
            {
                calls++;
                Assert.Equal("file:///doc.razor", p.GetProperty("textDocument").GetProperty("uri").GetString());
                return Task.FromResult<JsonElement?>(formattingResult);
            },
            static (_, _) => Task.FromResult<JsonElement?>(null),
            static () => JsonSerializer.SerializeToElement(new { }));

        var requestParams = JsonSerializer.SerializeToElement(new
        {
            textDocument = new { uri = "file:///doc.razor" }
        });

        var result = await dispatcher.HandleAsync(LspMethods.TextDocumentFormatting, requestParams, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.True(result.HasValue);
        Assert.Equal(JsonValueKind.Array, result.Value.ValueKind);
        Assert.Single(result.Value.EnumerateArray());
    }

    [Fact]
    public async Task HandleAsync_InvokesFormattingHandlerForRangeFormatting()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var calls = 0;
        var formattingResult = JsonSerializer.SerializeToElement(new[] { new { newText = "y" } });

        var dispatcher = new RoslynReverseRequestDispatcher(
            loggerFactory.CreateLogger<RoslynReverseRequestDispatcher>(),
            static _ => null,
            static (_, _, _) => Task.FromResult<JsonElement?>(null),
            static _ => Task.CompletedTask,
            static (_, _) => Task.FromResult<JsonElement?>(null),
            (_, _) =>
            {
                calls++;
                return Task.FromResult<JsonElement?>(formattingResult);
            },
            static () => JsonSerializer.SerializeToElement(new { }));

        var requestParams = JsonSerializer.SerializeToElement(new
        {
            textDocument = new { uri = "file:///doc.razor" },
            range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 1 } }
        });

        var result = await dispatcher.HandleAsync(LspMethods.TextDocumentRangeFormatting, requestParams, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.True(result.HasValue);
        Assert.Equal(JsonValueKind.Array, result.Value.ValueKind);
        Assert.Single(result.Value.EnumerateArray());
    }

    [Fact]
    public async Task HandleAsync_ReturnsNullForUnknownMethod()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var forwardCalls = 0;
        var updateCalls = 0;
        var formatCalls = 0;
        var rangeFormatCalls = 0;

        var dispatcher = new RoslynReverseRequestDispatcher(
            loggerFactory.CreateLogger<RoslynReverseRequestDispatcher>(),
            static _ => null,
            (_, _, _) =>
            {
                forwardCalls++;
                return Task.FromResult<JsonElement?>(null);
            },
            _ =>
            {
                updateCalls++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                formatCalls++;
                return Task.FromResult<JsonElement?>(null);
            },
            (_, _) =>
            {
                rangeFormatCalls++;
                return Task.FromResult<JsonElement?>(null);
            },
            static () => JsonSerializer.SerializeToElement(new { }));

        var result = await dispatcher.HandleAsync("custom/method", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.False(result.HasValue);
        Assert.Equal(0, forwardCalls);
        Assert.Equal(0, updateCalls);
        Assert.Equal(0, formatCalls);
        Assert.Equal(0, rangeFormatCalls);
    }
}
