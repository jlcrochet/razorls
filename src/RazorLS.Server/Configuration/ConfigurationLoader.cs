using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RazorLS.Server.Configuration;

/// <summary>
/// Loads omnisharp.json configuration files from standard locations.
/// Follows OmniSharp's discovery pattern:
/// 1. Global: ~/.omnisharp/omnisharp.json (or $OMNISHARPHOME/omnisharp.json)
/// 2. Local: {workspaceRoot}/omnisharp.json
/// Local settings override global settings.
/// </summary>
public class ConfigurationLoader
{
    private const string ConfigFileName = "omnisharp.json";
    private const string GlobalConfigDirName = ".omnisharp";

    private readonly ILogger<ConfigurationLoader> _logger;
    private OmniSharpConfiguration _configuration = new();
    private string? _workspaceRoot;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ConfigurationLoader(ILogger<ConfigurationLoader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the currently loaded configuration.
    /// </summary>
    public OmniSharpConfiguration Configuration => _configuration;

    /// <summary>
    /// Sets the workspace root and reloads configuration.
    /// </summary>
    public void SetWorkspaceRoot(string? workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
        Reload();
    }

    /// <summary>
    /// Reloads configuration from all sources.
    /// </summary>
    public void Reload()
    {
        var config = new OmniSharpConfiguration();

        // Load global config first
        var globalPath = GetGlobalConfigPath();
        if (globalPath != null && File.Exists(globalPath))
        {
            _logger.LogInformation("Loading global omnisharp.json from {Path}", globalPath);
            var globalConfig = LoadFromFile(globalPath);
            if (globalConfig != null)
            {
                config = MergeConfigurations(config, globalConfig);
            }
        }

        // Load local config (overrides global)
        if (_workspaceRoot != null)
        {
            var localPath = Path.Combine(_workspaceRoot, ConfigFileName);
            if (File.Exists(localPath))
            {
                _logger.LogInformation("Loading local omnisharp.json from {Path}", localPath);
                var localConfig = LoadFromFile(localPath);
                if (localConfig != null)
                {
                    config = MergeConfigurations(config, localConfig);
                }
            }
        }

        _configuration = config;
        _logger.LogDebug("Configuration loaded");
    }

    /// <summary>
    /// Gets the value for a specific configuration section.
    /// Used to respond to workspace/configuration requests.
    /// </summary>
    public object? GetConfigurationValue(string section)
    {
        return section switch
        {
            "omnisharp" => _configuration,
            "omnisharp.formattingOptions" or "FormattingOptions" => _configuration.FormattingOptions,
            "omnisharp.roslynExtensionsOptions" or "RoslynExtensionsOptions" => _configuration.RoslynExtensionsOptions,
            "omnisharp.renameOptions" or "RenameOptions" => _configuration.RenameOptions,
            "csharp" => _configuration.CSharp,

            // Inlay hints - commonly requested
            "csharp.inlayHints.parameters.enabled" => _configuration.RoslynExtensionsOptions?.InlayHintsOptions?.EnableForParameters,
            "csharp.inlayHints.parameters.forLiteralParameters" => _configuration.RoslynExtensionsOptions?.InlayHintsOptions?.ForLiteralParameters,
            "csharp.inlayHints.parameters.forIndexerParameters" => _configuration.RoslynExtensionsOptions?.InlayHintsOptions?.ForIndexerParameters,
            "csharp.inlayHints.parameters.forObjectCreationParameters" => _configuration.RoslynExtensionsOptions?.InlayHintsOptions?.ForObjectCreationParameters,
            "csharp.inlayHints.parameters.forOtherParameters" => _configuration.RoslynExtensionsOptions?.InlayHintsOptions?.ForOtherParameters,
            "csharp.inlayHints.parameters.suppressForParametersThatDifferOnlyBySuffix" => _configuration.RoslynExtensionsOptions?.InlayHintsOptions?.SuppressForParametersThatDifferOnlyBySuffix,
            "csharp.inlayHints.parameters.suppressForParametersThatMatchMethodIntent" => _configuration.RoslynExtensionsOptions?.InlayHintsOptions?.SuppressForParametersThatMatchMethodIntent,
            "csharp.inlayHints.parameters.suppressForParametersThatMatchArgumentName" => _configuration.RoslynExtensionsOptions?.InlayHintsOptions?.SuppressForParametersThatMatchArgumentName,
            "csharp.inlayHints.types.enabled" => _configuration.RoslynExtensionsOptions?.InlayHintsOptions?.EnableForTypes,
            "csharp.inlayHints.types.forImplicitVariableTypes" => _configuration.RoslynExtensionsOptions?.InlayHintsOptions?.ForImplicitVariableTypes,
            "csharp.inlayHints.types.forLambdaParameterTypes" => _configuration.RoslynExtensionsOptions?.InlayHintsOptions?.ForLambdaParameterTypes,
            "csharp.inlayHints.types.forImplicitObjectCreation" => _configuration.RoslynExtensionsOptions?.InlayHintsOptions?.ForImplicitObjectCreation,

            // Formatting
            "omnisharp.enableEditorConfigSupport" => _configuration.FormattingOptions?.EnableEditorConfigSupport,
            "omnisharp.organizeImportsOnFormat" => _configuration.FormattingOptions?.OrganizeImports,

            // Analyzers
            "omnisharp.enableRoslynAnalyzers" => _configuration.RoslynExtensionsOptions?.EnableAnalyzersSupport,
            "omnisharp.enableDecompilationSupport" => _configuration.RoslynExtensionsOptions?.EnableDecompilationSupport,
            "omnisharp.enableImportCompletion" => _configuration.RoslynExtensionsOptions?.EnableImportCompletion,
            "omnisharp.enableAsyncCompletion" => _configuration.RoslynExtensionsOptions?.EnableAsyncCompletion,
            "omnisharp.analyzeOpenDocumentsOnly" => _configuration.RoslynExtensionsOptions?.AnalyzeOpenDocumentsOnly,

            _ => null
        };
    }

    private static string? GetGlobalConfigPath()
    {
        // Check OMNISHARPHOME environment variable first
        var omniSharpHome = Environment.GetEnvironmentVariable("OMNISHARPHOME");
        if (!string.IsNullOrEmpty(omniSharpHome))
        {
            return Path.Combine(omniSharpHome, ConfigFileName);
        }

        // Fall back to ~/.omnisharp/omnisharp.json
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(homeDir))
        {
            return Path.Combine(homeDir, GlobalConfigDirName, ConfigFileName);
        }

        return null;
    }

    private OmniSharpConfiguration? LoadFromFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<OmniSharpConfiguration>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse omnisharp.json at {Path}", path);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read omnisharp.json at {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Merges two configurations, with values from 'overlay' taking precedence.
    /// </summary>
    private static OmniSharpConfiguration MergeConfigurations(OmniSharpConfiguration baseConfig, OmniSharpConfiguration overlay)
    {
        return new OmniSharpConfiguration
        {
            FormattingOptions = MergeFormattingOptions(baseConfig.FormattingOptions, overlay.FormattingOptions),
            RoslynExtensionsOptions = MergeRoslynExtensionsOptions(baseConfig.RoslynExtensionsOptions, overlay.RoslynExtensionsOptions),
            RenameOptions = MergeRenameOptions(baseConfig.RenameOptions, overlay.RenameOptions),
            CSharp = MergeCSharpOptions(baseConfig.CSharp, overlay.CSharp)
        };
    }

    private static FormattingOptions? MergeFormattingOptions(FormattingOptions? baseOpts, FormattingOptions? overlay)
    {
        if (overlay == null) return baseOpts;
        if (baseOpts == null) return overlay;

        return new FormattingOptions
        {
            EnableEditorConfigSupport = overlay.EnableEditorConfigSupport ?? baseOpts.EnableEditorConfigSupport,
            OrganizeImports = overlay.OrganizeImports ?? baseOpts.OrganizeImports,
            NewLine = overlay.NewLine ?? baseOpts.NewLine,
            UseTabs = overlay.UseTabs ?? baseOpts.UseTabs,
            TabSize = overlay.TabSize ?? baseOpts.TabSize,
            IndentationSize = overlay.IndentationSize ?? baseOpts.IndentationSize,
            SpacingAfterMethodDeclarationName = overlay.SpacingAfterMethodDeclarationName ?? baseOpts.SpacingAfterMethodDeclarationName,
            SpaceWithinMethodDeclarationParenthesis = overlay.SpaceWithinMethodDeclarationParenthesis ?? baseOpts.SpaceWithinMethodDeclarationParenthesis,
            SpaceBetweenEmptyMethodDeclarationParentheses = overlay.SpaceBetweenEmptyMethodDeclarationParentheses ?? baseOpts.SpaceBetweenEmptyMethodDeclarationParentheses,
            SpaceAfterMethodCallName = overlay.SpaceAfterMethodCallName ?? baseOpts.SpaceAfterMethodCallName,
            SpaceWithinMethodCallParentheses = overlay.SpaceWithinMethodCallParentheses ?? baseOpts.SpaceWithinMethodCallParentheses,
            SpaceBetweenEmptyMethodCallParentheses = overlay.SpaceBetweenEmptyMethodCallParentheses ?? baseOpts.SpaceBetweenEmptyMethodCallParentheses,
            SpaceAfterControlFlowStatementKeyword = overlay.SpaceAfterControlFlowStatementKeyword ?? baseOpts.SpaceAfterControlFlowStatementKeyword,
            SpaceAfterSemicolonsInForStatement = overlay.SpaceAfterSemicolonsInForStatement ?? baseOpts.SpaceAfterSemicolonsInForStatement,
            SpaceBeforeSemicolonsInForStatement = overlay.SpaceBeforeSemicolonsInForStatement ?? baseOpts.SpaceBeforeSemicolonsInForStatement,
            SpaceAfterCast = overlay.SpaceAfterCast ?? baseOpts.SpaceAfterCast,
            SpaceAfterComma = overlay.SpaceAfterComma ?? baseOpts.SpaceAfterComma,
            SpaceBeforeComma = overlay.SpaceBeforeComma ?? baseOpts.SpaceBeforeComma,
            SpaceAfterDot = overlay.SpaceAfterDot ?? baseOpts.SpaceAfterDot,
            SpaceBeforeDot = overlay.SpaceBeforeDot ?? baseOpts.SpaceBeforeDot,
            NewLinesForBracesInTypes = overlay.NewLinesForBracesInTypes ?? baseOpts.NewLinesForBracesInTypes,
            NewLinesForBracesInMethods = overlay.NewLinesForBracesInMethods ?? baseOpts.NewLinesForBracesInMethods,
            NewLinesForBracesInProperties = overlay.NewLinesForBracesInProperties ?? baseOpts.NewLinesForBracesInProperties,
            NewLinesForBracesInAccessors = overlay.NewLinesForBracesInAccessors ?? baseOpts.NewLinesForBracesInAccessors,
            NewLinesForBracesInAnonymousMethods = overlay.NewLinesForBracesInAnonymousMethods ?? baseOpts.NewLinesForBracesInAnonymousMethods,
            NewLinesForBracesInControlBlocks = overlay.NewLinesForBracesInControlBlocks ?? baseOpts.NewLinesForBracesInControlBlocks,
            NewLinesForBracesInAnonymousTypes = overlay.NewLinesForBracesInAnonymousTypes ?? baseOpts.NewLinesForBracesInAnonymousTypes,
            NewLinesForBracesInObjectCollectionArrayInitializers = overlay.NewLinesForBracesInObjectCollectionArrayInitializers ?? baseOpts.NewLinesForBracesInObjectCollectionArrayInitializers,
            NewLinesForBracesInLambdaExpressionBody = overlay.NewLinesForBracesInLambdaExpressionBody ?? baseOpts.NewLinesForBracesInLambdaExpressionBody,
            NewLineForElse = overlay.NewLineForElse ?? baseOpts.NewLineForElse,
            NewLineForCatch = overlay.NewLineForCatch ?? baseOpts.NewLineForCatch,
            NewLineForFinally = overlay.NewLineForFinally ?? baseOpts.NewLineForFinally
        };
    }

    private static RoslynExtensionsOptions? MergeRoslynExtensionsOptions(RoslynExtensionsOptions? baseOpts, RoslynExtensionsOptions? overlay)
    {
        if (overlay == null) return baseOpts;
        if (baseOpts == null) return overlay;

        return new RoslynExtensionsOptions
        {
            EnableAnalyzersSupport = overlay.EnableAnalyzersSupport ?? baseOpts.EnableAnalyzersSupport,
            EnableDecompilationSupport = overlay.EnableDecompilationSupport ?? baseOpts.EnableDecompilationSupport,
            EnableImportCompletion = overlay.EnableImportCompletion ?? baseOpts.EnableImportCompletion,
            EnableAsyncCompletion = overlay.EnableAsyncCompletion ?? baseOpts.EnableAsyncCompletion,
            DocumentAnalysisTimeoutMs = overlay.DocumentAnalysisTimeoutMs ?? baseOpts.DocumentAnalysisTimeoutMs,
            DiagnosticWorkersThreadCount = overlay.DiagnosticWorkersThreadCount ?? baseOpts.DiagnosticWorkersThreadCount,
            AnalyzeOpenDocumentsOnly = overlay.AnalyzeOpenDocumentsOnly ?? baseOpts.AnalyzeOpenDocumentsOnly,
            InlayHintsOptions = MergeInlayHintsOptions(baseOpts.InlayHintsOptions, overlay.InlayHintsOptions)
        };
    }

    private static InlayHintsOptions? MergeInlayHintsOptions(InlayHintsOptions? baseOpts, InlayHintsOptions? overlay)
    {
        if (overlay == null) return baseOpts;
        if (baseOpts == null) return overlay;

        return new InlayHintsOptions
        {
            EnableForParameters = overlay.EnableForParameters ?? baseOpts.EnableForParameters,
            ForLiteralParameters = overlay.ForLiteralParameters ?? baseOpts.ForLiteralParameters,
            ForIndexerParameters = overlay.ForIndexerParameters ?? baseOpts.ForIndexerParameters,
            ForObjectCreationParameters = overlay.ForObjectCreationParameters ?? baseOpts.ForObjectCreationParameters,
            ForOtherParameters = overlay.ForOtherParameters ?? baseOpts.ForOtherParameters,
            SuppressForParametersThatDifferOnlyBySuffix = overlay.SuppressForParametersThatDifferOnlyBySuffix ?? baseOpts.SuppressForParametersThatDifferOnlyBySuffix,
            SuppressForParametersThatMatchMethodIntent = overlay.SuppressForParametersThatMatchMethodIntent ?? baseOpts.SuppressForParametersThatMatchMethodIntent,
            SuppressForParametersThatMatchArgumentName = overlay.SuppressForParametersThatMatchArgumentName ?? baseOpts.SuppressForParametersThatMatchArgumentName,
            EnableForTypes = overlay.EnableForTypes ?? baseOpts.EnableForTypes,
            ForImplicitVariableTypes = overlay.ForImplicitVariableTypes ?? baseOpts.ForImplicitVariableTypes,
            ForLambdaParameterTypes = overlay.ForLambdaParameterTypes ?? baseOpts.ForLambdaParameterTypes,
            ForImplicitObjectCreation = overlay.ForImplicitObjectCreation ?? baseOpts.ForImplicitObjectCreation
        };
    }

    private static RenameOptions? MergeRenameOptions(RenameOptions? baseOpts, RenameOptions? overlay)
    {
        if (overlay == null) return baseOpts;
        if (baseOpts == null) return overlay;

        return new RenameOptions
        {
            RenameOverloads = overlay.RenameOverloads ?? baseOpts.RenameOverloads,
            RenameInStrings = overlay.RenameInStrings ?? baseOpts.RenameInStrings,
            RenameInComments = overlay.RenameInComments ?? baseOpts.RenameInComments
        };
    }

    private static CSharpOptions? MergeCSharpOptions(CSharpOptions? baseOpts, CSharpOptions? overlay)
    {
        if (overlay == null) return baseOpts;
        if (baseOpts == null) return overlay;

        return new CSharpOptions
        {
            MaxProjectFileCountForDiagnosticAnalysis = overlay.MaxProjectFileCountForDiagnosticAnalysis ?? baseOpts.MaxProjectFileCountForDiagnosticAnalysis,
            SuppressDotnetRestoreNotification = overlay.SuppressDotnetRestoreNotification ?? baseOpts.SuppressDotnetRestoreNotification
        };
    }

}
