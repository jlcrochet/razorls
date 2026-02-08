using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Protocol;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class PendingOpenCoalescingIntegrationTests
{
    [Fact]
    public async Task DidChangeBeforeInit_CoalescesIntoDidOpen()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        var notifications = new List<(string Method, object? Params)>();

        server.SetForwardToRoslynNotificationOverrideForTests((method, @params) =>
        {
            notifications.Add((method, @params));
            return Task.CompletedTask;
        });

        try
        {
            var didOpen = JsonSerializer.SerializeToElement(new
            {
                textDocument = new
                {
                    uri = "file:///test.cs",
                    languageId = "c-sharp",
                    version = 1,
                    text = "abc\n123"
                }
            });

            await server.HandleDidOpenAsync(didOpen);

            var didChange = JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///test.cs", version = 2 },
                contentChanges = new[]
                {
                    new
                    {
                        range = new
                        {
                            start = new { line = 1, character = 0 },
                            end = new { line = 1, character = 3 }
                        },
                        text = "456"
                    }
                }
            });

            await server.HandleDidChangeAsync(didChange);

            await server.HandleRoslynNotificationForTests(LspMethods.ProjectInitializationComplete, null, CancellationToken.None);

            var opens = notifications.Where(entry => entry.Method == LspMethods.TextDocumentDidOpen).ToList();
            var changes = notifications.Where(entry => entry.Method == LspMethods.TextDocumentDidChange).ToList();

            Assert.Single(opens);
            Assert.Empty(changes);

            var openParams = ToJsonElement(opens[0].Params);
            var textDocument = openParams.GetProperty("textDocument");
            Assert.Equal("abc\n456", textDocument.GetProperty("text").GetString());
            Assert.Equal(2, textDocument.GetProperty("version").GetInt32());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task DidChangeDuringDidOpenReplay_IsBufferedUntilReplayCompletes()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        var firstReplayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstReplay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondChangeForwarded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwardedDidChangeVersions = new List<int>();
        var forwardedDidOpenCount = 0;
        var lockObj = new object();

        server.SetForwardToRoslynNotificationOverrideForTests(async (method, @params) =>
        {
            var json = ToJsonElement(@params);
            if (method == LspMethods.TextDocumentDidOpen)
            {
                lock (lockObj)
                {
                    forwardedDidOpenCount++;
                }
                return;
            }

            if (method != LspMethods.TextDocumentDidChange)
            {
                return;
            }

            var version = json.GetProperty("textDocument").GetProperty("version").GetInt32();
            lock (lockObj)
            {
                forwardedDidChangeVersions.Add(version);
            }

            if (version == 2)
            {
                firstReplayEntered.TrySetResult(true);
                await releaseFirstReplay.Task;
            }
            else if (version == 3)
            {
                secondChangeForwarded.TrySetResult(true);
            }
        });

        try
        {
            var didOpen = JsonSerializer.SerializeToElement(new
            {
                textDocument = new
                {
                    uri = "file:///test.cs",
                    languageId = "c-sharp",
                    version = 1,
                    text = "initial"
                }
            });

            var firstBufferedChange = JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///test.cs", version = 2 },
                contentChanges = new
                {
                    invalid = true
                }
            });

            var secondBufferedChange = JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///test.cs", version = 3 },
                contentChanges = new
                {
                    invalid = true
                }
            });

            await server.HandleDidOpenAsync(didOpen);
            await server.HandleDidChangeAsync(firstBufferedChange);

            var flushTask = server.HandleRoslynNotificationForTests(
                LspMethods.ProjectInitializationComplete,
                null,
                CancellationToken.None);

            await AwaitOrTimeout(firstReplayEntered.Task, 2000, "First replayed didChange was not forwarded.");

            await server.HandleDidChangeAsync(secondBufferedChange);
            await Task.Delay(100);
            Assert.False(secondChangeForwarded.Task.IsCompleted);

            releaseFirstReplay.TrySetResult(true);

            await AwaitOrTimeout(flushTask, 2000, "Pending didOpen replay did not complete.");
            await AwaitOrTimeout(secondChangeForwarded.Task, 2000, "Second didChange was not forwarded.");

            lock (lockObj)
            {
                Assert.Equal(1, forwardedDidOpenCount);
                Assert.Equal(new[] { 2, 3 }, forwardedDidChangeVersions.ToArray());
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    static async Task AwaitOrTimeout(Task task, int timeoutMs, string message)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)) == task;
        Assert.True(completed, message);
    }

    static JsonElement ToJsonElement(object? value)
    {
        if (value is JsonElement json)
        {
            return json;
        }

        return JsonSerializer.SerializeToElement(value);
    }
}
