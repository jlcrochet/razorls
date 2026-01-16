using System.Text.Json.Serialization;

namespace RazorSharp.Protocol.Types;

/// <summary>
/// The result of a hover request.
/// </summary>
public record Hover(
    [property: JsonPropertyName("contents")] MarkupContent Contents,
    [property: JsonPropertyName("range")] Range? Range);

/// <summary>
/// A `MarkupContent` literal represents a string value which content is interpreted based on its kind flag.
/// </summary>
public record MarkupContent(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] string Value)
{
    public static class Kinds
    {
        public const string PlainText = "plaintext";
        public const string Markdown = "markdown";
    }
}
