using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Protocol;
using RazorSharp.Protocol.Messages;
using RazorSharp.Protocol.Types;
using RazorSharp.Server.Configuration;
using RazorSharp.Server.Html;
using RazorSharp.Server.Roslyn;
using RazorSharp.Server.Workspace;
using StreamJsonRpc;

namespace RazorSharp.Server;

/// <summary>
/// The main Razor language server that orchestrates communication with Roslyn.
/// </summary>
public class RazorLanguageServer : IAsyncDisposable
{
    readonly ILogger<RazorLanguageServer> _logger;
    readonly ILoggerFactory _loggerFactory;
    readonly DependencyManager _dependencyManager;
    readonly WorkspaceManager _workspaceManager;
    readonly ConfigurationLoader _configurationLoader;
    readonly HtmlLanguageClient _htmlClient;
    RoslynClient? _roslynClient;
    JsonRpc? _clientRpc;
    InitializeParams? _initParams;
    InitializationOptions? _initOptions;
    string? _cliSolutionPath;
    string? _workspaceRoot;
    string _logLevel = "Information";
    bool _disposed;
    readonly TaskCompletionSource _roslynProjectInitialized = new();
    DateTime _workspaceOpenedAt;
    readonly HashSet<string> _openDocuments = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<JsonElement>> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _documentTrackingLock = new();

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    static readonly string Version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    static readonly int[] SymbolKindValues = Array.ConvertAll(Enum.GetValues<SymbolKind>(), v => (int)v);
    static readonly int[] CompletionItemKindValues = Array.ConvertAll(Enum.GetValues<CompletionItemKind>(), v => (int)v);

    // Cached JSON elements for common responses
    static readonly JsonElement EmptyArrayResponse = JsonSerializer.SerializeToElement(Array.Empty<object>());
    static readonly JsonElement DefaultFormattingOptions = JsonSerializer.SerializeToElement(new { tabSize = 4, insertSpaces = true });

    const string VirtualHtmlSuffix = "__virtual.html";

    // Language identifiers
    const string LanguageIdAspNetCoreRazor = "aspnetcorerazor";
    const string LanguageIdCSharp = "csharp";

    // Roslyn client command names
    const string RoslynClientCommandPrefix = "roslyn.client.";
    const string NestedCodeActionCommand = "roslyn.client.nestedCodeAction";
    const string FixAllCodeActionCommand = "roslyn.client.fixAllCodeAction";
    const string CompletionComplexEditCommand = "roslyn.client.completionComplexEdit";

    // Static arrays for capabilities to avoid repeated allocations
    static readonly string[] DocumentationFormats = ["markdown", "plaintext"];
    static readonly string[] CompletionResolveProperties = ["documentation", "detail", "additionalTextEdits"];
    static readonly string[] CodeActionKindValues =
    [
        "quickfix", "refactor", "refactor.extract", "refactor.inline",
        "refactor.rewrite", "source", "source.organizeImports", "source.fixAll"
    ];
    static readonly string[] CodeActionResolveProperties = ["edit"];
    static readonly int[] DiagnosticTagValues = [1, 2];
    static readonly string[] FoldingRangeKindValues = ["comment", "imports", "region"];
    static readonly string[] SemanticTokenTypes =
    [
        "namespace", "type", "class", "enum", "interface", "struct",
        "typeParameter", "parameter", "variable", "property", "enumMember",
        "event", "function", "method", "macro", "keyword", "modifier",
        "comment", "string", "number", "regexp", "operator", "decorator"
    ];
    static readonly string[] SemanticTokenModifiers =
    [
        "declaration", "definition", "readonly", "static", "deprecated",
        "abstract", "async", "modification", "documentation", "defaultLibrary"
    ];
    static readonly string[] SemanticTokenFormats = ["relative"];
    static readonly string[] InlayHintResolveProperties = ["tooltip", "textEdits", "label.tooltip", "label.location", "label.command"];

    public RazorLanguageServer(ILoggerFactory loggerFactory, DependencyManager dependencyManager)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RazorLanguageServer>();
        _dependencyManager = dependencyManager;
        _workspaceManager = new WorkspaceManager(loggerFactory.CreateLogger<WorkspaceManager>());
        _configurationLoader = new ConfigurationLoader(loggerFactory.CreateLogger<ConfigurationLoader>());
        _htmlClient = new HtmlLanguageClient(loggerFactory.CreateLogger<HtmlLanguageClient>());
    }

    /// <summary>
    /// Gets the configuration loader for accessing omnisharp.json settings.
    /// </summary>
    public ConfigurationLoader ConfigurationLoader => _configurationLoader;

    /// <summary>
    /// Sets the solution/project path from CLI argument.
    /// </summary>
    public void SetSolutionPath(string path)
    {
        _cliSolutionPath = Path.GetFullPath(path);
    }

    /// <summary>
    /// Sets the log level to use for Roslyn language server.
    /// </summary>
    public void SetLogLevel(LogLevel level)
    {
        _logLevel = level.ToString();
    }

    /// <summary>
    /// Runs the language server, listening on stdin/stdout.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Razor Language Server...");

        // Dependencies are now checked/downloaded at startup in Program.cs
        // before we start listening on stdin, to avoid LSP timeout issues

        // Set up JSON-RPC over stdin/stdout
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = JsonOptions
        };

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        var handler = new HeaderDelimitedMessageHandler(stdout, stdin, formatter);

        _clientRpc = new JsonRpc(handler);

        // Register this instance as the RPC target - methods with [JsonRpcMethod] will be called
        _clientRpc.AddLocalRpcTarget(this, new JsonRpcTargetOptions
        {
            // Allow methods to receive a single object parameter
            UseSingleObjectParameterDeserialization = true
        });

        _clientRpc.StartListening();

        _logger.LogInformation("Language server listening on stdio");

        // Wait for the client to disconnect or cancellation
        try
        {
            await _clientRpc.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Server shutdown requested");
        }
    }

    #region Lifecycle Handlers

    [JsonRpcMethod(LspMethods.Initialize, UseSingleObjectParameterDeserialization = true)]
    public async Task<InitializeResult> HandleInitializeAsync(JsonElement paramsJson, CancellationToken ct)
    {
        // Deserialize manually to avoid StreamJsonRpc parameter matching issues
        var @params = JsonSerializer.Deserialize<InitializeParams>(paramsJson, JsonOptions);
        _logger.LogInformation("Received initialize request from {Client}", @params?.ClientInfo?.Name ?? "unknown");
        _initParams = @params;

        // Load omnisharp.json configuration
        // Priority: CLI path > rootUri > workspaceFolders
        string? workspaceRoot = null;
        if (_cliSolutionPath != null)
        {
            workspaceRoot = Directory.Exists(_cliSolutionPath)
                ? _cliSolutionPath
                : Path.GetDirectoryName(_cliSolutionPath);
        }
        else if (@params?.RootUri != null)
        {
            workspaceRoot = new Uri(@params.RootUri).LocalPath;
        }
        else if (@params?.WorkspaceFolders?.Length > 0)
        {
            workspaceRoot = new Uri(@params.WorkspaceFolders[0].Uri).LocalPath;
        }

        _workspaceRoot = workspaceRoot;
        _configurationLoader.SetWorkspaceRoot(workspaceRoot);

        // Parse initializationOptions from the editor (e.g., Helix config = { ... })
        InitializationOptions? initOptions = null;
        if (@params?.InitializationOptions != null)
        {
            try
            {
                // Use strict deserialization to catch unknown properties
                var strictOptions = new JsonSerializerOptions(JsonOptions)
                {
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
                };
                initOptions = JsonSerializer.Deserialize<InitializationOptions>(
                    @params.InitializationOptions.Value.GetRawText(), strictOptions);
                _initOptions = initOptions;
                _logger.LogDebug("Parsed initializationOptions: html.enable={HtmlEnable}, capabilities.completionProvider.triggerCharacters={TriggerChars}",
                    initOptions?.Html?.Enable,
                    initOptions?.Capabilities?.CompletionProvider?.TriggerCharacters != null
                        ? string.Join(",", initOptions.Capabilities.CompletionProvider.TriggerCharacters)
                        : "default");
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse initializationOptions - check for invalid or unknown properties");
            }
        }

        // Start Roslyn
        _roslynClient = new RoslynClient(_loggerFactory.CreateLogger<RoslynClient>());
        _roslynClient.SetConfigurationLoader(_configurationLoader);

        var roslynOptions = RoslynClient.CreateStartOptions(_dependencyManager,
            Path.Combine(_dependencyManager.BasePath, "logs"), _logLevel);

        await _roslynClient.StartAsync(roslynOptions, ct);

        // Configure HTML language server (started lazily when first Razor file is opened)
        _htmlClient.Configure(initOptions?.Html, @params?.RootUri);

        // Wire up Roslyn events
        _roslynClient.NotificationReceived += OnRoslynNotification;
        _roslynClient.RequestReceived += OnRoslynRequestAsync;
        _roslynClient.ProcessExited += OnRoslynProcessExited;

        // Forward initialize to Roslyn with cohosting enabled
        var roslynInitParams = CreateRoslynInitParams(@params);
        var roslynResult = await _roslynClient.SendRequestAsync<object, JsonElement>(
            LspMethods.Initialize, roslynInitParams, ct);

        _logger.LogInformation("Roslyn initialized");
        if (roslynResult.TryGetProperty("capabilities", out var caps))
        {
            _logger.LogTrace("Roslyn capabilities: {Caps}", caps.GetRawText());
        }

        var result = CreateInitializeResult();
        _logger.LogDebug("Server capabilities: DiagnosticProvider = {DiagProvider}",
            result.Capabilities?.DiagnosticProvider != null ? "enabled" : "disabled");
        return result;
    }

    [JsonRpcMethod(LspMethods.Initialized, UseSingleObjectParameterDeserialization = true)]
    public void HandleInitialized()
    {
        _logger.LogInformation("Client initialized");

        // Forward to Roslyn - run in background but track completion
        _ = Task.Run(async () =>
        {
            try
            {
                if (_roslynClient != null)
                {
                    await _roslynClient.SendNotificationAsync(LspMethods.Initialized, (object?)null);
                    _logger.LogDebug("Roslyn received initialized notification");

                    // Open solution or projects FIRST - before documents
                    // Documents opened before project load won't have proper context
                    // Priority: CLI path > rootUri > workspaceFolders
                    _logger.LogDebug("Workspace discovery: cliPath={CliPath}, rootUri={RootUri}, workspaceFolders={WorkspaceFolders}",
                        _cliSolutionPath,
                        _initParams?.RootUri,
                        _initParams?.WorkspaceFolders?.Length ?? 0);
                    if (_cliSolutionPath != null)
                    {
                        await OpenWorkspaceAsync(new Uri(_cliSolutionPath).AbsoluteUri);
                    }
                    else if (_initParams?.RootUri != null)
                    {
                        await OpenWorkspaceAsync(_initParams.RootUri);
                    }
                    else if (_initParams?.WorkspaceFolders?.Length > 0)
                    {
                        await OpenWorkspaceAsync(_initParams.WorkspaceFolders[0].Uri);
                    }

                    _logger.LogDebug("Workspace opened, waiting for project initialization...");
                }
                // Note: _roslynProjectInitialized will be set when we receive
                // workspace/projectInitializationComplete notification from Roslyn
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Roslyn initialization");
                _roslynProjectInitialized.TrySetException(ex);
            }
        });
    }

    [JsonRpcMethod(LspMethods.Shutdown, UseSingleObjectParameterDeserialization = true)]
    public Task HandleShutdownAsync()
    {
        _logger.LogDebug("Shutdown requested");
        // Don't wait for Roslyn - just acknowledge shutdown immediately
        // Resources will be cleaned up in Exit
        return Task.CompletedTask;
    }

    [JsonRpcMethod(LspMethods.Exit, UseSingleObjectParameterDeserialization = true)]
    public void HandleExit()
    {
        _logger.LogDebug("Exit requested");

        // Gracefully shut down subprocesses (they have internal timeouts before force-kill)
        try
        {
            // Run disposal in parallel for both clients
            Task.WhenAll(
                _roslynClient?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                _htmlClient.DisposeAsync().AsTask()
            ).Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore disposal errors during exit
        }

        Environment.Exit(0);
    }

    #endregion

    #region Text Document Sync

    [JsonRpcMethod(LspMethods.TextDocumentDidOpen, UseSingleObjectParameterDeserialization = true)]
    public async Task HandleDidOpenAsync(JsonElement paramsJson)
    {
        var @params = JsonSerializer.Deserialize<DidOpenTextDocumentParams>(paramsJson, JsonOptions);
        if (@params == null) return;

        var uri = @params.TextDocument.Uri;
        _logger.LogDebug("Document opened: {Uri} (language: {Lang})", uri, @params.TextDocument.LanguageId);

        // Transform languageId for Roslyn (Helix sends "c-sharp", Roslyn expects "csharp")
        var languageId = @params.TextDocument.LanguageId;
        if (IsRazorUri(uri))
        {
            languageId = LanguageIdAspNetCoreRazor;
        }
        else if (IsCSharpUri(uri) || languageId == "c-sharp")
        {
            languageId = LanguageIdCSharp;
        }

        var roslynParams = new
        {
            textDocument = new
            {
                uri,
                languageId,
                version = @params.TextDocument.Version,
                text = @params.TextDocument.Text
            }
        };
        var roslynParamsJson = JsonSerializer.SerializeToElement(roslynParams, JsonOptions);

        // Wait briefly for Roslyn project initialization, but don't block forever
        // If not initialized yet, forward anyway - Roslyn will queue the document
        if (_roslynClient != null)
        {
            if (!_roslynProjectInitialized.Task.IsCompleted)
            {
                try
                {
                    _logger.LogDebug("Waiting for project initialization before forwarding didOpen...");
                    await _roslynProjectInitialized.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    _logger.LogDebug("Project not yet initialized, forwarding didOpen anyway for {Uri}", uri);
                }
            }

            _logger.LogDebug("Forwarding didOpen to Roslyn for {Uri}", uri);
            await _roslynClient.SendNotificationAsync(LspMethods.TextDocumentDidOpen, roslynParamsJson);

            // Track this document as open and retrieve any buffered changes atomically
            // to prevent race conditions with HandleDidChangeAsync
            List<JsonElement>? pendingChanges = null;
            lock (_documentTrackingLock)
            {
                _openDocuments.Add(uri);
                if (_pendingChanges.TryGetValue(uri, out pendingChanges))
                {
                    _pendingChanges.Remove(uri);
                }
            }

            if (pendingChanges != null && pendingChanges.Count > 0)
            {
                _logger.LogDebug("Replaying {Count} buffered changes for {Uri}", pendingChanges.Count, uri);
                foreach (var change in pendingChanges)
                {
                    await _roslynClient.SendNotificationAsync(LspMethods.TextDocumentDidChange, change);
                }
            }
        }
        else
        {
            _logger.LogWarning("Roslyn client is null, cannot forward didOpen");
        }
    }

    private static bool IsRazorUri(string uri)
    {
        return uri.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || uri.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCSharpUri(string uri)
    {
        return uri.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedUri(string uri)
    {
        return IsRazorUri(uri) || IsCSharpUri(uri);
    }

    [JsonRpcMethod(LspMethods.TextDocumentDidChange, UseSingleObjectParameterDeserialization = true)]
    public async Task HandleDidChangeAsync(JsonElement paramsJson)
    {
        // Only forward didChange if we've already forwarded didOpen for this document
        // Roslyn crashes if it receives didChange for a document it doesn't know about
        if (_roslynClient != null && _roslynClient.IsRunning)
        {
            var uri = paramsJson.TryGetProperty("textDocument", out var td) && td.TryGetProperty("uri", out var u)
                ? u.GetString() : null;

            if (uri != null)
            {
                // Check document state and buffer if needed atomically
                // to prevent race conditions with HandleDidOpenAsync
                bool isOpen;
                lock (_documentTrackingLock)
                {
                    isOpen = _openDocuments.Contains(uri);
                    if (!isOpen)
                    {
                        // Buffer the change to replay after didOpen is forwarded
                        _logger.LogDebug("Buffering didChange for {Uri} - document not yet open in Roslyn", uri);
                        if (!_pendingChanges.TryGetValue(uri, out var changes))
                        {
                            changes = new List<JsonElement>();
                            _pendingChanges[uri] = changes;
                        }
                        changes.Add(paramsJson.Clone());
                    }
                }

                if (isOpen)
                {
                    await _roslynClient.SendNotificationAsync(LspMethods.TextDocumentDidChange, paramsJson);
                }
            }
        }
    }

    [JsonRpcMethod(LspMethods.TextDocumentDidClose, UseSingleObjectParameterDeserialization = true)]
    public async Task HandleDidCloseAsync(JsonElement paramsJson)
    {
        var @params = JsonSerializer.Deserialize<DidCloseTextDocumentParams>(paramsJson, JsonOptions);
        if (@params == null) return;

        var uri = @params.TextDocument.Uri;
        _logger.LogDebug("Document closed: {Uri}", uri);

        // Remove from open document tracking and clean up pending changes
        // Must use lock to prevent race with HandleDidOpenAsync and HandleDidChangeAsync
        lock (_documentTrackingLock)
        {
            _openDocuments.Remove(uri);
            _pendingChanges.Remove(uri);
        }

        // Forward to Roslyn
        if (_roslynClient != null && _roslynClient.IsRunning)
        {
            await _roslynClient.SendNotificationAsync(LspMethods.TextDocumentDidClose, paramsJson);
        }
    }

    [JsonRpcMethod(LspMethods.TextDocumentDidSave, UseSingleObjectParameterDeserialization = true)]
    public async Task HandleDidSaveAsync(JsonElement paramsJson)
    {
        // Forward to Roslyn
        if (_roslynClient != null && _roslynClient.IsRunning)
        {
            await _roslynClient.SendNotificationAsync(LspMethods.TextDocumentDidSave, paramsJson);
        }
    }

    #endregion

    #region Language Feature Handlers

    [JsonRpcMethod(LspMethods.TextDocumentCompletion, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleCompletionAsync(JsonElement @params, CancellationToken ct)
    {
        _logger.LogDebug("Completion request received");
        return await ForwardToRoslynAsync(LspMethods.TextDocumentCompletion, @params, ct);
    }

    [JsonRpcMethod(LspMethods.CompletionItemResolve, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleCompletionResolveAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.CompletionItemResolve, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentHover, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleHoverAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentHover, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentDefinition, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleDefinitionAsync(JsonElement @params, CancellationToken ct)
    {
        var result = await ForwardToRoslynAsync(LspMethods.TextDocumentDefinition, @params, ct);
        return result.HasValue ? TransformSourceGeneratedUris(result.Value) : result;
    }

    [JsonRpcMethod(LspMethods.TextDocumentReferences, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleReferencesAsync(JsonElement @params, CancellationToken ct)
    {
        var result = await ForwardToRoslynAsync(LspMethods.TextDocumentReferences, @params, ct);
        return result.HasValue ? TransformSourceGeneratedUris(result.Value) : result;
    }

    [JsonRpcMethod(LspMethods.TextDocumentImplementation, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleImplementationAsync(JsonElement @params, CancellationToken ct)
    {
        var result = await ForwardToRoslynAsync(LspMethods.TextDocumentImplementation, @params, ct);
        return result.HasValue ? TransformSourceGeneratedUris(result.Value) : result;
    }

    [JsonRpcMethod(LspMethods.TextDocumentDocumentHighlight, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleDocumentHighlightAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentDocumentHighlight, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentDocumentSymbol, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleDocumentSymbolAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentDocumentSymbol, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentCodeAction, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleCodeActionAsync(JsonElement @params, CancellationToken ct)
    {
        _logger.LogDebug("Forwarding textDocument/codeAction to Roslyn");
        var result = await ForwardToRoslynAsync(LspMethods.TextDocumentCodeAction, @params, ct);

        // Transform code actions to expand nested actions for editors that don't support them
        // Also pre-resolve actions that need resolution (have data but no edit)
        if (result.HasValue)
        {
            var expanded = await ExpandAndResolveCodeActionsAsync(result.Value, ct);
            return expanded;
        }

        return result;
    }

    /// <summary>
    /// Expands nested code actions into top-level actions and pre-resolves them.
    /// Roslyn uses roslyn.client.nestedCodeAction commands for grouped actions,
    /// but many editors don't support this pattern. We flatten the structure
    /// and resolve actions that have data but no edit (since Helix doesn't call codeAction/resolve).
    /// </summary>
    private async Task<JsonElement> ExpandAndResolveCodeActionsAsync(JsonElement codeActions, CancellationToken ct)
    {
        if (codeActions.ValueKind != JsonValueKind.Array)
        {
            _logger.LogDebug("codeAction response is not an array, returning as-is");
            return codeActions;
        }

        var originalCount = codeActions.GetArrayLength();
        _logger.LogDebug("Processing {Count} code actions from Roslyn", originalCount);

        var expandedActions = new List<JsonElement>();
        var nestedCount = 0;

        foreach (var action in codeActions.EnumerateArray())
        {
            var title = action.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : "(no title)";

            if (TryGetNestedCodeActions(action, out var nestedActions))
            {
                nestedCount++;
                _logger.LogDebug("Expanding nested action '{Title}' with {Count} sub-actions", title, nestedActions.GetArrayLength());
                await ExpandNestedActionsAsync(nestedActions, title, expandedActions, ct);
            }
            else if (IsNestedCodeActionWithoutChildren(action))
            {
                _logger.LogDebug("Nested action '{Title}' has no NestedCodeActions, keeping original", title);
                expandedActions.Add(action);
            }
            else
            {
                var resolved = await ResolveCodeActionIfNeededAsync(action, title, ct);
                expandedActions.Add(resolved);
            }
        }

        _logger.LogDebug("Expanded {NestedCount} nested actions, returning {TotalCount} total actions", nestedCount, expandedActions.Count);
        return JsonSerializer.SerializeToElement(expandedActions, JsonOptions);
    }

    /// <summary>
    /// Checks if the action is a roslyn.client.nestedCodeAction and extracts the nested actions.
    /// </summary>
    private static bool TryGetNestedCodeActions(JsonElement action, out JsonElement nestedActions)
    {
        nestedActions = default;

        if (action.TryGetProperty("command", out var command) &&
            command.TryGetProperty("command", out var commandName) &&
            commandName.GetString() == NestedCodeActionCommand &&
            command.TryGetProperty("arguments", out var arguments) &&
            arguments.GetArrayLength() > 0)
        {
            var arg = arguments[0];
            if (arg.TryGetProperty("NestedCodeActions", out nestedActions))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if the action has a nestedCodeAction command but no actual nested actions.
    /// </summary>
    private static bool IsNestedCodeActionWithoutChildren(JsonElement action)
    {
        return action.TryGetProperty("command", out var command) &&
               command.TryGetProperty("command", out var commandName) &&
               commandName.GetString() == NestedCodeActionCommand;
    }

    /// <summary>
    /// Expands nested code actions into the result list, resolving each if needed.
    /// </summary>
    private async Task ExpandNestedActionsAsync(
        JsonElement nestedActions,
        string? parentTitle,
        List<JsonElement> expandedActions,
        CancellationToken ct)
    {
        foreach (var nested in nestedActions.EnumerateArray())
        {
            var originalTitle = nested.TryGetProperty("title", out var nestedTitleProp)
                ? nestedTitleProp.GetString()
                : null;
            var nestedTitle = string.IsNullOrEmpty(originalTitle) ? parentTitle : originalTitle;

            if (string.IsNullOrEmpty(nestedTitle))
            {
                _logger.LogError("Skipping nested code action with no title");
                continue;
            }

            var finalAction = await ResolveCodeActionIfNeededAsync(nested, nestedTitle, ct);

            // Clone with parent title if the nested action had no title
            if (string.IsNullOrEmpty(originalTitle))
            {
                finalAction = CloneWithNewTitle(finalAction, nestedTitle);
            }

            expandedActions.Add(finalAction);
            _logger.LogDebug("  Added expanded action: {Title}", nestedTitle);
        }
    }

    /// <summary>
    /// Resolves a code action if it has data but no edit.
    /// </summary>
    private async Task<JsonElement> ResolveCodeActionIfNeededAsync(JsonElement action, string? title, CancellationToken ct)
    {
        var hasEdit = action.TryGetProperty("edit", out _);
        var hasData = action.TryGetProperty("data", out _);

        if (!hasEdit && hasData)
        {
            _logger.LogDebug("Pre-resolving action: {Title}", title);
            var resolved = await ForwardToRoslynAsync(LspMethods.CodeActionResolve, action, ct);
            if (resolved.HasValue)
            {
                _logger.LogDebug("Resolved action: {Title}", title);
                return resolved.Value;
            }
            _logger.LogWarning("Failed to resolve action: {Title}", title);
        }

        return action;
    }

    /// <summary>
    /// Creates a copy of a code action with a new title.
    /// Uses Utf8JsonWriter to avoid Dictionary allocation.
    /// </summary>
    private static JsonElement CloneWithNewTitle(JsonElement action, string newTitle)
    {
        return CloneJsonElementWithReplacedProperty(action, "title", newTitle);
    }

    [JsonRpcMethod(LspMethods.CodeActionResolve, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleCodeActionResolveAsync(JsonElement @params, CancellationToken ct)
    {
        var title = @params.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : "(no title)";
        _logger.LogDebug("Resolving code action: {Title}", title);

        var result = await ForwardToRoslynAsync(LspMethods.CodeActionResolve, @params, ct);

        if (result.HasValue)
        {
            var hasEdit = result.Value.TryGetProperty("edit", out _);
            var hasCommand = result.Value.TryGetProperty("command", out var cmd);
            var cmdName = hasCommand && cmd.TryGetProperty("command", out var cmdNameProp) ? cmdNameProp.GetString() : null;
            _logger.LogDebug("Resolved code action - hasEdit: {HasEdit}, hasCommand: {HasCommand}, commandName: {CmdName}", hasEdit, hasCommand, cmdName);
        }
        else
        {
            _logger.LogDebug("Code action resolve returned null");
        }

        return result;
    }

    [JsonRpcMethod(LspMethods.TextDocumentFormatting, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleFormattingAsync(JsonElement @params, CancellationToken ct)
    {
        _logger.LogTrace("Formatting request params: {Params}", @params.GetRawText());
        var result = await ForwardToRoslynAsync(LspMethods.TextDocumentFormatting, @params, ct);
        _logger.LogTrace("Formatting response: {Result}", result?.GetRawText() ?? "null");
        return result;
    }

    [JsonRpcMethod(LspMethods.TextDocumentRangeFormatting, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleRangeFormattingAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentRangeFormatting, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentOnTypeFormatting, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleOnTypeFormattingAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentOnTypeFormatting, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentRename, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleRenameAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentRename, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentPrepareRename, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandlePrepareRenameAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentPrepareRename, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentSignatureHelp, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleSignatureHelpAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentSignatureHelp, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentFoldingRange, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleFoldingRangeAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentFoldingRange, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentSemanticTokensFull, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleSemanticTokensFullAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentSemanticTokensFull, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentSemanticTokensRange, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleSemanticTokensRangeAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentSemanticTokensRange, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentInlayHint, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleInlayHintAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentInlayHint, @params, ct);

    [JsonRpcMethod(LspMethods.InlayHintResolve, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleInlayHintResolveAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.InlayHintResolve, @params, ct);

    [JsonRpcMethod(LspMethods.WorkspaceSymbol, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleWorkspaceSymbolAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.WorkspaceSymbol, @params, ct);

    [JsonRpcMethod(LspMethods.WorkspaceExecuteCommand, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleExecuteCommandAsync(JsonElement @params, CancellationToken ct)
    {
        // Check if this is a roslyn.client.* command that we need to handle
        if (@params.TryGetProperty("command", out var commandProp))
        {
            var command = commandProp.GetString();
            if (command != null && command.StartsWith(RoslynClientCommandPrefix, StringComparison.Ordinal))
            {
                return await HandleRoslynClientCommandAsync(command, @params, ct);
            }
        }

        // Forward other commands to Roslyn
        return await ForwardToRoslynAsync(LspMethods.WorkspaceExecuteCommand, @params, ct);
    }

    /// <summary>
    /// Handles roslyn.client.* commands that Roslyn expects the client to process.
    /// </summary>
    private async Task<JsonElement?> HandleRoslynClientCommandAsync(string command, JsonElement @params, CancellationToken ct)
    {
        _logger.LogDebug("Handling Roslyn client command: {Command}", command);

        switch (command)
        {
            case NestedCodeActionCommand:
                return await HandleNestedCodeActionAsync(@params, ct);

            case FixAllCodeActionCommand:
                // Fix All actions need special handling - forward to Roslyn's fix all resolver
                return await HandleFixAllCodeActionAsync(@params, ct);

            case CompletionComplexEditCommand:
                // Complex completion edits - these should be handled by the editor
                // We can't do much here as they require cursor positioning
                _logger.LogDebug("completionComplexEdit: Ignoring (editor should handle)");
                return null;

            default:
                _logger.LogWarning("Unknown Roslyn client command: {Command}", command);
                return null;
        }
    }

    /// <summary>
    /// Handles roslyn.client.nestedCodeAction by resolving the selected nested action.
    /// The arguments contain NestedCodeActions array - we take the first one (user's selection)
    /// and resolve it to get the workspace edit.
    /// </summary>
    private async Task<JsonElement?> HandleNestedCodeActionAsync(JsonElement @params, CancellationToken ct)
    {
        try
        {
            // Extract arguments from the command params
            if (!@params.TryGetProperty("arguments", out var arguments) ||
                arguments.GetArrayLength() == 0)
            {
                _logger.LogWarning("nestedCodeAction: No arguments provided");
                return null;
            }

            var arg = arguments[0];

            // Get the nested code actions
            if (!arg.TryGetProperty("NestedCodeActions", out var nestedActions) ||
                nestedActions.GetArrayLength() == 0)
            {
                _logger.LogWarning("nestedCodeAction: No NestedCodeActions in arguments");
                return null;
            }

            // For now, take the first nested action
            // In a proper implementation, we'd ask the user to choose,
            // but since we're a proxy, we'll just take the first one
            // The editor has already selected which action to execute
            var selectedAction = nestedActions[0];

            _logger.LogDebug("nestedCodeAction: Resolving first nested action");

            // Resolve the code action to get the workspace edit
            var resolveResult = await ForwardToRoslynAsync(LspMethods.CodeActionResolve, selectedAction, ct);

            if (resolveResult.HasValue && resolveResult.Value.TryGetProperty("edit", out var edit))
            {
                // Apply the workspace edit via the client
                _logger.LogDebug("nestedCodeAction: Applying workspace edit");
                await _clientRpc!.NotifyAsync(LspMethods.WorkspaceApplyEdit, new { edit });
                return JsonSerializer.SerializeToElement(new { success = true }, JsonOptions);
            }

            _logger.LogWarning("nestedCodeAction: No edit in resolved action");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling nestedCodeAction");
            return null;
        }
    }

    /// <summary>
    /// Handles roslyn.client.fixAllCodeAction by resolving through codeAction/resolveFixAll.
    /// </summary>
    private async Task<JsonElement?> HandleFixAllCodeActionAsync(JsonElement @params, CancellationToken ct)
    {
        try
        {
            // Extract arguments from the command params
            if (!@params.TryGetProperty("arguments", out var arguments) ||
                arguments.GetArrayLength() == 0)
            {
                _logger.LogWarning("fixAllCodeAction: No arguments provided");
                return null;
            }

            var arg = arguments[0];

            // The argument should contain the code action to resolve with fix all scope
            // We'll take the first FixAllFlavors scope if available
            if (arg.TryGetProperty("FixAllFlavors", out var flavors) &&
                flavors.GetArrayLength() > 0)
            {
                var scope = flavors[0].GetString() ?? "document";
                _logger.LogDebug("fixAllCodeAction: Using scope {Scope}", scope);

                // Create resolve request with the scope
                var resolveParams = new {
                    codeAction = JsonSerializer.Deserialize<object>(arg.GetRawText()),
                    scope
                };

                var resolveResult = await ForwardToRoslynAsync(
                    "codeAction/resolveFixAll",
                    JsonSerializer.SerializeToElement(resolveParams, JsonOptions),
                    ct);

                if (resolveResult.HasValue && resolveResult.Value.TryGetProperty("edit", out var edit))
                {
                    _logger.LogDebug("fixAllCodeAction: Applying workspace edit");
                    await _clientRpc!.NotifyAsync(LspMethods.WorkspaceApplyEdit, new { edit });
                    return JsonSerializer.SerializeToElement(new { success = true }, JsonOptions);
                }
            }

            _logger.LogWarning("fixAllCodeAction: Could not resolve fix all action");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling fixAllCodeAction");
            return null;
        }
    }

    [JsonRpcMethod(LspMethods.TextDocumentDiagnostic, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleDiagnosticAsync(JsonElement @params, CancellationToken ct)
    {
        var uri = @params.TryGetProperty("textDocument", out var td) && td.TryGetProperty("uri", out var u)
            ? u.GetString() : "unknown";

        var diagConfig = _initOptions?.Capabilities?.DiagnosticProvider;

        // Check if diagnostics are disabled entirely
        if (diagConfig?.Enabled == false)
        {
            return JsonSerializer.SerializeToElement(new
            {
                kind = "full",
                resultId = "disabled",
                items = Array.Empty<object>()
            }, JsonOptions);
        }

        // For C# files, request configured diagnostic categories from Roslyn
        // Roslyn separates diagnostics into categories:
        // - "syntax" (DocumentCompilerSyntax) for syntax errors like missing braces
        // - "DocumentCompilerSemantic" for semantic errors like type mismatches
        // - "DocumentAnalyzerSyntax" for analyzer syntax diagnostics
        // - "DocumentAnalyzerSemantic" for analyzer semantic diagnostics
        if (uri?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Determine which categories to request (defaults: syntax=true, semantic=true, analyzers=false)
            var requestSyntax = diagConfig?.Syntax ?? true;
            var requestSemantic = diagConfig?.Semantic ?? true;
            var requestAnalyzerSyntax = diagConfig?.AnalyzerSyntax ?? false;
            var requestAnalyzerSemantic = diagConfig?.AnalyzerSemantic ?? false;

            var tasks = new List<Task<JsonElement?>>();

            if (requestSyntax)
            {
                var syntaxParams = JsonSerializer.SerializeToElement(new
                {
                    identifier = "syntax",
                    textDocument = new { uri }
                }, JsonOptions);
                tasks.Add(ForwardToRoslynAsync(LspMethods.TextDocumentDiagnostic, syntaxParams, ct));
            }

            if (requestSemantic)
            {
                var semanticParams = JsonSerializer.SerializeToElement(new
                {
                    identifier = "DocumentCompilerSemantic",
                    textDocument = new { uri }
                }, JsonOptions);
                tasks.Add(ForwardToRoslynAsync(LspMethods.TextDocumentDiagnostic, semanticParams, ct));
            }

            if (requestAnalyzerSyntax)
            {
                var analyzerSyntaxParams = JsonSerializer.SerializeToElement(new
                {
                    identifier = "DocumentAnalyzerSyntax",
                    textDocument = new { uri }
                }, JsonOptions);
                tasks.Add(ForwardToRoslynAsync(LspMethods.TextDocumentDiagnostic, analyzerSyntaxParams, ct));
            }

            if (requestAnalyzerSemantic)
            {
                var analyzerSemanticParams = JsonSerializer.SerializeToElement(new
                {
                    identifier = "DocumentAnalyzerSemantic",
                    textDocument = new { uri }
                }, JsonOptions);
                tasks.Add(ForwardToRoslynAsync(LspMethods.TextDocumentDiagnostic, analyzerSemanticParams, ct));
            }

            if (tasks.Count == 0)
            {
                // All categories disabled
                return JsonSerializer.SerializeToElement(new
                {
                    kind = "full",
                    resultId = "none",
                    items = Array.Empty<object>()
                }, JsonOptions);
            }

            _logger.LogDebug("Requesting {Count} diagnostic categories for {Uri}", tasks.Count, uri);

            // Request all categories in parallel
            var results = await Task.WhenAll(tasks);

            // Merge the diagnostic items from all responses
            var mergedItems = new List<JsonElement>();
            foreach (var result in results)
            {
                if (result.HasValue && result.Value.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        mergedItems.Add(item.Clone());
                    }
                }
            }

            _logger.LogDebug("Merged {Count} diagnostics for {Uri}", mergedItems.Count, uri);

            return JsonSerializer.SerializeToElement(new
            {
                kind = "full",
                resultId = $"merged:{DateTime.UtcNow.Ticks}",
                items = mergedItems
            }, JsonOptions);
        }

        // For non-C# files, forward as-is
        _logger.LogDebug("Forwarding textDocument/diagnostic to Roslyn for {Uri}", uri);
        var forwardedResult = await ForwardToRoslynAsync(LspMethods.TextDocumentDiagnostic, @params, ct);

        // Return empty report if forwarding failed (e.g., project not initialized)
        if (!forwardedResult.HasValue)
        {
            return JsonSerializer.SerializeToElement(new
            {
                kind = "full",
                resultId = "pending",
                items = Array.Empty<object>()
            }, JsonOptions);
        }

        return forwardedResult;
    }

    #endregion

    #region Roslyn Event Handlers

    private void OnRoslynProcessExited(object? sender, int exitCode)
    {
        _logger.LogError("Roslyn process exited unexpectedly with code {ExitCode}. Language features will be unavailable.", exitCode);
        // Mark initialization as failed so new requests don't wait forever
        _roslynProjectInitialized.TrySetException(new Exception($"Roslyn process exited with code {exitCode}"));
    }

    private void OnRoslynNotification(object? sender, RoslynNotificationEventArgs e)
    {
        _logger.LogDebug("Received notification from Roslyn: {Method}", e.Method);

        _ = Task.Run(async () =>
        {
            try
            {
                switch (e.Method)
                {
                    case LspMethods.RazorLog:
                        HandleRazorLog(e.Params);
                        break;

                    case LspMethods.TextDocumentPublishDiagnostics:
                        // Forward diagnostics to client
                        if (e.Params.HasValue)
                        {
                            var uri = e.Params.Value.TryGetProperty("uri", out var uriProp) ? uriProp.GetString() : "unknown";
                            var diagCount = e.Params.Value.TryGetProperty("diagnostics", out var diags) && diags.ValueKind == JsonValueKind.Array
                                ? diags.GetArrayLength()
                                : 0;
                            _logger.LogInformation("Publishing {Count} diagnostics for {Uri}", diagCount, uri);
                        }
                        await ForwardNotificationToClientAsync(e.Method, e.Params);
                        break;

                    case LspMethods.ProjectInitializationComplete:
                        _logger.LogInformation("Roslyn project initialization complete - server ready");
                        _roslynProjectInitialized.TrySetResult();
                        break;

                    case "window/logMessage":
                        // Log window messages for debugging
                        if (e.Params.HasValue && e.Params.Value.TryGetProperty("message", out var msgProp))
                        {
                            _logger.LogDebug("[Roslyn] {Message}", msgProp.GetString());
                        }
                        await ForwardNotificationToClientAsync(e.Method, e.Params);
                        break;

                    case "window/showMessage":
                        // Forward window messages to client
                        await ForwardNotificationToClientAsync(e.Method, e.Params);
                        break;

                    case LspMethods.Progress:
                        // Forward progress notifications to client for status bar spinners
                        await ForwardNotificationToClientAsync(e.Method, e.Params);
                        break;

                    default:
                        _logger.LogDebug("Unhandled Roslyn notification: {Method}", e.Method);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling Roslyn notification: {Method}", e.Method);
            }
        });
    }

    private async Task<JsonElement?> OnRoslynRequestAsync(string method, JsonElement? @params, long requestId, CancellationToken ct)
    {
        _logger.LogDebug("Roslyn reverse request: {Method}", method);

        try
        {
            // Handle progress creation requests - forward to editor for status bar spinners
            if (method == LspMethods.WindowWorkDoneProgressCreate)
            {
                return await ForwardRequestToClientAsync(method, @params, ct);
            }

            // Handle razor/updateHtml - sync HTML projection to HTML LS
            if (method == LspMethods.RazorUpdateHtml && @params.HasValue)
            {
                await HandleRazorUpdateHtmlAsync(@params.Value);
                return JsonSerializer.SerializeToElement(new { }, JsonOptions);
            }

            // Handle HTML formatting requests from Roslyn's Razor extension
            if (method == LspMethods.TextDocumentFormatting && @params.HasValue)
            {
                return await HandleHtmlFormattingRequestAsync(@params.Value, ct);
            }

            if (method == LspMethods.TextDocumentRangeFormatting && @params.HasValue)
            {
                return await HandleHtmlRangeFormattingRequestAsync(@params.Value, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Roslyn reverse request: {Method}", method);
        }

        return null;
    }

    private async Task HandleRazorUpdateHtmlAsync(JsonElement @params)
    {
        try
        {
            if (!@params.TryGetProperty("textDocument", out var textDocument) ||
                !textDocument.TryGetProperty("uri", out var uriProp) ||
                !@params.TryGetProperty("checksum", out var checksumProp) ||
                !@params.TryGetProperty("text", out var textProp))
            {
                _logger.LogWarning("razor/updateHtml missing required properties");
                return;
            }

            var uri = uriProp.GetString();
            var checksum = checksumProp.GetString();
            var text = textProp.GetString();

            if (uri != null && checksum != null && text != null)
            {
                _logger.LogDebug("Updating HTML projection for {Uri}, checksum: {Checksum}, length: {Length}",
                    uri, checksum, text.Length);
                await _htmlClient.UpdateHtmlProjectionAsync(uri, checksum, text);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling razor/updateHtml");
        }
    }

    /// <summary>
    /// Extracts common HTML formatting parameters from the request.
    /// </summary>
    private (string? razorUri, HtmlProjection? projection, JsonElement options, JsonElement range) ExtractHtmlFormattingParams(JsonElement @params)
    {
        var uri = @params.GetProperty("textDocument").GetProperty("uri").GetString();
        if (uri == null)
        {
            return (null, null, default, default);
        }

        var razorUri = uri.EndsWith(VirtualHtmlSuffix) ? uri[..^VirtualHtmlSuffix.Length] : uri;

        var checksum = @params.TryGetProperty("checksum", out var checksumProp)
            ? checksumProp.GetString()
            : null;

        JsonElement range = default;
        JsonElement options = default;
        if (@params.TryGetProperty("request", out var request))
        {
            if (request.TryGetProperty("range", out var r))
                range = r;
            if (request.TryGetProperty("options", out var opts))
                options = opts;
        }
        else
        {
            if (@params.TryGetProperty("range", out var r))
                range = r;
            if (@params.TryGetProperty("options", out var opts))
                options = opts;
        }

        // Find projection by checksum first, then fall back to URI
        HtmlProjection? projection = null;
        if (!string.IsNullOrEmpty(checksum))
        {
            projection = _htmlClient.GetProjection(checksum);
        }
        if (projection == null)
        {
            projection = _htmlClient.GetProjectionByRazorUri(razorUri);
        }

        // Default options if not provided
        if (options.ValueKind == JsonValueKind.Undefined)
        {
            options = DefaultFormattingOptions;
        }

        return (razorUri, projection, options, range);
    }

    private async Task<JsonElement?> HandleHtmlFormattingRequestAsync(JsonElement @params, CancellationToken ct)
    {
        try
        {
            var (razorUri, projection, options, _) = ExtractHtmlFormattingParams(@params);

            if (razorUri == null || projection == null)
            {
                return EmptyArrayResponse;
            }

            var result = await _htmlClient.FormatAsync(razorUri, projection.Checksum, options, ct);
            return result ?? EmptyArrayResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling HTML formatting request");
            return EmptyArrayResponse;
        }
    }

    private async Task<JsonElement?> HandleHtmlRangeFormattingRequestAsync(JsonElement @params, CancellationToken ct)
    {
        try
        {
            var (razorUri, projection, options, range) = ExtractHtmlFormattingParams(@params);

            if (razorUri == null || projection == null || range.ValueKind == JsonValueKind.Undefined)
            {
                return EmptyArrayResponse;
            }

            var result = await _htmlClient.FormatRangeAsync(razorUri, projection.Checksum, range, options, ct);
            return result ?? EmptyArrayResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling HTML range formatting request");
            return EmptyArrayResponse;
        }
    }

    private void HandleRazorLog(JsonElement? @params)
    {
        if (@params == null)
        {
            return;
        }

        try
        {
            var type = @params.Value.GetProperty("type").GetInt32();
            var message = @params.Value.GetProperty("message").GetString();

            var logLevel = type switch
            {
                1 => LogLevel.Error,
                2 => LogLevel.Warning,
                _ => LogLevel.Debug  // Info and below go to Debug to reduce noise
            };

            _logger.Log(logLevel, "[Razor] {Message}", message);
        }
        catch
        {
            // Ignore log parsing errors
        }
    }

    #endregion

    #region Helper Methods

    private async Task<JsonElement?> ForwardToRoslynAsync(string method, JsonElement @params, CancellationToken ct)
    {
        if (_roslynClient == null)
        {
            _logger.LogWarning("Cannot forward {Method} - Roslyn client not initialized", method);
            return null;
        }

        // Check if Roslyn is still running
        if (!_roslynClient.IsRunning)
        {
            _logger.LogWarning("Cannot forward {Method} - Roslyn process has exited", method);
            return null;
        }

        // Check if Roslyn project is initialized
        // If not initialized yet, either wait briefly or proceed after a grace period
        if (!_roslynProjectInitialized.Task.IsCompleted)
        {
            // After 15 seconds from workspace open, proceed anyway even without initialization
            // This handles cases where Roslyn never sends projectInitializationComplete
            var timeSinceOpen = DateTime.UtcNow - _workspaceOpenedAt;
            if (timeSinceOpen < TimeSpan.FromSeconds(15))
            {
                try
                {
                    // Wait up to 5 seconds for initialization
                    await _roslynProjectInitialized.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
                catch (TimeoutException)
                {
                    _logger.LogDebug("Project not yet initialized, cannot forward {Method}", method);
                    return null;
                }
            }
            else
            {
                _logger.LogDebug("Proceeding without initialization complete for {Method} (grace period elapsed)", method);
            }
        }

        try
        {
            _logger.LogDebug("Forwarding {Method} to Roslyn", method);
            var result = await _roslynClient.SendRequestAsync(method, @params, ct);
            _logger.LogDebug("Received response from Roslyn for {Method}", method);
            return result;
        }
        catch (IOException ex)
        {
            // Roslyn process died - don't propagate this as an LSP error
            _logger.LogWarning("Roslyn process died while forwarding {Method}: {Message}", method, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error forwarding {Method} to Roslyn", method);
            throw;
        }
    }

    private async Task ForwardNotificationToClientAsync(string method, JsonElement? @params)
    {
        if (_clientRpc == null)
        {
            return;
        }

        if (@params.HasValue)
        {
            await _clientRpc.NotifyWithParameterObjectAsync(method, @params.Value);
        }
        else
        {
            await _clientRpc.NotifyAsync(method);
        }
    }

    private async Task<JsonElement?> ForwardRequestToClientAsync(string method, JsonElement? @params, CancellationToken ct)
    {
        if (_clientRpc == null)
        {
            return null;
        }

        if (@params.HasValue)
        {
            return await _clientRpc.InvokeWithParameterObjectAsync<JsonElement?>(method, @params.Value, ct);
        }
        else
        {
            return await _clientRpc.InvokeAsync<JsonElement?>(method, ct);
        }
    }

    private async Task OpenWorkspaceAsync(string rootUri)
    {
        if (_roslynClient == null)
        {
            return;
        }

        _workspaceOpenedAt = DateTime.UtcNow;
        var rootPath = new Uri(rootUri).LocalPath;
        var solution = _workspaceManager.FindSolution(rootPath);

        if (solution != null)
        {
            _logger.LogInformation("Opening solution: {Solution}", solution);
            await _roslynClient.SendNotificationAsync(LspMethods.SolutionOpen, new SolutionOpenParams
            {
                Solution = new Uri(solution).AbsoluteUri
            });
        }
        else
        {
            // No solution file found - open projects directly
            var projects = _workspaceManager.FindProjects(rootPath);
            if (projects.Length > 0)
            {
                _logger.LogInformation("Opening {Count} projects directly", projects.Length);
                await _roslynClient.SendNotificationAsync(LspMethods.ProjectOpen, new ProjectOpenParams
                {
                    Projects = projects.Select(p => new Uri(p).AbsoluteUri).ToArray()
                });
            }
        }
    }

    private object CreateRoslynInitParams(InitializeParams? clientParams)
    {
        // Build capabilities that Roslyn/Razor expects
        var capabilities = new
        {
            workspace = new
            {
                configuration = true,
                workspaceFolders = true,
                applyEdit = true,
                workspaceEdit = new { documentChanges = true },
                symbol = new
                {
                    dynamicRegistration = true,
                    symbolKind = new { valueSet = SymbolKindValues }
                }
            },
            window = new
            {
                workDoneProgress = true
            },
            textDocument = new
            {
                synchronization = new
                {
                    dynamicRegistration = true,
                    willSave = true,
                    willSaveWaitUntil = true,
                    didSave = true
                },
                completion = new
                {
                    dynamicRegistration = true,
                    completionItem = new
                    {
                        snippetSupport = true,
                        commitCharactersSupport = true,
                        documentationFormat = DocumentationFormats,
                        deprecatedSupport = true,
                        preselectSupport = true,
                        insertReplaceSupport = true,
                        resolveSupport = new { properties = CompletionResolveProperties }
                    },
                    completionItemKind = new { valueSet = CompletionItemKindValues },
                    contextSupport = true
                },
                hover = new
                {
                    dynamicRegistration = true,
                    contentFormat = DocumentationFormats
                },
                signatureHelp = new
                {
                    dynamicRegistration = true,
                    signatureInformation = new
                    {
                        documentationFormat = DocumentationFormats,
                        parameterInformation = new { labelOffsetSupport = true },
                        activeParameterSupport = true
                    },
                    contextSupport = true
                },
                definition = new { dynamicRegistration = true, linkSupport = true },
                typeDefinition = new { dynamicRegistration = true, linkSupport = true },
                implementation = new { dynamicRegistration = true, linkSupport = true },
                references = new { dynamicRegistration = true },
                documentHighlight = new { dynamicRegistration = true },
                documentSymbol = new
                {
                    dynamicRegistration = true,
                    symbolKind = new { valueSet = SymbolKindValues },
                    hierarchicalDocumentSymbolSupport = true
                },
                codeAction = new
                {
                    dynamicRegistration = true,
                    codeActionLiteralSupport = new
                    {
                        codeActionKind = new
                        {
                            valueSet = CodeActionKindValues
                        }
                    },
                    isPreferredSupport = true,
                    disabledSupport = true,
                    dataSupport = true,
                    resolveSupport = new { properties = CodeActionResolveProperties }
                },
                formatting = new { dynamicRegistration = true },
                rangeFormatting = new { dynamicRegistration = true },
                onTypeFormatting = new { dynamicRegistration = true },
                rename = new
                {
                    dynamicRegistration = true,
                    prepareSupport = true,
                    prepareSupportDefaultBehavior = 1,
                    honorsChangeAnnotations = true
                },
                publishDiagnostics = new
                {
                    relatedInformation = true,
                    tagSupport = new { valueSet = DiagnosticTagValues },
                    versionSupport = true,
                    codeDescriptionSupport = true,
                    dataSupport = true
                },
                foldingRange = new
                {
                    dynamicRegistration = true,
                    rangeLimit = 5000,
                    lineFoldingOnly = false,
                    foldingRangeKind = new { valueSet = FoldingRangeKindValues }
                },
                semanticTokens = new
                {
                    dynamicRegistration = true,
                    requests = new { range = true, full = new { delta = true } },
                    tokenTypes = SemanticTokenTypes,
                    tokenModifiers = SemanticTokenModifiers,
                    formats = SemanticTokenFormats,
                    overlappingTokenSupport = false,
                    multilineTokenSupport = true
                },
                inlayHint = new
                {
                    dynamicRegistration = true,
                    resolveSupport = new { properties = InlayHintResolveProperties }
                },
                diagnostic = new
                {
                    dynamicRegistration = true,
                    relatedDocumentSupport = true
                }
            }
        };

        return new
        {
            processId = Environment.ProcessId,
            clientInfo = new { name = "RazorSharp", version = Version },
            rootUri = clientParams?.RootUri,
            capabilities
        };
    }

    private InitializeResult CreateInitializeResult()
    {
        var caps = _initOptions?.Capabilities;

        // Build completion provider if enabled (default: true)
        CompletionOptions? completionProvider = null;
        if (caps?.CompletionProvider?.Enabled != false)
        {
            completionProvider = new CompletionOptions
            {
                TriggerCharacters = DedupeWithWarning(
                    caps?.CompletionProvider?.TriggerCharacters,
                    [".", "<", "@", "(", "=", "/"],
                    "completionProvider.triggerCharacters"),
                ResolveProvider = true
            };
        }

        // Build signature help provider if enabled (default: true)
        SignatureHelpOptions? signatureHelpProvider = null;
        if (caps?.SignatureHelpProvider?.Enabled != false)
        {
            signatureHelpProvider = new SignatureHelpOptions
            {
                TriggerCharacters = DedupeWithWarning(
                    caps?.SignatureHelpProvider?.TriggerCharacters,
                    ["(", ","],
                    "signatureHelpProvider.triggerCharacters"),
                RetriggerCharacters = DedupeWithWarning(
                    caps?.SignatureHelpProvider?.RetriggerCharacters,
                    [")"],
                    "signatureHelpProvider.retriggerCharacters")
            };
        }

        // Build document on-type formatting provider if enabled (default: true)
        DocumentOnTypeFormattingOptions? documentOnTypeFormattingProvider = null;
        if (caps?.DocumentOnTypeFormattingProvider?.Enabled != false)
        {
            var moreTriggers = DedupeWithWarning(
                caps?.DocumentOnTypeFormattingProvider?.MoreTriggerCharacter,
                ["}", "\n"],
                "documentOnTypeFormattingProvider.moreTriggerCharacter");

            documentOnTypeFormattingProvider = new DocumentOnTypeFormattingOptions
            {
                FirstTriggerCharacter = caps?.DocumentOnTypeFormattingProvider?.FirstTriggerCharacter ?? ";",
                MoreTriggerCharacter = moreTriggers
            };
        }

        // Build semantic tokens provider if enabled (default: true)
        SemanticTokensOptions? semanticTokensProvider = null;
        if (caps?.SemanticTokensProvider?.Enabled != false)
        {
            semanticTokensProvider = new SemanticTokensOptions
            {
                Legend = new SemanticTokensLegend
                {
                    TokenTypes =
                    [
                        "namespace", "type", "class", "enum", "interface", "struct",
                        "typeParameter", "parameter", "variable", "property", "enumMember",
                        "event", "function", "method", "macro", "keyword", "modifier",
                        "comment", "string", "number", "regexp", "operator", "decorator"
                    ],
                    TokenModifiers =
                    [
                        "declaration", "definition", "readonly", "static", "deprecated",
                        "abstract", "async", "modification", "documentation", "defaultLibrary"
                    ]
                },
                Full = caps?.SemanticTokensProvider?.Full ?? true,
                Range = caps?.SemanticTokensProvider?.Range ?? true
            };
        }

        return new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = 2, // Incremental
                    Save = new SaveOptions { IncludeText = false }
                },
                CompletionProvider = completionProvider,
                HoverProvider = caps?.HoverProvider ?? true,
                SignatureHelpProvider = signatureHelpProvider,
                DefinitionProvider = caps?.DefinitionProvider ?? true,
                TypeDefinitionProvider = caps?.TypeDefinitionProvider ?? true,
                ImplementationProvider = caps?.ImplementationProvider ?? true,
                ReferencesProvider = caps?.ReferencesProvider ?? true,
                DocumentHighlightProvider = caps?.DocumentHighlightProvider ?? true,
                DocumentSymbolProvider = caps?.DocumentSymbolProvider ?? true,
                CodeActionProvider = caps?.CodeActionProvider ?? true,
                ExecuteCommandProvider = new ExecuteCommandOptions
                {
                    // Empty array - we forward all commands to Roslyn
                    Commands = []
                },
                DocumentFormattingProvider = caps?.DocumentFormattingProvider ?? true,
                DocumentRangeFormattingProvider = caps?.DocumentRangeFormattingProvider ?? true,
                DocumentOnTypeFormattingProvider = documentOnTypeFormattingProvider,
                RenameProvider = caps?.RenameProvider ?? true,
                FoldingRangeProvider = caps?.FoldingRangeProvider ?? true,
                WorkspaceSymbolProvider = caps?.WorkspaceSymbolProvider ?? true,
                SemanticTokensProvider = semanticTokensProvider,
                InlayHintProvider = caps?.InlayHintProvider ?? true,
                DiagnosticProvider = new DiagnosticOptions
                {
                    Identifier = "razorsharp",
                    InterFileDependencies = true,
                    WorkspaceDiagnostics = false
                }
            },
            ServerInfo = new ServerInfo("RazorSharp", Version)
        };
    }

    #endregion

    private string[] DedupeWithWarning(string[]? custom, string[] defaults, string optionName)
    {
        if (custom == null)
            return defaults;

        // Use HashSet for O(n) duplicate detection in a single pass
        var seen = new HashSet<string>();
        List<string>? duplicates = null;

        foreach (var item in custom)
        {
            if (!seen.Add(item))
            {
                duplicates ??= [];
                if (!duplicates.Contains(item))
                    duplicates.Add(item);
            }
        }

        if (duplicates != null)
        {
            _logger.LogWarning("Duplicate {OptionName}: {Duplicates}",
                optionName, string.Join(", ", duplicates.Select(c => '"' + c + '"')));
            return seen.ToArray();
        }

        return custom;
    }

    /// <summary>
    /// Transforms roslyn-source-generated:// URIs in location responses to file:// URIs
    /// pointing to the generated files on disk (when EmitCompilerGeneratedFiles is enabled).
    /// This allows editors like Helix that don't support custom URI schemes to navigate to generated code.
    /// </summary>
    private JsonElement TransformSourceGeneratedUris(JsonElement response)
    {
        if (_workspaceRoot == null)
        {
            return response;
        }

        // Response can be: null, Location, Location[], LocationLink[]
        if (response.ValueKind == JsonValueKind.Null)
        {
            return response;
        }

        if (response.ValueKind == JsonValueKind.Array)
        {
            var items = response.EnumerateArray().ToArray();
            var anyChanged = false;

            var transformed = Array.ConvertAll(items, item => {
                var newItem = TransformLocationElement(item);
                if (!anyChanged) anyChanged = !item.Equals(newItem);
                return newItem;
            });

            if (anyChanged)
            {
                return JsonSerializer.SerializeToElement(transformed);
            }
            else
            {
                return response;
            }
        }

        // Single location
        return TransformLocationElement(response);
    }

    private JsonElement TransformLocationElement(JsonElement element)
    {
        // Check if this is a Location (has "uri") or LocationLink (has "targetUri")
        if (element.TryGetProperty("uri", out var uriProp))
        {
            var uri = uriProp.GetString();
            if (uri != null && TryMapSourceGeneratedUri(uri, out var filePath))
            {
                return CloneJsonElementWithReplacedProperty(element, "uri", new Uri(filePath).AbsoluteUri);
            }
        }
        else if (element.TryGetProperty("targetUri", out var targetUriProp))
        {
            var uri = targetUriProp.GetString();
            if (uri != null && TryMapSourceGeneratedUri(uri, out var filePath))
            {
                return CloneJsonElementWithReplacedProperty(element, "targetUri", new Uri(filePath).AbsoluteUri);
            }
        }

        return element;
    }

    /// <summary>
    /// Clones a JsonElement, replacing a single string property value.
    /// Uses Utf8JsonWriter to avoid expensive Dictionary deserialization/serialization.
    /// </summary>
    private static JsonElement CloneJsonElementWithReplacedProperty(JsonElement element, string propertyName, string newValue)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name == propertyName)
                {
                    writer.WriteString(propertyName, newValue);
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.GetBuffer().AsMemory(0, (int)stream.Length)).RootElement.Clone();
    }

    /// <summary>
    /// Tries to map a roslyn-source-generated:// URI to a file path on disk.
    /// The URI format is: roslyn-source-generated://{projectId}/{hintName}?assemblyName=...&typeName=...&hintName=...
    /// Generated files are typically at: obj/{Configuration}/{TFM}/generated/{assemblyName}/{typeName}/{hintName}
    /// </summary>
    private bool TryMapSourceGeneratedUri(string uri, out string filePath)
    {
        filePath = "";

        if (!uri.StartsWith("roslyn-source-generated://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var parsed = new Uri(uri);
            var query = System.Web.HttpUtility.ParseQueryString(parsed.Query);

            var assemblyName = query["assemblyName"];
            var typeName = query["typeName"];
            var hintName = query["hintName"];

            if (string.IsNullOrEmpty(assemblyName) || string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(hintName))
            {
                _logger.LogDebug("Source generated URI missing required query parameters: {Uri}", uri);
                return false;
            }

            // Search for the generated file, preferring Debug over Release
            var found = FindGeneratedFile(assemblyName, typeName, hintName);

            if (found != null)
            {
                filePath = found;
                _logger.LogDebug("Mapped source generated URI {Uri} to {FilePath}", uri, filePath);
                return true;
            }

            _logger.LogDebug("No generated file found for URI: {Uri}", uri);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse source generated URI: {Uri}", uri);
            return false;
        }
    }

    /// <summary>
    /// Finds the first matching generated file, preferring Debug over other configurations.
    /// </summary>
    private string? FindGeneratedFile(string assemblyName, string typeName, string hintName)
    {
        // Search in workspace root and all subdirectories (for multi-project solutions)
        foreach (var objDir in Directory.EnumerateDirectories(_workspaceRoot!, "obj", SearchOption.AllDirectories))
        {
            // Enumerate all configuration directories, but check Debug first if it exists
            // OrderByDescending with bool puts true (Debug match) first since true > false
            var configDirs = Directory.EnumerateDirectories(objDir)
                .OrderByDescending(d => Path.GetFileName(d).Equals("Debug", StringComparison.OrdinalIgnoreCase) ? 1 : 0);

            foreach (var configDir in configDirs)
            {
                var path = FindGeneratedFileInConfig(configDir, assemblyName, typeName, hintName);
                if (path != null) return path;
            }
        }

        return null;
    }

    private static string? FindGeneratedFileInConfig(string configDir, string assemblyName, string typeName, string hintName)
    {
        // Look in all TFM directories (net8.0, net9.0, etc.)
        foreach (var tfmDir in Directory.EnumerateDirectories(configDir))
        {
            var generatedPath = Path.Combine(tfmDir, "generated", assemblyName, typeName, hintName);
            if (File.Exists(generatedPath))
            {
                return generatedPath;
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_roslynClient != null)
        {
            await _roslynClient.DisposeAsync();
        }

        await _htmlClient.DisposeAsync();

        _clientRpc?.Dispose();
    }
}
