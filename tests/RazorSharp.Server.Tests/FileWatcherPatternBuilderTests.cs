using System.Text.Json;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class FileWatcherPatternBuilderTests
{
    [Fact]
    public void CreateFileWatchers_UsesBaseUriObjectGlobPattern()
    {
        var watchers = FileWatcherPatternBuilder.CreateFileWatchers(
            baseUri: "file:///workspace",
            workspaceReloadExtensions: [".sln"],
            workspaceReloadFileNames: ["global.json"],
            omniSharpConfigFileName: "omnisharp.json",
            fileWatchKindAll: 7);

        var json = JsonSerializer.SerializeToElement(watchers);
        var first = json[0];
        var glob = first.GetProperty("globPattern");
        Assert.Equal(JsonValueKind.Object, glob.ValueKind);
        Assert.Equal("file:///workspace", glob.GetProperty("baseUri").GetString());
        Assert.Equal("**/*.sln", glob.GetProperty("pattern").GetString());
    }

    [Fact]
    public void CreateFileWatchers_UsesStringGlobPatternWithoutBaseUri()
    {
        var watchers = FileWatcherPatternBuilder.CreateFileWatchers(
            baseUri: null,
            workspaceReloadExtensions: [".sln"],
            workspaceReloadFileNames: ["global.json"],
            omniSharpConfigFileName: "omnisharp.json",
            fileWatchKindAll: 7);

        var json = JsonSerializer.SerializeToElement(watchers);
        var first = json[0];
        var glob = first.GetProperty("globPattern");
        Assert.Equal(JsonValueKind.String, glob.ValueKind);
        Assert.Equal("**/*.sln", glob.GetString());
    }

    [Fact]
    public void CreateFileWatchers_IncludesExpectedDefaultWatchers()
    {
        var watchers = FileWatcherPatternBuilder.CreateFileWatchers(
            baseUri: null,
            workspaceReloadExtensions: [".sln"],
            workspaceReloadFileNames: ["global.json"],
            omniSharpConfigFileName: "omnisharp.json",
            fileWatchKindAll: 7);

        var json = JsonSerializer.SerializeToElement(watchers);
        var patterns = json.EnumerateArray()
            .Select(w => w.GetProperty("globPattern").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("**/*.sln", patterns);
        Assert.Contains("**/global.json", patterns);
        Assert.Contains("**/omnisharp.json", patterns);
        Assert.Contains("**/*.razor", patterns);
        Assert.Contains("**/*.cshtml", patterns);
        Assert.Contains("**/*.razor.cs", patterns);
        Assert.Contains("**/*.cs", patterns);
        Assert.Contains("**/*.csproj.user", patterns);
        Assert.Contains("**/.editorconfig", patterns);
        Assert.Contains("**/obj/**/generated/**", patterns);
    }
}
