using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace RazorSharp.Server.Configuration;

/// <summary>
/// Loads omnisharp.json configuration files from standard locations.
/// Follows OmniSharp's discovery pattern:
/// 1. Global: ~/.omnisharp/omnisharp.json (or $OMNISHARPHOME/omnisharp.json)
/// 2. Local: {workspaceRoot}/omnisharp.json
/// Local settings override global settings.
/// </summary>
public class ConfigurationLoader
{
    const string ConfigFileName = "omnisharp.json";
    const string GlobalConfigDirName = ".omnisharp";

    readonly ILogger<ConfigurationLoader> _logger;
    OmniSharpConfiguration _configuration = new();
    string? _workspaceRoot;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    static readonly JsonDocumentOptions JsonDocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
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
        JsonNode? merged = null;

        // Load global config first
        var globalPath = GetGlobalConfigPath();
        if (globalPath != null && File.Exists(globalPath))
        {
            _logger.LogInformation("Loading global omnisharp.json from {Path}", globalPath);
            merged = LoadJsonFromFile(globalPath);
        }

        // Load local config (overrides global)
        if (_workspaceRoot != null)
        {
            var localPath = Path.Combine(_workspaceRoot, ConfigFileName);
            if (File.Exists(localPath))
            {
                _logger.LogInformation("Loading local omnisharp.json from {Path}", localPath);
                var localJson = LoadJsonFromFile(localPath);
                if (localJson != null)
                {
                    merged = MergeJsonNodes(merged, localJson);
                }
            }
        }

        // Deserialize merged JSON to strongly-typed configuration
        if (merged != null)
        {
            try
            {
                _configuration = merged.Deserialize<OmniSharpConfiguration>(JsonOptions) ?? new();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize merged configuration");
                _configuration = new();
            }
        }
        else
        {
            _configuration = new();
        }

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

    private JsonNode? LoadJsonFromFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonNode.Parse(json, documentOptions: JsonDocOptions);
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
    /// Recursively merges two JSON nodes, with values from 'overlay' taking precedence.
    /// For objects, properties are merged recursively. For other types, overlay replaces base.
    /// </summary>
    private static JsonNode? MergeJsonNodes(JsonNode? baseNode, JsonNode? overlay)
    {
        if (overlay == null) return baseNode?.DeepClone();
        if (baseNode == null) return overlay.DeepClone();

        // Only merge objects; for arrays and primitives, overlay wins
        if (baseNode is JsonObject baseObj && overlay is JsonObject overlayObj)
        {
            var result = new JsonObject();

            // Add all properties from base
            foreach (var prop in baseObj)
            {
                result[prop.Key] = prop.Value?.DeepClone();
            }

            // Merge/override with overlay properties
            foreach (var prop in overlayObj)
            {
                if (result.ContainsKey(prop.Key) && result[prop.Key] is JsonObject && prop.Value is JsonObject)
                {
                    // Recursively merge nested objects
                    result[prop.Key] = MergeJsonNodes(result[prop.Key], prop.Value);
                }
                else
                {
                    // Override with overlay value
                    result[prop.Key] = prop.Value?.DeepClone();
                }
            }

            return result;
        }

        // For non-objects, overlay takes precedence
        return overlay.DeepClone();
    }
}
