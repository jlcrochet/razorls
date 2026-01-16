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
