using System.Text.Json;
using System.Text.Json.Serialization;

namespace RazorSharp.Protocol.Messages;

public record InitializeParams
{
    [JsonPropertyName("processId")]
    public int? ProcessId { get; init; }

    [JsonPropertyName("clientInfo")]
    public ClientInfo? ClientInfo { get; init; }

    [JsonPropertyName("locale")]
    public string? Locale { get; init; }

    [JsonPropertyName("rootPath")]
    public string? RootPath { get; init; }

    [JsonPropertyName("rootUri")]
    public string? RootUri { get; init; }

    [JsonPropertyName("initializationOptions")]
    public JsonElement? InitializationOptions { get; init; }

    [JsonPropertyName("capabilities")]
    public ClientCapabilities? Capabilities { get; init; }

    [JsonPropertyName("trace")]
    public string? Trace { get; init; }

    [JsonPropertyName("workspaceFolders")]
    public WorkspaceFolder[]? WorkspaceFolders { get; init; }
}

public record ClientInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version);

public record WorkspaceFolder(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name);

public record ClientCapabilities
{
    [JsonPropertyName("workspace")]
    public WorkspaceClientCapabilities? Workspace { get; init; }

    [JsonPropertyName("textDocument")]
    public TextDocumentClientCapabilities? TextDocument { get; init; }

    [JsonPropertyName("window")]
    public WindowClientCapabilities? Window { get; init; }

    [JsonPropertyName("general")]
    public GeneralClientCapabilities? General { get; init; }

    [JsonPropertyName("experimental")]
    public JsonElement? Experimental { get; init; }
}

public record WorkspaceClientCapabilities
{
    [JsonPropertyName("applyEdit")]
    public bool? ApplyEdit { get; init; }

    [JsonPropertyName("workspaceEdit")]
    public WorkspaceEditClientCapabilities? WorkspaceEdit { get; init; }

    [JsonPropertyName("didChangeConfiguration")]
    public DidChangeConfigurationClientCapabilities? DidChangeConfiguration { get; init; }

    [JsonPropertyName("didChangeWatchedFiles")]
    public DidChangeWatchedFilesClientCapabilities? DidChangeWatchedFiles { get; init; }

    [JsonPropertyName("symbol")]
    public WorkspaceSymbolClientCapabilities? Symbol { get; init; }

    [JsonPropertyName("executeCommand")]
    public ExecuteCommandClientCapabilities? ExecuteCommand { get; init; }

    [JsonPropertyName("workspaceFolders")]
    public bool? WorkspaceFolders { get; init; }

    [JsonPropertyName("configuration")]
    public bool? Configuration { get; init; }

    [JsonPropertyName("semanticTokens")]
    public SemanticTokensWorkspaceClientCapabilities? SemanticTokens { get; init; }

    [JsonPropertyName("codeLens")]
    public CodeLensWorkspaceClientCapabilities? CodeLens { get; init; }

    [JsonPropertyName("fileOperations")]
    public FileOperationClientCapabilities? FileOperations { get; init; }

    [JsonPropertyName("inlineValue")]
    public InlineValueWorkspaceClientCapabilities? InlineValue { get; init; }

    [JsonPropertyName("inlayHint")]
    public InlayHintWorkspaceClientCapabilities? InlayHint { get; init; }

    [JsonPropertyName("diagnostics")]
    public DiagnosticWorkspaceClientCapabilities? Diagnostics { get; init; }
}

public record TextDocumentClientCapabilities
{
    [JsonPropertyName("synchronization")]
    public TextDocumentSyncClientCapabilities? Synchronization { get; init; }

    [JsonPropertyName("completion")]
    public CompletionClientCapabilities? Completion { get; init; }

    [JsonPropertyName("hover")]
    public HoverClientCapabilities? Hover { get; init; }

    [JsonPropertyName("signatureHelp")]
    public SignatureHelpClientCapabilities? SignatureHelp { get; init; }

    [JsonPropertyName("declaration")]
    public DeclarationClientCapabilities? Declaration { get; init; }

    [JsonPropertyName("definition")]
    public DefinitionClientCapabilities? Definition { get; init; }

    [JsonPropertyName("typeDefinition")]
    public TypeDefinitionClientCapabilities? TypeDefinition { get; init; }

    [JsonPropertyName("implementation")]
    public ImplementationClientCapabilities? Implementation { get; init; }

    [JsonPropertyName("references")]
    public ReferenceClientCapabilities? References { get; init; }

    [JsonPropertyName("documentHighlight")]
    public DocumentHighlightClientCapabilities? DocumentHighlight { get; init; }

    [JsonPropertyName("documentSymbol")]
    public DocumentSymbolClientCapabilities? DocumentSymbol { get; init; }

    [JsonPropertyName("codeAction")]
    public CodeActionClientCapabilities? CodeAction { get; init; }

    [JsonPropertyName("codeLens")]
    public CodeLensClientCapabilities? CodeLens { get; init; }

    [JsonPropertyName("documentLink")]
    public DocumentLinkClientCapabilities? DocumentLink { get; init; }

    [JsonPropertyName("colorProvider")]
    public DocumentColorClientCapabilities? ColorProvider { get; init; }

    [JsonPropertyName("formatting")]
    public DocumentFormattingClientCapabilities? Formatting { get; init; }

    [JsonPropertyName("rangeFormatting")]
    public DocumentRangeFormattingClientCapabilities? RangeFormatting { get; init; }

    [JsonPropertyName("onTypeFormatting")]
    public DocumentOnTypeFormattingClientCapabilities? OnTypeFormatting { get; init; }

    [JsonPropertyName("rename")]
    public RenameClientCapabilities? Rename { get; init; }

    [JsonPropertyName("publishDiagnostics")]
    public PublishDiagnosticsClientCapabilities? PublishDiagnostics { get; init; }

    [JsonPropertyName("foldingRange")]
    public FoldingRangeClientCapabilities? FoldingRange { get; init; }

    [JsonPropertyName("selectionRange")]
    public SelectionRangeClientCapabilities? SelectionRange { get; init; }

    [JsonPropertyName("linkedEditingRange")]
    public LinkedEditingRangeClientCapabilities? LinkedEditingRange { get; init; }

    [JsonPropertyName("callHierarchy")]
    public CallHierarchyClientCapabilities? CallHierarchy { get; init; }

    [JsonPropertyName("semanticTokens")]
    public SemanticTokensClientCapabilities? SemanticTokens { get; init; }

    [JsonPropertyName("moniker")]
    public MonikerClientCapabilities? Moniker { get; init; }

    [JsonPropertyName("typeHierarchy")]
    public TypeHierarchyClientCapabilities? TypeHierarchy { get; init; }

    [JsonPropertyName("inlineValue")]
    public InlineValueClientCapabilities? InlineValue { get; init; }

    [JsonPropertyName("inlayHint")]
    public InlayHintClientCapabilities? InlayHint { get; init; }

    [JsonPropertyName("diagnostic")]
    public DiagnosticClientCapabilities? Diagnostic { get; init; }
}

public record WindowClientCapabilities
{
    [JsonPropertyName("workDoneProgress")]
    public bool? WorkDoneProgress { get; init; }

    [JsonPropertyName("showMessage")]
    public ShowMessageRequestClientCapabilities? ShowMessage { get; init; }

    [JsonPropertyName("showDocument")]
    public ShowDocumentClientCapabilities? ShowDocument { get; init; }
}

public record GeneralClientCapabilities
{
    [JsonPropertyName("staleRequestSupport")]
    public StaleRequestSupportClientCapabilities? StaleRequestSupport { get; init; }

    [JsonPropertyName("regularExpressions")]
    public RegularExpressionsClientCapabilities? RegularExpressions { get; init; }

    [JsonPropertyName("markdown")]
    public MarkdownClientCapabilities? Markdown { get; init; }

    [JsonPropertyName("positionEncodings")]
    public string[]? PositionEncodings { get; init; }
}

// Stub records for capabilities - implement as needed
public record WorkspaceEditClientCapabilities;
public record DidChangeConfigurationClientCapabilities;
public record DidChangeWatchedFilesClientCapabilities;
public record WorkspaceSymbolClientCapabilities;
public record ExecuteCommandClientCapabilities;
public record SemanticTokensWorkspaceClientCapabilities;
public record CodeLensWorkspaceClientCapabilities;
public record FileOperationClientCapabilities;
public record InlineValueWorkspaceClientCapabilities;
public record InlayHintWorkspaceClientCapabilities;
public record DiagnosticWorkspaceClientCapabilities;
public record TextDocumentSyncClientCapabilities;
public record CompletionClientCapabilities;
public record HoverClientCapabilities;
public record SignatureHelpClientCapabilities;
public record DeclarationClientCapabilities;
public record DefinitionClientCapabilities;
public record TypeDefinitionClientCapabilities;
public record ImplementationClientCapabilities;
public record ReferenceClientCapabilities;
public record DocumentHighlightClientCapabilities;
public record DocumentSymbolClientCapabilities;
public record CodeActionClientCapabilities;
public record CodeLensClientCapabilities;
public record DocumentLinkClientCapabilities;
public record DocumentColorClientCapabilities;
public record DocumentFormattingClientCapabilities;
public record DocumentRangeFormattingClientCapabilities;
public record DocumentOnTypeFormattingClientCapabilities;
public record RenameClientCapabilities;
public record PublishDiagnosticsClientCapabilities;
public record FoldingRangeClientCapabilities;
public record SelectionRangeClientCapabilities;
public record LinkedEditingRangeClientCapabilities;
public record CallHierarchyClientCapabilities;
public record SemanticTokensClientCapabilities;
public record MonikerClientCapabilities;
public record TypeHierarchyClientCapabilities;
public record InlineValueClientCapabilities;
public record InlayHintClientCapabilities;
public record DiagnosticClientCapabilities;
public record ShowMessageRequestClientCapabilities;
public record ShowDocumentClientCapabilities;
public record StaleRequestSupportClientCapabilities;
public record RegularExpressionsClientCapabilities;
public record MarkdownClientCapabilities;

public record InitializeResult
{
    [JsonPropertyName("capabilities")]
    public required ServerCapabilities Capabilities { get; init; }

    [JsonPropertyName("serverInfo")]
    public ServerInfo? ServerInfo { get; init; }
}

public record ServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version);

public record ServerCapabilities
{
    [JsonPropertyName("positionEncoding")]
    public string? PositionEncoding { get; init; }

    [JsonPropertyName("textDocumentSync")]
    public TextDocumentSyncOptions? TextDocumentSync { get; init; }

    [JsonPropertyName("completionProvider")]
    public CompletionOptions? CompletionProvider { get; init; }

    [JsonPropertyName("hoverProvider")]
    public bool? HoverProvider { get; init; }

    [JsonPropertyName("signatureHelpProvider")]
    public SignatureHelpOptions? SignatureHelpProvider { get; init; }

    [JsonPropertyName("declarationProvider")]
    public bool? DeclarationProvider { get; init; }

    [JsonPropertyName("definitionProvider")]
    public bool? DefinitionProvider { get; init; }

    [JsonPropertyName("typeDefinitionProvider")]
    public bool? TypeDefinitionProvider { get; init; }

    [JsonPropertyName("implementationProvider")]
    public bool? ImplementationProvider { get; init; }

    [JsonPropertyName("referencesProvider")]
    public bool? ReferencesProvider { get; init; }

    [JsonPropertyName("documentHighlightProvider")]
    public bool? DocumentHighlightProvider { get; init; }

    [JsonPropertyName("documentSymbolProvider")]
    public bool? DocumentSymbolProvider { get; init; }

    [JsonPropertyName("codeActionProvider")]
    public object? CodeActionProvider { get; init; }

    [JsonPropertyName("codeLensProvider")]
    public CodeLensOptions? CodeLensProvider { get; init; }

    [JsonPropertyName("documentLinkProvider")]
    public DocumentLinkOptions? DocumentLinkProvider { get; init; }

    [JsonPropertyName("colorProvider")]
    public bool? ColorProvider { get; init; }

    [JsonPropertyName("documentFormattingProvider")]
    public bool? DocumentFormattingProvider { get; init; }

    [JsonPropertyName("documentRangeFormattingProvider")]
    public bool? DocumentRangeFormattingProvider { get; init; }

    [JsonPropertyName("documentOnTypeFormattingProvider")]
    public DocumentOnTypeFormattingOptions? DocumentOnTypeFormattingProvider { get; init; }

    [JsonPropertyName("renameProvider")]
    public object? RenameProvider { get; init; }

    [JsonPropertyName("foldingRangeProvider")]
    public bool? FoldingRangeProvider { get; init; }

    [JsonPropertyName("executeCommandProvider")]
    public ExecuteCommandOptions? ExecuteCommandProvider { get; init; }

    [JsonPropertyName("selectionRangeProvider")]
    public bool? SelectionRangeProvider { get; init; }

    [JsonPropertyName("linkedEditingRangeProvider")]
    public bool? LinkedEditingRangeProvider { get; init; }

    [JsonPropertyName("callHierarchyProvider")]
    public bool? CallHierarchyProvider { get; init; }

    [JsonPropertyName("semanticTokensProvider")]
    public SemanticTokensOptions? SemanticTokensProvider { get; init; }

    [JsonPropertyName("monikerProvider")]
    public bool? MonikerProvider { get; init; }

    [JsonPropertyName("typeHierarchyProvider")]
    public bool? TypeHierarchyProvider { get; init; }

    [JsonPropertyName("inlineValueProvider")]
    public bool? InlineValueProvider { get; init; }

    [JsonPropertyName("inlayHintProvider")]
    public object? InlayHintProvider { get; init; }

    [JsonPropertyName("diagnosticProvider")]
    public DiagnosticOptions? DiagnosticProvider { get; init; }

    [JsonPropertyName("workspaceSymbolProvider")]
    public bool? WorkspaceSymbolProvider { get; init; }

    [JsonPropertyName("workspace")]
    public ServerWorkspaceCapabilities? Workspace { get; init; }

    [JsonPropertyName("experimental")]
    public JsonElement? Experimental { get; init; }
}

public record TextDocumentSyncOptions
{
    [JsonPropertyName("openClose")]
    public bool? OpenClose { get; init; }

    [JsonPropertyName("change")]
    public int? Change { get; init; }

    [JsonPropertyName("willSave")]
    public bool? WillSave { get; init; }

    [JsonPropertyName("willSaveWaitUntil")]
    public bool? WillSaveWaitUntil { get; init; }

    [JsonPropertyName("save")]
    public SaveOptions? Save { get; init; }
}

public record SaveOptions
{
    [JsonPropertyName("includeText")]
    public bool? IncludeText { get; init; }
}

public record CompletionOptions
{
    [JsonPropertyName("triggerCharacters")]
    public string[]? TriggerCharacters { get; init; }

    [JsonPropertyName("allCommitCharacters")]
    public string[]? AllCommitCharacters { get; init; }

    [JsonPropertyName("resolveProvider")]
    public bool? ResolveProvider { get; init; }

    [JsonPropertyName("completionItem")]
    public CompletionItemOptions? CompletionItem { get; init; }
}

public record CompletionItemOptions
{
    [JsonPropertyName("labelDetailsSupport")]
    public bool? LabelDetailsSupport { get; init; }
}

public record SignatureHelpOptions
{
    [JsonPropertyName("triggerCharacters")]
    public string[]? TriggerCharacters { get; init; }

    [JsonPropertyName("retriggerCharacters")]
    public string[]? RetriggerCharacters { get; init; }
}

public record CodeLensOptions
{
    [JsonPropertyName("resolveProvider")]
    public bool? ResolveProvider { get; init; }
}

public record DocumentLinkOptions
{
    [JsonPropertyName("resolveProvider")]
    public bool? ResolveProvider { get; init; }
}

public record DocumentOnTypeFormattingOptions
{
    [JsonPropertyName("firstTriggerCharacter")]
    public required string FirstTriggerCharacter { get; init; }

    [JsonPropertyName("moreTriggerCharacter")]
    public string[]? MoreTriggerCharacter { get; init; }
}

public record ExecuteCommandOptions
{
    [JsonPropertyName("commands")]
    public required string[] Commands { get; init; }
}

public record SemanticTokensOptions
{
    [JsonPropertyName("legend")]
    public required SemanticTokensLegend Legend { get; init; }

    [JsonPropertyName("range")]
    public bool? Range { get; init; }

    [JsonPropertyName("full")]
    public object? Full { get; init; }
}

public record SemanticTokensLegend
{
    [JsonPropertyName("tokenTypes")]
    public required string[] TokenTypes { get; init; }

    [JsonPropertyName("tokenModifiers")]
    public required string[] TokenModifiers { get; init; }
}

public record DiagnosticOptions
{
    [JsonPropertyName("identifier")]
    public string? Identifier { get; init; }

    [JsonPropertyName("interFileDependencies")]
    public bool InterFileDependencies { get; init; }

    [JsonPropertyName("workspaceDiagnostics")]
    public bool WorkspaceDiagnostics { get; init; }
}

public record ServerWorkspaceCapabilities
{
    [JsonPropertyName("workspaceFolders")]
    public WorkspaceFoldersServerCapabilities? WorkspaceFolders { get; init; }

    [JsonPropertyName("fileOperations")]
    public FileOperationOptions? FileOperations { get; init; }
}

public record WorkspaceFoldersServerCapabilities
{
    [JsonPropertyName("supported")]
    public bool? Supported { get; init; }

    [JsonPropertyName("changeNotifications")]
    public object? ChangeNotifications { get; init; }
}

public record FileOperationOptions;
