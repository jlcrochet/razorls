using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Protocol;
using RazorSharp.Protocol.Messages;

namespace RazorSharp.Server;

internal sealed class WorkspaceWatchedFilesHandler
{
    readonly ILogger _logger;
    readonly WorkspaceWatchConfigContextFactory _configContextFactory;
    readonly WorkspaceWatchedFilesAnalyzer _analyzer;
    readonly Action _reloadConfiguration;
    readonly Func<string, object?, Task> _sendRoslynNotificationAsync;
    readonly Action _refreshSourceGeneratedIndex;
    readonly Action _scheduleWorkspaceReload;

    public WorkspaceWatchedFilesHandler(
        ILogger logger,
        WorkspaceWatchConfigContextFactory configContextFactory,
        WorkspaceWatchedFilesAnalyzer analyzer,
        Action reloadConfiguration,
        Func<string, object?, Task> sendRoslynNotificationAsync,
        Action refreshSourceGeneratedIndex,
        Action scheduleWorkspaceReload)
    {
        _logger = logger;
        _configContextFactory = configContextFactory;
        _analyzer = analyzer;
        _reloadConfiguration = reloadConfiguration;
        _sendRoslynNotificationAsync = sendRoslynNotificationAsync;
        _refreshSourceGeneratedIndex = refreshSourceGeneratedIndex;
        _scheduleWorkspaceReload = scheduleWorkspaceReload;
    }

    public async Task HandleAsync(
        DidChangeWatchedFilesParams @params,
        JsonElement paramsJson,
        string? workspaceRoot,
        bool canSendRoslynNotifications)
    {
        // Forward to Roslyn first so it can react immediately.
        if (canSendRoslynNotifications)
        {
            await _sendRoslynNotificationAsync(LspMethods.WorkspaceDidChangeWatchedFiles, paramsJson);
        }

        var configContext = _configContextFactory.Create(workspaceRoot);
        var analysis = _analyzer.Analyze(
            @params.Changes,
            configContext.LocalConfigPath,
            configContext.GlobalConfigPath);

        if (analysis.ConfigChanged)
        {
            _logger.LogInformation("omnisharp.json changed; reloading configuration");
            _reloadConfiguration();
            if (canSendRoslynNotifications)
            {
                await _sendRoslynNotificationAsync(LspMethods.WorkspaceDidChangeConfiguration, new { settings = new { } });
            }
        }

        if (analysis.SourceGeneratedFullRefreshNeeded)
        {
            _logger.LogDebug("Source-generated files changed; refreshing index");
            _refreshSourceGeneratedIndex();
        }
        else if (analysis.SourceGeneratedIncrementalApplied)
        {
            _logger.LogDebug("Source-generated files changed; updated index incrementally");
        }

        if (analysis.WorkspaceReloadNeeded)
        {
            _scheduleWorkspaceReload();
        }
    }
}
