using System.IO.Compression;
using RazorSharp.Dependencies;

namespace RazorSharp.Server.Tests;

public class DependencyManagerZipSlipTests
{
    [Fact]
    public void ExtractZipToDirectorySafe_RejectsPathTraversal()
    {
        var tempRoot = CreateTempDir();
        var zipPath = Path.Combine(tempRoot, "test.zip");
        var extractPath = Path.Combine(tempRoot, "extract");

        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntry("../evil.txt");
                var entry = zip.CreateEntry("safe.txt");
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream);
                writer.Write("ok");
            }

            Assert.Throws<InvalidOperationException>(() =>
                DependencyManager.ExtractZipToDirectorySafe(zipPath, extractPath));
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void ExtractZipToDirectorySafe_RejectsBackslashTraversal()
    {
        var tempRoot = CreateTempDir();
        var zipPath = Path.Combine(tempRoot, "test.zip");
        var extractPath = Path.Combine(tempRoot, "extract");

        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntry("..\\evil.txt");
                var entry = zip.CreateEntry("safe.txt");
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream);
                writer.Write("ok");
            }

            Assert.Throws<InvalidOperationException>(() =>
                DependencyManager.ExtractZipToDirectorySafe(zipPath, extractPath));
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void ExtractZipToDirectorySafe_NormalizesAbsoluteAndBackslashPaths()
    {
        var tempRoot = CreateTempDir();
        var zipPath = Path.Combine(tempRoot, "test.zip");
        var extractPath = Path.Combine(tempRoot, "extract");

        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var absEntry = zip.CreateEntry("/abs.txt");
                using (var stream = absEntry.Open())
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write("abs");
                }

                var nestedEntry = zip.CreateEntry("dir\\file.txt");
                using (var stream = nestedEntry.Open())
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write("nested");
                }
            }

            DependencyManager.ExtractZipToDirectorySafe(zipPath, extractPath);

            Assert.True(File.Exists(Path.Combine(extractPath, "abs.txt")));
            Assert.True(File.Exists(Path.Combine(extractPath, "dir", "file.txt")));
        }
        finally
        {
            DeleteTempDir(tempRoot);
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
