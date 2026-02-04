using System.Buffers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Protocol;
using RazorSharp.Protocol.Messages;
using RazorSharp.Protocol.Types;
using RazorSharp.Server.Configuration;
using RazorSharp.Server.Html;
using RazorSharp.Server.Roslyn;
using RazorSharp.Server.Utilities;
using RazorSharp.Server.Workspace;
using RazorSharp.Utilities;
using StreamJsonRpc;

namespace RazorSharp.Server;

/// <summary>
/// The main Razor language server that orchestrates communication with Roslyn.
/// </summary>
public partial class RazorLanguageServer : IAsyncDisposable
{
    readonly ILogger<RazorLanguageServer> _logger;
    readonly ILoggerFactory _loggerFactory;
    readonly DependencyManager _dependencyManager;
    readonly WorkspaceManager _workspaceManager;
    readonly ConfigurationLoader _configurationLoader;
    readonly HtmlLanguageClient _htmlClient;
    RoslynClient? _roslynClient;
    JsonRpc? _clientRpc;
    IProgressRpc? _progressRpc;
    InitializeParams? _initParams;
    InitializationOptions? _initOptions;
    string? _cliSolutionPath;
    string? _workspaceOpenTarget;
    string? _workspaceRoot;
    string _logLevel = "Information";
    bool _skipDependencyCheck;
    bool _autoUpdateEnabledFromCli = true;
    bool _autoUpdateEnabled = true;
    TimeSpan _autoUpdateInterval = TimeSpan.FromHours(24);
    Task? _autoUpdateTask;
    readonly Lock _autoUpdateLock = new();
    Task? _dependencyDownloadTask;
    readonly Lock _dependencyDownloadLock = new();
    bool _dependenciesMissing;
    bool _skipDependencyCheckSetByCli;
    bool _forceUpdateCheck;
    LoggingLevelSwitch? _loggingLevelSwitch;
    LogFileSwitch? _logFileSwitch;
    bool _logLevelSetByCli;
    bool _logFileSetByCli;
    bool _disposed;
    bool _fileWatchersRegistered;
    readonly TaskCompletionSource _roslynProjectInitialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    DateTime _workspaceOpenedAt;
    HashSet<string> _openDocuments;
    Dictionary<string, PendingOpenState> _pendingOpens;
    Dictionary<string, List<JsonElement>> _pendingChanges;
    readonly Lock _documentTrackingLock = new();
    Dictionary<string, string> _sourceGeneratedUriCache;
    readonly Dictionary<string, List<SourceGeneratedEntry>> _sourceGeneratedIndex = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _sourceGeneratedCacheLock = new();
    DateTime _sourceGeneratedIndexLastFullScan;
    bool _sourceGeneratedIndexHasFullScan;
    bool _sourceGeneratedIndexHasIncrementalUpdates;
    int _sourceGeneratedIndexRefreshInProgress;
    readonly Lock _workspaceReloadLock = new();
    readonly Lock _workspaceInitLock = new();
    CancellationTokenSource? _workspaceReloadCts;
    Task? _workspaceReloadTask;
    WorkDoneProgressScope? _workspaceInitProgress;
    Task? _workspaceInitTimeoutTask;
    bool _workspaceInitStarted;
    int _workspaceInitProgressTimeoutMs = DefaultWorkspaceInitProgressTimeoutMs;
    readonly Lock _roslynStartLock = new();
    Task<bool>? _roslynStartTask;
    long _workDoneProgressCounter;
    Func<string, JsonElement, CancellationToken, Task<JsonElement?>>? _forwardToRoslynOverride;
    Func<string, object?, Task>? _forwardToRoslynNotificationOverride;
    Func<string, object?, CancellationToken, Task<JsonElement?>>? _clientRequestOverride;
    Func<string, object?, Task>? _clientNotificationOverride;
    Func<CancellationToken, Task<bool>>? _startRoslynOverride;
    bool _clientInitialized;
    readonly Channel<NotificationWorkItem> _notificationChannel;
    readonly CancellationTokenSource _notificationCts;
    readonly Task _notificationTask;
    readonly CancellationTokenSource _lifetimeCts = new();
    static readonly TimeSpan DependencyProgressReportThrottle = TimeSpan.FromMilliseconds(250);
    long _droppedRoslynNotifications;
    long _roslynRequestTimeouts;
    long _sourceGeneratedCacheHits;
    long _sourceGeneratedCacheMisses;
    long _sourceGeneratedIndexRefreshes;
    long _sourceGeneratedIndexIncrementalUpdates;
    long _nextDroppedNotificationWarnAt = DroppedNotificationsWarnEvery;
    long _nextRoslynTimeoutWarnAt = RoslynTimeoutsWarnEvery;
    DateTime _lastTelemetryWarnUtc;
    readonly Lock _telemetryWarnLock = new();
    const int DefaultDiagnosticsProgressDelayMs = 250;
    const int WorkDoneProgressCreateTimeoutMs = 2000;
    const int MaxFastStartDelayMs = 60000;
    const int MaxPendingChangesPerDocument = 200;
    const int MaxContentChangesToApply = 50;
    const int WorkspaceReloadDebounceMs = 1000;
    const int DefaultUserRequestProgressDelayMs = 1000;
    const int DefaultWorkspaceInitProgressTimeoutMs = 60000;
    const long DroppedNotificationsWarnEvery = 1000;
    const long RoslynTimeoutsWarnEvery = 100;
    static readonly TimeSpan TelemetryWarnMinInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan AutoUpdateStartupDelay = TimeSpan.FromSeconds(5);
    const int FileWatchKindAll = 7;
    const string FileWatchRegistrationId = "razorsharp.didChangeWatchedFiles";
    const string OmniSharpConfigFileName = "omnisharp.json";
    const string DirectoryBuildPropsFileName = "Directory.Build.props";
    const string DirectoryBuildTargetsFileName = "Directory.Build.targets";
    const string DirectoryBuildRspFileName = "Directory.Build.rsp";
    const string DirectoryPackagesPropsFileName = "Directory.Packages.props";
    const string DirectoryPackagesTargetsFileName = "Directory.Packages.targets";
    const string NuGetConfigFileName = "NuGet.Config";
    const string NuGetConfigLowerFileName = "nuget.config";
    const string StyleCopConfigFileName = "stylecop.json";
    const string PackagesLockFileName = "packages.lock.json";
    const string GlobalJsonFileName = "global.json";
    const string SolutionFilterFileName = ".slnf";
    const string SolutionXmlFileName = ".slnx";
    static readonly TimeSpan DefaultRoslynRequestTimeout = TimeSpan.FromSeconds(10);
    int _diagnosticsProgressDelayMs = DefaultDiagnosticsProgressDelayMs;
    TimeSpan _roslynRequestTimeout = DefaultRoslynRequestTimeout;
    int _userRequestProgressDelayMs = DefaultUserRequestProgressDelayMs;
    static readonly TimeSpan SourceGeneratedIndexRefreshInterval = TimeSpan.FromSeconds(60);
    static readonly EnumerationOptions SourceGeneratedEnumerateOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    StringComparer _uriComparer = StringComparer.Ordinal;
    StringComparison _uriComparison = StringComparison.Ordinal;

    static readonly string Version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    static readonly int[] SymbolKindValues = Array.ConvertAll(Enum.GetValues<SymbolKind>(), v => (int)v);
    static readonly int[] CompletionItemKindValues = Array.ConvertAll(Enum.GetValues<CompletionItemKind>(), v => (int)v);

    // Cached JSON elements for common responses
    static readonly JsonElement EmptyArrayResponse = JsonSerializer.SerializeToElement(Array.Empty<object>());
    static readonly JsonElement DefaultFormattingOptions = JsonSerializer.SerializeToElement(new { tabSize = 4, insertSpaces = true });

    // Cached diagnostic response templates (static parts that don't change per-request)
    static readonly JsonElement DiagnosticResponseDisabled = JsonSerializer.SerializeToElement(new
    {
        kind = "full",
        resultId = "disabled",
        items = Array.Empty<object>()
    });
    static readonly JsonElement DiagnosticResponseNone = JsonSerializer.SerializeToElement(new
    {
        kind = "full",
        resultId = "none",
        items = Array.Empty<object>()
    });
    static readonly JsonElement SuccessResponse = JsonSerializer.SerializeToElement(new { success = true });

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
    static readonly string[] WorkspaceReloadExtensions =
    [
        ".sln",
        SolutionFilterFileName,
        SolutionXmlFileName,
        ".csproj",
        ".props",
        ".targets",
        ".globalconfig",
        ".ruleset",
        ".rsp"
    ];
    static readonly string[] WorkspaceReloadFileNames =
    [
        DirectoryBuildPropsFileName,
        DirectoryBuildTargetsFileName,
        DirectoryBuildRspFileName,
        DirectoryPackagesPropsFileName,
        DirectoryPackagesTargetsFileName,
        NuGetConfigFileName,
        NuGetConfigLowerFileName,
        StyleCopConfigFileName,
        PackagesLockFileName,
        GlobalJsonFileName
    ];

    readonly record struct NotificationWorkItem(string Method, JsonElement? Params);
    readonly record struct PendingOpenState(string Uri, string LanguageId, int Version, string Text);
    readonly record struct SourceGeneratedEntry(string Path, bool IsDebug, DateTime LastWriteUtc);
    internal enum MessageType
    {
        Error = 1,
        Warning = 2,
        Info = 3,
        Log = 4
    }

    public RazorLanguageServer(ILoggerFactory loggerFactory, DependencyManager dependencyManager)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RazorLanguageServer>();
        _dependencyManager = dependencyManager;
        _workspaceManager = new WorkspaceManager(loggerFactory.CreateLogger<WorkspaceManager>());
        _configurationLoader = new ConfigurationLoader(loggerFactory.CreateLogger<ConfigurationLoader>());
        _htmlClient = new HtmlLanguageClient(loggerFactory.CreateLogger<HtmlLanguageClient>());
        _notificationChannel = Channel.CreateBounded<NotificationWorkItem>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _notificationCts = new CancellationTokenSource();
        _notificationTask = Task.Run(() => ProcessNotificationsAsync(_notificationCts.Token));
        _openDocuments = new HashSet<string>(_uriComparer);
        _pendingOpens = new Dictionary<string, PendingOpenState>(_uriComparer);
        _pendingChanges = new Dictionary<string, List<JsonElement>>(_uriComparer);
        _sourceGeneratedUriCache = new Dictionary<string, string>(_uriComparer);
        _workspaceOpenedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the configuration loader for accessing omnisharp.json settings.
    /// </summary>
    public ConfigurationLoader ConfigurationLoader => _configurationLoader;

    private bool FastStartEnabled => _initOptions?.FastStart != false;
    private bool FileWatchingEnabled => _initOptions?.Workspace?.EnableFileWatching != false;
    private bool FileWatchingRegistrationEnabled => _initOptions?.Workspace?.EnableFileWatchingRegistration != false;
    private bool SupportsWorkDoneProgress => _initParams?.Capabilities?.Window?.WorkDoneProgress == true;
    private bool SupportsShowMessageRequest => _initParams?.Capabilities?.Window?.ShowMessage != null;
    private bool CanSendRoslynNotifications =>
        _forwardToRoslynNotificationOverride != null || _roslynClient?.IsRunning == true;

    internal void SetProgressRpcForTests(IProgressRpc progressRpc) => _progressRpc = progressRpc;
    internal void SetInitializeParamsForTests(InitializeParams? initParams) => _initParams = initParams;
    internal void SetForwardToRoslynOverrideForTests(Func<string, JsonElement, CancellationToken, Task<JsonElement?>>? overrideFunc)
        => _forwardToRoslynOverride = overrideFunc;
    internal void SetForwardToRoslynNotificationOverrideForTests(Func<string, object?, Task>? overrideFunc)
        => _forwardToRoslynNotificationOverride = overrideFunc;
    internal void SetClientRequestOverrideForTests(Func<string, object?, CancellationToken, Task<JsonElement?>>? overrideFunc)
        => _clientRequestOverride = overrideFunc;
    internal void SetClientNotificationOverrideForTests(Func<string, object?, Task>? overrideFunc)
        => _clientNotificationOverride = overrideFunc;
    internal void SetRoslynRequestTimeoutForTests(TimeSpan timeout)
        => _roslynRequestTimeout = timeout;
    internal void SetDiagnosticsProgressDelayForTests(int delayMs)
        => _diagnosticsProgressDelayMs = Math.Max(0, delayMs);
    internal void SetUserRequestProgressDelayForTests(int delayMs)
        => _userRequestProgressDelayMs = Math.Max(0, delayMs);
    internal void SetWorkspaceInitProgressTimeoutForTests(int timeoutMs)
        => _workspaceInitProgressTimeoutMs = Math.Max(0, timeoutMs);
    internal void ApplyAutoUpdateSettingsForTests(RoslynOptions? options) => ApplyAutoUpdateSettings(options);
    internal void ApplyDependencySettingsForTests(DependencyOptions? options) => ApplyDependencySettings(options);
    internal bool AutoUpdateEnabledForTests => _autoUpdateEnabled;
    internal bool ForceUpdateCheckForTests => _forceUpdateCheck;
    internal Task? GetDependencyDownloadTaskForTests() => _dependencyDownloadTask;
    internal void SetStartRoslynOverrideForTests(Func<CancellationToken, Task<bool>>? overrideFunc)
        => _startRoslynOverride = overrideFunc;
    internal void SetDependenciesMissingForTests(bool missing)
        => _dependenciesMissing = missing;
    internal void MarkClientInitializedForTests()
    {
        _clientInitialized = true;
    }
    internal Task HandleRoslynNotificationForTests(string method, JsonElement? @params, CancellationToken ct)
        => HandleRoslynNotificationAsync(new NotificationWorkItem(method, @params), ct);
    internal void HandleRoslynProcessExitedForTests(int exitCode)
        => OnRoslynProcessExited(this, exitCode);
    internal Task<bool> SendWorkDoneProgressBeginForTests(string token, string title, string? message, CancellationToken ct)
        => SendWorkDoneProgressBeginAsync(token, title, message, ct);
    internal Task NotifyRestartForTests(string message, MessageType type)
        => NotifyRestartAsync(message, type);
    internal Task StartBackgroundDependencyDownloadForTests()
    {
        StartBackgroundDependencyDownload();
        return _dependencyDownloadTask ?? Task.CompletedTask;
    }

    private int FastStartDelayMs
    {
        get
        {
            var delay = _initOptions?.FastStartDelayMs ?? 0;
            if (delay < 0) return 0;
            return delay > MaxFastStartDelayMs ? MaxFastStartDelayMs : delay;
        }
    }

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

    public void SetAutoUpdateEnabledFromCli(bool enabled)
    {
        _autoUpdateEnabledFromCli = enabled;
    }

    public void SetForceUpdateCheck(bool force)
    {
        _forceUpdateCheck = force;
    }

    internal void SetLoggingOptions(LoggingLevelSwitch levelSwitch, LogFileSwitch fileSwitch, bool logLevelSetByCli, bool logFileSetByCli)
    {
        _loggingLevelSwitch = levelSwitch;
        _logFileSwitch = fileSwitch;
        _logLevelSetByCli = logLevelSetByCli;
        _logFileSetByCli = logFileSetByCli;
    }

    public void SetSkipDependencyCheck(bool skip, bool setByCli)
    {
        _skipDependencyCheck = skip;
        _skipDependencyCheckSetByCli = setByCli;
    }

    private void ApplyRoslynTimeout(RoslynOptions? options)
    {
        if (options?.RequestTimeoutMs == null)
        {
            return;
        }

        var timeoutMs = options.RequestTimeoutMs.Value;
        if (timeoutMs <= 0)
        {
            _roslynRequestTimeout = Timeout.InfiniteTimeSpan;
            return;
        }

        if (timeoutMs < 100)
        {
            timeoutMs = 100;
        }

        _roslynRequestTimeout = TimeSpan.FromMilliseconds(timeoutMs);
    }

    private void ApplyLoggingSettings(LoggingOptions? options)
    {
        if (options == null)
        {
            return;
        }

        if (_loggingLevelSwitch != null && !_logLevelSetByCli && !string.IsNullOrWhiteSpace(options.Level))
        {
            if (Enum.TryParse<LogLevel>(options.Level, true, out var parsed))
            {
                _loggingLevelSwitch.MinimumLevel = parsed;
                _logLevel = parsed.ToString();
                _logger.LogInformation("Log level set to {Level} via initializationOptions", parsed);
            }
            else
            {
                _logger.LogWarning("Invalid log level '{Level}' in initializationOptions.logging.level", options.Level);
            }
        }

        if (_logFileSwitch != null && !_logFileSetByCli && !string.IsNullOrWhiteSpace(options.File))
        {
            _logFileSwitch.SetLogFile(options.File);
            _logger.LogInformation("Logging to file {Path} via initializationOptions", options.File);
        }
    }

    private void ApplyDependencySettings(DependencyOptions? options)
    {
        if (options?.SkipDependencyCheck == null)
        {
            // Still apply pinned versions even if skipDependencyCheck isn't specified.
            if (options != null)
            {
                ApplyPinnedDependencyVersions(options);
            }
            return;
        }

        if (_skipDependencyCheckSetByCli)
        {
            ApplyPinnedDependencyVersions(options);
            return;
        }

        _skipDependencyCheck = options.SkipDependencyCheck.Value;
        if (_skipDependencyCheck)
        {
            _logger.LogInformation("Dependency checks disabled by initializationOptions");
        }

        ApplyPinnedDependencyVersions(options);
    }

    private void ApplyPinnedDependencyVersions(DependencyOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PinnedRoslynVersion) &&
            string.IsNullOrWhiteSpace(options.PinnedExtensionVersion))
        {
            return;
        }

        _dependencyManager.ConfigurePinnedVersions(
            options.PinnedRoslynVersion,
            options.PinnedExtensionVersion);

        _logger.LogInformation(
            "Pinned dependency versions configured (Roslyn {RoslynVersion}, Extension {ExtensionVersion})",
            options.PinnedRoslynVersion ?? "latest",
            options.PinnedExtensionVersion ?? "latest");

        _autoUpdateEnabled = false;
        _forceUpdateCheck = false;
        _logger.LogInformation("Auto-update checks disabled because pinned versions are configured.");
    }

    private void ApplyAutoUpdateSettings(RoslynOptions? options)
    {
        var enabled = options?.AutoUpdate ?? true;
        _autoUpdateEnabled = _autoUpdateEnabledFromCli && enabled;

        var intervalHours = options?.AutoUpdateIntervalHours ?? 24;
        if (intervalHours < 0)
        {
            intervalHours = 0;
        }

        _autoUpdateInterval = TimeSpan.FromHours(intervalHours);

        if (!_autoUpdateEnabled)
        {
            _logger.LogInformation("Dependency auto-update disabled");
        }
    }

    private void ApplyRequestProgressSettings(InitializationOptions? options)
    {
        if (options?.RequestProgressDelayMs == null)
        {
            return;
        }

        var delayMs = options.RequestProgressDelayMs.Value;
        if (delayMs < 0)
        {
            _userRequestProgressDelayMs = -1;
            _logger.LogInformation("Request progress indicators disabled by initializationOptions");
            return;
        }

        if (delayMs > MaxFastStartDelayMs)
        {
            delayMs = MaxFastStartDelayMs;
        }

        _userRequestProgressDelayMs = delayMs;
        _logger.LogInformation("Request progress delay set to {DelayMs}ms via initializationOptions", delayMs);
    }

    /// <summary>
    /// Runs the language server, listening on stdin/stdout.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Razor Language Server...");

        // Dependency checks and auto-updates are handled after initialize
        // to avoid blocking the LSP handshake.

        // Set up JSON-RPC over stdin/stdout
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = JsonOptions
        };

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        var handler = new HeaderDelimitedMessageHandler(stdout, stdin, formatter);

        _clientRpc = new JsonRpc(handler);
        _progressRpc = new JsonRpcProgressClient(_clientRpc);

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
            workspaceRoot = TryGetLocalPath(@params.RootUri);
        }
        else if (@params?.WorkspaceFolders?.Length > 0)
        {
            workspaceRoot = TryGetLocalPath(@params.WorkspaceFolders[0].Uri);
        }

        _workspaceRoot = workspaceRoot;
        _configurationLoader.SetWorkspaceRoot(workspaceRoot);
        var workspaceProbePath = GetWorkspaceProbePath(@params);
        ConfigureUriComparers(workspaceProbePath);
        _workspaceManager.SetCaseSensitivity(workspaceProbePath);

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
                _workspaceManager.ConfigureExcludedDirectories(
                    initOptions?.Workspace?.ExcludeDirectoriesOverride,
                    initOptions?.Workspace?.ExcludeDirectories);
                ApplyLoggingSettings(initOptions?.Logging);
                ApplyDependencySettings(initOptions?.Dependencies);
                ApplyRoslynTimeout(initOptions?.Roslyn);
                ApplyAutoUpdateSettings(initOptions?.Roslyn);
                ApplyRequestProgressSettings(initOptions);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var triggerChars = initOptions?.Capabilities?.CompletionProvider?.TriggerCharacters;
                    var triggerText = triggerChars != null ? string.Join(",", triggerChars) : "default";
                    _logger.LogDebug("Parsed initializationOptions: html.enable={HtmlEnable}, capabilities.completionProvider.triggerCharacters={TriggerChars}",
                        initOptions?.Html?.Enable,
                        triggerText);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse initializationOptions - check for invalid or unknown properties");
            }
        }

        _htmlClient.Configure(_initOptions?.Html, @params?.RootUri);

        if (_initOptions == null)
        {
            ApplyAutoUpdateSettings(null);
        }

        if (!_skipDependencyCheck && !_dependencyManager.AreDependenciesComplete())
        {
            if (_autoUpdateEnabled)
            {
                _dependenciesMissing = true;
                _logger.LogInformation("Dependencies are missing; downloading in the background.");
                StartBackgroundDependencyDownload();
                _ = NotifyUserAsync("RazorSharp is downloading dependencies. Language features will start when ready.", MessageType.Info);
            }
            else
            {
                var message = "RazorSharp dependencies are not installed. Run with --download-dependencies first.";
                _logger.LogError(message);
                _ = NotifyUserAsync(message, MessageType.Error);
                throw new InvalidOperationException(message);
            }
        }

        if (!_dependenciesMissing)
        {
            if (!await EnsureRoslynStartedAsync(ct))
            {
                var message = "Failed to start Roslyn. Check logs for details.";
                _logger.LogError(message);
                _ = NotifyUserAsync(message, MessageType.Error);
                throw new InvalidOperationException(message);
            }
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
        _clientInitialized = true;

        if (FileWatchingEnabled && FileWatchingRegistrationEnabled)
        {
            _ = TryRegisterFileWatchersAsync();
        }
        else if (FileWatchingEnabled && !FileWatchingRegistrationEnabled)
        {
            _logger.LogInformation("Dynamic file watching registration disabled by initOptions; relying on client-side watchers.");
        }

        if (_workspaceOpenedAt == default)
        {
            _workspaceOpenedAt = DateTime.UtcNow;
        }

        if (_dependenciesMissing)
        {
            StartAutoUpdateCheck();
            return;
        }

        StartWorkspaceInitialization();

        StartAutoUpdateCheck();
    }

    private void StartBackgroundDependencyDownload()
    {
        lock (_dependencyDownloadLock)
        {
            if (_dependencyDownloadTask != null)
            {
                return;
            }

            _dependencyDownloadTask = Task.Run(async () =>
            {
                WorkDoneProgressScope? progress = null;
                Task reportChain = Task.CompletedTask;
                try
                {
                    progress = BeginWorkDoneProgress(
                        "razorsharp.dependencies",
                        "RazorSharp",
                        "Downloading dependencies",
                        delayMs: 0,
                        _lifetimeCts.Token);

                    var lastReport = DateTime.MinValue;
                    var reportLock = new Lock();
                    void Report(string message)
                    {
                        if (progress == null)
                        {
                            return;
                        }

                        var now = DateTime.UtcNow;
                        lock (reportLock)
                        {
                            if (now - lastReport < DependencyProgressReportThrottle)
                            {
                                return;
                            }
                            lastReport = now;

                            // Keep reports ordered/observable so fast downloads don't drop the last update.
                            reportChain = reportChain.ContinueWith(
                                _ => progress.ReportAsync(message),
                                CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default).Unwrap();
                        }
                    }

                    var success = await _dependencyManager.EnsureDependenciesAsync(_lifetimeCts.Token, Report);
                    if (success)
                    {
                        _dependenciesMissing = false;
                        _logger.LogInformation("Dependencies downloaded. Starting language services.");

                        var started = await StartRoslynAfterDownloadAsync();
                        if (started)
                        {
                            _logger.LogInformation("Roslyn started after dependency download.");
                            await NotifyUserAsync(
                                "RazorSharp finished downloading dependencies. Starting language services.",
                                MessageType.Info);
                        }
                        else
                        {
                            await NotifyRestartAsync(
                                "RazorSharp downloaded dependencies but couldn't start language services. Restart your editor.",
                                MessageType.Warning);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Failed to download dependencies.");
                        await NotifyUserAsync(
                            "RazorSharp failed to download dependencies. Check logs for details.",
                            MessageType.Error);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutdown requested.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background dependency download failed");
                    await NotifyUserAsync(
                        "RazorSharp failed to download dependencies. Check logs for details.",
                        MessageType.Error);
                }
                finally
                {
                    if (progress != null)
                    {
                        try
                        {
                            await reportChain;
                        }
                        catch
                        {
                            // Ignore progress reporting failures.
                        }

                        await progress.DisposeAsync();
                    }
                }
            });
        }
    }

    private async Task<bool> StartRoslynAfterDownloadAsync()
    {
        if (_initParams == null)
        {
            _logger.LogWarning("Cannot start Roslyn after download: initialize parameters are missing.");
            return false;
        }

        var started = await EnsureRoslynStartedAsync(_lifetimeCts.Token);
        if (!started)
        {
            return false;
        }

        if (_clientInitialized)
        {
            StartWorkspaceInitialization();
        }

        return true;
    }

    private async Task<bool> EnsureRoslynStartedAsync(CancellationToken ct)
    {
        Task<bool> startTask;
        lock (_roslynStartLock)
        {
            if (_roslynStartTask != null)
            {
                startTask = _roslynStartTask;
            }
            else
            {
                startTask = StartRoslynAsync(ct);
                _roslynStartTask = startTask;
            }
        }

        bool result;
        try
        {
            result = await startTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Roslyn start failed");
            result = false;
        }

        if (!result)
        {
            lock (_roslynStartLock)
            {
                if (_roslynStartTask == startTask)
                {
                    _roslynStartTask = null;
                }
            }
        }

        return result;
    }

    private async Task<bool> StartRoslynAsync(CancellationToken ct)
    {
        if (_startRoslynOverride != null)
        {
            return await _startRoslynOverride(ct);
        }

        if (_roslynClient != null && _roslynClient.IsRunning)
        {
            return true;
        }

        if (_initParams == null)
        {
            _logger.LogWarning("Cannot start Roslyn: initialize parameters are missing.");
            return false;
        }

        try
        {
            // Start Roslyn
            _roslynClient = new RoslynClient(_loggerFactory.CreateLogger<RoslynClient>());
            _roslynClient.SetConfigurationLoader(_configurationLoader);

            // Wire up Roslyn events before starting to avoid missing early notifications
            _roslynClient.NotificationReceived += OnRoslynNotification;
            _roslynClient.RequestReceived += OnRoslynRequestAsync;
            _roslynClient.ProcessExited += OnRoslynProcessExited;

            var roslynOptions = RoslynClient.CreateStartOptions(_dependencyManager,
                Path.Combine(_dependencyManager.BasePath, "logs"), _logLevel);

            await _roslynClient.StartAsync(roslynOptions, ct);

            // Forward initialize to Roslyn with cohosting enabled
            var roslynInitParams = CreateRoslynInitParams(_initParams);
            var roslynResult = await _roslynClient.SendRequestAsync<object, JsonElement>(
                LspMethods.Initialize, roslynInitParams, ct);

            _logger.LogInformation("Roslyn initialized");
            if (_logger.IsEnabled(LogLevel.Trace) && roslynResult.TryGetProperty("capabilities", out var caps))
            {
                _logger.LogTrace("Roslyn capabilities: {Caps}", caps.GetRawText());
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Roslyn");
            if (_roslynClient != null)
            {
                try
                {
                    await _roslynClient.DisposeAsync();
                }
                catch
                {
                    // Ignore cleanup failures
                }
                _roslynClient = null;
            }
            return false;
        }
    }

    private void StartWorkspaceInitialization()
    {
        if (_dependenciesMissing)
        {
            return;
        }

        lock (_workspaceInitLock)
        {
            if (_workspaceInitStarted)
            {
                return;
            }

            _workspaceInitStarted = true;
        }

        _workspaceInitProgress ??= BeginWorkDoneProgress(
            "razorsharp.init",
            "RazorSharp",
            "Initializing workspace",
            delayMs: 0,
            CancellationToken.None);

        StartWorkspaceInitializationTimeout();

        // Forward to Roslyn - run in background but track completion
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await EnsureRoslynStartedAsync(_lifetimeCts.Token))
                {
                    _logger.LogWarning("Roslyn failed to start; workspace initialization skipped.");
                    lock (_workspaceInitLock)
                    {
                        _workspaceInitStarted = false;
                    }
                    await EndWorkspaceInitializationProgressAsync();
                    return;
                }

                if (_roslynClient != null)
                {
                    await SendRoslynNotificationAsync(LspMethods.Initialized, null);
                    _logger.LogDebug("Roslyn received initialized notification");

                    // Open solution or projects FIRST - before documents
                    // Documents opened before project load won't have proper context
                    // Priority: CLI path > rootUri > workspaceFolders
                    _logger.LogDebug("Workspace discovery: cliPath={CliPath}, rootUri={RootUri}, workspaceFolders={WorkspaceFolders}",
                        _cliSolutionPath,
                        _initParams?.RootUri,
                        _initParams?.WorkspaceFolders?.Length ?? 0);
                    string? openTarget = null;
                    if (_cliSolutionPath != null)
                    {
                        openTarget = _cliSolutionPath;
                    }
                    else if (_initParams?.RootUri != null)
                    {
                        openTarget = _initParams.RootUri;
                    }
                    else if (_initParams?.WorkspaceFolders?.Length > 0)
                    {
                        openTarget = _initParams.WorkspaceFolders[0].Uri;
                    }

                    if (openTarget != null)
                    {
                        _workspaceOpenTarget = openTarget;
                        await OpenWorkspaceAsync(openTarget);
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
                lock (_workspaceInitLock)
                {
                    _workspaceInitStarted = false;
                }
                await EndWorkspaceInitializationProgressAsync();
            }
        });
    }

    private void StartWorkspaceInitializationTimeout()
    {
        if (_workspaceInitProgress == null)
        {
            return;
        }

        lock (_workspaceInitLock)
        {
            if (_workspaceInitTimeoutTask != null)
            {
                return;
            }

            var timeout = TimeSpan.FromMilliseconds(_workspaceInitProgressTimeoutMs);
            _workspaceInitTimeoutTask = Task.Run(async () =>
            {
                try
                {
                    var completed = await Task.WhenAny(
                        _roslynProjectInitialized.Task,
                        Task.Delay(timeout, _lifetimeCts.Token));

                    if (completed != _roslynProjectInitialized.Task && !_roslynProjectInitialized.Task.IsCompleted)
                    {
                        _logger.LogWarning(
                            "Workspace initialization did not complete within {TimeoutSeconds}s; stopping progress indicator.",
                            timeout.TotalSeconds);
                        await EndWorkspaceInitializationProgressAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutdown requested.
                }
                finally
                {
                    lock (_workspaceInitLock)
                    {
                        _workspaceInitTimeoutTask = null;
                    }
                }
            });
        }
    }

    private void StartAutoUpdateCheck()
    {
        if (_dependencyManager.HasPinnedVersions)
        {
            return;
        }

        if (!_autoUpdateEnabled && !_forceUpdateCheck)
        {
            return;
        }

        if (!_dependencyManager.AreDependenciesComplete())
        {
            return;
        }

        lock (_autoUpdateLock)
        {
            if (_autoUpdateTask != null)
            {
                return;
            }

            var delay = _forceUpdateCheck ? TimeSpan.Zero : AutoUpdateStartupDelay;
            _autoUpdateTask = Task.Run(() => RunAutoUpdateCheckAsync(delay, _forceUpdateCheck, _lifetimeCts.Token));
        }
    }

    private async Task RunAutoUpdateCheckAsync(TimeSpan delay, bool force, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            var interval = force ? TimeSpan.Zero : _autoUpdateInterval;
            var result = await _dependencyManager.CheckForUpdatesAsync(interval, ct);
            if (result.Status is DependencyUpdateStatus.UpdateDownloaded or DependencyUpdateStatus.UpdateAlreadyPending)
            {
                var versionLabel = result.TargetVersion ?? "a newer version";
                _logger.LogInformation("Dependency update {Version} is ready; restart to use it.", versionLabel);
                await NotifyRestartAsync(
                    $"RazorSharp downloaded dependency update {versionLabel}. Restart your editor to use it.",
                    MessageType.Info);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-update check failed");
        }
    }

    private async Task NotifyUserAsync(string message, MessageType type)
    {
        if (_clientNotificationOverride != null)
        {
            await _clientNotificationOverride(LspMethods.WindowShowMessage, new
            {
                type = (int)type,
                message
            });
            return;
        }

        if (_clientRpc == null)
        {
            return;
        }

        try
        {
            await _clientRpc.NotifyWithParameterObjectAsync(LspMethods.WindowShowMessage, new
            {
                type = (int)type,
                message
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send window/showMessage");
        }
    }

    private async Task NotifyRestartAsync(string message, MessageType type)
    {
        if (!SupportsShowMessageRequest || (_clientRpc == null && _clientRequestOverride == null))
        {
            await NotifyUserAsync(message, type);
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var payload = new
            {
                type = (int)type,
                message,
                actions = new[]
                {
                    new { title = "Restart" },
                    new { title = "Dismiss" }
                }
            };

            JsonElement? response;
            if (_clientRequestOverride != null)
            {
                response = await _clientRequestOverride(LspMethods.WindowShowMessageRequest, payload, cts.Token);
            }
            else if (_clientRpc != null)
            {
                response = await _clientRpc.InvokeWithParameterObjectAsync<JsonElement?>(
                    LspMethods.WindowShowMessageRequest,
                    payload,
                    cts.Token);
            }
            else
            {
                await NotifyUserAsync(message, type);
                return;
            }

            if (response.HasValue &&
                response.Value.ValueKind == JsonValueKind.Object &&
                response.Value.TryGetProperty("title", out var title) &&
                string.Equals(title.GetString(), "Restart", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("User requested restart; please restart the editor to apply updates.");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("ShowMessageRequest timed out");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send window/showMessageRequest");
        }
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

        LogTelemetrySummary();

        // Dispose the JSON-RPC handler so RunAsync completes and Program can run DisposeAsync.
        try
        {
            _clientRpc?.Dispose();
        }
        catch
        {
            // Ignore shutdown errors during exit
        }
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
        var openState = new PendingOpenState(
            uri,
            languageId,
            @params.TextDocument.Version,
            @params.TextDocument.Text ?? string.Empty);

        // If Roslyn isn't available yet, queue opens for replay later.
        if (!CanSendRoslynNotifications || !_roslynProjectInitialized.Task.IsCompleted)
        {
            lock (_documentTrackingLock)
            {
                _pendingOpens[uri] = openState;
            }
            _logger.LogDebug("Buffering didOpen for {Uri} until Roslyn is ready", uri);
            return;
        }

        if (_roslynProjectInitialized.Task.IsFaulted || _roslynProjectInitialized.Task.IsCanceled)
        {
            _logger.LogWarning("Project initialization failed; cannot forward didOpen for {Uri}", uri);
            return;
        }

        _logger.LogDebug("Forwarding didOpen to Roslyn for {Uri}", uri);
        await SendDidOpenAndReplayAsync(openState);
    }

    private static bool IsRazorUri(string uri)
    {
        return uri.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || uri.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCSharpUri(string uri)
    {
        return uri.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryUpdatePendingOpenText(PendingOpenState pendingOpen, JsonElement paramsJson, out PendingOpenState updated)
    {
        updated = pendingOpen;
        if (!paramsJson.TryGetProperty("contentChanges", out var contentChanges) ||
            contentChanges.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        if (!TryApplyContentChanges(pendingOpen.Text, contentChanges, out var newText))
        {
            return false;
        }

        var version = pendingOpen.Version;
        if (paramsJson.TryGetProperty("textDocument", out var td) &&
            td.TryGetProperty("version", out var v) &&
            v.TryGetInt32(out var parsedVersion))
        {
            version = parsedVersion;
        }

        updated = pendingOpen with { Text = newText, Version = version };
        return true;
    }

    private static bool TryApplyContentChanges(string originalText, JsonElement changes, out string updatedText)
    {
        updatedText = originalText;

        if (changes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        if (changes.GetArrayLength() > MaxContentChangesToApply)
        {
            var last = changes[changes.GetArrayLength() - 1];
            if (last.TryGetProperty("range", out var lastRange) && lastRange.ValueKind != JsonValueKind.Null)
            {
                return false;
            }

            if (!last.TryGetProperty("text", out var lastText))
            {
                return false;
            }

            updatedText = lastText.GetString() ?? string.Empty;
            return true;
        }

        List<int>? lineIndex = null;
        foreach (var change in changes.EnumerateArray())
        {
            if (!change.TryGetProperty("text", out var textProp))
            {
                return false;
            }

            var newText = textProp.GetString() ?? string.Empty;

            if (!change.TryGetProperty("range", out var rangeProp) || rangeProp.ValueKind == JsonValueKind.Null)
            {
                updatedText = newText;
                continue;
            }

            if (!TryGetRange(rangeProp, out var startLine, out var startCharacter, out var endLine, out var endCharacter))
            {
                return false;
            }

            lineIndex ??= BuildLineStartIndex(updatedText);
            if (!TryGetOffset(updatedText, lineIndex, startLine, startCharacter, out var startOffset) ||
                !TryGetOffset(updatedText, lineIndex, endLine, endCharacter, out var endOffset))
            {
                return false;
            }

            if (startOffset > endOffset || startOffset < 0 || endOffset > updatedText.Length)
            {
                return false;
            }

            updatedText = ApplyTextChange(updatedText, startOffset, endOffset, newText);
            lineIndex = null;
        }

        return true;
    }

    private static string ApplyTextChange(string text, int startOffset, int endOffset, string newText)
    {
        var newLength = text.Length - (endOffset - startOffset) + newText.Length;
        return string.Create(newLength, (text, startOffset, endOffset, newText),
            static (span, state) =>
            {
                var dest = span;
                var prefix = state.text.AsSpan(0, state.startOffset);
                prefix.CopyTo(dest);
                dest = dest[prefix.Length..];

                var insert = state.newText.AsSpan();
                insert.CopyTo(dest);
                dest = dest[insert.Length..];

                state.text.AsSpan(state.endOffset).CopyTo(dest);
            });
    }

    private static bool TryGetRange(JsonElement range, out int startLine, out int startCharacter, out int endLine, out int endCharacter)
    {
        startLine = startCharacter = endLine = endCharacter = 0;

        if (!range.TryGetProperty("start", out var start) ||
            !range.TryGetProperty("end", out var end))
        {
            return false;
        }

        return TryGetPosition(start, out startLine, out startCharacter)
            && TryGetPosition(end, out endLine, out endCharacter);
    }

    private static bool TryGetPosition(JsonElement position, out int line, out int character)
    {
        line = character = 0;
        if (!position.TryGetProperty("line", out var lineProp) ||
            !position.TryGetProperty("character", out var charProp))
        {
            return false;
        }

        return lineProp.TryGetInt32(out line) && charProp.TryGetInt32(out character);
    }

    private static bool TryGetOffset(string text, int line, int character, out int offset)
    {
        offset = 0;

        if (line < 0 || character < 0)
        {
            return false;
        }

        var lineStart = 0;
        var currentLine = 0;
        while (currentLine < line)
        {
            var nextLine = text.IndexOf('\n', lineStart);
            if (nextLine < 0)
            {
                return false;
            }

            lineStart = nextLine + 1;
            currentLine++;
        }

        var lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
        {
            lineEnd--;
        }

        var lineLength = lineEnd - lineStart;
        if (character > lineLength)
        {
            return false;
        }

        offset = lineStart + character;
        return true;
    }

    private static List<int> BuildLineStartIndex(string text)
    {
        var lineStarts = new List<int> { 0 };
        var index = 0;
        while (index < text.Length)
        {
            var newline = text.IndexOf('\n', index);
            if (newline < 0)
            {
                break;
            }

            var nextStart = newline + 1;
            if (nextStart < text.Length)
            {
                lineStarts.Add(nextStart);
            }

            index = nextStart;
        }

        return lineStarts;
    }

    private static bool TryGetOffset(string text, List<int> lineStarts, int line, int character, out int offset)
    {
        offset = 0;

        if (line < 0 || character < 0)
        {
            return false;
        }

        if (line >= lineStarts.Count)
        {
            return false;
        }

        var lineStart = lineStarts[line];
        var lineEnd = line + 1 < lineStarts.Count ? lineStarts[line + 1] - 1 : text.Length;
        if (lineEnd > lineStart && lineEnd <= text.Length && text[lineEnd - 1] == '\r')
        {
            lineEnd--;
        }

        var lineLength = lineEnd - lineStart;
        if (character > lineLength)
        {
            return false;
        }

        offset = lineStart + character;
        return true;
    }

    [JsonRpcMethod(LspMethods.TextDocumentDidChange, UseSingleObjectParameterDeserialization = true)]
    public async Task HandleDidChangeAsync(JsonElement paramsJson)
    {
        var uri = paramsJson.TryGetProperty("textDocument", out var td) && td.TryGetProperty("uri", out var u)
            ? u.GetString() : null;

        if (uri == null)
        {
            return;
        }

        // Check document state and buffer if needed atomically
        // to prevent race conditions with HandleDidOpenAsync
        bool isOpen;
        lock (_documentTrackingLock)
        {
            isOpen = _openDocuments.Contains(uri);
            if (!isOpen)
            {
                if (_pendingOpens.TryGetValue(uri, out var pendingOpen) &&
                    TryUpdatePendingOpenText(pendingOpen, paramsJson, out var updatedOpen))
                {
                    _pendingOpens[uri] = updatedOpen;
                    _pendingChanges.Remove(uri);
                    _logger.LogDebug("Coalesced didChange into pending didOpen for {Uri}", uri);
                }
                else
                {
                    // Buffer the change to replay after didOpen is forwarded
                    _logger.LogDebug("Buffering didChange for {Uri} - document not yet open in Roslyn", uri);
                    if (!_pendingChanges.TryGetValue(uri, out var changes))
                    {
                        changes = new List<JsonElement>();
                        _pendingChanges[uri] = changes;
                    }
                    changes.Add(paramsJson.Clone());

                    if (changes.Count > MaxPendingChangesPerDocument)
                    {
                        _logger.LogWarning(
                            "Too many buffered changes for {Uri}; keeping latest change only.",
                            uri);
                        var latest = changes[^1];
                        changes.Clear();
                        changes.Add(latest);
                    }
                }
            }
        }

        if (!CanSendRoslynNotifications)
        {
            return;
        }

        if (isOpen)
        {
            await SendRoslynNotificationAsync(LspMethods.TextDocumentDidChange, paramsJson);
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
            _pendingOpens.Remove(uri);
            _pendingChanges.Remove(uri);
        }

        if (CanSendRoslynNotifications)
        {
            await SendRoslynNotificationAsync(LspMethods.TextDocumentDidClose, paramsJson);
        }
    }

    [JsonRpcMethod(LspMethods.TextDocumentDidSave, UseSingleObjectParameterDeserialization = true)]
    public async Task HandleDidSaveAsync(JsonElement paramsJson)
    {
        // Forward to Roslyn
        if (CanSendRoslynNotifications)
        {
            await SendRoslynNotificationAsync(LspMethods.TextDocumentDidSave, paramsJson);
        }
    }

    #endregion

    #region Workspace Notifications

    [JsonRpcMethod(LspMethods.WorkspaceDidChangeWatchedFiles, UseSingleObjectParameterDeserialization = true)]
    public async Task HandleDidChangeWatchedFilesAsync(JsonElement paramsJson)
    {
        if (!FileWatchingEnabled)
        {
            _logger.LogDebug("Ignoring workspace/didChangeWatchedFiles (disabled by initOptions).");
            return;
        }

        var @params = JsonSerializer.Deserialize<DidChangeWatchedFilesParams>(paramsJson, JsonOptions);
        if (@params?.Changes == null || @params.Changes.Length == 0)
        {
            return;
        }

        // Forward to Roslyn first so it can react immediately.
        if (CanSendRoslynNotifications)
        {
            await SendRoslynNotificationAsync(LspMethods.WorkspaceDidChangeWatchedFiles, paramsJson);
        }

        var workspaceRoot = _workspaceRoot;
        var localConfigPath = workspaceRoot != null
            ? TryGetFullPath(Path.Combine(workspaceRoot, OmniSharpConfigFileName))
            : null;
        var globalConfigPath = TryGetGlobalOmniSharpConfigPath();

        var configChanged = false;
        var sourceGeneratedFullRefreshNeeded = false;
        var sourceGeneratedIncrementalApplied = false;
        var workspaceReloadNeeded = false;

        foreach (var change in @params.Changes)
        {
            var localPath = TryGetLocalPath(change.Uri);
            if (localPath == null)
            {
                continue;
            }

            if (!configChanged && IsOmniSharpConfigPath(localPath, localConfigPath, globalConfigPath))
            {
                configChanged = true;
            }

            if (IsSourceGeneratedPath(localPath))
            {
                if (TryUpdateSourceGeneratedIndexForChange(localPath, change.Type))
                {
                    sourceGeneratedIncrementalApplied = true;
                }
                else
                {
                    sourceGeneratedFullRefreshNeeded = true;
                }
            }

            if (!workspaceReloadNeeded && IsWorkspaceReloadTriggerPath(localPath))
            {
                workspaceReloadNeeded = true;
            }
        }

        if (configChanged)
        {
            _logger.LogInformation("omnisharp.json changed; reloading configuration");
            _configurationLoader.Reload();
            if (CanSendRoslynNotifications)
            {
                await SendRoslynNotificationAsync(LspMethods.WorkspaceDidChangeConfiguration, new { settings = new { } });
            }
        }

        if (sourceGeneratedFullRefreshNeeded)
        {
            _logger.LogDebug("Source-generated files changed; refreshing index");
            RefreshSourceGeneratedIndex();
        }
        else if (sourceGeneratedIncrementalApplied)
        {
            _logger.LogDebug("Source-generated files changed; updated index incrementally");
        }

        if (workspaceReloadNeeded)
        {
            ScheduleWorkspaceReload();
        }
    }

    #endregion

    #region Language Feature Handlers

    [JsonRpcMethod(LspMethods.TextDocumentCompletion, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleCompletionAsync(JsonElement @params, CancellationToken ct)
    {
        _logger.LogDebug("Completion request received");
        return await ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentCompletion, @params, "Completion", ct);
    }

    [JsonRpcMethod(LspMethods.CompletionItemResolve, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleCompletionResolveAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynWithProgressAsync(LspMethods.CompletionItemResolve, @params, "Completion resolve", ct);

    [JsonRpcMethod(LspMethods.TextDocumentHover, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleHoverAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentHover, @params, "Hover", ct);

    [JsonRpcMethod(LspMethods.TextDocumentDefinition, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleDefinitionAsync(JsonElement @params, CancellationToken ct)
    {
        var result = await ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentDefinition, @params, "Definition", ct);
        return result.HasValue ? TransformSourceGeneratedUris(result.Value) : result;
    }

    [JsonRpcMethod(LspMethods.TextDocumentReferences, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleReferencesAsync(JsonElement @params, CancellationToken ct)
    {
        var result = await ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentReferences, @params, "References", ct);
        return result.HasValue ? TransformSourceGeneratedUris(result.Value) : result;
    }

    [JsonRpcMethod(LspMethods.TextDocumentImplementation, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleImplementationAsync(JsonElement @params, CancellationToken ct)
    {
        var result = await ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentImplementation, @params, "Implementation", ct);
        return result.HasValue ? TransformSourceGeneratedUris(result.Value) : result;
    }

    [JsonRpcMethod(LspMethods.TextDocumentDocumentHighlight, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleDocumentHighlightAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentDocumentHighlight, @params, "Document highlights", ct);

    [JsonRpcMethod(LspMethods.TextDocumentDocumentSymbol, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleDocumentSymbolAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentDocumentSymbol, @params, "Document symbols", ct);

    [JsonRpcMethod(LspMethods.TextDocumentCodeAction, UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> HandleCodeActionAsync(JsonElement @params, CancellationToken ct)
    {
        _logger.LogDebug("Forwarding textDocument/codeAction to Roslyn");
        var result = await ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentCodeAction, @params, "Code actions", ct);

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

        var expandedActions = ListPool<JsonElement>.Rent(originalCount);
        try
        {
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
        finally
        {
            ListPool<JsonElement>.Return(expandedActions);
        }
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

        var result = await ForwardToRoslynWithProgressAsync(LspMethods.CodeActionResolve, @params, "Resolve code action", ct);

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
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Formatting request params: {Params}", @params.GetRawText());
        }
        var result = await ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentFormatting, @params, "Formatting", ct);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Formatting response: {Result}", result?.GetRawText() ?? "null");
        }
        return result;
    }

    [JsonRpcMethod(LspMethods.TextDocumentRangeFormatting, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleRangeFormattingAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentRangeFormatting, @params, "Range formatting", ct);

    [JsonRpcMethod(LspMethods.TextDocumentOnTypeFormatting, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleOnTypeFormattingAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynAsync(LspMethods.TextDocumentOnTypeFormatting, @params, ct);

    [JsonRpcMethod(LspMethods.TextDocumentRename, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleRenameAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentRename, @params, "Rename", ct);

    [JsonRpcMethod(LspMethods.TextDocumentPrepareRename, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandlePrepareRenameAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentPrepareRename, @params, "Prepare rename", ct);

    [JsonRpcMethod(LspMethods.TextDocumentSignatureHelp, UseSingleObjectParameterDeserialization = true)]
    public Task<JsonElement?> HandleSignatureHelpAsync(JsonElement @params, CancellationToken ct)
        => ForwardToRoslynWithProgressAsync(LspMethods.TextDocumentSignatureHelp, @params, "Signature help", ct);

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
        => ForwardToRoslynWithProgressAsync(LspMethods.WorkspaceSymbol, @params, "Workspace symbols", ct);

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
                    if (_clientRpc == null)
                    {
                        _logger.LogWarning("nestedCodeAction: Client RPC not available to apply edit");
                        return null;
                    }

                    // Apply the workspace edit via the client
                    _logger.LogDebug("nestedCodeAction: Applying workspace edit");
                    await _clientRpc.InvokeWithParameterObjectAsync<JsonElement?>(
                        LspMethods.WorkspaceApplyEdit,
                        new { edit },
                        ct);
                    return SuccessResponse;
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
                    if (_clientRpc == null)
                    {
                        _logger.LogWarning("fixAllCodeAction: Client RPC not available to apply edit");
                        return null;
                    }

                    _logger.LogDebug("fixAllCodeAction: Applying workspace edit");
                    await _clientRpc.InvokeWithParameterObjectAsync<JsonElement?>(
                        LspMethods.WorkspaceApplyEdit,
                        new { edit },
                        ct);
                    return SuccessResponse;
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
            return DiagnosticResponseDisabled;
        }

        if (!_roslynProjectInitialized.Task.IsCompletedSuccessfully)
        {
            _logger.LogDebug("Diagnostics requested before project initialization completes; returning empty report.");
            return DiagnosticResponseNone;
        }

        WorkDoneProgressScope? progress = null;
        try
        {
            progress = BeginWorkDoneProgress(
                "razorsharp.diagnostics",
                "RazorSharp",
                "Diagnostics",
                _diagnosticsProgressDelayMs,
                ct);

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

                var tasks = ListPool<Task<JsonElement?>>.Rent(4);

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
                    ListPool<Task<JsonElement?>>.Return(tasks);
                    return DiagnosticResponseNone;
                }

                _logger.LogDebug("Requesting {Count} diagnostic categories for {Uri}", tasks.Count, uri);

                // Request all categories in parallel
                var results = await Task.WhenAll(tasks);
                ListPool<Task<JsonElement?>>.Return(tasks);

                Dictionary<string, JsonElement>? relatedDocuments = null;
                foreach (var result in results)
                {
                    if (result.HasValue &&
                        result.Value.TryGetProperty("relatedDocuments", out var related) &&
                        related.ValueKind == JsonValueKind.Object)
                    {
                        relatedDocuments ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                        foreach (var property in related.EnumerateObject())
                        {
                            relatedDocuments[property.Name] = property.Value.Clone();
                        }
                    }
                }

                var bufferWriter = new ArrayPoolBufferWriter();
                try
                {
                    using var writer = new Utf8JsonWriter(bufferWriter);
                    writer.WriteStartObject();
                    writer.WriteString("kind", "full");
                    writer.WriteString("resultId", $"merged:{DateTime.UtcNow.Ticks}");
                    writer.WritePropertyName("items");
                    writer.WriteStartArray();

                    var mergedCount = 0;
                    foreach (var result in results)
                    {
                        if (result.HasValue && result.Value.TryGetProperty("items", out var items))
                        {
                            foreach (var item in items.EnumerateArray())
                            {
                                item.WriteTo(writer);
                                mergedCount++;
                            }
                        }
                    }

                    writer.WriteEndArray();

                    if (relatedDocuments != null && relatedDocuments.Count > 0)
                    {
                        writer.WritePropertyName("relatedDocuments");
                        writer.WriteStartObject();
                        foreach (var kvp in relatedDocuments)
                        {
                            writer.WritePropertyName(kvp.Key);
                            kvp.Value.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                    writer.Flush();

                    _logger.LogDebug("Merged {Count} diagnostics for {Uri}", mergedCount, uri);

                    using var doc = JsonDocument.Parse(bufferWriter.WrittenMemory);
                    return doc.RootElement.Clone();
                }
                finally
                {
                    bufferWriter.Dispose();
                }
            }

            // For non-C# files, forward as-is
            _logger.LogDebug("Forwarding textDocument/diagnostic to Roslyn for {Uri}", uri);
            var forwardedResult = await ForwardToRoslynAsync(LspMethods.TextDocumentDiagnostic, @params, ct);

            // Return empty report if forwarding failed (e.g., project not initialized)
            if (!forwardedResult.HasValue)
            {
                return DiagnosticResponseNone;
            }

            return forwardedResult;
        }
        finally
        {
            if (progress != null)
            {
                await progress.DisposeAsync();
            }
        }
    }

    #endregion

    #region Roslyn Event Handlers

    private void OnRoslynProcessExited(object? sender, int exitCode)
    {
        _logger.LogError("Roslyn process exited unexpectedly with code {ExitCode}. Language features will be unavailable.", exitCode);
        // Mark initialization as failed so new requests don't wait forever
        _roslynProjectInitialized.TrySetException(new Exception($"Roslyn process exited with code {exitCode}"));
        _ = EndWorkspaceInitializationProgressAsync();

        lock (_documentTrackingLock)
        {
            _pendingOpens.Clear();
            _pendingChanges.Clear();
            _openDocuments.Clear();
        }
    }

    private async Task ProcessNotificationsAsync(CancellationToken ct)
    {
        try
        {
            while (await _notificationChannel.Reader.WaitToReadAsync(ct))
            {
                while (_notificationChannel.Reader.TryRead(out var item))
                {
                    await HandleRoslynNotificationAsync(item, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Roslyn notifications");
        }
    }

    private async Task HandleRoslynNotificationAsync(NotificationWorkItem item, CancellationToken ct)
    {
        try
        {
            switch (item.Method)
            {
                case LspMethods.RazorLog:
                    HandleRazorLog(item.Params);
                    break;

                case LspMethods.TextDocumentPublishDiagnostics:
                    if (!_roslynProjectInitialized.Task.IsCompletedSuccessfully)
                    {
                        _logger.LogDebug("Skipping diagnostics publish before project initialization completes");
                        break;
                    }
                    // Forward diagnostics to client
                    if (item.Params.HasValue)
                    {
                        var uri = item.Params.Value.TryGetProperty("uri", out var uriProp) ? uriProp.GetString() : "unknown";
                        var diagCount = item.Params.Value.TryGetProperty("diagnostics", out var diags) && diags.ValueKind == JsonValueKind.Array
                            ? diags.GetArrayLength()
                            : 0;
                        _logger.LogInformation("Publishing {Count} diagnostics for {Uri}", diagCount, uri);
                    }
                    await ForwardNotificationToClientAsync(item.Method, item.Params);
                    break;

                case LspMethods.ProjectInitializationComplete:
                    _logger.LogInformation("Roslyn project initialization complete - server ready");
                    _roslynProjectInitialized.TrySetResult();
                    await EndWorkspaceInitializationProgressAsync();
                    await FlushPendingOpensAsync();
                    break;

                case "window/logMessage":
                    if (item.Params.HasValue)
                    {
                        var message = item.Params.Value.TryGetProperty("message", out var msgProp)
                            ? msgProp.GetString()
                            : null;
                        var type = item.Params.Value.TryGetProperty("type", out var typeProp)
                            ? typeProp.GetInt32()
                            : 0;

                        // Roslyn uses this channel for extremely noisy tracing (including assembly-load probing).
                        // Never forward those to the client (they should go to logs instead).
                        if (type > 4 ||
                            (message != null && message.Contains("not found in this load context", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (message != null)
                            {
                                _logger.LogTrace("[Roslyn] {Message}", message);
                            }
                            break;
                        }

                        if (message != null)
                        {
                            _logger.LogDebug("[Roslyn] {Message}", message);
                        }

                        var clampedType = Math.Clamp(type, 1, 4);
                        var forwardedParams = JsonSerializer.SerializeToElement(new { type = clampedType, message });
                        await ForwardNotificationToClientAsync(item.Method, forwardedParams);
                    }
                    else
                    {
                        await ForwardNotificationToClientAsync(item.Method, item.Params);
                    }
                    break;


                case "window/showMessage":
                    // Forward window messages to client
                    await ForwardNotificationToClientAsync(item.Method, item.Params);
                    break;

                case LspMethods.Progress:
                    var progressToken = TryGetProgressToken(item.Params);
                    var progressKind = TryGetProgressKind(item.Params);
                    _logger.LogDebug("Forwarding progress to client: token={Token}, kind={Kind}",
                        progressToken ?? "<none>",
                        progressKind ?? "<none>");
                    // Forward progress notifications to client for status bar spinners
                    await ForwardNotificationToClientAsync(item.Method, item.Params);
                    break;

                default:
                    _logger.LogDebug("Unhandled Roslyn notification: {Method}", item.Method);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Roslyn notification: {Method}", item.Method);
        }
    }

    private void OnRoslynNotification(object? sender, RoslynNotificationEventArgs e)
    {
        // This can get extremely noisy (especially for `window/logMessage` and `razor/log`),
        // so keep it at trace level for chatty notifications.
        if (e.Method == LspMethods.RazorLog || e.Method == "window/logMessage")
        {
            _logger.LogTrace("Received notification from Roslyn: {Method}", e.Method);
        }
        else
        {
            _logger.LogDebug("Received notification from Roslyn: {Method}", e.Method);
        }

        var item = new NotificationWorkItem(e.Method, e.Params);
        if (!_notificationChannel.Writer.TryWrite(item))
        {
            // Ensure initialization completion isn't dropped under backpressure
            if (e.Method == LspMethods.ProjectInitializationComplete)
            {
                _ = HandleRoslynNotificationAsync(item, _notificationCts.Token);
            }
            else
            {
                RecordDroppedRoslynNotification(e.Method);
                _logger.LogDebug("Dropping Roslyn notification due to backpressure: {Method}", e.Method);
            }
        }
    }

    private void RecordDroppedRoslynNotification(string method)
    {
        var count = Interlocked.Increment(ref _droppedRoslynNotifications);
        TryLogTelemetryWarning(
            count,
            DroppedNotificationsWarnEvery,
            ref _nextDroppedNotificationWarnAt,
            "Dropped {Count} Roslyn notifications due to backpressure. Latest method: {Method}",
            method);
    }

    private void RecordRoslynRequestTimeout(string method)
    {
        var count = Interlocked.Increment(ref _roslynRequestTimeouts);
        TryLogTelemetryWarning(
            count,
            RoslynTimeoutsWarnEvery,
            ref _nextRoslynTimeoutWarnAt,
            "Roslyn request timeouts reached {Count}. Latest method: {Method}",
            method);
    }

    private void TryLogTelemetryWarning(
        long count,
        long warnEvery,
        ref long nextWarnAt,
        string message,
        string method)
    {
        if (count < Volatile.Read(ref nextWarnAt))
        {
            return;
        }

        var now = DateTime.UtcNow;
        lock (_telemetryWarnLock)
        {
            if (count < nextWarnAt)
            {
                return;
            }

            if (now - _lastTelemetryWarnUtc < TelemetryWarnMinInterval)
            {
                return;
            }

            _lastTelemetryWarnUtc = now;
            _logger.LogWarning(message, count, method);
            nextWarnAt = count + warnEvery;
        }
    }

    private async Task<JsonElement?> OnRoslynRequestAsync(string method, JsonElement? @params, long requestId, CancellationToken ct)
    {
        _logger.LogDebug("Roslyn reverse request: {Method}", method);

        try
        {
            // Handle progress creation requests - forward to editor for status bar spinners
            if (method == LspMethods.WindowWorkDoneProgressCreate)
            {
                var token = TryGetProgressToken(@params);
                _logger.LogDebug("Forwarding workDoneProgress/create to client: token={Token}", token ?? "<none>");
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
        if (!@params.TryGetProperty("textDocument", out var textDocument) ||
            !textDocument.TryGetProperty("uri", out var uriProp))
        {
            _logger.LogWarning("HTML formatting request missing textDocument.uri");
            return (null, null, default, default);
        }

        var uri = uriProp.GetString();
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

    private WorkDoneProgressScope? BeginWorkDoneProgress(
        string tokenPrefix,
        string title,
        string? message,
        int delayMs,
        CancellationToken ct)
    {
        if (!SupportsWorkDoneProgress || _progressRpc == null)
        {
            return null;
        }

        var token = CreateWorkDoneProgressToken(tokenPrefix);
        return new WorkDoneProgressScope(this, token, title, message, delayMs, ct);
    }

    private async Task<JsonElement?> ForwardToRoslynWithProgressAsync(
        string method,
        JsonElement @params,
        string message,
        CancellationToken ct)
    {
        return await WithUserRequestProgressAsync(message, ct, () => ForwardToRoslynAsync(method, @params, ct));
    }

    private async Task<JsonElement?> WithUserRequestProgressAsync(
        string message,
        CancellationToken ct,
        Func<Task<JsonElement?>> action)
    {
        if (_userRequestProgressDelayMs < 0)
        {
            return await action();
        }

        var progress = BeginWorkDoneProgress(
            "razorsharp.request",
            "RazorSharp",
            message,
            _userRequestProgressDelayMs,
            ct);
        try
        {
            return await action();
        }
        finally
        {
            if (progress != null)
            {
                await progress.DisposeAsync();
            }
        }
    }

    private string CreateWorkDoneProgressToken(string prefix)
    {
        var id = Interlocked.Increment(ref _workDoneProgressCounter);
        return $"{prefix}:{id}";
    }
    private async Task<bool> SendWorkDoneProgressBeginAsync(
        string token,
        string title,
        string? message,
        CancellationToken ct)
    {
        if (_progressRpc == null)
        {
            return false;
        }

        var created = false;
        // Avoid server->client requests (workDoneProgress/create) while we are still in the
        // initialize handshake. Many clients will not respond until after `initialized`.
        if (_clientInitialized)
        {
            var createTimeout = TimeSpan.FromMilliseconds(WorkDoneProgressCreateTimeoutMs);
            try
            {
                // Some clients may not respond to workDoneProgress/create quickly (or at all).
                // Don't let that block begin/report/end; attempt create, but proceed after a timeout.
                using var timeoutCts = new CancellationTokenSource(createTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                var createTask = _progressRpc.InvokeWithParameterObjectAsync(
                    LspMethods.WindowWorkDoneProgressCreate,
                    new { token },
                    linkedCts.Token);

                try
                {
                    await createTask.WaitAsync(createTimeout, ct);
                    created = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Caller cancelled: don't emit progress.
                    return false;
                }
                catch (OperationCanceledException)
                {
                    // Timed out or cancelled by the create timeout; proceed anyway.
                }
                catch (TimeoutException)
                {
                    // Timed out; proceed anyway.
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to create work done progress {Token}", token);
                }

                // Observe any late exceptions to avoid unobserved task exceptions if the create
                // call finishes after we stop awaiting it.
                _ = createTask.ContinueWith(
                    t =>
                    {
                        if (t.IsFaulted && t.Exception != null)
                        {
                            _logger.LogDebug(t.Exception, "workDoneProgress/create failed for {Token}", token);
                        }
                    },
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
        }

        try
        {
            await _progressRpc.NotifyWithParameterObjectAsync(LspMethods.Progress, new
            {
                token,
                value = new
                {
                    kind = "begin",
                    title,
                    message,
                    cancellable = false
                }
            });

            _logger.LogDebug("Sent progress begin to client: token={Token} created={Created}", token, created);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to start work done progress {Token}", token);
            return false;
        }
    }

    private async Task SendWorkDoneProgressEndAsync(string token, string? message)
    {
        if (_progressRpc == null)
        {
            return;
        }

        try
        {
            await _progressRpc.NotifyWithParameterObjectAsync(LspMethods.Progress, new
            {
                token,
                value = new
                {
                    kind = "end",
                    message
                }
            });

            _logger.LogDebug("Sent progress end to client: token={Token}", token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to end work done progress {Token}", token);
        }
    }

    private async Task SendWorkDoneProgressReportAsync(string token, string? message)
    {
        if (_progressRpc == null)
        {
            return;
        }

        try
        {
            await _progressRpc.NotifyWithParameterObjectAsync(LspMethods.Progress, new
            {
                token,
                value = new
                {
                    kind = "report",
                    message
                }
            });

            _logger.LogDebug("Sent progress report to client: token={Token}", token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to report work done progress {Token}", token);
        }
    }

    private static string? TryGetProgressToken(JsonElement? @params)
    {
        if (!@params.HasValue)
        {
            return null;
        }

        if (!@params.Value.TryGetProperty("token", out var token))
        {
            return null;
        }

        return token.ValueKind == JsonValueKind.String ? token.GetString() : token.GetRawText();
    }

    private static string? TryGetProgressKind(JsonElement? @params)
    {
        if (!@params.HasValue)
        {
            return null;
        }

        if (!@params.Value.TryGetProperty("value", out var value) ||
            !value.TryGetProperty("kind", out var kind))
        {
            return null;
        }

        return kind.GetString();
    }

    private Task SendRoslynNotificationAsync(string method, object? parameters)
    {
        if (_forwardToRoslynNotificationOverride != null)
        {
            return _forwardToRoslynNotificationOverride(method, parameters);
        }

        if (_roslynClient == null || !_roslynClient.IsRunning)
        {
            return Task.CompletedTask;
        }

        if (parameters is JsonElement json)
        {
            return _roslynClient.SendNotificationAsync(method, json);
        }

        return _roslynClient.SendNotificationAsync(method, parameters);
    }

    private async Task EndWorkspaceInitializationProgressAsync()
    {
        var progress = Interlocked.Exchange(ref _workspaceInitProgress, null);
        if (progress != null)
        {
            await progress.DisposeAsync();
        }
    }

    private sealed class WorkDoneProgressScope : IAsyncDisposable
    {
        readonly RazorLanguageServer _server;
        readonly string _token;
        readonly string _title;
        readonly CancellationToken _ct;
        readonly string? _message;
        readonly CancellationTokenSource _delayCts;
        readonly Task<bool> _startTask;
        bool _disposed;

        public WorkDoneProgressScope(
            RazorLanguageServer server,
            string token,
            string title,
            string? message,
            int delayMs,
            CancellationToken ct)
        {
            _server = server;
            _token = token;
            _title = title;
            _ct = ct;
            _message = message;
            _delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _startTask = StartAsync(delayMs, _delayCts.Token);
        }

        async Task<bool> StartAsync(int delayMs, CancellationToken delayToken)
        {
            if (delayMs > 0)
            {
                try
                {
                    await Task.Delay(delayMs, delayToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return await _server.SendWorkDoneProgressBeginAsync(
                _token,
                _title,
                _message,
                _ct);
        }

        public async Task ReportAsync(string? message)
        {
            if (_disposed)
            {
                return;
            }

            bool started;
            try
            {
                started = await _startTask;
            }
            catch
            {
                return;
            }

            if (!started || _disposed)
            {
                return;
            }

            await _server.SendWorkDoneProgressReportAsync(_token, message);
        }

        public async ValueTask DisposeAsync()
        {
            _disposed = true;
            _delayCts.Cancel();
            bool started = false;
            try
            {
                started = await _startTask;
            }
            catch
            {
                // Ignore failures during progress initialization
            }
            finally
            {
                _delayCts.Dispose();
            }

            if (started)
            {
                await _server.SendWorkDoneProgressEndAsync(_token, message: null);
            }
        }
    }

    private async Task<JsonElement?> ForwardToRoslynAsync(string method, JsonElement @params, CancellationToken ct)
    {
        if (_dependenciesMissing)
        {
            return null;
        }

        if (_forwardToRoslynOverride != null)
        {
            return await _forwardToRoslynOverride(method, @params, ct);
        }

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
            if (_workspaceOpenedAt == default)
            {
                _workspaceOpenedAt = DateTime.UtcNow;
            }

            if (FastStartEnabled)
            {
                var delayMs = FastStartDelayMs;
                if (delayMs > 0)
                {
                    var timeSinceOpen = DateTime.UtcNow - _workspaceOpenedAt;
                    if (timeSinceOpen < TimeSpan.FromMilliseconds(delayMs))
                    {
                        _logger.LogDebug("Fast-start delay active, cannot forward {Method} yet", method);
                        return null;
                    }
                }

                _logger.LogDebug("Fast-start enabled; forwarding {Method} before project initialization completes", method);
            }
            else
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
        }

        try
        {
            _logger.LogDebug("Forwarding {Method} to Roslyn", method);
            using var timeoutCts = new CancellationTokenSource(_roslynRequestTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var result = await _roslynClient.SendRequestAsync(method, @params, linkedCts.Token);
            _logger.LogDebug("Received response from Roslyn for {Method}", method);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested)
            {
                RecordRoslynRequestTimeout(method);
                _logger.LogDebug("Roslyn request timed out for {Method}", method);
            }
            else
            {
                _logger.LogDebug("Roslyn request canceled by client for {Method}", method);
            }
            return null;
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

    private async Task SendDidOpenAndReplayAsync(PendingOpenState openState)
    {
        if (!CanSendRoslynNotifications)
        {
            return;
        }

        var roslynParams = new
        {
            textDocument = new
            {
                uri = openState.Uri,
                languageId = openState.LanguageId,
                version = openState.Version,
                text = openState.Text
            }
        };
        var roslynParamsJson = JsonSerializer.SerializeToElement(roslynParams, JsonOptions);
        await SendRoslynNotificationAsync(LspMethods.TextDocumentDidOpen, roslynParamsJson);

        List<JsonElement>? pendingChanges = null;
        lock (_documentTrackingLock)
        {
            _openDocuments.Add(openState.Uri);
            _pendingOpens.Remove(openState.Uri);
            if (_pendingChanges.TryGetValue(openState.Uri, out pendingChanges))
            {
                _pendingChanges.Remove(openState.Uri);
            }
        }

        if (pendingChanges != null && pendingChanges.Count > 0)
        {
            _logger.LogDebug("Replaying {Count} buffered changes for {Uri}", pendingChanges.Count, openState.Uri);
            foreach (var change in pendingChanges)
            {
                await SendRoslynNotificationAsync(LspMethods.TextDocumentDidChange, change);
            }
        }
    }

    private async Task FlushPendingOpensAsync()
    {
        if (!CanSendRoslynNotifications)
        {
            return;
        }

        List<PendingOpenState> pending;
        lock (_documentTrackingLock)
        {
            if (_pendingOpens.Count == 0)
            {
                return;
            }

            pending = new List<PendingOpenState>(_pendingOpens.Count);
            foreach (var kvp in _pendingOpens)
            {
                pending.Add(kvp.Value);
            }
            _pendingOpens.Clear();
        }

        foreach (var openState in pending)
        {
            try
            {
                _logger.LogDebug("Flushing pending didOpen for {Uri}", openState.Uri);
                await SendDidOpenAndReplayAsync(openState);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush pending didOpen for {Uri}", openState.Uri);
            }
        }
    }

    private async Task OpenWorkspaceAsync(string rootUriOrPath)
    {
        if (!CanSendRoslynNotifications)
        {
            return;
        }

        _workspaceOpenedAt = DateTime.UtcNow;
        var rootPath = TryGetLocalPath(rootUriOrPath);
        if (rootPath == null)
        {
            _logger.LogWarning("Invalid workspace root: {Root}", rootUriOrPath);
            return;
        }

        if (File.Exists(rootPath))
        {
            var extension = Path.GetExtension(rootPath);
            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(SolutionFilterFileName, StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(SolutionXmlFileName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Opening solution: {Solution}", rootPath);
                await SendRoslynNotificationAsync(LspMethods.SolutionOpen, new SolutionOpenParams
                {
                    Solution = new Uri(rootPath).AbsoluteUri
                });
            }
            else if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Opening project: {Project}", rootPath);
                await SendRoslynNotificationAsync(LspMethods.ProjectOpen, new ProjectOpenParams
                {
                    Projects = [new Uri(rootPath).AbsoluteUri]
                });
            }
            else
            {
                _logger.LogWarning("Workspace path is a file but not a supported project/solution: {Path}", rootPath);
            }
            return;
        }

        if (!Directory.Exists(rootPath))
        {
            _logger.LogWarning("Workspace root does not exist: {Path}", rootPath);
            return;
        }

        var solution = _workspaceManager.FindSolution(rootPath);

        if (solution != null)
        {
            _logger.LogInformation("Opening solution: {Solution}", solution);
            await SendRoslynNotificationAsync(LspMethods.SolutionOpen, new SolutionOpenParams
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
                await SendRoslynNotificationAsync(LspMethods.ProjectOpen, new ProjectOpenParams
                {
                    Projects = projects.Select(p => new Uri(p).AbsoluteUri).ToArray()
                });
            }
        }
    }

    private async Task TryRegisterFileWatchersAsync()
    {
        if (_fileWatchersRegistered)
        {
            return;
        }

        var didChangeWatchedFiles = _initParams?.Capabilities?.Workspace?.DidChangeWatchedFiles;
        if (didChangeWatchedFiles?.DynamicRegistration != true)
        {
            _logger.LogDebug("Client does not support dynamic didChangeWatchedFiles registration; skipping registration");
            return;
        }

        var baseUri = TryGetWorkspaceBaseUri();
        if (baseUri == null)
        {
            _logger.LogInformation("Workspace baseUri not available; file watchers will not be scoped to a workspace root.");
        }

        if (_clientRpc == null)
        {
            return;
        }

        var registrations = new[]
        {
            new
            {
                id = FileWatchRegistrationId,
                method = LspMethods.WorkspaceDidChangeWatchedFiles,
                registerOptions = new
                {
                    watchers = CreateFileWatchers(baseUri)
                }
            }
        };

        try
        {
            await _clientRpc.InvokeWithParameterObjectAsync<JsonElement?>(
                "client/registerCapability",
                new { registrations },
                CancellationToken.None);
            _fileWatchersRegistered = true;
            _logger.LogInformation("Registered workspace/didChangeWatchedFiles with client");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register workspace/didChangeWatchedFiles with client");
        }
    }

    private static object[] CreateFileWatchers(string? baseUri)
    {
        var watchers = new List<object>();
        foreach (var extension in WorkspaceReloadExtensions)
        {
            watchers.Add(CreateWatcher($"**/*{extension}", baseUri));
        }
        foreach (var fileName in WorkspaceReloadFileNames)
        {
            watchers.Add(CreateWatcher($"**/{fileName}", baseUri));
        }

        watchers.Add(CreateWatcher($"**/{OmniSharpConfigFileName}", baseUri));
        watchers.Add(CreateWatcher("**/*.razor", baseUri));
        watchers.Add(CreateWatcher("**/*.cshtml", baseUri));
        watchers.Add(CreateWatcher("**/*.razor.cs", baseUri));
        watchers.Add(CreateWatcher("**/*.cs", baseUri));
        watchers.Add(CreateWatcher("**/*.csproj.user", baseUri));
        watchers.Add(CreateWatcher("**/.editorconfig", baseUri));
        watchers.Add(CreateWatcher("**/obj/**/generated/**", baseUri));
        return watchers.ToArray();
    }

    private static object CreateWatcher(string pattern, string? baseUri)
    {
        if (!string.IsNullOrEmpty(baseUri))
        {
            return new
            {
                globPattern = new
                {
                    baseUri,
                    pattern
                },
                kind = FileWatchKindAll
            };
        }

        return new
        {
            globPattern = pattern,
            kind = FileWatchKindAll
        };
    }

    private void ScheduleWorkspaceReload()
    {
        var target = GetWorkspaceOpenTarget();
        if (target == null || !CanSendRoslynNotifications)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_workspaceReloadLock)
        {
            _workspaceReloadCts?.Cancel();
            _workspaceReloadCts?.Dispose();
            _workspaceReloadCts = new CancellationTokenSource();
            cts = _workspaceReloadCts;
        }

        _workspaceReloadTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(WorkspaceReloadDebounceMs, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogInformation("Workspace files changed; re-opening workspace");
                await OpenWorkspaceAsync(target);
            }
            catch (OperationCanceledException)
            {
                // Debounced by a newer change
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-open workspace after file changes");
            }
        }, cts.Token);
    }

    private string? GetWorkspaceOpenTarget()
    {
        if (!string.IsNullOrEmpty(_workspaceOpenTarget))
        {
            return _workspaceOpenTarget;
        }

        if (!string.IsNullOrEmpty(_cliSolutionPath))
        {
            return _cliSolutionPath;
        }

        if (_initParams?.RootUri != null)
        {
            return _initParams.RootUri;
        }

        if (_initParams?.WorkspaceFolders?.Length > 0)
        {
            return _initParams.WorkspaceFolders[0].Uri;
        }

        return _workspaceRoot;
    }

    private string? TryGetWorkspaceBaseUri()
    {
        var rootPath = _workspaceRoot;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            var target = GetWorkspaceOpenTarget();
            rootPath = target != null ? TryGetLocalPath(target) : null;
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        if (File.Exists(rootPath))
        {
            rootPath = Path.GetDirectoryName(rootPath);
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        var fullPath = TryGetFullPath(rootPath);
        if (fullPath == null)
        {
            return null;
        }

        try
        {
            return new Uri(fullPath).AbsoluteUri;
        }
        catch
        {
            return null;
        }
    }

    private void ConfigureUriComparers(string? probePath)
    {
        var isCaseInsensitive = FileSystemCaseSensitivity.IsCaseInsensitiveForPath(probePath);
        _uriComparer = isCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _uriComparison = isCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _openDocuments = new HashSet<string>(_uriComparer);
        _pendingOpens = new Dictionary<string, PendingOpenState>(_uriComparer);
        _pendingChanges = new Dictionary<string, List<JsonElement>>(_uriComparer);
        _sourceGeneratedUriCache = new Dictionary<string, string>(_uriComparer);
    }

    private string? GetWorkspaceProbePath(InitializeParams? initParams)
    {
        if (!string.IsNullOrWhiteSpace(_cliSolutionPath))
        {
            return TryGetLocalPath(_cliSolutionPath) ?? _cliSolutionPath;
        }

        if (initParams?.RootUri != null)
        {
            var rootPath = TryGetLocalPath(initParams.RootUri);
            if (!string.IsNullOrWhiteSpace(rootPath))
            {
                return rootPath;
            }
        }

        if (initParams?.WorkspaceFolders?.Length > 0)
        {
            return TryGetLocalPath(initParams.WorkspaceFolders[0].Uri);
        }

        return null;
    }

    private static string? TryGetLocalPath(string uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath))
        {
            return null;
        }

        if (IsLikelyWindowsPath(uriOrPath))
        {
            try
            {
                return Path.GetFullPath(uriOrPath);
            }
            catch
            {
                return null;
            }
        }

        if (Uri.TryCreate(uriOrPath, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return uri.LocalPath;
            }
            return null;
        }

        try
        {
            return Path.GetFullPath(uriOrPath);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLikelyWindowsPath(string value)
    {
        // Drive letter path (e.g., C:\path or C:/path)
        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
        {
            return true;
        }

        // UNC path (e.g., \\server\share)
        if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string? TryGetGlobalOmniSharpConfigPath()
    {
        var omniSharpHome = Environment.GetEnvironmentVariable("OMNISHARPHOME");
        if (!string.IsNullOrEmpty(omniSharpHome))
        {
            return TryGetFullPath(Path.Combine(omniSharpHome, OmniSharpConfigFileName));
        }

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(homeDir))
        {
            return TryGetFullPath(Path.Combine(homeDir, ".omnisharp", OmniSharpConfigFileName));
        }

        return null;
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private bool IsOmniSharpConfigPath(string path, string? localPath, string? globalPath)
    {
        if (localPath != null && path.Equals(localPath, _uriComparison))
        {
            return true;
        }

        if (globalPath != null && path.Equals(globalPath, _uriComparison))
        {
            return true;
        }

        return false;
    }

    private static bool IsWorkspaceReloadTriggerPath(string path)
    {
        var extension = Path.GetExtension(path);
        foreach (var reloadExtension in WorkspaceReloadExtensions)
        {
            if (extension.Equals(reloadExtension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var fileName = Path.GetFileName(path);
        foreach (var reloadFileName in WorkspaceReloadFileNames)
        {
            if (fileName.Equals(reloadFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private object CreateRoslynInitParams(InitializeParams? clientParams)
    {
        var workDoneProgress = clientParams?.Capabilities?.Window?.WorkDoneProgress == true;

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
                workDoneProgress
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
                    TokenTypes = SemanticTokenTypes,
                    TokenModifiers = SemanticTokenModifiers
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

        // Use HashSet for O(n) duplicate detection while preserving original order.
        var seen = new HashSet<string>();
        HashSet<string>? duplicates = null;

        foreach (var item in custom)
        {
            if (!seen.Add(item))
            {
                duplicates ??= [];
                duplicates.Add(item);
            }
        }

        if (duplicates == null)
        {
            return custom;
        }

        var ordered = new List<string>(custom.Length);
        seen.Clear();
        foreach (var item in custom)
        {
            if (seen.Add(item))
            {
                ordered.Add(item);
            }
        }

        _logger.LogWarning("Duplicate {OptionName}: {Duplicates}",
            optionName, string.Join(", ", duplicates.Select(c => '"' + c + '"')));
        return [.. ordered];
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

        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();

        lock (_workspaceReloadLock)
        {
            _workspaceReloadCts?.Cancel();
            _workspaceReloadCts?.Dispose();
            _workspaceReloadCts = null;
        }

        if (_workspaceReloadTask != null)
        {
            try
            {
                await _workspaceReloadTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Ignore shutdown errors
            }
        }

        _clientRpc?.Dispose();

        _notificationChannel.Writer.TryComplete();
        _notificationCts.Cancel();
        try
        {
            await _notificationTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore shutdown errors
        }
        _notificationCts.Dispose();

        LogTelemetrySummary();
    }

    private void LogTelemetrySummary()
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        _logger.LogDebug(
            "Telemetry: droppedRoslynNotifications={DroppedNotifications}, roslynRequestTimeouts={RequestTimeouts}, " +
            "sourceGeneratedCacheHits={CacheHits}, sourceGeneratedCacheMisses={CacheMisses}, " +
            "sourceGeneratedIndexRefreshes={IndexRefreshes}, sourceGeneratedIndexIncrementalUpdates={IncrementalUpdates}",
            Interlocked.Read(ref _droppedRoslynNotifications),
            Interlocked.Read(ref _roslynRequestTimeouts),
            Interlocked.Read(ref _sourceGeneratedCacheHits),
            Interlocked.Read(ref _sourceGeneratedCacheMisses),
            Interlocked.Read(ref _sourceGeneratedIndexRefreshes),
            Interlocked.Read(ref _sourceGeneratedIndexIncrementalUpdates));
    }
}

internal interface IProgressRpc
{
    Task<JsonElement?> InvokeWithParameterObjectAsync(string method, object @params, CancellationToken ct);
    Task NotifyWithParameterObjectAsync(string method, object @params);
}

internal sealed class JsonRpcProgressClient : IProgressRpc
{
    readonly JsonRpc _rpc;

    public JsonRpcProgressClient(JsonRpc rpc)
    {
        _rpc = rpc;
    }

    public Task<JsonElement?> InvokeWithParameterObjectAsync(string method, object @params, CancellationToken ct)
        => _rpc.InvokeWithParameterObjectAsync<JsonElement?>(method, @params, ct);

    public Task NotifyWithParameterObjectAsync(string method, object @params)
        => _rpc.NotifyWithParameterObjectAsync(method, @params);
}
