using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Protocol;
using RazorSharp.Protocol.Messages;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class WorkspaceWatchedFilesPipelineTests
{
    [Fact]
    public async Task HandleAsync_DoesNothing_WhenFileWatchingDisabled()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var notifications = new List<string>();
        var reloadCalls = 0;
        var pipeline = CreatePipeline(loggerFactory, notifications, () => reloadCalls++);
        var payload = JsonSerializer.SerializeToElement(new DidChangeWatchedFilesParams
        {
            Changes =
            [
                new FileEvent { Uri = "/workspace/omnisharp.json", Type = FileChangeType.Changed }
            ]
        });

        await pipeline.HandleAsync(
            fileWatchingEnabled: false,
            payload,
            workspaceRoot: "/workspace",
            canSendRoslynNotifications: true);

        Assert.Empty(notifications);
        Assert.Equal(0, reloadCalls);
    }

    [Fact]
    public async Task HandleAsync_DoesNothing_WhenParsedChangesAreEmpty()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var notifications = new List<string>();
        var reloadCalls = 0;
        var pipeline = CreatePipeline(loggerFactory, notifications, () => reloadCalls++);
        var payload = JsonSerializer.SerializeToElement(new DidChangeWatchedFilesParams { Changes = [] });

        await pipeline.HandleAsync(
            fileWatchingEnabled: true,
            payload,
            workspaceRoot: "/workspace",
            canSendRoslynNotifications: true);

        Assert.Empty(notifications);
        Assert.Equal(0, reloadCalls);
    }

    [Fact]
    public async Task HandleAsync_ForwardsToHandler_WhenRequestParses()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var notifications = new List<string>();
        var reloadCalls = 0;
        var pipeline = CreatePipeline(loggerFactory, notifications, () => reloadCalls++);
        var payloadObject = new DidChangeWatchedFilesParams
        {
            Changes =
            [
                new FileEvent { Uri = "/workspace/omnisharp.json", Type = FileChangeType.Changed }
            ]
        };
        var payload = JsonSerializer.SerializeToElement(payloadObject);

        await pipeline.HandleAsync(
            fileWatchingEnabled: true,
            payload,
            workspaceRoot: "/workspace",
            canSendRoslynNotifications: true);

        Assert.Equal(
            [LspMethods.WorkspaceDidChangeWatchedFiles, LspMethods.WorkspaceDidChangeConfiguration],
            notifications);
        Assert.Equal(1, reloadCalls);
    }

    static WorkspaceWatchedFilesPipeline CreatePipeline(
        ILoggerFactory loggerFactory,
        List<string> notifications,
        Action reloadConfiguration)
    {
        var parser = new WorkspaceWatchedFilesRequestParser(
            loggerFactory.CreateLogger<WorkspaceWatchedFilesRequestParser>(),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            });
        var configFactory = new WorkspaceWatchConfigContextFactory(
            static path => path,
            static () => "/global/omnisharp.json",
            "omnisharp.json");
        var analyzer = new WorkspaceWatchedFilesAnalyzer(
            static uri => uri,
            static (path, local, global) => path == local || path == global,
            static _ => false,
            static (_, _) => false,
            static _ => false);
        var handler = new WorkspaceWatchedFilesHandler(
            loggerFactory.CreateLogger<WorkspaceWatchedFilesHandler>(),
            configFactory,
            analyzer,
            reloadConfiguration,
            (method, _) =>
            {
                notifications.Add(method);
                return Task.CompletedTask;
            },
            static () => { },
            static () => { });
        return new WorkspaceWatchedFilesPipeline(parser, handler);
    }
}
