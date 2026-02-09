using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RazorSharp.Server.Tests;

public class RoslynClientCommandDispatcherTests
{
    const string NestedCommand = "roslyn.client.nestedCodeAction";
    const string FixAllCommand = "roslyn.client.fixAllCodeAction";
    const string CompletionCommand = "roslyn.client.completionComplexEdit";

    [Fact]
    public async Task HandleAsync_InvokesNestedHandler()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var nestedCalls = 0;
        var fixAllCalls = 0;
        var expected = JsonSerializer.SerializeToElement(new { ok = "nested" });

        var dispatcher = new RoslynClientCommandDispatcher(
            loggerFactory.CreateLogger<RoslynClientCommandDispatcher>(),
            NestedCommand,
            FixAllCommand,
            CompletionCommand,
            (_, _) =>
            {
                nestedCalls++;
                return Task.FromResult<JsonElement?>(expected);
            },
            (_, _) =>
            {
                fixAllCalls++;
                return Task.FromResult<JsonElement?>(null);
            });

        var result = await dispatcher.HandleAsync(NestedCommand, JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.Equal(1, nestedCalls);
        Assert.Equal(0, fixAllCalls);
        Assert.True(result.HasValue);
        Assert.Equal("nested", result.Value.GetProperty("ok").GetString());
    }

    [Fact]
    public async Task HandleAsync_InvokesFixAllHandler()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var nestedCalls = 0;
        var fixAllCalls = 0;
        var expected = JsonSerializer.SerializeToElement(new { ok = "fixall" });

        var dispatcher = new RoslynClientCommandDispatcher(
            loggerFactory.CreateLogger<RoslynClientCommandDispatcher>(),
            NestedCommand,
            FixAllCommand,
            CompletionCommand,
            (_, _) =>
            {
                nestedCalls++;
                return Task.FromResult<JsonElement?>(null);
            },
            (_, _) =>
            {
                fixAllCalls++;
                return Task.FromResult<JsonElement?>(expected);
            });

        var result = await dispatcher.HandleAsync(FixAllCommand, JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.Equal(0, nestedCalls);
        Assert.Equal(1, fixAllCalls);
        Assert.True(result.HasValue);
        Assert.Equal("fixall", result.Value.GetProperty("ok").GetString());
    }

    [Fact]
    public async Task HandleAsync_CompletionComplexEditReturnsNullWithoutCallingHandlers()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var nestedCalls = 0;
        var fixAllCalls = 0;

        var dispatcher = new RoslynClientCommandDispatcher(
            loggerFactory.CreateLogger<RoslynClientCommandDispatcher>(),
            NestedCommand,
            FixAllCommand,
            CompletionCommand,
            (_, _) =>
            {
                nestedCalls++;
                return Task.FromResult<JsonElement?>(null);
            },
            (_, _) =>
            {
                fixAllCalls++;
                return Task.FromResult<JsonElement?>(null);
            });

        var result = await dispatcher.HandleAsync(CompletionCommand, JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.False(result.HasValue);
        Assert.Equal(0, nestedCalls);
        Assert.Equal(0, fixAllCalls);
    }

    [Fact]
    public async Task HandleAsync_UnknownCommandReturnsNull()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var nestedCalls = 0;
        var fixAllCalls = 0;

        var dispatcher = new RoslynClientCommandDispatcher(
            loggerFactory.CreateLogger<RoslynClientCommandDispatcher>(),
            NestedCommand,
            FixAllCommand,
            CompletionCommand,
            (_, _) =>
            {
                nestedCalls++;
                return Task.FromResult<JsonElement?>(null);
            },
            (_, _) =>
            {
                fixAllCalls++;
                return Task.FromResult<JsonElement?>(null);
            });

        var result = await dispatcher.HandleAsync("roslyn.client.unknown", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.False(result.HasValue);
        Assert.Equal(0, nestedCalls);
        Assert.Equal(0, fixAllCalls);
    }
}
