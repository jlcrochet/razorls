using RazorSharp.Protocol.Messages;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class WorkspaceWatchedFilesAnalyzerTests
{
    [Fact]
    public void Analyze_MarksConfigChanged_ForLocalConfigPath()
    {
        var analyzer = CreateAnalyzer(
            tryGetLocalPath: static uri => uri,
            isOmniSharpConfigPath: static (path, local, global) => path == local || path == global,
            isSourceGeneratedPath: static _ => false,
            tryUpdateSourceGeneratedIndexForChange: static (_, _) => false,
            isWorkspaceReloadTriggerPath: static _ => false);

        var changes = new[]
        {
            new FileEvent { Uri = "/workspace/omnisharp.json", Type = FileChangeType.Changed }
        };

        var analysis = analyzer.Analyze(changes, "/workspace/omnisharp.json", null);

        Assert.True(analysis.ConfigChanged);
        Assert.False(analysis.SourceGeneratedFullRefreshNeeded);
        Assert.False(analysis.SourceGeneratedIncrementalApplied);
        Assert.False(analysis.WorkspaceReloadNeeded);
    }

    [Fact]
    public void Analyze_MarksIncrementalAndReload_WhenDetected()
    {
        var analyzer = CreateAnalyzer(
            tryGetLocalPath: static uri => uri,
            isOmniSharpConfigPath: static (_, _, _) => false,
            isSourceGeneratedPath: static path => path.Contains("/obj/generated/", StringComparison.Ordinal),
            tryUpdateSourceGeneratedIndexForChange: static (_, _) => true,
            isWorkspaceReloadTriggerPath: static path => path.EndsWith(".csproj", StringComparison.Ordinal));

        var changes = new[]
        {
            new FileEvent { Uri = "/workspace/obj/generated/file.g.cs", Type = FileChangeType.Changed },
            new FileEvent { Uri = "/workspace/project.csproj", Type = FileChangeType.Changed }
        };

        var analysis = analyzer.Analyze(changes, null, null);

        Assert.False(analysis.ConfigChanged);
        Assert.False(analysis.SourceGeneratedFullRefreshNeeded);
        Assert.True(analysis.SourceGeneratedIncrementalApplied);
        Assert.True(analysis.WorkspaceReloadNeeded);
    }

    [Fact]
    public void Analyze_MarksFullRefresh_WhenIncrementalUpdateFails()
    {
        var analyzer = CreateAnalyzer(
            tryGetLocalPath: static uri => uri,
            isOmniSharpConfigPath: static (_, _, _) => false,
            isSourceGeneratedPath: static _ => true,
            tryUpdateSourceGeneratedIndexForChange: static (_, _) => false,
            isWorkspaceReloadTriggerPath: static _ => false);

        var changes = new[]
        {
            new FileEvent { Uri = "/workspace/obj/generated/file.g.cs", Type = FileChangeType.Deleted }
        };

        var analysis = analyzer.Analyze(changes, null, null);

        Assert.False(analysis.ConfigChanged);
        Assert.True(analysis.SourceGeneratedFullRefreshNeeded);
        Assert.False(analysis.SourceGeneratedIncrementalApplied);
        Assert.False(analysis.WorkspaceReloadNeeded);
    }

    [Fact]
    public void Analyze_SkipsChanges_ThatCannotResolveToLocalPath()
    {
        var analyzer = CreateAnalyzer(
            tryGetLocalPath: static _ => null,
            isOmniSharpConfigPath: static (_, _, _) => throw new InvalidOperationException("should not be called"),
            isSourceGeneratedPath: static _ => throw new InvalidOperationException("should not be called"),
            tryUpdateSourceGeneratedIndexForChange: static (_, _) => throw new InvalidOperationException("should not be called"),
            isWorkspaceReloadTriggerPath: static _ => throw new InvalidOperationException("should not be called"));

        var changes = new[]
        {
            new FileEvent { Uri = "untitled:doc", Type = FileChangeType.Changed }
        };

        var analysis = analyzer.Analyze(changes, null, null);

        Assert.False(analysis.ConfigChanged);
        Assert.False(analysis.SourceGeneratedFullRefreshNeeded);
        Assert.False(analysis.SourceGeneratedIncrementalApplied);
        Assert.False(analysis.WorkspaceReloadNeeded);
    }

    static WorkspaceWatchedFilesAnalyzer CreateAnalyzer(
        Func<string, string?> tryGetLocalPath,
        Func<string, string?, string?, bool> isOmniSharpConfigPath,
        Func<string, bool> isSourceGeneratedPath,
        Func<string, FileChangeType, bool> tryUpdateSourceGeneratedIndexForChange,
        Func<string, bool> isWorkspaceReloadTriggerPath)
    {
        return new WorkspaceWatchedFilesAnalyzer(
            tryGetLocalPath,
            isOmniSharpConfigPath,
            isSourceGeneratedPath,
            tryUpdateSourceGeneratedIndexForChange,
            isWorkspaceReloadTriggerPath);
    }
}
