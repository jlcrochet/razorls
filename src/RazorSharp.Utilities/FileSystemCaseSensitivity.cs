namespace RazorSharp.Utilities;

public static class FileSystemCaseSensitivity
{
    public static bool IsCaseInsensitiveForPath(string? probePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        string? lowerPath = null;
        string? upperPath = null;
        try
        {
            var probeDirectory = ResolveProbeDirectory(probePath);
            var name = "razorsharp-case-test-" + Guid.NewGuid().ToString("N");
            lowerPath = Path.Combine(probeDirectory, name.ToLowerInvariant());
            upperPath = Path.Combine(probeDirectory, name.ToUpperInvariant());

            File.WriteAllText(lowerPath, "");
            return File.Exists(upperPath);
        }
        catch
        {
            return OperatingSystem.IsMacOS();
        }
        finally
        {
            TryDelete(lowerPath);
            TryDelete(upperPath);
        }
    }

    private static string ResolveProbeDirectory(string? probePath)
    {
        if (string.IsNullOrWhiteSpace(probePath))
        {
            return Path.GetTempPath();
        }

        try
        {
            var fullPath = Path.GetFullPath(probePath);

            if (File.Exists(fullPath))
            {
                return Path.GetDirectoryName(fullPath) ?? Path.GetTempPath();
            }

            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }

            var current = fullPath;
            while (!string.IsNullOrEmpty(current))
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current)
                {
                    break;
                }

                if (Directory.Exists(parent))
                {
                    return parent;
                }

                current = parent;
            }
        }
        catch
        {
        }

        return Path.GetTempPath();
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
