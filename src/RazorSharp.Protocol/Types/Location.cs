using System.Text.Json.Serialization;

namespace RazorSharp.Protocol.Types;

/// <summary>
/// Represents a location inside a resource, such as a line inside a text file.
/// </summary>
public record Location(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] Range Range);

/// <summary>
/// Represents a link between a source and a target location.
/// </summary>
public record LocationLink(
    [property: JsonPropertyName("originSelectionRange")] Range? OriginSelectionRange,
    [property: JsonPropertyName("targetUri")] string TargetUri,
    [property: JsonPropertyName("targetRange")] Range TargetRange,
    [property: JsonPropertyName("targetSelectionRange")] Range TargetSelectionRange);
