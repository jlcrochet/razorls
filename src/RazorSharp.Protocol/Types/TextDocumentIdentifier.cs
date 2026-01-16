using System.Text.Json.Serialization;

namespace RazorSharp.Protocol.Types;

/// <summary>
/// Text documents are identified using a URI.
/// </summary>
public record TextDocumentIdentifier
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }
}

/// <summary>
/// An identifier to denote a specific version of a text document.
/// </summary>
public record VersionedTextDocumentIdentifier : TextDocumentIdentifier
{
    [JsonPropertyName("version")]
    public required int Version { get; init; }
}

/// <summary>
/// A literal to identify a text document in the client.
/// </summary>
public record TextDocumentItem
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("languageId")]
    public required string LanguageId { get; init; }

    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}
