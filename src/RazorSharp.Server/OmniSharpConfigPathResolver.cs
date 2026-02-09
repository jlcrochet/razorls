namespace RazorSharp.Server;

internal sealed class OmniSharpConfigPathResolver
{
    readonly Func<string, string?> _tryGetFullPath;
    readonly string _configFileName;

    public OmniSharpConfigPathResolver(Func<string, string?> tryGetFullPath, string configFileName)
    {
        _tryGetFullPath = tryGetFullPath;
        _configFileName = configFileName;
    }

    public string? TryGetGlobalConfigPath()
    {
        var omniSharpHome = Environment.GetEnvironmentVariable("OMNISHARPHOME");
        if (!string.IsNullOrEmpty(omniSharpHome))
        {
            return _tryGetFullPath(Path.Combine(omniSharpHome, _configFileName));
        }

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(homeDir))
        {
            return _tryGetFullPath(Path.Combine(homeDir, ".omnisharp", _configFileName));
        }

        return null;
    }

    public static bool IsConfigPath(string path, string? localPath, string? globalPath, StringComparison comparison)
    {
        if (localPath != null && path.Equals(localPath, comparison))
        {
            return true;
        }

        if (globalPath != null && path.Equals(globalPath, comparison))
        {
            return true;
        }

        return false;
    }
}
