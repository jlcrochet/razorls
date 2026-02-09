using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Protocol;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class NestedCodeActionCommandTests
{
    [Fact]
    public async Task HandleExecuteCommandAsync_NestedCodeAction_UsesSelectedIndex()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        JsonElement? selected = null;

        server.SetForwardToRoslynOverrideForTests((method, @params, _) =>
        {
            Assert.Equal(LspMethods.CodeActionResolve, method);
            selected = @params.Clone();
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { }));
        });

        try
        {
            var command = JsonSerializer.SerializeToElement(new
            {
                command = "roslyn.client.nestedCodeAction",
                arguments = new object[]
                {
                    new
                    {
                        NestedCodeActions = new object[]
                        {
                            new { title = "First action", data = new { id = 1 } },
                            new { title = "Second action", data = new { id = 2 } }
                        },
                        NestedCodeActionIndex = 1
                    }
                }
            });

            await server.HandleExecuteCommandAsync(command, CancellationToken.None);

            Assert.True(selected.HasValue);
            Assert.Equal("Second action", selected.Value.GetProperty("title").GetString());
            Assert.Equal(2, selected.Value.GetProperty("data").GetProperty("id").GetInt32());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleExecuteCommandAsync_NestedCodeAction_WithoutSelectionDoesNotResolve()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        var resolveCalls = 0;

        server.SetForwardToRoslynOverrideForTests((method, _, _) =>
        {
            if (method == LspMethods.CodeActionResolve)
            {
                resolveCalls++;
            }

            return Task.FromResult<JsonElement?>(null);
        });

        try
        {
            var command = JsonSerializer.SerializeToElement(new
            {
                command = "roslyn.client.nestedCodeAction",
                arguments = new object[]
                {
                    new
                    {
                        NestedCodeActions = new object[]
                        {
                            new { title = "First action" },
                            new { title = "Second action" }
                        }
                    }
                }
            });

            var result = await server.HandleExecuteCommandAsync(command, CancellationToken.None);

            Assert.False(result.HasValue);
            Assert.Equal(0, resolveCalls);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleExecuteCommandAsync_NestedCodeAction_SingleCandidateResolves()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        JsonElement? selected = null;

        server.SetForwardToRoslynOverrideForTests((method, @params, _) =>
        {
            Assert.Equal(LspMethods.CodeActionResolve, method);
            selected = @params.Clone();
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { }));
        });

        try
        {
            var command = JsonSerializer.SerializeToElement(new
            {
                command = "roslyn.client.nestedCodeAction",
                arguments = new object[]
                {
                    new
                    {
                        NestedCodeActions = new object[]
                        {
                            new { title = "Only action", data = new { id = 7 } }
                        }
                    }
                }
            });

            await server.HandleExecuteCommandAsync(command, CancellationToken.None);

            Assert.True(selected.HasValue);
            Assert.Equal("Only action", selected.Value.GetProperty("title").GetString());
            Assert.Equal(7, selected.Value.GetProperty("data").GetProperty("id").GetInt32());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
