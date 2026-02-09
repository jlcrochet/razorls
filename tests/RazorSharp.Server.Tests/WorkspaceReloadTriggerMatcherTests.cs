using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class WorkspaceReloadTriggerMatcherTests
{
    [Fact]
    public void IsWorkspaceReloadTriggerPath_MatchesByExtension()
    {
        var result = WorkspaceReloadTriggerMatcher.IsWorkspaceReloadTriggerPath(
            "/tmp/project.CSPROJ",
            workspaceReloadExtensions: [".sln", ".csproj"],
            workspaceReloadFileNames: ["global.json"]);

        Assert.True(result);
    }

    [Fact]
    public void IsWorkspaceReloadTriggerPath_MatchesByFileName()
    {
        var result = WorkspaceReloadTriggerMatcher.IsWorkspaceReloadTriggerPath(
            "/tmp/GLOBAL.JSON",
            workspaceReloadExtensions: [".sln", ".csproj"],
            workspaceReloadFileNames: ["global.json", "Directory.Build.props"]);

        Assert.True(result);
    }

    [Fact]
    public void IsWorkspaceReloadTriggerPath_ReturnsFalseWhenNoMatch()
    {
        var result = WorkspaceReloadTriggerMatcher.IsWorkspaceReloadTriggerPath(
            "/tmp/readme.md",
            workspaceReloadExtensions: [".sln", ".csproj"],
            workspaceReloadFileNames: ["global.json"]);

        Assert.False(result);
    }
}
