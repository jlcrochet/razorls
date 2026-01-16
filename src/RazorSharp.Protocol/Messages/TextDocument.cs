using System.Text.Json.Serialization;
using RazorSharp.Protocol.Types;

namespace RazorSharp.Protocol.Messages;

public record DidOpenTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentItem TextDocument { get; init; }
}

public record DidChangeTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public required VersionedTextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("contentChanges")]
    public required TextDocumentContentChangeEvent[] ContentChanges { get; init; }
}

public record DidCloseTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }
}

public record DidSaveTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

public record TextDocumentPositionParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("position")]
    public required Position Position { get; init; }
}

public record CompletionParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("position")]
    public required Position Position { get; init; }

    [JsonPropertyName("context")]
    public CompletionContext? Context { get; init; }
}

public record HoverParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("position")]
    public required Position Position { get; init; }
}

public record DefinitionParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("position")]
    public required Position Position { get; init; }
}

public record ReferenceParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("position")]
    public required Position Position { get; init; }

    [JsonPropertyName("context")]
    public required ReferenceContext Context { get; init; }
}

public record ReferenceContext
{
    [JsonPropertyName("includeDeclaration")]
    public bool IncludeDeclaration { get; init; }
}

public record DocumentFormattingParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("options")]
    public required FormattingOptions Options { get; init; }
}

public record FormattingOptions
{
    [JsonPropertyName("tabSize")]
    public required int TabSize { get; init; }

    [JsonPropertyName("insertSpaces")]
    public required bool InsertSpaces { get; init; }

    [JsonPropertyName("trimTrailingWhitespace")]
    public bool? TrimTrailingWhitespace { get; init; }

    [JsonPropertyName("insertFinalNewline")]
    public bool? InsertFinalNewline { get; init; }

    [JsonPropertyName("trimFinalNewlines")]
    public bool? TrimFinalNewlines { get; init; }
}

public record CodeActionParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("range")]
    public required Types.Range Range { get; init; }

    [JsonPropertyName("context")]
    public required CodeActionContext Context { get; init; }
}

public record CodeActionContext
{
    [JsonPropertyName("diagnostics")]
    public required Diagnostic[] Diagnostics { get; init; }

    [JsonPropertyName("only")]
    public string[]? Only { get; init; }

    [JsonPropertyName("triggerKind")]
    public int? TriggerKind { get; init; }
}

public record RenameParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("position")]
    public required Position Position { get; init; }

    [JsonPropertyName("newName")]
    public required string NewName { get; init; }
}

public record PublishDiagnosticsParams
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("version")]
    public int? Version { get; init; }

    [JsonPropertyName("diagnostics")]
    public required Diagnostic[] Diagnostics { get; init; }
}
