using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Server.Configuration;
using RazorSharp.Utilities;
using StreamJsonRpc;

namespace RazorSharp.Server.Html;

/// <summary>
/// Client for communicating with an external HTML language server.
/// Uses vscode-html-language-server for HTML formatting in Razor files.
/// </summary>
public class HtmlLanguageClient : IAsyncDisposable
{
    readonly ILogger<HtmlLanguageClient> _logger;
    readonly SemaphoreSlim _startLock = new(1, 1);
    Process? _process;
    JsonRpc? _rpc;
    Task? _stderrTask;
    CancellationTokenSource? _processCts;
    bool _initialized;
    bool _disposed;
    bool _enabled = true;
    bool _startAttempted;
    bool _restartAttempted;
    string? _rootUri;
    Func<object, Task>? _didOpenOverrideForTests;
    StringComparer _uriComparer = StringComparer.Ordinal;

    // Track HTML projections by checksum (Roslyn uses checksums to identify HTML versions)
    readonly Dictionary<string, HtmlProjection> _projections = new();
    // Secondary index for O(1) lookup by Razor URI
    Dictionary<string, string> _razorUriToChecksum;
    // Lock for atomic updates to both projection dictionaries
    readonly Lock _projectionsLock = new();

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // Cached suffix for virtual HTML URIs to avoid repeated string allocations
    internal const string VirtualHtmlSuffix = "__virtual.html";

    public HtmlLanguageClient(ILogger<HtmlLanguageClient> logger)
    {
        _logger = logger;
        _razorUriToChecksum = new Dictionary<string, string>(_uriComparer);
    }

    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>
    /// Configures the HTML language client. The server will be started lazily
    /// when the first Razor file is opened.
    /// </summary>
    public void Configure(HtmlOptions? options, string? rootUri)
    {
        if (options?.Enable == false)
        {
            _enabled = false;
            _logger.LogInformation("HTML language server disabled by configuration");
        }
        SetUriComparer(rootUri);
        _rootUri = rootUri;
    }

    private void SetUriComparer(string? rootUri)
    {
        var probePath = TryGetLocalPath(rootUri);
        var isCaseInsensitive = FileSystemCaseSensitivity.IsCaseInsensitiveForPath(probePath);
        _uriComparer = isCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        lock (_projectionsLock)
        {
            _razorUriToChecksum = new Dictionary<string, string>(_uriComparer);
        }
    }

    private static string? TryGetLocalPath(string? uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath))
        {
            return null;
        }

        if (Uri.TryCreate(uriOrPath, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
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

    /// <summary>
    /// Ensures the HTML language server is started. Called lazily when needed.
    /// </summary>
    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (!_enabled || (_initialized && _process != null && !_process.HasExited))
            return;

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process != null && _process.HasExited)
            {
                HandleHtmlServerExit("process exited unexpectedly");
            }

            if (_initialized || _startAttempted)
                return;

            _startAttempted = true;
            var started = await StartAsync(cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                return;
            }
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// Starts the HTML language server (vscode-html-language-server).
    /// </summary>
    private async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        // Find vscode-html-language-server
        var serverPath = FindHtmlLanguageServer();
        if (serverPath == null)
        {
            DisableHtmlServer("HTML language server not found. Install with: npm/pnpm/yarn global vscode-langservers-extracted.");
            return false;
        }

        // Determine how to run the server:
        // - .js files: run with node
        // - Shell scripts (pnpm wrappers) or executables: run directly
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (serverPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "node";
            psi.ArgumentList.Add(serverPath);
        }
        else
        {
            psi.FileName = serverPath;
        }

        psi.ArgumentList.Add("--stdio");

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            DisableHtmlServer("Failed to start HTML language server. Is Node.js installed?", ex);
            return false;
        }

        if (_process == null)
        {
            DisableHtmlServer("Failed to start HTML language server (process was null).");
            return false;
        }

        _processCts?.Cancel();
        _processCts?.Dispose();
        _processCts = new CancellationTokenSource();

        // Capture stderr (track task for cleanup)
        _stderrTask = Task.Run(async () =>
        {
            try
            {
                while (_process != null && !_process.HasExited)
                {
                    var line = await _process.StandardError.ReadLineAsync(_processCts.Token);
                    if (line != null)
                    {
                        _logger.LogDebug("[HTML LS stderr] {Line}", line);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }, _processCts.Token);

        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = JsonOptions
        };

        var handler = new HeaderDelimitedMessageHandler(
            _process.StandardInput.BaseStream,
            _process.StandardOutput.BaseStream,
            formatter);
        _rpc = new JsonRpc(handler);
        _rpc.StartListening();

        _logger.LogInformation("HTML language server started (PID: {Pid})", _process.Id);
        return true;
    }

    /// <summary>
    /// Initializes the HTML language server.
    /// </summary>
    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_rpc == null || _initialized)
        {
            return;
        }

        var initParams = new
        {
            processId = Environment.ProcessId,
            rootUri = _rootUri,
            capabilities = new
            {
                textDocument = new
                {
                    formatting = new { dynamicRegistration = true },
                    rangeFormatting = new { dynamicRegistration = true }
                }
            }
        };

        try
        {
            await _rpc.InvokeWithParameterObjectAsync<JsonElement>("initialize", initParams, cancellationToken).ConfigureAwait(false);
            await _rpc.NotifyAsync("initialized").ConfigureAwait(false);

            _initialized = true;
            _logger.LogInformation("HTML language server initialized");
            try
            {
                await OpenCachedProjectionsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to open cached HTML projections");
            }
        }
        catch (Exception ex)
        {
            DisableHtmlServer("Failed to initialize HTML language server.", ex);
        }
    }

    /// <summary>
    /// Updates the HTML projection for a Razor document.
    /// Called when we receive razor/updateHtml from Roslyn.
    /// </summary>
    public async Task UpdateHtmlProjectionAsync(string razorUri, string checksum, string htmlContent, CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            return;
        }

        // Lazily start the HTML language server on first Razor file
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (!_enabled)
        {
            return;
        }

        // Store the projection even if HTML LS failed to start
        if (_rpc == null || !_initialized)
        {
            lock (_projectionsLock)
            {
                if (_razorUriToChecksum.TryGetValue(razorUri, out var existingChecksum))
                {
                    if (!string.Equals(existingChecksum, checksum, StringComparison.Ordinal))
                    {
                        _projections.Remove(existingChecksum);
                    }
                }

                _projections[checksum] = new HtmlProjection(razorUri, checksum, 1, htmlContent);
                _razorUriToChecksum[razorUri] = checksum;
            }
            return;
        }

        var virtualUri = GetVirtualHtmlUri(razorUri);

        // Atomically check and update projections
        HtmlProjection? existingByUri = null;
        int newVersion = 1;
        lock (_projectionsLock)
        {
            if (_razorUriToChecksum.TryGetValue(razorUri, out var existingChecksum))
            {
                _projections.TryGetValue(existingChecksum, out existingByUri);
            }

            if (existingByUri != null)
            {
                // Update existing document
                newVersion = existingByUri.Version + 1;
                _projections.Remove(existingByUri.Checksum);
            }

            _projections[checksum] = new HtmlProjection(razorUri, checksum, newVersion, Content: null);
            _razorUriToChecksum[razorUri] = checksum;
        }

        if (existingByUri != null)
        {
            try
            {
                await _rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new
                {
                    textDocument = new { uri = virtualUri, version = newVersion },
                    contentChanges = new[] { new { text = htmlContent } }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HandleHtmlServerExit("send failed", ex);
                return;
            }

            _logger.LogDebug("Updated HTML projection for {Uri} (checksum: {Checksum})", razorUri, checksum);
        }
        else
        {
            try
            {
                await _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new
                {
                    textDocument = new
                    {
                        uri = virtualUri,
                        languageId = "html",
                        version = 1,
                        text = htmlContent
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HandleHtmlServerExit("send failed", ex);
                return;
            }

            _logger.LogDebug("Opened HTML projection for {Uri} (checksum: {Checksum})", razorUri, checksum);
        }
    }

    private async Task OpenCachedProjectionsAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            return;
        }

        if (_rpc == null && _didOpenOverrideForTests == null)
        {
            return;
        }

        List<HtmlProjection>? cached = null;
        List<string>? keysToClear = null;
        lock (_projectionsLock)
        {
            foreach (var kvp in _projections)
            {
                if (kvp.Value.Content == null)
                {
                    continue;
                }

                cached ??= new List<HtmlProjection>();
                cached.Add(kvp.Value);

                keysToClear ??= new List<string>();
                keysToClear.Add(kvp.Key);
            }

            if (keysToClear != null)
            {
                foreach (var key in keysToClear)
                {
                    if (_projections.TryGetValue(key, out var projection))
                    {
                        _projections[key] = projection with { Content = null };
                    }
                }
            }
        }

        if (cached == null)
        {
            return;
        }

        foreach (var projection in cached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualUri = GetVirtualHtmlUri(projection.RazorUri);
            var payload = new
            {
                textDocument = new
                {
                    uri = virtualUri,
                    languageId = "html",
                    version = projection.Version,
                    text = projection.Content
                }
            };

            if (_didOpenOverrideForTests != null)
            {
                await _didOpenOverrideForTests(payload).ConfigureAwait(false);
            }
            else
            {
                await _rpc!.NotifyWithParameterObjectAsync("textDocument/didOpen", payload).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Formats an HTML document and returns the text edits.
    /// </summary>
    public async Task<JsonElement?> FormatAsync(string razorUri, string checksum, JsonElement options, CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return null;
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (_rpc == null || !_initialized)
        {
            _logger.LogDebug("HTML LS not available for formatting");
            return null;
        }

        HtmlProjection? projection;
        lock (_projectionsLock)
        {
            _projections.TryGetValue(checksum, out projection);
        }

        if (projection == null)
        {
            _logger.LogDebug("No HTML projection found for checksum {Checksum}", checksum);
            return null;
        }

        var virtualUri = GetVirtualHtmlUri(razorUri);

        try
        {
            var @params = new
            {
                textDocument = new { uri = virtualUri },
                options
            };

            var result = await _rpc.InvokeWithParameterObjectAsync<JsonElement?>(
                "textDocument/formatting",
                @params,
                cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("HTML formatting returned {Count} edits",
                result?.ValueKind == JsonValueKind.Array ? result.Value.GetArrayLength() : 0);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting HTML formatting");
            return null;
        }
    }

    /// <summary>
    /// Formats a range of an HTML document and returns the text edits.
    /// </summary>
    public async Task<JsonElement?> FormatRangeAsync(
        string razorUri,
        string checksum,
        JsonElement range,
        JsonElement options,
        CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return null;
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (_rpc == null || !_initialized)
        {
            return null;
        }

        bool hasProjection;
        lock (_projectionsLock)
        {
            hasProjection = _projections.ContainsKey(checksum);
        }

        if (!hasProjection)
        {
            _logger.LogDebug("No HTML projection found for checksum {Checksum}", checksum);
            return null;
        }

        var virtualUri = GetVirtualHtmlUri(razorUri);

        try
        {
            var @params = new
            {
                textDocument = new { uri = virtualUri },
                range,
                options
            };

            return await _rpc.InvokeWithParameterObjectAsync<JsonElement?>(
                "textDocument/rangeFormatting",
                @params,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting HTML range formatting");
            return null;
        }
    }

    /// <summary>
    /// Gets the HTML projection by checksum.
    /// </summary>
    public HtmlProjection? GetProjection(string checksum)
    {
        lock (_projectionsLock)
        {
            _projections.TryGetValue(checksum, out var projection);
            return projection;
        }
    }

    /// <summary>
    /// Gets the HTML projection by Razor URI.
    /// </summary>
    public HtmlProjection? GetProjectionByRazorUri(string razorUri)
    {
        lock (_projectionsLock)
        {
            if (_razorUriToChecksum.TryGetValue(razorUri, out var checksum))
            {
                _projections.TryGetValue(checksum, out var projection);
                return projection;
            }
            return null;
        }
    }

    internal void SetDidOpenOverrideForTests(Func<object, Task> overrideFunc)
    {
        _didOpenOverrideForTests = overrideFunc;
    }

    internal void SetInitializedForTests(bool initialized)
    {
        _initialized = initialized;
    }

    internal void SetStartAttemptedForTests(bool attempted = true)
    {
        _startAttempted = attempted;
    }

    internal Task FlushCachedProjectionsForTestsAsync(CancellationToken cancellationToken = default)
    {
        return OpenCachedProjectionsAsync(cancellationToken);
    }

    internal void TriggerHtmlServerExitForTests()
    {
        HandleHtmlServerExit("test");
    }

    internal bool IsRestartAttemptedForTests() => _restartAttempted;

    internal bool IsEnabledForTests() => _enabled;

    internal bool IsInitializedForTests() => _initialized;

    internal bool IsStartAttemptedForTests() => _startAttempted;

    private static string GetVirtualHtmlUri(string razorUri)
    {
        return string.Concat(razorUri, VirtualHtmlSuffix);
    }

    /// <summary>
    /// Adds pnpm global paths for all store versions found in the given directory.
    /// Pnpm uses numbered directories (5, 6, etc.) for different store format versions.
    /// </summary>
    private static void AddPnpmGlobalPaths(List<string> paths, string pnpmGlobalDir)
    {
        if (!Directory.Exists(pnpmGlobalDir))
        {
            return;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(pnpmGlobalDir))
            {
                var dirName = Path.GetFileName(dir);
                // pnpm store versions are numeric (5, 6, etc.)
                if (int.TryParse(dirName, out _))
                {
                    paths.Add(Path.Combine(dir, "node_modules", "vscode-langservers-extracted", "bin", "vscode-html-language-server"));
                }
            }
        }
        catch
        {
            // Ignore errors reading directory
        }
    }

    private static string? FindHtmlLanguageServer()
    {
        var possiblePaths = new List<string>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: npm global install location
            possiblePaths.Add(Path.Combine(appData, "npm", "node_modules", "vscode-langservers-extracted", "bin", "vscode-html-language-server"));
            // Windows: yarn global
            possiblePaths.Add(Path.Combine(appData, "yarn", "global", "node_modules", "vscode-langservers-extracted", "bin", "vscode-html-language-server"));
            // Windows: pnpm global (check all store versions)
            AddPnpmGlobalPaths(possiblePaths, Path.Combine(appData, "pnpm", "global"));
        }
        else
        {
            // Unix: npm global install locations
            possiblePaths.Add(Path.Combine(userProfile, ".npm-global", "lib", "node_modules", "vscode-langservers-extracted", "bin", "vscode-html-language-server"));
            possiblePaths.Add("/usr/local/lib/node_modules/vscode-langservers-extracted/bin/vscode-html-language-server");
            possiblePaths.Add("/usr/lib/node_modules/vscode-langservers-extracted/bin/vscode-html-language-server");
            // Unix: yarn global
            possiblePaths.Add(Path.Combine(userProfile, ".yarn", "bin", "vscode-html-language-server"));
            // Unix: pnpm global (check all store versions)
            AddPnpmGlobalPaths(possiblePaths, Path.Combine(userProfile, ".local", "share", "pnpm", "global"));
        }

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
            // Check for .js extension (Windows npm shims)
            var jsPath = path + ".js";
            if (File.Exists(jsPath))
            {
                return jsPath;
            }
            // Check for .cmd extension (Windows)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var cmdPath = path + ".cmd";
                if (File.Exists(cmdPath))
                {
                    return cmdPath;
                }
            }
        }

        // Try to find via PATH using platform-specific command
        try
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "where" : "which",
                ArgumentList = { "vscode-html-language-server" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    // 'where' on Windows may return multiple lines; take the first
                    var newlineIdx = output.IndexOfAny(['\r', '\n']);
                    return newlineIdx >= 0 ? output[..newlineIdx] : output;
                }
            }
        }
        catch
        {
            // Ignore - command not found or other error
        }

        return null;
    }

    private void HandleHtmlServerExit(string reason, Exception? ex = null)
    {
        _initialized = false;
        _startAttempted = false;

        _processCts?.Cancel();
        _processCts?.Dispose();
        _processCts = null;

        _rpc?.Dispose();
        _rpc = null;

        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore
            }
            _process.Dispose();
            _process = null;
        }

        if (_restartAttempted)
        {
            DisableHtmlServer($"HTML language server disabled after restart attempt ({reason}).", ex);
            return;
        }

        _restartAttempted = true;
        if (ex != null)
        {
            _logger.LogWarning(ex, "HTML language server exited; attempting one restart ({Reason})", reason);
        }
        else
        {
            _logger.LogWarning("HTML language server exited; attempting one restart ({Reason})", reason);
        }
    }

    private void DisableHtmlServer(string reason, Exception? ex = null)
    {
        _enabled = false;
        _initialized = false;

        if (ex != null)
        {
            _logger.LogError(ex, "HTML language server disabled: {Reason}", reason);
        }
        else
        {
            _logger.LogError("HTML language server disabled: {Reason}", reason);
        }

        lock (_projectionsLock)
        {
            _projections.Clear();
            _razorUriToChecksum.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Attempt graceful LSP shutdown first
        if (_process != null && !_process.HasExited && _rpc != null && _initialized)
        {
            try
            {
                // Send shutdown request and wait for response
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _rpc.InvokeWithCancellationAsync<object?>("shutdown", cancellationToken: cts.Token).ConfigureAwait(false);

                // Send exit notification
                await _rpc.NotifyAsync("exit").ConfigureAwait(false);

                // Dispose RPC before waiting (closes streams, signals EOF to process)
                _rpc.Dispose();
                _rpc = null;

                // Wait briefly for process to exit gracefully
                if (!_process.WaitForExit(2000))
                {
                    _logger.LogWarning("HTML language server did not exit gracefully, forcing termination");
                    _process.Kill(entireProcessTree: true);
                }
                else
                {
                    _logger.LogDebug("HTML language server exited gracefully");
                }

                _processCts?.Cancel();

                // Wait for stderr task to complete
                if (_stderrTask != null)
                {
                    try { await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1)); }
                    catch { /* Ignore timeout */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during graceful shutdown, forcing termination");
                _rpc?.Dispose();
                _rpc = null;
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore - process may already be dead
                }
            }

            _process.Dispose();
            _process = null;
        }
        else
        {
            // Process not running or not initialized, just clean up
            _rpc?.Dispose();
            _rpc = null;

            if (_process != null)
            {
                if (!_process.HasExited)
                {
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Ignore
                    }
                }
                _process.Dispose();
                _process = null;
            }
        }

        _processCts?.Cancel();
        _processCts?.Dispose();
        _processCts = null;
    }
}

/// <summary>
/// Represents an HTML projection of a Razor document.
/// </summary>
public record HtmlProjection(string RazorUri, string Checksum, int Version, string? Content);
