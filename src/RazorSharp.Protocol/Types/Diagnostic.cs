using System.Text.Json.Serialization;

namespace RazorSharp.Protocol.Types;

public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4
}

public enum DiagnosticTag
{
    Unnecessary = 1,
    Deprecated = 2
}

/// <summary>
/// Represents a diagnostic, such as a compiler error or warning.
/// </summary>
public record Diagnostic
{
    [JsonPropertyName("range")]
    public required Range Range { get; init; }

    [JsonPropertyName("severity")]
    public DiagnosticSeverity? Severity { get; init; }

    [JsonPropertyName("code")]
    public object? Code { get; init; }

    [JsonPropertyName("codeDescription")]
    public CodeDescription? CodeDescription { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("tags")]
    public DiagnosticTag[]? Tags { get; init; }

    [JsonPropertyName("relatedInformation")]
    public DiagnosticRelatedInformation[]? RelatedInformation { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }
}

public record CodeDescription(
    [property: JsonPropertyName("href")] string Href);

public record DiagnosticRelatedInformation(
    [property: JsonPropertyName("location")] Location Location,
    [property: JsonPropertyName("message")] string Message);
