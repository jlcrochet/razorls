using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorLS.Dependencies;
using RazorLS.Protocol;
using RazorLS.Protocol.Messages;
using RazorLS.Server.Configuration;
using RazorLS.Server.Roslyn;
using RazorLS.Server.Workspace;
using StreamJsonRpc;

namespace RazorLS.Server;

/// <summary>
/// The main Razor language server that orchestrates communication with Roslyn.
/// </summary>
public class RazorLanguageServer : IAsyncDisposable
{
    private readonly ILogger<RazorLanguageServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DependencyManager _dependencyManager;
    private readonly WorkspaceManager _workspaceManager;
    private readonly ConfigurationLoader _configurationLoader;
    private RoslynRawClient? _roslynClient;
    private JsonRpc? _clientRpc;
    private InitializeParams? _initParams;
    private string? _cliSolutionPath;
    private bool _disposed;
    private readonly TaskCompletionSource _roslynProjectInitialized = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public RazorLanguageServer(ILoggerFactory loggerFactory, DependencyManager dependencyManager)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RazorLanguageServer>();
        _dependencyManager = dependencyManager;
        _workspaceManager = new WorkspaceManager(loggerFactory.CreateLogger<WorkspaceManager>());
        _configurationLoader = new ConfigurationLoader(loggerFactory.CreateLogger<ConfigurationLoader>());
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
    /// Runs the language server, listening on stdin/stdout.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Razor Language Server...");

        // Ensure dependencies are downloaded
        if (!await _dependencyManager.EnsureDependenciesAsync(cancellationToken))
        {
            _logger.LogError("Failed to ensure dependencies. Exiting.");
            return;
        }

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
        var @params = JsonSerializer.Deserialize<InitializeParams>(paramsJson.GetRawText(), JsonOptions);
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

        _configurationLoader.SetWorkspaceRoot(workspaceRoot);

        // Start Roslyn
        _roslynClient = new RoslynRawClient(_loggerFactory.CreateLogger<RoslynRawClient>());
        _roslynClient.SetConfigurationLoader(_configurationLoader);

        var roslynOptions = RoslynClient.CreateStartOptions(_dependencyManager,
            Path.Combine(_dependencyManager.BasePath, "logs"));

        await _roslynClient.StartAsync(roslynOptions, ct);

        // Wire up Roslyn notifications
        _roslynClient.NotificationReceived += OnRoslynNotification;
        _roslynClient.RequestReceived += OnRoslynRequestAsync;

        // Forward initialize to Roslyn with cohosting enabled
        var roslynInitParams = CreateRoslynInitParams(@params);
        var roslynResult = await _roslynClient.SendRequestAsync<object, JsonElement>(
            LspMethods.Initialize, roslynInitParams, ct);

        _logger.LogInformation("Roslyn initialized");

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

        // Kill subprocesses immediately - don't wait for graceful shutdown
        try
        {
            _roslynClient?.DisposeAsync().AsTask().Wait(100);
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
        var @params = JsonSerializer.Deserialize<DidOpenTextDocumentParams>(paramsJson.GetRawText(), JsonOptions);
        if (@params == null) return;

        var uri = @params.TextDocument.Uri;
        _logger.LogDebug("Document opened: {Uri} (language: {Lang})", uri, @params.TextDocument.LanguageId);

        // Wait for Roslyn project to be initialized before forwarding documents
        // Documents opened before project initialization won't have proper context
        if (_roslynClient != null)
        {
            try
            {
                // Wait for project initialization with a timeout
                _logger.LogDebug("Waiting for project initialization before forwarding didOpen...");
                await _roslynProjectInitialized.Task.WaitAsync(TimeSpan.FromSeconds(60));
                _logger.LogDebug("Forwarding didOpen to Roslyn for {Uri}", uri);
                await _roslynClient.SendNotificationAsync(LspMethods.TextDocumentDidOpen, paramsJson);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timeout waiting for project initialization, forwarding didOpen anyway for {Uri}", uri);
                await _roslynClient.SendNotificationAsync(LspMethods.TextDocumentDidOpen, paramsJson);
            }
        }
        else
        {
            _logger.LogWarning("Roslyn client is null, cannot forward didOpen");
        }
    }

    [JsonRpcMethod(LspMethods.TextDocumentDidChange, UseSingleObjectParameterDeserialization = true)]
    public async Task HandleDidChangeAsync(JsonElement paramsJson)
    {
        // Forward to Roslyn - it handles document state internally
        if (_roslynClient != null)
        {
            await _roslynClient.SendNotificationAsync(LspMethods.TextDocumentDidChange, paramsJson);
        }
    }

    [JsonRpcMethod(LspMethods.TextDocumentDidClose, UseSingleObjectParameterDeserialization = true)]
    public async Task HandleDidCloseAsync(JsonElement paramsJson)
    {
        var @params = JsonSerializer.Deserialize<DidCloseTextDocumentParams>(paramsJson.GetRawText(), JsonOptions);
        if (@params == null) return;

        var uri = @params.TextDocument.Uri;
        _logger.LogDebug("Document closed: {Uri}", uri);

        // Forward to Roslyn
        if (_roslynClient != null)
        {
            await _roslynClient.SendNotificationAsync(LspMethods.TextDocumentDidClose, paramsJson);
        }
    }

    [JsonRpcMethod(LspMethods.TextDocumentDidSave, UseSingleObjectParameterDeserialization = true)]
    public async Task HandleDidSaveAsync(JsonElement paramsJson)
    {
        // Forward to Roslyn
        if (_roslynClient != null)
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
    public Task<JsonElement?> HandleDefinitionAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentDefinition, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentReferences, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleReferencesAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentReferences, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentImplementation, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleImplementationAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentImplementation, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentDocumentHighlight, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleDocumentHighlightAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentDocumentHighlight, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentDocumentSymbol, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleDocumentSymbolAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentDocumentSymbol, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentCodeAction, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleCodeActionAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentCodeAction, @params, ct);

    [JsonRpcMethod(LspMethods.CodeActionResolve, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleCodeActionResolveAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.CodeActionResolve, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentFormatting, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleFormattingAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentFormatting, @params, ct);

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
    public Task<JsonElement?> HandleExecuteCommandAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.WorkspaceExecuteCommand, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentDiagnostic, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleDiagnosticAsync(JsonElement @params, CancellationToken ct)
    {
        _logger.LogDebug("Forwarding textDocument/diagnostic to Roslyn");
        var result = await ForwardToRoslynAsync(LspMethods.TextDocumentDiagnostic, @params, ct);
        if (result.HasValue)
        {
            _logger.LogDebug("Received diagnostic response from Roslyn");
        }
        return result;
    }

    #endregion

    #region Roslyn Event Handlers

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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Roslyn reverse request: {Method}", method);
        }

        return null;
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

        // Wait for Roslyn project to be fully initialized before forwarding requests
        try
        {
            await _roslynProjectInitialized.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timeout waiting for Roslyn project initialization, proceeding anyway");
        }

        try
        {
            _logger.LogDebug("Forwarding {Method} to Roslyn", method);
            var result = await _roslynClient.SendRequestAsync(method, @params, ct);
            _logger.LogDebug("Received response from Roslyn for {Method}", method);
            return result;
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
            var projects = _workspaceManager.FindProjects(rootPath);
            if (projects.Length > 0)
            {
                _logger.LogInformation("Opening {Count} projects", projects.Length);
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
                    symbolKind = new { valueSet = Enumerable.Range(1, 26).ToArray() }
                }
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
                        documentationFormat = new[] { "markdown", "plaintext" },
                        deprecatedSupport = true,
                        preselectSupport = true,
                        insertReplaceSupport = true,
                        resolveSupport = new { properties = new[] { "documentation", "detail", "additionalTextEdits" } }
                    },
                    completionItemKind = new { valueSet = Enumerable.Range(1, 25).ToArray() },
                    contextSupport = true
                },
                hover = new
                {
                    dynamicRegistration = true,
                    contentFormat = new[] { "markdown", "plaintext" }
                },
                signatureHelp = new
                {
                    dynamicRegistration = true,
                    signatureInformation = new
                    {
                        documentationFormat = new[] { "markdown", "plaintext" },
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
                    symbolKind = new { valueSet = Enumerable.Range(1, 26).ToArray() },
                    hierarchicalDocumentSymbolSupport = true
                },
                codeAction = new
                {
                    dynamicRegistration = true,
                    codeActionLiteralSupport = new
                    {
                        codeActionKind = new
                        {
                            valueSet = new[]
                            {
                                "quickfix", "refactor", "refactor.extract", "refactor.inline",
                                "refactor.rewrite", "source", "source.organizeImports", "source.fixAll"
                            }
                        }
                    },
                    isPreferredSupport = true,
                    disabledSupport = true,
                    dataSupport = true,
                    resolveSupport = new { properties = new[] { "edit" } }
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
                    tagSupport = new { valueSet = new[] { 1, 2 } },
                    versionSupport = true,
                    codeDescriptionSupport = true,
                    dataSupport = true
                },
                foldingRange = new
                {
                    dynamicRegistration = true,
                    rangeLimit = 5000,
                    lineFoldingOnly = false,
                    foldingRangeKind = new { valueSet = new[] { "comment", "imports", "region" } }
                },
                semanticTokens = new
                {
                    dynamicRegistration = true,
                    requests = new { range = true, full = new { delta = true } },
                    tokenTypes = new[]
                    {
                        "namespace", "type", "class", "enum", "interface", "struct",
                        "typeParameter", "parameter", "variable", "property", "enumMember",
                        "event", "function", "method", "macro", "keyword", "modifier",
                        "comment", "string", "number", "regexp", "operator", "decorator"
                    },
                    tokenModifiers = new[]
                    {
                        "declaration", "definition", "readonly", "static", "deprecated",
                        "abstract", "async", "modification", "documentation", "defaultLibrary"
                    },
                    formats = new[] { "relative" },
                    overlappingTokenSupport = false,
                    multilineTokenSupport = true
                },
                inlayHint = new
                {
                    dynamicRegistration = true,
                    resolveSupport = new { properties = new[] { "tooltip", "textEdits", "label.tooltip", "label.location", "label.command" } }
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
            clientInfo = new { name = "RazorLS", version = "0.1.0" },
            rootUri = clientParams?.RootUri,
            capabilities
        };
    }

    private InitializeResult CreateInitializeResult()
    {
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
                CompletionProvider = new CompletionOptions
                {
                    TriggerCharacters = [".", "<", "@", " ", "(", "\"", "'", "=", "/"],
                    ResolveProvider = true
                },
                HoverProvider = true,
                SignatureHelpProvider = new SignatureHelpOptions
                {
                    TriggerCharacters = ["(", ","],
                    RetriggerCharacters = [")"]
                },
                DefinitionProvider = true,
                TypeDefinitionProvider = true,
                ImplementationProvider = true,
                ReferencesProvider = true,
                DocumentHighlightProvider = true,
                DocumentSymbolProvider = true,
                CodeActionProvider = true,
                DocumentFormattingProvider = true,
                DocumentRangeFormattingProvider = true,
                DocumentOnTypeFormattingProvider = new DocumentOnTypeFormattingOptions
                {
                    FirstTriggerCharacter = ";",
                    MoreTriggerCharacter = ["}", "\n"]
                },
                RenameProvider = true,
                FoldingRangeProvider = true,
                WorkspaceSymbolProvider = true,
                SemanticTokensProvider = new SemanticTokensOptions
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
                    Full = true,
                    Range = true
                },
                InlayHintProvider = true,
                DiagnosticProvider = new DiagnosticOptions
                {
                    Identifier = "razorls",
                    InterFileDependencies = true,
                    WorkspaceDiagnostics = false
                }
            },
            ServerInfo = new ServerInfo("RazorLS", "0.1.0")
        };
    }

    #endregion

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

        _clientRpc?.Dispose();
    }
}
