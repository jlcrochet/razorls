using System.Text.Json.Serialization;

namespace RazorSharp.Protocol.Types;

/// <summary>
/// Position in a text document expressed as zero-based line and character offset.
/// </summary>
public record Position(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character)
{
    public static Position Zero => new(0, 0);
}
