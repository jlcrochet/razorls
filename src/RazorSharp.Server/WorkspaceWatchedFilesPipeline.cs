using System.Text.Json;

namespace RazorSharp.Server;

internal sealed class WorkspaceWatchedFilesPipeline
{
    readonly WorkspaceWatchedFilesRequestParser _requestParser;
    readonly WorkspaceWatchedFilesHandler _handler;

    public WorkspaceWatchedFilesPipeline(
        WorkspaceWatchedFilesRequestParser requestParser,
        WorkspaceWatchedFilesHandler handler)
    {
        _requestParser = requestParser;
        _handler = handler;
    }

    public async Task HandleAsync(
        bool fileWatchingEnabled,
        JsonElement paramsJson,
        string? workspaceRoot,
        bool canSendRoslynNotifications)
    {
        if (!_requestParser.TryParse(fileWatchingEnabled, paramsJson, out var @params) ||
            @params == null)
        {
            return;
        }

        await _handler.HandleAsync(
            @params,
            paramsJson,
            workspaceRoot,
            canSendRoslynNotifications);
    }
}
