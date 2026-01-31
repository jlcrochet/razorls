using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Protocol.Messages;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class RazorLanguageServerDependencyDropTests
{
    [Fact]
    public async Task ForwardRequests_Dropped_WhenDependenciesMissing()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        try
        {
            deps.EnsureDependenciesOverride = (_, __) => Task.FromResult(true);
            server.SetStartRoslynOverrideForTests(_ => Task.FromResult(true));

            var initParams = new InitializeParams
            {
                Capabilities = new ClientCapabilities
                {
                    Window = new WindowClientCapabilities()
                }
            };

            var initJson = JsonSerializer.SerializeToElement(initParams);
            await server.HandleInitializeAsync(initJson, CancellationToken.None);

            // Simulate missing dependencies at runtime and ensure requests are dropped.
            server.SetDependenciesMissingForTests(true);

            var hoverParams = JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///test.razor" },
                position = new { line = 0, character = 0 }
            });

            var result = await server.HandleHoverAsync(hoverParams, CancellationToken.None);
            Assert.Null(result);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
