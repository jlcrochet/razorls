using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Protocol.Messages;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class RestartPromptTests
{
    [Fact]
    public async Task NotifyRestart_UsesShowMessageRequest_WhenSupported()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        try
        {
            server.SetInitializeParamsForTests(new InitializeParams
            {
                Capabilities = new ClientCapabilities
                {
                    Window = new WindowClientCapabilities
                    {
                        ShowMessage = new ShowMessageRequestClientCapabilities()
                    }
                }
            });

            var requestCalled = false;
            var notificationCalled = false;

            server.SetClientRequestOverrideForTests((method, @params, _) =>
            {
                requestCalled = method == RazorSharp.Protocol.LspMethods.WindowShowMessageRequest;
                return Task.FromResult<JsonElement?>(null);
            });

            server.SetClientNotificationOverrideForTests((method, @params) =>
            {
                notificationCalled = method == RazorSharp.Protocol.LspMethods.WindowShowMessage;
                return Task.CompletedTask;
            });

            await server.NotifyRestartForTests("restart", RazorLanguageServer.MessageType.Info);

            Assert.True(requestCalled);
            Assert.False(notificationCalled);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task NotifyRestart_FallsBackToShowMessage_WhenRequestUnsupported()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        try
        {
            server.SetInitializeParamsForTests(new InitializeParams
            {
                Capabilities = new ClientCapabilities
                {
                    Window = new WindowClientCapabilities()
                }
            });

            var requestCalled = false;
            var notificationCalled = false;

            server.SetClientRequestOverrideForTests((method, @params, _) =>
            {
                requestCalled = true;
                return Task.FromResult<JsonElement?>(null);
            });

            server.SetClientNotificationOverrideForTests((method, @params) =>
            {
                notificationCalled = method == RazorSharp.Protocol.LspMethods.WindowShowMessage;
                return Task.CompletedTask;
            });

            await server.NotifyRestartForTests("restart", RazorLanguageServer.MessageType.Info);

            Assert.False(requestCalled);
            Assert.True(notificationCalled);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
