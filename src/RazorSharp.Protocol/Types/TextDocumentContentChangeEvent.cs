using System.Text.Json.Serialization;

namespace RazorSharp.Protocol.Types;

/// <summary>
/// An event describing a change to a text document. If range and rangeLength are omitted
/// the new text is considered to be the full content of the document.
/// </summary>
public record TextDocumentContentChangeEvent
{
    /// <summary>
    /// The range of the document that changed.
    /// </summary>
    [JsonPropertyName("range")]
    public Range? Range { get; init; }

    /// <summary>
    /// The optional length of the range that got replaced.
    /// </summary>
    [JsonPropertyName("rangeLength")]
    public int? RangeLength { get; init; }

    /// <summary>
    /// The new text for the provided range, or the full document if range is not provided.
    /// </summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}
