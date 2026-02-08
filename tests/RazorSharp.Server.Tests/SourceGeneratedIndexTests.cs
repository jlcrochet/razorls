using System.Linq;
using System.Reflection;
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

    [Fact]
    public async Task TryGetSourceGeneratedPath_UsesStaleIndexEntryAndSchedulesRefresh()
    {
        var root = CreateTempDir();
        var depsPath = Path.Combine(root, "deps");
        var staleFile = Path.Combine(root, "stale", "File.g.cs");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(staleFile)!);
            File.WriteAllText(staleFile, "stale");

            using var loggerFactory = LoggerFactory.Create(_ => { });
            using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "1.0.0-test", depsPath);
            var server = new RazorLanguageServer(loggerFactory, deps);
            try
            {
                SetPrivateField(server, "_workspaceRoot", root);
                var stateField = typeof(RazorLanguageServer).GetField("_sourceGeneratedIndexState", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(stateField);
                var fullScan = Enum.Parse(stateField!.FieldType, "FullScan");
                SetPrivateField(server, "_sourceGeneratedIndexState", fullScan);
                SetPrivateField(server, "_sourceGeneratedIndexLastFullScan", DateTime.UtcNow - TimeSpan.FromMinutes(5));

                var key = InvokeMakeSourceGeneratedKey("Asm", "Type", "File.g.cs");
                var staleEntry = CreateSourceGeneratedEntry(staleFile, isDebug: true);
                SetSourceGeneratedIndex(server, key, staleEntry);

                var found = InvokeTryGetSourceGeneratedPath(server, key, projectId: null, out var path);

                Assert.True(found);
                Assert.Equal(staleFile, path);
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

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static string InvokeMakeSourceGeneratedKey(string assemblyName, string typeName, string hintName)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "MakeSourceGeneratedKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [assemblyName, typeName, hintName])!;
    }

    private static object CreateSourceGeneratedEntry(string path, bool isDebug)
    {
        var type = typeof(RazorLanguageServer).GetNestedType("SourceGeneratedEntry", BindingFlags.NonPublic);
        Assert.NotNull(type);
        return Activator.CreateInstance(type!, [path, isDebug, File.GetLastWriteTimeUtc(path)])!;
    }

    private static void SetSourceGeneratedIndex(RazorLanguageServer server, string key, object entry)
    {
        var entryType = entry.GetType();
        var listType = typeof(List<>).MakeGenericType(entryType);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        list.Add(entry);

        var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), listType);
        var dict = (System.Collections.IDictionary)Activator.CreateInstance(dictType, StringComparer.OrdinalIgnoreCase)!;
        dict.Add(key, list);

        SetPrivateField(server, "_sourceGeneratedIndex", dict);
    }

    private static bool InvokeTryGetSourceGeneratedPath(RazorLanguageServer server, string key, string? projectId, out string path)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "TryGetSourceGeneratedPath",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var args = new object?[] { key, projectId, "" };
        var result = (bool)method!.Invoke(server, args)!;
        path = (string)args[2]!;
        return result;
    }
}
