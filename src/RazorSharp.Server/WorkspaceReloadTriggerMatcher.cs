namespace RazorSharp.Server;

internal static class WorkspaceReloadTriggerMatcher
{
    public static bool IsWorkspaceReloadTriggerPath(string path, string[] workspaceReloadExtensions, string[] workspaceReloadFileNames)
    {
        var extension = Path.GetExtension(path);
        foreach (var reloadExtension in workspaceReloadExtensions)
        {
            if (extension.Equals(reloadExtension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var fileName = Path.GetFileName(path);
        foreach (var reloadFileName in workspaceReloadFileNames)
        {
            if (fileName.Equals(reloadFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
