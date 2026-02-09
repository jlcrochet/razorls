using RazorSharp.Protocol.Messages;

namespace RazorSharp.Server;

internal sealed class WorkspaceTargetResolver
{
    readonly Func<string, string?> _tryGetLocalPath;
    readonly Func<string, string?> _tryGetFullPath;

    public WorkspaceTargetResolver(
        Func<string, string?> tryGetLocalPath,
        Func<string, string?> tryGetFullPath)
    {
        _tryGetLocalPath = tryGetLocalPath;
        _tryGetFullPath = tryGetFullPath;
    }

    public string? GetWorkspaceOpenTarget(
        string? workspaceOpenTarget,
        string? cliSolutionPath,
        InitializeParams? initParams,
        string? workspaceRoot)
    {
        if (!string.IsNullOrEmpty(workspaceOpenTarget))
        {
            return workspaceOpenTarget;
        }

        if (!string.IsNullOrEmpty(cliSolutionPath))
        {
            return cliSolutionPath;
        }

        if (initParams?.RootUri != null)
        {
            return initParams.RootUri;
        }

        if (initParams?.WorkspaceFolders?.Length > 0)
        {
            return initParams.WorkspaceFolders[0].Uri;
        }

        return workspaceRoot;
    }

    public string? TryGetWorkspaceBaseUri(
        string? workspaceRoot,
        string? workspaceOpenTarget,
        string? cliSolutionPath,
        InitializeParams? initParams)
    {
        var rootPath = workspaceRoot;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            var target = GetWorkspaceOpenTarget(workspaceOpenTarget, cliSolutionPath, initParams, workspaceRoot);
            rootPath = target != null ? _tryGetLocalPath(target) : null;
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        if (File.Exists(rootPath))
        {
            rootPath = Path.GetDirectoryName(rootPath);
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        var fullPath = _tryGetFullPath(rootPath);
        if (fullPath == null)
        {
            return null;
        }

        try
        {
            return new Uri(fullPath).AbsoluteUri;
        }
        catch
        {
            return null;
        }
    }
}
