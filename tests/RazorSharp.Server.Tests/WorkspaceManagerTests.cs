using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using RazorSharp.Server.Workspace;

namespace RazorSharp.Server.Tests;

public class WorkspaceManagerTests
{
    [Fact]
    public void FindProjects_SkipsBinObjAndGitDirectories()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var rootProject = Path.Combine(tempRoot, "Root.csproj");
            var binProject = Path.Combine(tempRoot, "bin", "Bin.csproj");
            var objProject = Path.Combine(tempRoot, "obj", "Obj.csproj");
            var gitProject = Path.Combine(tempRoot, ".git", "Git.csproj");
            var libProject = Path.Combine(tempRoot, "src", "Lib", "Lib.csproj");
            var nestedBinProject = Path.Combine(tempRoot, "src", "bin", "InnerBin.csproj");

            TouchFile(rootProject);
            TouchFile(binProject);
            TouchFile(objProject);
            TouchFile(gitProject);
            TouchFile(libProject);
            TouchFile(nestedBinProject);

            var manager = CreateManager();
            var projects = manager.FindProjects(tempRoot)
                .Select(Path.GetFullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var expected = new[]
            {
                Path.GetFullPath(rootProject),
                Path.GetFullPath(libProject)
            }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

            Assert.Equal(expected, projects);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void FindProjects_NonExistentPath_ReturnsEmpty()
    {
        var manager = CreateManager();
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var projects = manager.FindProjects(missingPath);
        Assert.Empty(projects);
    }

    [Fact]
    public void FindProjects_RespectsExcludePatterns()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var rootProject = Path.Combine(tempRoot, "Root.csproj");
            var packagesProject = Path.Combine(tempRoot, "packages", "Lib", "Lib.csproj");
            var nestedPackagesProject = Path.Combine(tempRoot, "src", "packages", "Inner", "Inner.csproj");
            var appProject = Path.Combine(tempRoot, "src", "App", "App.csproj");

            TouchFile(rootProject);
            TouchFile(packagesProject);
            TouchFile(nestedPackagesProject);
            TouchFile(appProject);

            var manager = CreateManager();
            manager.ConfigureExcludedDirectories(null, ["**/packages/**"]);

            var projects = manager.FindProjects(tempRoot)
                .Select(Path.GetFullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var expected = new[]
            {
                Path.GetFullPath(rootProject),
                Path.GetFullPath(appProject)
            }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

            Assert.Equal(expected, projects);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void FindProjects_ExcludeOverride_ReplacesDefaults()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var rootProject = Path.Combine(tempRoot, "Root.csproj");
            var nodeModulesProject = Path.Combine(tempRoot, "node_modules", "Pkg", "Pkg.csproj");

            TouchFile(rootProject);
            TouchFile(nodeModulesProject);

            var manager = CreateManager();
            manager.ConfigureExcludedDirectories(Array.Empty<string>(), null);

            var projects = manager.FindProjects(tempRoot)
                .Select(Path.GetFullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var expected = new[]
            {
                Path.GetFullPath(rootProject),
                Path.GetFullPath(nodeModulesProject)
            }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

            Assert.Equal(expected, projects);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void FindSolution_NoSolution_ReturnsNull()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var manager = CreateManager();
            var found = manager.FindSolution(tempRoot);
            Assert.Null(found);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void FindSolution_PrefersDirectoryNamedSolution()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var workspace = Path.Combine(tempRoot, "MyWorkspace");
            Directory.CreateDirectory(workspace);

            var preferred = Path.Combine(workspace, "MyWorkspace.sln");
            var other = Path.Combine(workspace, "Other.sln");
            TouchFile(preferred);
            TouchFile(other);

            var manager = CreateManager();
            var found = manager.FindSolution(workspace);

            Assert.Equal(Path.GetFullPath(preferred), found);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void FindSolution_WalksUpParentDirectories()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var parent = Path.Combine(tempRoot, "Parent");
            var child = Path.Combine(parent, "Child");
            Directory.CreateDirectory(child);

            var parentSolution = Path.Combine(parent, "Parent.sln");
            TouchFile(parentSolution);

            var manager = CreateManager();
            var found = manager.FindSolution(child);

            Assert.Equal(Path.GetFullPath(parentSolution), found);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void FindSolution_FindsSolutionFiltersAndSlnx()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var workspace = Path.Combine(tempRoot, "MyWorkspace");
            Directory.CreateDirectory(workspace);

            var filter = Path.Combine(workspace, "MyWorkspace.slnf");
            var slnx = Path.Combine(workspace, "MyWorkspace.slnx");
            TouchFile(filter);
            TouchFile(slnx);

            var manager = CreateManager();
            var found = manager.FindSolution(workspace);

            Assert.Equal(Path.GetFullPath(filter), found);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void FindSolution_PrefersSlnOverSlnfAndSlnx()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var workspace = Path.Combine(tempRoot, "MyWorkspace");
            Directory.CreateDirectory(workspace);

            var sln = Path.Combine(workspace, "MyWorkspace.sln");
            var slnf = Path.Combine(workspace, "MyWorkspace.slnf");
            var slnx = Path.Combine(workspace, "MyWorkspace.slnx");
            TouchFile(sln);
            TouchFile(slnf);
            TouchFile(slnx);

            var manager = CreateManager();
            var found = manager.FindSolution(workspace);

            Assert.Equal(Path.GetFullPath(sln), found);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    private static WorkspaceManager CreateManager()
    {
        var loggerFactory = LoggerFactory.Create(builder => { });
        return new WorkspaceManager(loggerFactory.CreateLogger<WorkspaceManager>());
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

    private static void TouchFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }
}
