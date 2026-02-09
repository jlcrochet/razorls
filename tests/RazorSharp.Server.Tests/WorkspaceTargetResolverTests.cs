using RazorSharp.Protocol.Messages;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class WorkspaceTargetResolverTests
{
    [Fact]
    public void GetWorkspaceOpenTarget_UsesExpectedPrecedence()
    {
        var resolver = new WorkspaceTargetResolver(static path => path, static path => path);
        var initParams = new InitializeParams
        {
            RootUri = "file:///root-uri",
            WorkspaceFolders =
            [
                new WorkspaceFolder("file:///workspace-folder", "workspace-folder")
            ]
        };

        var explicitTarget = resolver.GetWorkspaceOpenTarget(
            workspaceOpenTarget: "/workspace/explicit.sln",
            cliSolutionPath: "/workspace/cli.sln",
            initParams: initParams,
            workspaceRoot: "/workspace/root");
        Assert.Equal("/workspace/explicit.sln", explicitTarget);

        var cliTarget = resolver.GetWorkspaceOpenTarget(
            workspaceOpenTarget: null,
            cliSolutionPath: "/workspace/cli.sln",
            initParams: initParams,
            workspaceRoot: "/workspace/root");
        Assert.Equal("/workspace/cli.sln", cliTarget);

        var rootUriTarget = resolver.GetWorkspaceOpenTarget(
            workspaceOpenTarget: null,
            cliSolutionPath: null,
            initParams: initParams,
            workspaceRoot: "/workspace/root");
        Assert.Equal("file:///root-uri", rootUriTarget);

        var folderTarget = resolver.GetWorkspaceOpenTarget(
            workspaceOpenTarget: null,
            cliSolutionPath: null,
            initParams: new InitializeParams
            {
                WorkspaceFolders = [new WorkspaceFolder("file:///workspace-folder", "workspace-folder")]
            },
            workspaceRoot: "/workspace/root");
        Assert.Equal("file:///workspace-folder", folderTarget);

        var fallback = resolver.GetWorkspaceOpenTarget(
            workspaceOpenTarget: null,
            cliSolutionPath: null,
            initParams: null,
            workspaceRoot: "/workspace/root");
        Assert.Equal("/workspace/root", fallback);
    }

    [Fact]
    public void TryGetWorkspaceBaseUri_UsesWorkspaceRootFileParent()
    {
        var resolver = new WorkspaceTargetResolver(static path => path, Path.GetFullPath);
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var solutionPath = Path.Combine(tempRoot, "test.sln");
        File.WriteAllText(solutionPath, "");

        try
        {
            var baseUri = resolver.TryGetWorkspaceBaseUri(
                workspaceRoot: solutionPath,
                workspaceOpenTarget: null,
                cliSolutionPath: null,
                initParams: null);

            Assert.Equal(new Uri(Path.GetFullPath(tempRoot)).AbsoluteUri, baseUri);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TryGetWorkspaceBaseUri_FallsBackToResolvedTarget()
    {
        var resolver = new WorkspaceTargetResolver(
            static target => target == "file:///workspace/project.sln" ? "/workspace/project.sln" : null,
            static path => path);

        var baseUri = resolver.TryGetWorkspaceBaseUri(
            workspaceRoot: null,
            workspaceOpenTarget: null,
            cliSolutionPath: "file:///workspace/project.sln",
            initParams: null);

        Assert.Equal("file:///workspace/project.sln", baseUri);
    }
}
