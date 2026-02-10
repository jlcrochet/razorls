using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class FixAllCodeActionCommandTests
{
    const string FixAllCommand = "roslyn.client.fixAllCodeAction";

    [Fact]
    public async Task HandleExecuteCommandAsync_FixAllCodeAction_IgnoresNonArrayArguments()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        var resolveCalls = 0;

        server.SetForwardToRoslynOverrideForTests((method, _, _) =>
        {
            if (method == "codeAction/resolveFixAll")
            {
                resolveCalls++;
            }

            return Task.FromResult<JsonElement?>(null);
        });

        try
        {
            var command = JsonSerializer.SerializeToElement(new
            {
                command = FixAllCommand,
                arguments = new { invalid = true }
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
    public async Task HandleExecuteCommandAsync_FixAllCodeAction_IgnoresNonArrayFixAllFlavors()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        var resolveCalls = 0;

        server.SetForwardToRoslynOverrideForTests((method, _, _) =>
        {
            if (method == "codeAction/resolveFixAll")
            {
                resolveCalls++;
            }

            return Task.FromResult<JsonElement?>(null);
        });

        try
        {
            var command = JsonSerializer.SerializeToElement(new
            {
                command = FixAllCommand,
                arguments = new object[]
                {
                    new
                    {
                        FixAllFlavors = "document"
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
    public async Task HandleExecuteCommandAsync_FixAllCodeAction_IgnoresNonStringFixAllFlavor()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        var resolveCalls = 0;

        server.SetForwardToRoslynOverrideForTests((method, _, _) =>
        {
            if (method == "codeAction/resolveFixAll")
            {
                resolveCalls++;
            }

            return Task.FromResult<JsonElement?>(null);
        });

        try
        {
            var command = JsonSerializer.SerializeToElement(new
            {
                command = FixAllCommand,
                arguments = new object[]
                {
                    new
                    {
                        FixAllFlavors = new object[] { 123 }
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
}
