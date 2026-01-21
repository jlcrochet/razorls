using System.Buffers.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Server.Configuration;

namespace RazorSharp.Server.Roslyn;

/// <summary>
/// Client that manages communication with Roslyn using raw LSP protocol.
/// </summary>
public class RoslynClient : IAsyncDisposable
{
    readonly ILogger<RoslynClient> _logger;
    Process? _process;
    bool _disposed;
    CancellationTokenSource? _readCts;
    Task? _readTask;
    long _nextId = 0;
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pendingRequests = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    ConfigurationLoader? _configurationLoader;

    public event EventHandler<RoslynNotificationEventArgs>? NotificationReceived;
    public event Func<string, JsonElement?, long, CancellationToken, Task<JsonElement?>>? RequestReceived;
    public event EventHandler<int>? ProcessExited;

    public RoslynClient(ILogger<RoslynClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Sets the configuration loader for handling workspace/configuration requests.
    /// </summary>
    public void SetConfigurationLoader(ConfigurationLoader configurationLoader)
    {
        _configurationLoader = configurationLoader;
    }

    public bool IsRunning => _process != null && !_process.HasExited;

    public async Task StartAsync(RoslynStartOptions options, CancellationToken cancellationToken)
    {
        if (_process != null)
        {
            throw new InvalidOperationException("Roslyn client is already started");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                options.ServerDllPath,
                "--stdio",
                $"--logLevel={options.LogLevel}",
                $"--razorSourceGenerator={options.RazorSourceGeneratorPath}",
                $"--razorDesignTimePath={options.RazorDesignTimePath}",
                "--extension", options.RazorExtensionPath
            },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(options.LogDirectory))
        {
            psi.ArgumentList.Add($"--extensionLogDirectory={options.LogDirectory}");
        }

        _logger.LogDebug("Starting Roslyn: dotnet {Args}", string.Join(' ', psi.ArgumentList));

        _process = Process.Start(psi);
        if (_process == null)
        {
            throw new InvalidOperationException("Failed to start Roslyn process");
        }

        // Capture stderr for logging
        _ = Task.Run(async () =>
        {
            while (!_process.HasExited)
            {
                var line = await _process.StandardError.ReadLineAsync(cancellationToken);
                if (line != null)
                {
                    _logger.LogDebug("[Roslyn stderr] {Line}", line);
                }
            }
        }, cancellationToken);

        // Monitor process exit
        _ = Task.Run(async () =>
        {
            try
            {
                await _process.WaitForExitAsync(cancellationToken);
                var exitCode = _process.ExitCode;
                _logger.LogWarning("Roslyn process exited with code {ExitCode}", exitCode);

                // Fail all pending requests
                foreach (var kvp in _pendingRequests)
                {
                    kvp.Value.TrySetException(new IOException($"Roslyn process exited with code {exitCode}"));
                }

                ProcessExited?.Invoke(this, exitCode);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }, cancellationToken);

        // Start reading LSP messages from stdout
        _readCts = new CancellationTokenSource();
        _readTask = ReadMessagesAsync(_readCts.Token);

        _logger.LogInformation("Roslyn language server started (PID: {Pid})", _process.Id);
    }

    private async Task ReadMessagesAsync(CancellationToken ct)
    {
        var parser = new LspMessageParser();

        try
        {
            while (!ct.IsCancellationRequested && _process != null && !_process.HasExited)
            {
                var buffer = parser.GetBuffer();
                var bytesRead = await _process.StandardOutput.BaseStream.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                parser.Advance(bytesRead);

                // Try to parse complete messages from buffer
                while (parser.TryParseMessage(out var message))
                {
                    if (message != null)
                    {
                        await ProcessMessageAsync(message, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading from Roslyn");
        }
    }

    private async Task ProcessMessageAsync(JsonDocument doc, CancellationToken ct)
    {
        var root = doc.RootElement;

        // Check if it's a response to our request
        if (root.TryGetProperty("id", out var idProp) && !root.TryGetProperty("method", out _))
        {
            // It's a response
            if (idProp.ValueKind == JsonValueKind.Number)
            {
                var id = idProp.GetInt64();
                if (_pendingRequests.TryRemove(id, out var tcs))
                {
                    if (root.TryGetProperty("result", out var result))
                    {
                        tcs.TrySetResult(result.Clone());
                    }
                    else if (root.TryGetProperty("error", out var error))
                    {
                        tcs.TrySetException(new Exception($"JSON-RPC error: {error}"));
                    }
                    else
                    {
                        tcs.TrySetResult(null);
                    }
                }
            }
            return;
        }

        // Check if it's a request or notification from Roslyn
        if (root.TryGetProperty("method", out var methodProp))
        {
            var method = methodProp.GetString()!;
            JsonElement? @params = root.TryGetProperty("params", out var p) ? p.Clone() : null;

            _logger.LogDebug("Received from Roslyn: {Method}", method);

            // Check if it's a request (has id) or notification (no id)
            if (root.TryGetProperty("id", out var reqIdProp))
            {
                // It's a request - we need to respond
                long reqId = reqIdProp.ValueKind == JsonValueKind.Number ? reqIdProp.GetInt64() : 0;
                _logger.LogDebug("Received request from Roslyn: {Method} (id:{Id})", method, reqId);

                // Handle workspace/configuration specially
                if (method == "workspace/configuration")
                {
                    _logger.LogDebug("Handling workspace/configuration request");
                    var result = HandleWorkspaceConfiguration(@params);
                    await SendResponseAsync(reqId, result);
                    return;
                }

                // Handle client/registerCapability - accept all registrations
                if (method == "client/registerCapability")
                {
                    _logger.LogDebug("Accepting client/registerCapability request");
                    await SendResponseAsync(reqId, new { });
                    return;
                }

                // Handle client/unregisterCapability - accept all unregistrations
                if (method == "client/unregisterCapability")
                {
                    _logger.LogDebug("Accepting client/unregisterCapability request");
                    await SendResponseAsync(reqId, new { });
                    return;
                }

                // Forward other requests
                if (RequestReceived != null)
                {
                    _logger.LogDebug("Forwarding request from Roslyn: {Method} (id:{Id})", method, reqId);
                    var result = await RequestReceived.Invoke(method, @params, reqId, ct);
                    await SendResponseAsync(reqId, result);
                }
                else
                {
                    // Send empty response for unhandled requests
                    _logger.LogWarning("No handler for request from Roslyn: {Method} (id:{Id})", method, reqId);
                    await SendResponseAsync(reqId, null);
                }
            }
            else
            {
                // It's a notification
                NotificationReceived?.Invoke(this, new RoslynNotificationEventArgs
                {
                    Method = method,
                    Params = @params
                });
            }
        }

        doc.Dispose();
    }

    private object?[] HandleWorkspaceConfiguration(JsonElement? @params)
    {
        if (@params == null) return [];

        _logger.LogDebug("workspace/configuration request received");

        if (@params.Value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var results = new List<object?>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("section", out var section))
                {
                    var sectionName = section.GetString();
                    if (string.IsNullOrEmpty(sectionName))
                    {
                        _logger.LogWarning("workspace/configuration request contained empty section name");
                        results.Add(null);
                        continue;
                    }
                    _logger.LogDebug("Config section requested: {Section}", sectionName);

                    // First check hardcoded settings (required for cohosting and diagnostics)
                    var value = sectionName switch
                    {
                        "razor.format.code_block_brace_on_next_line" => (object?)false,
                        "razor.completion.commit_elements_with_space" => (object?)true,
                        "razor" => new { language_server = new { cohosting_enabled = true } },
                        // Enable diagnostics for open documents by default
                        "csharp|background_analysis.dotnet_compiler_diagnostics_scope" => "openFiles",
                        "csharp|background_analysis.dotnet_analyzer_diagnostics_scope" => "openFiles",
                        "visual_basic|background_analysis.dotnet_compiler_diagnostics_scope" => "openFiles",
                        "visual_basic|background_analysis.dotnet_analyzer_diagnostics_scope" => "openFiles",
                        _ => _configurationLoader?.GetConfigurationValue(sectionName)
                    };
                    results.Add(value);
                }
                else
                {
                    _logger.LogWarning("workspace/configuration request item missing 'section' property");
                    results.Add(null);
                }
            }
            return results.ToArray();
        }
        return [];
    }

    public async Task<TResponse?> SendRequestAsync<TRequest, TResponse>(
        string method,
        TRequest parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement?>();
        _pendingRequests[id] = tcs;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var request = new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters
            };

            await SendMessageAsync(request);
            _logger.LogDebug("Sent request to Roslyn: {Method} (id:{Id})", method, id);

            var result = await tcs.Task.WaitAsync(cts.Token);
            if (result == null) return default;

            return JsonSerializer.Deserialize<TResponse>(result.Value);
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    public async Task<JsonElement?> SendRequestAsync(
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement?>();
        _pendingRequests[id] = tcs;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            object request;
            if (parameters.HasValue)
            {
                request = new
                {
                    jsonrpc = "2.0",
                    id,
                    method,
                    @params = parameters.Value
                };
            }
            else
            {
                request = new
                {
                    jsonrpc = "2.0",
                    id,
                    method
                };
            }

            await SendMessageAsync(request);
            _logger.LogDebug("Sent request to Roslyn: {Method} (id:{Id})", method, id);

            return await tcs.Task.WaitAsync(cts.Token);
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    public async Task SendNotificationAsync<TParams>(string method, TParams parameters)
    {
        object notification;
        if (parameters == null)
        {
            // Don't include null params - Roslyn doesn't like it
            notification = new
            {
                jsonrpc = "2.0",
                method,
                @params = new { }  // Empty object instead of null
            };
        }
        else
        {
            notification = new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters
            };
        }

        await SendMessageAsync(notification);
        _logger.LogDebug("Sent notification to Roslyn: {Method}", method);
    }

    public async Task SendNotificationAsync(string method, JsonElement? parameters)
    {
        object notification;
        if (parameters.HasValue)
        {
            notification = new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters.Value
            };
        }
        else
        {
            notification = new
            {
                jsonrpc = "2.0",
                method
            };
        }

        await SendMessageAsync(notification);
        _logger.LogDebug("Sent notification to Roslyn: {Method}", method);
    }

    private async Task SendResponseAsync(long id, object? result)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id,
            result
        };

        await SendMessageAsync(response);
        _logger.LogDebug("Sent response to Roslyn: id:{Id}", id);
    }

    // Reusable JsonSerializerOptions for outbound messages
    static readonly JsonSerializerOptions SendJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Reusable header buffer for outbound messages (thread-safe via lock in SendMessageAsync)
    readonly byte[] _headerBuffer = new byte[32]; // "Content-Length: " (16) + digits (max 10) + "\r\n\r\n" (4)

    private async Task SendMessageAsync(object message)
    {
        if (_process == null) throw new InvalidOperationException("Process not started");
        if (_process.HasExited) throw new IOException($"Roslyn process has exited (code {_process.ExitCode})");

        // Serialize directly to UTF-8 bytes (no intermediate string)
        var content = JsonSerializer.SerializeToUtf8Bytes(message, SendJsonOptions);

        // Serialize writes to prevent interleaved messages when multiple requests are sent concurrently
        // The lock also protects _headerBuffer which is a shared instance field
        await _sendLock.WaitAsync();
        try
        {
            // Build header directly in bytes: "Content-Length: {length}\r\n\r\n"
            "Content-Length: "u8.CopyTo(_headerBuffer);
            Utf8Formatter.TryFormat(content.Length, _headerBuffer.AsSpan(16), out var bytesWritten);
            "\r\n\r\n"u8.CopyTo(_headerBuffer.AsSpan(16 + bytesWritten));
            var headerLength = 16 + bytesWritten + 4;

            var stream = _process.StandardInput.BaseStream;
            await stream.WriteAsync(_headerBuffer.AsMemory(0, headerLength));
            await stream.WriteAsync(content);
            await stream.FlushAsync();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Attempt graceful LSP shutdown first
        if (_process != null && !_process.HasExited)
        {
            try
            {
                // Send shutdown request and wait for response
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendRequestAsync<object, object>("shutdown", new { }, cts.Token);

                // Send exit notification
                await SendNotificationAsync("exit", (object?)null);

                // Wait briefly for process to exit gracefully
                if (!_process.WaitForExit(2000))
                {
                    _logger.LogWarning("Roslyn did not exit gracefully, forcing termination");
                    _process.Kill(entireProcessTree: true);
                }
                else
                {
                    _logger.LogDebug("Roslyn exited gracefully");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during graceful shutdown, forcing termination");
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

        _readCts?.Cancel();
        _sendLock.Dispose();
    }

    public static RoslynStartOptions CreateStartOptions(DependencyManager deps, string? logDirectory = null, string logLevel = "Information")
    {
        return new RoslynStartOptions
        {
            ServerDllPath = deps.RoslynServerDllPath,
            RazorSourceGeneratorPath = deps.RazorSourceGeneratorPath,
            RazorDesignTimePath = deps.RazorDesignTimePath,
            RazorExtensionPath = deps.RazorExtensionDllPath,
            LogDirectory = logDirectory,
            LogLevel = logLevel
        };
    }
}

/// <summary>
/// Configuration for starting the Roslyn language server.
/// </summary>
public record RoslynStartOptions
{
    public required string ServerDllPath { get; init; }
    public required string RazorSourceGeneratorPath { get; init; }
    public required string RazorDesignTimePath { get; init; }
    public required string RazorExtensionPath { get; init; }
    public string? LogDirectory { get; init; }
    public string LogLevel { get; init; } = "Information";
}

/// <summary>
/// Event args for notifications received from Roslyn.
/// </summary>
public class RoslynNotificationEventArgs : EventArgs
{
    public required string Method { get; init; }
    public JsonElement? Params { get; init; }
}
