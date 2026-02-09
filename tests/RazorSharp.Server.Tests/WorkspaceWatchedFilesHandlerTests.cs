using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Protocol;
using RazorSharp.Protocol.Messages;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class WorkspaceWatchedFilesHandlerTests
{
    [Fact]
    public async Task HandleAsync_ForwardsToRoslynAndReloadsConfig()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var notifications = new List<string>();
        var reloadCalls = 0;
        var refreshCalls = 0;
        var scheduleCalls = 0;

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
            () => reloadCalls++,
            (method, _) =>
            {
                notifications.Add(method);
                return Task.CompletedTask;
            },
            () => refreshCalls++,
            () => scheduleCalls++);

        var payload = new DidChangeWatchedFilesParams
        {
            Changes =
            [
                new FileEvent { Uri = "/workspace/omnisharp.json", Type = FileChangeType.Changed }
            ]
        };

        await handler.HandleAsync(
            payload,
            JsonSerializer.SerializeToElement(payload),
            workspaceRoot: "/workspace",
            canSendRoslynNotifications: true);

        Assert.Equal(
            [LspMethods.WorkspaceDidChangeWatchedFiles, LspMethods.WorkspaceDidChangeConfiguration],
            notifications);
        Assert.Equal(1, reloadCalls);
        Assert.Equal(0, refreshCalls);
        Assert.Equal(0, scheduleCalls);
    }

    [Fact]
    public async Task HandleAsync_RefreshesAndSchedules_WhenAnalysisRequiresIt()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var reloadCalls = 0;
        var refreshCalls = 0;
        var scheduleCalls = 0;
        var notifications = new List<string>();

        var configFactory = new WorkspaceWatchConfigContextFactory(
            static path => path,
            static () => null,
            "omnisharp.json");
        var analyzer = new WorkspaceWatchedFilesAnalyzer(
            static uri => uri,
            static (_, _, _) => false,
            static path => path.Contains("/obj/generated/", StringComparison.Ordinal),
            static (_, _) => false,
            static path => path.EndsWith(".csproj", StringComparison.Ordinal));
        var handler = new WorkspaceWatchedFilesHandler(
            loggerFactory.CreateLogger<WorkspaceWatchedFilesHandler>(),
            configFactory,
            analyzer,
            () => reloadCalls++,
            (method, _) =>
            {
                notifications.Add(method);
                return Task.CompletedTask;
            },
            () => refreshCalls++,
            () => scheduleCalls++);

        var payload = new DidChangeWatchedFilesParams
        {
            Changes =
            [
                new FileEvent { Uri = "/workspace/obj/generated/a.g.cs", Type = FileChangeType.Changed },
                new FileEvent { Uri = "/workspace/project.csproj", Type = FileChangeType.Changed }
            ]
        };

        await handler.HandleAsync(
            payload,
            JsonSerializer.SerializeToElement(payload),
            workspaceRoot: "/workspace",
            canSendRoslynNotifications: false);

        Assert.Empty(notifications);
        Assert.Equal(0, reloadCalls);
        Assert.Equal(1, refreshCalls);
        Assert.Equal(1, scheduleCalls);
    }
}
