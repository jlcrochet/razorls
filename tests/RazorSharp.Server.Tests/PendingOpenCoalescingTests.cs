using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class PendingOpenCoalescingTests
{
    [Fact]
    public void TryUpdatePendingOpenText_AppliesIncrementalChange()
    {
        using var server = CreateServer();
        var pending = CreatePendingOpenState("file:///test.cs", "csharp", 1, "hello\nworld");
        var change = CreateChangeParams("file:///test.cs", 2, new
        {
            range = new
            {
                start = new { line = 1, character = 0 },
                end = new { line = 1, character = 5 }
            },
            text = "there"
        });

        var updated = InvokeTryUpdatePendingOpenText(server, pending, change, out var result);

        Assert.True(result);
        Assert.Equal("hello\nthere", updated.Text);
        Assert.Equal(2, updated.Version);
    }

    [Fact]
    public void TryUpdatePendingOpenText_AppliesFullTextChange()
    {
        using var server = CreateServer();
        var pending = CreatePendingOpenState("file:///test.cs", "csharp", 1, "hello\nworld");
        var change = CreateChangeParams("file:///test.cs", 5, new
        {
            text = "replaced"
        });

        var updated = InvokeTryUpdatePendingOpenText(server, pending, change, out var result);

        Assert.True(result);
        Assert.Equal("replaced", updated.Text);
        Assert.Equal(5, updated.Version);
    }

    [Fact]
    public void TryUpdatePendingOpenText_InvalidRange_ReturnsFalse()
    {
        using var server = CreateServer();
        var pending = CreatePendingOpenState("file:///test.cs", "csharp", 1, "hello\nworld");
        var change = CreateChangeParams("file:///test.cs", 2, new
        {
            range = new
            {
                start = new { line = 3, character = 0 },
                end = new { line = 3, character = 1 }
            },
            text = "oops"
        });

        var updated = InvokeTryUpdatePendingOpenText(server, pending, change, out var result);

        Assert.False(result);
        Assert.Equal("hello\nworld", updated.Text);
        Assert.Equal(1, updated.Version);
    }

    [Fact]
    public void TryUpdatePendingOpenText_AppliesMultipleChangesInOrder()
    {
        using var server = CreateServer();
        var pending = CreatePendingOpenState("file:///test.cs", "csharp", 1, "abc\ndef");
        var change = CreateChangeParams("file:///test.cs", 3,
            new
            {
                range = new
                {
                    start = new { line = 0, character = 1 },
                    end = new { line = 0, character = 2 }
                },
                text = "X"
            },
            new
            {
                range = new
                {
                    start = new { line = 1, character = 0 },
                    end = new { line = 1, character = 3 }
                },
                text = "XYZ"
            });

        var updated = InvokeTryUpdatePendingOpenText(server, pending, change, out var result);

        Assert.True(result);
        Assert.Equal("aXc\nXYZ", updated.Text);
        Assert.Equal(3, updated.Version);
    }

    [Fact]
    public void TryUpdatePendingOpenText_HandlesCrLfOffsets()
    {
        using var server = CreateServer();
        var pending = CreatePendingOpenState("file:///test.cs", "csharp", 1, "a\r\nb\r\nc");
        var change = CreateChangeParams("file:///test.cs", 2, new
        {
            range = new
            {
                start = new { line = 1, character = 0 },
                end = new { line = 1, character = 1 }
            },
            text = "B"
        });

        var updated = InvokeTryUpdatePendingOpenText(server, pending, change, out var result);

        Assert.True(result);
        Assert.Equal("a\r\nB\r\nc", updated.Text);
        Assert.Equal(2, updated.Version);
    }

    [Fact]
    public void TryUpdatePendingOpenText_InvalidCharacter_ReturnsFalse()
    {
        using var server = CreateServer();
        var pending = CreatePendingOpenState("file:///test.cs", "csharp", 1, "a\nb");
        var change = CreateChangeParams("file:///test.cs", 2, new
        {
            range = new
            {
                start = new { line = 0, character = 5 },
                end = new { line = 0, character = 6 }
            },
            text = "X"
        });

        var updated = InvokeTryUpdatePendingOpenText(server, pending, change, out var result);

        Assert.False(result);
        Assert.Equal("a\nb", updated.Text);
        Assert.Equal(1, updated.Version);
    }

    sealed class ServerHolder : IDisposable
    {
        readonly ILoggerFactory _loggerFactory;
        readonly DependencyManager _deps;
        readonly RazorLanguageServer _server;

        public ServerHolder(ILoggerFactory loggerFactory, DependencyManager deps, RazorLanguageServer server)
        {
            _loggerFactory = loggerFactory;
            _deps = deps;
            _server = server;
        }

        public RazorLanguageServer Server => _server;

        public void Dispose()
        {
            _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _deps.Dispose();
            _loggerFactory.Dispose();
        }
    }

    static ServerHolder CreateServer()
    {
        var loggerFactory = LoggerFactory.Create(builder => { });
        var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        return new ServerHolder(loggerFactory, deps, server);
    }

    static JsonElement CreateChangeParams(string uri, int version, params object[] changes)
        => JsonSerializer.SerializeToElement(new
        {
            textDocument = new { uri, version },
            contentChanges = changes
        });

    static object CreatePendingOpenState(string uri, string languageId, int version, string text)
    {
        var type = typeof(RazorLanguageServer).GetNestedType("PendingOpenState", BindingFlags.NonPublic);
        if (type == null)
        {
            throw new InvalidOperationException("PendingOpenState type not found.");
        }

        return Activator.CreateInstance(type, [uri, languageId, version, text])!;
    }

    static (string Uri, string LanguageId, int Version, string Text) ReadPendingOpenState(object state)
    {
        var type = state.GetType();
        return (
            (string)type.GetProperty("Uri", BindingFlags.Public | BindingFlags.Instance)!.GetValue(state)!,
            (string)type.GetProperty("LanguageId", BindingFlags.Public | BindingFlags.Instance)!.GetValue(state)!,
            (int)type.GetProperty("Version", BindingFlags.Public | BindingFlags.Instance)!.GetValue(state)!,
            (string)type.GetProperty("Text", BindingFlags.Public | BindingFlags.Instance)!.GetValue(state)!
        );
    }

    static (string Uri, string LanguageId, int Version, string Text) InvokeTryUpdatePendingOpenText(
        ServerHolder server,
        object pendingOpen,
        JsonElement change,
        out bool result)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "TryUpdatePendingOpenText",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var pendingType = pendingOpen.GetType();
        var updated = Activator.CreateInstance(pendingType)!;
        var args = new object?[] { pendingOpen, change, updated };
        result = (bool)method!.Invoke(server.Server, args)!;

        return ReadPendingOpenState(args[2]!);
    }
}
