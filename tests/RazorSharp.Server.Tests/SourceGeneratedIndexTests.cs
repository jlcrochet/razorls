using System.Linq;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class SourceGeneratedIndexTests
{
    [Fact]
    public async Task EnumerateObjDirectories_SkipsExcludedDirectories()
    {
        var root = CreateTempDir();
        var depsPath = Path.Combine(root, "deps");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "obj"));
            Directory.CreateDirectory(Path.Combine(root, "src", "obj"));
            Directory.CreateDirectory(Path.Combine(root, "bin", "obj"));
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "obj"));
            Directory.CreateDirectory(Path.Combine(root, ".git", "obj"));
            Directory.CreateDirectory(Path.Combine(root, ".vs", "obj"));
            Directory.CreateDirectory(Path.Combine(root, ".idea", "obj"));

            using var loggerFactory = LoggerFactory.Create(_ => { });
            using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "1.0.0-test", depsPath);
            var server = new RazorLanguageServer(loggerFactory, deps);
            try
            {
                var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;

                var expected = new HashSet<string>(comparer)
                {
                    Path.GetFullPath(Path.Combine(root, "obj")),
                    Path.GetFullPath(Path.Combine(root, "src", "obj")),
                    Path.GetFullPath(Path.Combine(root, ".idea", "obj"))
                };

                var actual = server.EnumerateObjDirectoriesForTests(root)
                    .Select(Path.GetFullPath)
                    .ToHashSet(comparer);

                Assert.Equal(expected, actual);
            }
            finally
            {
                await server.DisposeAsync();
            }
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public async Task EnumerateObjDirectories_RespectsWorkspaceExcludes()
    {
        var root = CreateTempDir();
        var depsPath = Path.Combine(root, "deps");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "obj"));
            Directory.CreateDirectory(Path.Combine(root, "src", "obj"));
            Directory.CreateDirectory(Path.Combine(root, "other", "obj"));

            using var loggerFactory = LoggerFactory.Create(_ => { });
            using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "1.0.0-test", depsPath);
            var server = new RazorLanguageServer(loggerFactory, deps);
            try
            {
                server.ConfigureExcludedDirectoriesForTests(overrideDirectories: null, additionalDirectories: new[] { "src" });

                var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;

                var expected = new HashSet<string>(comparer)
                {
                    Path.GetFullPath(Path.Combine(root, "obj")),
                    Path.GetFullPath(Path.Combine(root, "other", "obj"))
                };

                var actual = server.EnumerateObjDirectoriesForTests(root)
                    .Select(Path.GetFullPath)
                    .ToHashSet(comparer);

                Assert.Equal(expected, actual);
            }
            finally
            {
                await server.DisposeAsync();
            }
        }
        finally
        {
            DeleteTempDir(root);
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
