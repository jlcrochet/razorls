using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Protocol.Messages;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class WorkspaceWatchedFilesRequestParserTests
{
    [Fact]
    public void TryParse_ReturnsFalse_WhenFileWatchingDisabled()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var parser = new WorkspaceWatchedFilesRequestParser(
            loggerFactory.CreateLogger<WorkspaceWatchedFilesRequestParser>(),
            CreateJsonOptions());
        var payload = JsonSerializer.SerializeToElement(new DidChangeWatchedFilesParams
        {
            Changes =
            [
                new FileEvent { Uri = "/workspace/a.cs", Type = FileChangeType.Changed }
            ]
        });

        var result = parser.TryParse(fileWatchingEnabled: false, payload, out var parsed);

        Assert.False(result);
        Assert.Null(parsed);
    }

    [Fact]
    public void TryParse_ReturnsFalse_WhenChangesMissingOrEmpty()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var parser = new WorkspaceWatchedFilesRequestParser(
            loggerFactory.CreateLogger<WorkspaceWatchedFilesRequestParser>(),
            CreateJsonOptions());

        var missingChanges = JsonSerializer.SerializeToElement(new { });
        var emptyChanges = JsonSerializer.SerializeToElement(new DidChangeWatchedFilesParams { Changes = [] });

        Assert.Throws<JsonException>(() => parser.TryParse(fileWatchingEnabled: true, missingChanges, out _));

        var emptyResult = parser.TryParse(fileWatchingEnabled: true, emptyChanges, out var emptyParsed);

        Assert.False(emptyResult);
        Assert.NotNull(emptyParsed);
        Assert.Empty(emptyParsed!.Changes);
    }

    [Fact]
    public void TryParse_ReturnsTrue_WhenValid()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var parser = new WorkspaceWatchedFilesRequestParser(
            loggerFactory.CreateLogger<WorkspaceWatchedFilesRequestParser>(),
            CreateJsonOptions());
        var payload = JsonSerializer.SerializeToElement(new DidChangeWatchedFilesParams
        {
            Changes =
            [
                new FileEvent { Uri = "/workspace/a.cs", Type = FileChangeType.Changed }
            ]
        });

        var result = parser.TryParse(fileWatchingEnabled: true, payload, out var parsed);

        Assert.True(result);
        Assert.NotNull(parsed);
        Assert.Single(parsed!.Changes);
        Assert.Equal("/workspace/a.cs", parsed.Changes[0].Uri);
    }

    static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }
}
