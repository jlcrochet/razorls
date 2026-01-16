using System.Text.Json;
using System.Text.Json.Serialization;
using RazorSharp.Protocol.Types;

namespace RazorSharp.Protocol.Messages;

/// <summary>
/// Notification sent from Roslyn to update HTML content for a Razor file.
/// Method: razor/updateHtml
/// </summary>
public record RazorUpdateHtmlParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("checksum")]
    public required string Checksum { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>
/// Request sent from Roslyn to forward an HTML request to the HTML LSP.
/// </summary>
public record RazorHtmlForwardedRequest
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("checksum")]
    public required string Checksum { get; init; }

    [JsonPropertyName("request")]
    public required JsonElement Request { get; init; }
}

/// <summary>
/// Notification for opening a solution.
/// Method: solution/open
/// </summary>
public record SolutionOpenParams
{
    [JsonPropertyName("solution")]
    public required string Solution { get; init; }
}

/// <summary>
/// Notification for opening projects.
/// Method: project/open
/// </summary>
public record ProjectOpenParams
{
    [JsonPropertyName("projects")]
    public required string[] Projects { get; init; }
}

/// <summary>
/// Log message from Razor.
/// Method: razor/log
/// </summary>
public record RazorLogParams
{
    [JsonPropertyName("type")]
    public required int Type { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>
/// Notification that project initialization is complete.
/// Method: workspace/projectInitializationComplete
/// </summary>
public record ProjectInitializationCompleteParams;

/// <summary>
/// Notification that a project needs restore.
/// Method: workspace/_roslyn_projectNeedsRestore
/// </summary>
public record ProjectNeedsRestoreParams
{
    [JsonPropertyName("projectFilePaths")]
    public required string[] ProjectFilePaths { get; init; }
}
