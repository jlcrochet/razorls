namespace RazorSharp.Server;

internal static class FileWatcherPatternBuilder
{
    public static object[] CreateFileWatchers(
        string? baseUri,
        string[] workspaceReloadExtensions,
        string[] workspaceReloadFileNames,
        string omniSharpConfigFileName,
        int fileWatchKindAll)
    {
        var watchers = new List<object>();
        foreach (var extension in workspaceReloadExtensions)
        {
            watchers.Add(CreateWatcher($"**/*{extension}", baseUri, fileWatchKindAll));
        }

        foreach (var fileName in workspaceReloadFileNames)
        {
            watchers.Add(CreateWatcher($"**/{fileName}", baseUri, fileWatchKindAll));
        }

        watchers.Add(CreateWatcher($"**/{omniSharpConfigFileName}", baseUri, fileWatchKindAll));
        watchers.Add(CreateWatcher("**/*.razor", baseUri, fileWatchKindAll));
        watchers.Add(CreateWatcher("**/*.cshtml", baseUri, fileWatchKindAll));
        watchers.Add(CreateWatcher("**/*.razor.cs", baseUri, fileWatchKindAll));
        watchers.Add(CreateWatcher("**/*.cs", baseUri, fileWatchKindAll));
        watchers.Add(CreateWatcher("**/*.csproj.user", baseUri, fileWatchKindAll));
        watchers.Add(CreateWatcher("**/.editorconfig", baseUri, fileWatchKindAll));
        watchers.Add(CreateWatcher("**/obj/**/generated/**", baseUri, fileWatchKindAll));
        return watchers.ToArray();
    }

    static object CreateWatcher(string pattern, string? baseUri, int fileWatchKindAll)
    {
        if (!string.IsNullOrEmpty(baseUri))
        {
            return new
            {
                globPattern = new
                {
                    baseUri,
                    pattern
                },
                kind = fileWatchKindAll
            };
        }

        return new
        {
            globPattern = pattern,
            kind = fileWatchKindAll
        };
    }
}
