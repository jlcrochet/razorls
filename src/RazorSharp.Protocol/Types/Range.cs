using System.Text.Json.Serialization;

namespace RazorSharp.Protocol.Types;

/// <summary>
/// A range in a text document expressed as start and end positions.
/// </summary>
public record Range(
    [property: JsonPropertyName("start")] Position Start,
    [property: JsonPropertyName("end")] Position End)
{
    public static Range Empty => new(Position.Zero, Position.Zero);
}
