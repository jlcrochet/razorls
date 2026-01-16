namespace RazorSharp.Protocol;

/// <summary>
/// LSP method names as constants.
/// </summary>
public static class LspMethods
{
    // Lifecycle
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";

    // Text Document Synchronization
    public const string TextDocumentDidOpen = "textDocument/didOpen";
    public const string TextDocumentDidChange = "textDocument/didChange";
    public const string TextDocumentDidClose = "textDocument/didClose";
    public const string TextDocumentDidSave = "textDocument/didSave";
    public const string TextDocumentWillSave = "textDocument/willSave";
    public const string TextDocumentWillSaveWaitUntil = "textDocument/willSaveWaitUntil";

    // Language Features
    public const string TextDocumentCompletion = "textDocument/completion";
    public const string CompletionItemResolve = "completionItem/resolve";
    public const string TextDocumentHover = "textDocument/hover";
    public const string TextDocumentSignatureHelp = "textDocument/signatureHelp";
    public const string TextDocumentDeclaration = "textDocument/declaration";
    public const string TextDocumentDefinition = "textDocument/definition";
    public const string TextDocumentTypeDefinition = "textDocument/typeDefinition";
    public const string TextDocumentImplementation = "textDocument/implementation";
    public const string TextDocumentReferences = "textDocument/references";
    public const string TextDocumentDocumentHighlight = "textDocument/documentHighlight";
    public const string TextDocumentDocumentSymbol = "textDocument/documentSymbol";
    public const string TextDocumentCodeAction = "textDocument/codeAction";
    public const string CodeActionResolve = "codeAction/resolve";
    public const string TextDocumentCodeLens = "textDocument/codeLens";
    public const string CodeLensResolve = "codeLens/resolve";
    public const string TextDocumentDocumentLink = "textDocument/documentLink";
    public const string DocumentLinkResolve = "documentLink/resolve";
    public const string TextDocumentDocumentColor = "textDocument/documentColor";
    public const string TextDocumentColorPresentation = "textDocument/colorPresentation";
    public const string TextDocumentFormatting = "textDocument/formatting";
    public const string TextDocumentRangeFormatting = "textDocument/rangeFormatting";
    public const string TextDocumentOnTypeFormatting = "textDocument/onTypeFormatting";
    public const string TextDocumentRename = "textDocument/rename";
    public const string TextDocumentPrepareRename = "textDocument/prepareRename";
    public const string TextDocumentFoldingRange = "textDocument/foldingRange";
    public const string TextDocumentSelectionRange = "textDocument/selectionRange";
    public const string TextDocumentLinkedEditingRange = "textDocument/linkedEditingRange";
    public const string TextDocumentSemanticTokensFull = "textDocument/semanticTokens/full";
    public const string TextDocumentSemanticTokensDelta = "textDocument/semanticTokens/full/delta";
    public const string TextDocumentSemanticTokensRange = "textDocument/semanticTokens/range";
    public const string TextDocumentInlayHint = "textDocument/inlayHint";
    public const string InlayHintResolve = "inlayHint/resolve";
    public const string TextDocumentDiagnostic = "textDocument/diagnostic";

    // Workspace
    public const string WorkspaceSymbol = "workspace/symbol";
    public const string WorkspaceSymbolResolve = "workspaceSymbol/resolve";
    public const string WorkspaceDidChangeConfiguration = "workspace/didChangeConfiguration";
    public const string WorkspaceDidChangeWatchedFiles = "workspace/didChangeWatchedFiles";
    public const string WorkspaceExecuteCommand = "workspace/executeCommand";
    public const string WorkspaceApplyEdit = "workspace/applyEdit";
    public const string WorkspaceDiagnostic = "workspace/diagnostic";

    // Window
    public const string WindowShowMessage = "window/showMessage";
    public const string WindowShowMessageRequest = "window/showMessageRequest";
    public const string WindowLogMessage = "window/logMessage";
    public const string WindowWorkDoneProgressCreate = "window/workDoneProgress/create";
    public const string WindowWorkDoneProgressCancel = "window/workDoneProgress/cancel";
    public const string Progress = "$/progress";

    // Diagnostics
    public const string TextDocumentPublishDiagnostics = "textDocument/publishDiagnostics";

    // Razor-specific
    public const string RazorUpdateHtml = "razor/updateHtml";
    public const string RazorLog = "razor/log";

    // Roslyn-specific
    public const string SolutionOpen = "solution/open";
    public const string ProjectOpen = "project/open";
    public const string ProjectInitializationComplete = "workspace/projectInitializationComplete";
    public const string ProjectNeedsRestore = "workspace/_roslyn_projectNeedsRestore";
    public const string RoslynRestore = "workspace/_roslyn_restore";
    public const string SourceGeneratedDocumentGetText = "sourceGeneratedDocument/_roslyn_getText";
    public const string RefreshSourceGeneratedDocument = "workspace/refreshSourceGeneratedDocument";
}
