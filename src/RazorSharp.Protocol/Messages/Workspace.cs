using System.Text.Json.Serialization;

namespace RazorSharp.Protocol.Messages;

public record DidChangeWatchedFilesParams
{
    [JsonPropertyName("changes")]
    public required FileEvent[] Changes { get; init; }
}

public record FileEvent
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("type")]
    public required FileChangeType Type { get; init; }
}

public enum FileChangeType
{
    Created = 1,
    Changed = 2,
    Deleted = 3
}
