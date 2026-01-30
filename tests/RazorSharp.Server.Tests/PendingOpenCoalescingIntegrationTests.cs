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

    static JsonElement ToJsonElement(object? value)
    {
        if (value is JsonElement json)
        {
            return json;
        }

        return JsonSerializer.SerializeToElement(value);
    }
}
