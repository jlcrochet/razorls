using System.Text.Json;
using System.Text.Json.Serialization;

namespace RazorSharp.Server.Configuration;

/// <summary>
/// Configuration model for omnisharp.json settings.
/// This is a minimal subset of OmniSharp settings that are commonly used.
/// </summary>
public class OmniSharpConfiguration
{
    /// <summary>
    /// Formatting options for C# code.
    /// </summary>
    [JsonPropertyName("FormattingOptions")]
    public FormattingOptions? FormattingOptions { get; set; }

    /// <summary>
    /// Roslyn extensions options (analyzers, inlay hints, etc.).
    /// </summary>
    [JsonPropertyName("RoslynExtensionsOptions")]
    public RoslynExtensionsOptions? RoslynExtensionsOptions { get; set; }

    /// <summary>
    /// Rename refactoring options.
    /// </summary>
    [JsonPropertyName("RenameOptions")]
    public RenameOptions? RenameOptions { get; set; }

    /// <summary>
    /// C# specific options (mirrors "csharp" section in VS Code settings).
    /// </summary>
    [JsonPropertyName("csharp")]
    public CSharpOptions? CSharp { get; set; }
}

/// <summary>
/// Configuration passed via LSP initializationOptions from the editor.
/// In Helix, this is configured via the "config" key in languages.toml.
/// </summary>
public class InitializationOptions
{
    /// <summary>
    /// HTML language server options.
    /// </summary>
    [JsonPropertyName("html")]
    public HtmlOptions? Html { get; set; }

    /// <summary>
    /// Server capabilities configuration.
    /// Allows enabling/disabling specific LSP features.
    /// </summary>
    [JsonPropertyName("capabilities")]
    public CapabilitiesConfig? Capabilities { get; set; }
}

/// <summary>
/// Configuration for server capabilities.
/// Set properties to false to disable specific features.
/// </summary>
public class CapabilitiesConfig
{
    /// <summary>
    /// Completion provider configuration.
    /// </summary>
    [JsonPropertyName("completionProvider")]
    public CompletionProviderConfig? CompletionProvider { get; set; }

    /// <summary>
    /// Enable or disable hover. Default: true
    /// </summary>
    [JsonPropertyName("hoverProvider")]
    public bool? HoverProvider { get; set; }

    /// <summary>
    /// Signature help provider configuration.
    /// </summary>
    [JsonPropertyName("signatureHelpProvider")]
    public SignatureHelpProviderConfig? SignatureHelpProvider { get; set; }

    /// <summary>
    /// Enable or disable go to definition. Default: true
    /// </summary>
    [JsonPropertyName("definitionProvider")]
    public bool? DefinitionProvider { get; set; }

    /// <summary>
    /// Enable or disable go to type definition. Default: true
    /// </summary>
    [JsonPropertyName("typeDefinitionProvider")]
    public bool? TypeDefinitionProvider { get; set; }

    /// <summary>
    /// Enable or disable go to implementation. Default: true
    /// </summary>
    [JsonPropertyName("implementationProvider")]
    public bool? ImplementationProvider { get; set; }

    /// <summary>
    /// Enable or disable find references. Default: true
    /// </summary>
    [JsonPropertyName("referencesProvider")]
    public bool? ReferencesProvider { get; set; }

    /// <summary>
    /// Enable or disable document highlight. Default: true
    /// </summary>
    [JsonPropertyName("documentHighlightProvider")]
    public bool? DocumentHighlightProvider { get; set; }

    /// <summary>
    /// Enable or disable document symbols. Default: true
    /// </summary>
    [JsonPropertyName("documentSymbolProvider")]
    public bool? DocumentSymbolProvider { get; set; }

    /// <summary>
    /// Enable or disable code actions. Default: true
    /// </summary>
    [JsonPropertyName("codeActionProvider")]
    public bool? CodeActionProvider { get; set; }

    /// <summary>
    /// Enable or disable document formatting. Default: true
    /// </summary>
    [JsonPropertyName("documentFormattingProvider")]
    public bool? DocumentFormattingProvider { get; set; }

    /// <summary>
    /// Enable or disable document range formatting. Default: true
    /// </summary>
    [JsonPropertyName("documentRangeFormattingProvider")]
    public bool? DocumentRangeFormattingProvider { get; set; }

    /// <summary>
    /// Document on-type formatting provider configuration.
    /// </summary>
    [JsonPropertyName("documentOnTypeFormattingProvider")]
    public DocumentOnTypeFormattingProviderConfig? DocumentOnTypeFormattingProvider { get; set; }

    /// <summary>
    /// Enable or disable rename. Default: true
    /// </summary>
    [JsonPropertyName("renameProvider")]
    public bool? RenameProvider { get; set; }

    /// <summary>
    /// Enable or disable folding range. Default: true
    /// </summary>
    [JsonPropertyName("foldingRangeProvider")]
    public bool? FoldingRangeProvider { get; set; }

    /// <summary>
    /// Enable or disable workspace symbol search. Default: true
    /// </summary>
    [JsonPropertyName("workspaceSymbolProvider")]
    public bool? WorkspaceSymbolProvider { get; set; }

    /// <summary>
    /// Semantic tokens provider configuration.
    /// </summary>
    [JsonPropertyName("semanticTokensProvider")]
    public SemanticTokensProviderConfig? SemanticTokensProvider { get; set; }

    /// <summary>
    /// Enable or disable inlay hints. Default: true
    /// </summary>
    [JsonPropertyName("inlayHintProvider")]
    public bool? InlayHintProvider { get; set; }
}

/// <summary>
/// Completion provider configuration.
/// </summary>
public class CompletionProviderConfig
{
    /// <summary>
    /// Enable or disable completion. Default: true
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// Characters that trigger completion.
    /// Must be a subset of the valid trigger characters: [".", "<", "@", " ", "(", "\"", "'", "=", "/"]
    /// Default: all valid trigger characters
    /// </summary>
    [JsonPropertyName("triggerCharacters")]
    public string[]? TriggerCharacters { get; set; }
}

/// <summary>
/// Signature help provider configuration.
/// </summary>
public class SignatureHelpProviderConfig
{
    /// <summary>
    /// Enable or disable signature help. Default: true
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// Characters that trigger signature help.
    /// Default: ["(", ","]
    /// </summary>
    [JsonPropertyName("triggerCharacters")]
    public string[]? TriggerCharacters { get; set; }

    /// <summary>
    /// Characters that re-trigger signature help.
    /// Default: [")"]
    /// </summary>
    [JsonPropertyName("retriggerCharacters")]
    public string[]? RetriggerCharacters { get; set; }
}

/// <summary>
/// Document on-type formatting provider configuration.
/// </summary>
public class DocumentOnTypeFormattingProviderConfig
{
    /// <summary>
    /// Enable or disable on-type formatting. Default: true
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// The first trigger character for on-type formatting.
    /// Default: ";"
    /// </summary>
    [JsonPropertyName("firstTriggerCharacter")]
    public string? FirstTriggerCharacter { get; set; }

    /// <summary>
    /// Additional trigger characters for on-type formatting.
    /// Default: ["}", "\n"]
    /// </summary>
    [JsonPropertyName("moreTriggerCharacter")]
    public string[]? MoreTriggerCharacter { get; set; }
}

/// <summary>
/// Semantic tokens provider configuration.
/// </summary>
public class SemanticTokensProviderConfig
{
    /// <summary>
    /// Enable or disable semantic tokens. Default: true
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// Enable or disable range requests. Default: true
    /// </summary>
    [JsonPropertyName("range")]
    public bool? Range { get; set; }

    /// <summary>
    /// Enable or disable full document requests. Default: true (with delta support)
    /// </summary>
    [JsonPropertyName("full")]
    public bool? Full { get; set; }
}

/// <summary>
/// C# formatting options.
/// </summary>
public class FormattingOptions
{
    [JsonPropertyName("enableEditorConfigSupport")]
    public bool? EnableEditorConfigSupport { get; set; }

    [JsonPropertyName("organizeImports")]
    public bool? OrganizeImports { get; set; }

    [JsonPropertyName("newLine")]
    public string? NewLine { get; set; }

    [JsonPropertyName("useTabs")]
    public bool? UseTabs { get; set; }

    [JsonPropertyName("tabSize")]
    public int? TabSize { get; set; }

    [JsonPropertyName("indentationSize")]
    public int? IndentationSize { get; set; }

    // Spacing options
    [JsonPropertyName("spacingAfterMethodDeclarationName")]
    public bool? SpacingAfterMethodDeclarationName { get; set; }

    [JsonPropertyName("spaceWithinMethodDeclarationParenthesis")]
    public bool? SpaceWithinMethodDeclarationParenthesis { get; set; }

    [JsonPropertyName("spaceBetweenEmptyMethodDeclarationParentheses")]
    public bool? SpaceBetweenEmptyMethodDeclarationParentheses { get; set; }

    [JsonPropertyName("spaceAfterMethodCallName")]
    public bool? SpaceAfterMethodCallName { get; set; }

    [JsonPropertyName("spaceWithinMethodCallParentheses")]
    public bool? SpaceWithinMethodCallParentheses { get; set; }

    [JsonPropertyName("spaceBetweenEmptyMethodCallParentheses")]
    public bool? SpaceBetweenEmptyMethodCallParentheses { get; set; }

    [JsonPropertyName("spaceAfterControlFlowStatementKeyword")]
    public bool? SpaceAfterControlFlowStatementKeyword { get; set; }

    [JsonPropertyName("spaceAfterSemicolonsInForStatement")]
    public bool? SpaceAfterSemicolonsInForStatement { get; set; }

    [JsonPropertyName("spaceBeforeSemicolonsInForStatement")]
    public bool? SpaceBeforeSemicolonsInForStatement { get; set; }

    [JsonPropertyName("spaceAfterCast")]
    public bool? SpaceAfterCast { get; set; }

    [JsonPropertyName("spaceAfterComma")]
    public bool? SpaceAfterComma { get; set; }

    [JsonPropertyName("spaceBeforeComma")]
    public bool? SpaceBeforeComma { get; set; }

    [JsonPropertyName("spaceAfterDot")]
    public bool? SpaceAfterDot { get; set; }

    [JsonPropertyName("spaceBeforeDot")]
    public bool? SpaceBeforeDot { get; set; }

    // Brace placement
    [JsonPropertyName("newLinesForBracesInTypes")]
    public bool? NewLinesForBracesInTypes { get; set; }

    [JsonPropertyName("newLinesForBracesInMethods")]
    public bool? NewLinesForBracesInMethods { get; set; }

    [JsonPropertyName("newLinesForBracesInProperties")]
    public bool? NewLinesForBracesInProperties { get; set; }

    [JsonPropertyName("newLinesForBracesInAccessors")]
    public bool? NewLinesForBracesInAccessors { get; set; }

    [JsonPropertyName("newLinesForBracesInAnonymousMethods")]
    public bool? NewLinesForBracesInAnonymousMethods { get; set; }

    [JsonPropertyName("newLinesForBracesInControlBlocks")]
    public bool? NewLinesForBracesInControlBlocks { get; set; }

    [JsonPropertyName("newLinesForBracesInAnonymousTypes")]
    public bool? NewLinesForBracesInAnonymousTypes { get; set; }

    [JsonPropertyName("newLinesForBracesInObjectCollectionArrayInitializers")]
    public bool? NewLinesForBracesInObjectCollectionArrayInitializers { get; set; }

    [JsonPropertyName("newLinesForBracesInLambdaExpressionBody")]
    public bool? NewLinesForBracesInLambdaExpressionBody { get; set; }

    [JsonPropertyName("newLineForElse")]
    public bool? NewLineForElse { get; set; }

    [JsonPropertyName("newLineForCatch")]
    public bool? NewLineForCatch { get; set; }

    [JsonPropertyName("newLineForFinally")]
    public bool? NewLineForFinally { get; set; }
}

/// <summary>
/// Roslyn extensions options.
/// </summary>
public class RoslynExtensionsOptions
{
    [JsonPropertyName("enableAnalyzersSupport")]
    public bool? EnableAnalyzersSupport { get; set; }

    [JsonPropertyName("enableDecompilationSupport")]
    public bool? EnableDecompilationSupport { get; set; }

    [JsonPropertyName("enableImportCompletion")]
    public bool? EnableImportCompletion { get; set; }

    [JsonPropertyName("enableAsyncCompletion")]
    public bool? EnableAsyncCompletion { get; set; }

    [JsonPropertyName("documentAnalysisTimeoutMs")]
    public int? DocumentAnalysisTimeoutMs { get; set; }

    [JsonPropertyName("diagnosticWorkersThreadCount")]
    public int? DiagnosticWorkersThreadCount { get; set; }

    [JsonPropertyName("analyzeOpenDocumentsOnly")]
    public bool? AnalyzeOpenDocumentsOnly { get; set; }

    [JsonPropertyName("inlayHintsOptions")]
    public InlayHintsOptions? InlayHintsOptions { get; set; }
}

/// <summary>
/// Inlay hints configuration options.
/// </summary>
public class InlayHintsOptions
{
    [JsonPropertyName("enableForParameters")]
    public bool? EnableForParameters { get; set; }

    [JsonPropertyName("forLiteralParameters")]
    public bool? ForLiteralParameters { get; set; }

    [JsonPropertyName("forIndexerParameters")]
    public bool? ForIndexerParameters { get; set; }

    [JsonPropertyName("forObjectCreationParameters")]
    public bool? ForObjectCreationParameters { get; set; }

    [JsonPropertyName("forOtherParameters")]
    public bool? ForOtherParameters { get; set; }

    [JsonPropertyName("suppressForParametersThatDifferOnlyBySuffix")]
    public bool? SuppressForParametersThatDifferOnlyBySuffix { get; set; }

    [JsonPropertyName("suppressForParametersThatMatchMethodIntent")]
    public bool? SuppressForParametersThatMatchMethodIntent { get; set; }

    [JsonPropertyName("suppressForParametersThatMatchArgumentName")]
    public bool? SuppressForParametersThatMatchArgumentName { get; set; }

    [JsonPropertyName("enableForTypes")]
    public bool? EnableForTypes { get; set; }

    [JsonPropertyName("forImplicitVariableTypes")]
    public bool? ForImplicitVariableTypes { get; set; }

    [JsonPropertyName("forLambdaParameterTypes")]
    public bool? ForLambdaParameterTypes { get; set; }

    [JsonPropertyName("forImplicitObjectCreation")]
    public bool? ForImplicitObjectCreation { get; set; }
}

/// <summary>
/// Rename refactoring options.
/// </summary>
public class RenameOptions
{
    [JsonPropertyName("renameOverloads")]
    public bool? RenameOverloads { get; set; }

    [JsonPropertyName("renameInStrings")]
    public bool? RenameInStrings { get; set; }

    [JsonPropertyName("renameInComments")]
    public bool? RenameInComments { get; set; }
}

/// <summary>
/// C# language-specific options (mirrors VS Code csharp.* settings).
/// </summary>
public class CSharpOptions
{
    [JsonPropertyName("maxProjectFileCountForDiagnosticAnalysis")]
    public int? MaxProjectFileCountForDiagnosticAnalysis { get; set; }

    [JsonPropertyName("suppressDotnetRestoreNotification")]
    public bool? SuppressDotnetRestoreNotification { get; set; }
}

/// <summary>
/// HTML language server configuration options.
/// </summary>
public class HtmlOptions
{
    /// <summary>
    /// Enable or disable the HTML language server (vscode-html-language-server).
    /// Default: true
    /// </summary>
    [JsonPropertyName("enable")]
    public bool? Enable { get; set; }
}
