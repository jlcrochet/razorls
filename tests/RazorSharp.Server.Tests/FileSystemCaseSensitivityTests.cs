using RazorSharp.Utilities;

namespace RazorSharp.Server.Tests;

public class FileSystemCaseSensitivityTests
{
    [Fact]
    public void IsCaseInsensitiveForPath_Windows_ReturnsTrue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(FileSystemCaseSensitivity.IsCaseInsensitiveForPath(Path.GetTempPath()));
    }

    [Fact]
    public void IsCaseInsensitiveForPath_NonExistentPath_DoesNotThrow()
    {
        var probePath = Path.Combine(Path.GetTempPath(), "razorsharp-tests", Guid.NewGuid().ToString("N"), "missing");
        var result = FileSystemCaseSensitivity.IsCaseInsensitiveForPath(probePath);
        Assert.True(result || !result);
    }

    [Fact]
    public void IsCaseInsensitiveForPath_UsesProbeDirectory()
    {
        var root = CreateTempDir();
        var name = "razorsharp-case-check-" + Guid.NewGuid().ToString("N");
        var lowerPath = Path.Combine(root, name.ToLowerInvariant());
        var upperPath = Path.Combine(root, name.ToUpperInvariant());

        try
        {
            File.WriteAllText(lowerPath, "");
            var expected = File.Exists(upperPath);
            var actual = FileSystemCaseSensitivity.IsCaseInsensitiveForPath(root);

            Assert.Equal(expected, actual);
        }
        finally
        {
            TryDelete(lowerPath);
            TryDelete(upperPath);
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void IsCaseInsensitiveForPath_ReadOnlyDirectory_UsesConservativeFallbackOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
        {
            return;
        }

        var root = CreateTempDir();
        var modeChanged = false;

        try
        {
            try
            {
                File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                modeChanged = true;
            }
            catch (PlatformNotSupportedException)
            {
                return;
            }
            catch (NotSupportedException)
            {
                return;
            }

            var result = FileSystemCaseSensitivity.IsCaseInsensitiveForPath(root);
            Assert.False(result);
        }
        finally
        {
            if (modeChanged)
            {
                try
                {
                    File.SetUnixFileMode(
                        root,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
                }
                catch
                {
                }
            }

            DeleteTempDir(root);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "razorsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
