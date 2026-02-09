using RazorSharp.Protocol.Messages;

namespace RazorSharp.Server;

internal readonly record struct WorkspaceWatchedFilesAnalysis(
    bool ConfigChanged,
    bool SourceGeneratedFullRefreshNeeded,
    bool SourceGeneratedIncrementalApplied,
    bool WorkspaceReloadNeeded);

internal sealed class WorkspaceWatchedFilesAnalyzer
{
    readonly Func<string, string?> _tryGetLocalPath;
    readonly Func<string, string?, string?, bool> _isOmniSharpConfigPath;
    readonly Func<string, bool> _isSourceGeneratedPath;
    readonly Func<string, FileChangeType, bool> _tryUpdateSourceGeneratedIndexForChange;
    readonly Func<string, bool> _isWorkspaceReloadTriggerPath;

    public WorkspaceWatchedFilesAnalyzer(
        Func<string, string?> tryGetLocalPath,
        Func<string, string?, string?, bool> isOmniSharpConfigPath,
        Func<string, bool> isSourceGeneratedPath,
        Func<string, FileChangeType, bool> tryUpdateSourceGeneratedIndexForChange,
        Func<string, bool> isWorkspaceReloadTriggerPath)
    {
        _tryGetLocalPath = tryGetLocalPath;
        _isOmniSharpConfigPath = isOmniSharpConfigPath;
        _isSourceGeneratedPath = isSourceGeneratedPath;
        _tryUpdateSourceGeneratedIndexForChange = tryUpdateSourceGeneratedIndexForChange;
        _isWorkspaceReloadTriggerPath = isWorkspaceReloadTriggerPath;
    }

    public WorkspaceWatchedFilesAnalysis Analyze(FileEvent[] changes, string? localConfigPath, string? globalConfigPath)
    {
        var configChanged = false;
        var sourceGeneratedFullRefreshNeeded = false;
        var sourceGeneratedIncrementalApplied = false;
        var workspaceReloadNeeded = false;

        foreach (var change in changes)
        {
            var localPath = _tryGetLocalPath(change.Uri);
            if (localPath == null)
            {
                continue;
            }

            if (!configChanged && _isOmniSharpConfigPath(localPath, localConfigPath, globalConfigPath))
            {
                configChanged = true;
            }

            if (_isSourceGeneratedPath(localPath))
            {
                if (_tryUpdateSourceGeneratedIndexForChange(localPath, change.Type))
                {
                    sourceGeneratedIncrementalApplied = true;
                }
                else
                {
                    sourceGeneratedFullRefreshNeeded = true;
                }
            }

            if (!workspaceReloadNeeded && _isWorkspaceReloadTriggerPath(localPath))
            {
                workspaceReloadNeeded = true;
            }
        }

        return new WorkspaceWatchedFilesAnalysis(
            ConfigChanged: configChanged,
            SourceGeneratedFullRefreshNeeded: sourceGeneratedFullRefreshNeeded,
            SourceGeneratedIncrementalApplied: sourceGeneratedIncrementalApplied,
            WorkspaceReloadNeeded: workspaceReloadNeeded);
    }
}
