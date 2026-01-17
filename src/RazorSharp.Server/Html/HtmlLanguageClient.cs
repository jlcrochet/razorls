using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Server.Configuration;
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
    bool _initialized;
    bool _disposed;
    bool _enabled = true;
    bool _startAttempted;
    string? _rootUri;

    // Track HTML projections by checksum (Roslyn uses checksums to identify HTML versions)
    readonly ConcurrentDictionary<string, HtmlProjection> _projections = new();
    // Secondary index for O(1) lookup by Razor URI
    readonly ConcurrentDictionary<string, string> _razorUriToChecksum = new();

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public HtmlLanguageClient(ILogger<HtmlLanguageClient> logger)
    {
        _logger = logger;
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
        _rootUri = rootUri;
    }

    /// <summary>
    /// Ensures the HTML language server is started. Called lazily when needed.
    /// </summary>
    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (!_enabled || _initialized || _startAttempted)
            return;

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized || _startAttempted)
                return;

            _startAttempted = true;
            await StartAsync(cancellationToken);
            await InitializeAsync(cancellationToken);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// Starts the HTML language server (vscode-html-language-server).
    /// </summary>
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        // Find vscode-html-language-server
        var serverPath = FindHtmlLanguageServer();
        if (serverPath == null)
        {
            _logger.LogWarning("HTML language server not found. HTML formatting will be limited. Install with: `npm install -g vscode-langservers-extracted` or `pnpm install -g vscode-langservers-extracted` or `yarn global add vscode-langservers-extracted`");
            return;
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
            _logger.LogWarning(ex, "Failed to start HTML language server. Is Node.js installed?");
            return;
        }

        if (_process == null)
        {
            _logger.LogError("Failed to start HTML language server");
            return;
        }

        // Capture stderr
        _ = Task.Run(async () =>
        {
            while (_process != null && !_process.HasExited)
            {
                var line = await _process.StandardError.ReadLineAsync(cancellationToken);
                if (line != null)
                {
                    _logger.LogDebug("[HTML LS stderr] {Line}", line);
                }
            }
        }, cancellationToken);

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

        await _rpc.InvokeWithParameterObjectAsync<JsonElement>("initialize", initParams, cancellationToken);
        await _rpc.NotifyAsync("initialized");

        _initialized = true;
        _logger.LogInformation("HTML language server initialized");
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
        await EnsureStartedAsync(cancellationToken);

        // Store the projection even if HTML LS failed to start
        if (_rpc == null || !_initialized)
        {
            _projections[checksum] = new HtmlProjection(razorUri, checksum, htmlContent, 1);
            _razorUriToChecksum[razorUri] = checksum;
            return;
        }

        var virtualUri = GetVirtualHtmlUri(razorUri);

        // Use secondary index for O(1) lookup
        HtmlProjection? existingByUri = null;
        if (_razorUriToChecksum.TryGetValue(razorUri, out var existingChecksum))
        {
            _projections.TryGetValue(existingChecksum, out existingByUri);
        }

        if (existingByUri != null)
        {
            // Update existing document
            var newVersion = existingByUri.Version + 1;
            _projections.TryRemove(existingByUri.Checksum, out _);
            _projections[checksum] = new HtmlProjection(razorUri, checksum, htmlContent, newVersion);
            _razorUriToChecksum[razorUri] = checksum;

            await _rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new
            {
                textDocument = new { uri = virtualUri, version = newVersion },
                contentChanges = new[] { new { text = htmlContent } }
            });

            _logger.LogDebug("Updated HTML projection for {Uri} (checksum: {Checksum})", razorUri, checksum);
        }
        else
        {
            // Open new document
            _projections[checksum] = new HtmlProjection(razorUri, checksum, htmlContent, 1);
            _razorUriToChecksum[razorUri] = checksum;

            await _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new
            {
                textDocument = new
                {
                    uri = virtualUri,
                    languageId = "html",
                    version = 1,
                    text = htmlContent
                }
            });

            _logger.LogDebug("Opened HTML projection for {Uri} (checksum: {Checksum})", razorUri, checksum);
        }
    }

    /// <summary>
    /// Formats an HTML document and returns the text edits.
    /// </summary>
    public async Task<JsonElement?> FormatAsync(string razorUri, string checksum, JsonElement options, CancellationToken cancellationToken)
    {
        if (!_enabled || _rpc == null || !_initialized)
        {
            _logger.LogDebug("HTML LS not available for formatting");
            return null;
        }

        if (!_projections.TryGetValue(checksum, out var projection))
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
                cancellationToken);

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
        if (!_enabled || _rpc == null || !_initialized)
        {
            return null;
        }

        if (!_projections.TryGetValue(checksum, out _))
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
                cancellationToken);
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
        _projections.TryGetValue(checksum, out var projection);
        return projection;
    }

    /// <summary>
    /// Gets the HTML projection by Razor URI.
    /// </summary>
    public HtmlProjection? GetProjectionByRazorUri(string razorUri)
    {
        if (_razorUriToChecksum.TryGetValue(razorUri, out var checksum))
        {
            _projections.TryGetValue(checksum, out var projection);
            return projection;
        }
        return null;
    }

    private static string GetVirtualHtmlUri(string razorUri)
    {
        return razorUri + "__virtual.html";
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
                    var firstLine = output.Split(['\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    return firstLine;
                }
            }
        }
        catch
        {
            // Ignore - command not found or other error
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

        // Attempt graceful LSP shutdown first
        if (_process != null && !_process.HasExited && _rpc != null && _initialized)
        {
            try
            {
                // Send shutdown request and wait for response
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _rpc.InvokeWithCancellationAsync<object?>("shutdown", cancellationToken: cts.Token);

                // Send exit notification
                await _rpc.NotifyAsync("exit");

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
    }
}

/// <summary>
/// Represents an HTML projection of a Razor document.
/// </summary>
public record HtmlProjection(string RazorUri, string Checksum, string HtmlContent, int Version);
