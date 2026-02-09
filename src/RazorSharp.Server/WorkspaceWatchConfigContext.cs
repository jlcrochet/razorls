namespace RazorSharp.Server;

internal readonly record struct WorkspaceWatchConfigContext(
    string? LocalConfigPath,
    string? GlobalConfigPath);

internal sealed class WorkspaceWatchConfigContextFactory
{
    readonly Func<string, string?> _tryGetFullPath;
    readonly Func<string?> _tryGetGlobalConfigPath;
    readonly string _configFileName;

    public WorkspaceWatchConfigContextFactory(
        Func<string, string?> tryGetFullPath,
        Func<string?> tryGetGlobalConfigPath,
        string configFileName)
    {
        _tryGetFullPath = tryGetFullPath;
        _tryGetGlobalConfigPath = tryGetGlobalConfigPath;
        _configFileName = configFileName;
    }

    public WorkspaceWatchConfigContext Create(string? workspaceRoot)
    {
        var localConfigPath = workspaceRoot != null
            ? _tryGetFullPath(Path.Combine(workspaceRoot, _configFileName))
            : null;
        var globalConfigPath = _tryGetGlobalConfigPath();
        return new WorkspaceWatchConfigContext(localConfigPath, globalConfigPath);
    }
}
