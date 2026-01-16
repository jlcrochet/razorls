using System.Text.Json.Serialization;

namespace RazorSharp.Protocol.Types;

public enum CompletionItemKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25
}

public enum InsertTextFormat
{
    PlainText = 1,
    Snippet = 2
}

public enum CompletionTriggerKind
{
    Invoked = 1,
    TriggerCharacter = 2,
    TriggerForIncompleteCompletions = 3
}

public record CompletionContext(
    [property: JsonPropertyName("triggerKind")] CompletionTriggerKind TriggerKind,
    [property: JsonPropertyName("triggerCharacter")] string? TriggerCharacter);

public record CompletionItem
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("labelDetails")]
    public CompletionItemLabelDetails? LabelDetails { get; init; }

    [JsonPropertyName("kind")]
    public CompletionItemKind? Kind { get; init; }

    [JsonPropertyName("tags")]
    public int[]? Tags { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("documentation")]
    public object? Documentation { get; init; }

    [JsonPropertyName("deprecated")]
    public bool? Deprecated { get; init; }

    [JsonPropertyName("preselect")]
    public bool? Preselect { get; init; }

    [JsonPropertyName("sortText")]
    public string? SortText { get; init; }

    [JsonPropertyName("filterText")]
    public string? FilterText { get; init; }

    [JsonPropertyName("insertText")]
    public string? InsertText { get; init; }

    [JsonPropertyName("insertTextFormat")]
    public InsertTextFormat? InsertTextFormat { get; init; }

    [JsonPropertyName("textEdit")]
    public object? TextEdit { get; init; }

    [JsonPropertyName("textEditText")]
    public string? TextEditText { get; init; }

    [JsonPropertyName("additionalTextEdits")]
    public TextEdit[]? AdditionalTextEdits { get; init; }

    [JsonPropertyName("commitCharacters")]
    public string[]? CommitCharacters { get; init; }

    [JsonPropertyName("command")]
    public Command? Command { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }
}

public record CompletionItemLabelDetails(
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("description")] string? Description);

public record CompletionList
{
    [JsonPropertyName("isIncomplete")]
    public bool IsIncomplete { get; init; }

    [JsonPropertyName("itemDefaults")]
    public CompletionItemDefaults? ItemDefaults { get; init; }

    [JsonPropertyName("items")]
    public required CompletionItem[] Items { get; init; }
}

public record CompletionItemDefaults
{
    [JsonPropertyName("commitCharacters")]
    public string[]? CommitCharacters { get; init; }

    [JsonPropertyName("editRange")]
    public object? EditRange { get; init; }

    [JsonPropertyName("insertTextFormat")]
    public InsertTextFormat? InsertTextFormat { get; init; }

    [JsonPropertyName("insertTextMode")]
    public int? InsertTextMode { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }
}

public record TextEdit(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("newText")] string NewText);

public record Command(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("command")] string CommandName,
    [property: JsonPropertyName("arguments")] object[]? Arguments);
