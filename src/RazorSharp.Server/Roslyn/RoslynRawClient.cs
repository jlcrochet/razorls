using System.Buffers.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Server.Configuration;

namespace RazorSharp.Server.Roslyn;

/// <summary>
/// Client that manages communication with Roslyn using raw LSP protocol (no StreamJsonRpc).
/// This bypasses any potential issues with StreamJsonRpc's message handling.
/// </summary>
public class RoslynRawClient : IAsyncDisposable
{
    readonly ILogger<RoslynRawClient> _logger;
    Process? _process;
    bool _disposed;
    CancellationTokenSource? _readCts;
    Task? _readTask;
    long _nextId = 0;
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pendingRequests = new();
    ConfigurationLoader? _configurationLoader;

    public event EventHandler<RoslynNotificationEventArgs>? NotificationReceived;
    public event Func<string, JsonElement?, long, CancellationToken, Task<JsonElement?>>? RequestReceived;

    public RoslynRawClient(ILogger<RoslynRawClient> logger)
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

        // Start reading LSP messages from stdout
        _readCts = new CancellationTokenSource();
        _readTask = ReadMessagesAsync(_readCts.Token);

        _logger.LogInformation("Roslyn language server started (PID: {Pid})", _process.Id);
    }

    private async Task ReadMessagesAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];
        var parser = new LspMessageParser();

        try
        {
            while (!ct.IsCancellationRequested && _process != null && !_process.HasExited)
            {
                var bytesRead = await _process.StandardOutput.BaseStream.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                parser.Append(buffer, bytesRead);

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
                    var sectionName = section.GetString() ?? "";
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

            return JsonSerializer.Deserialize<TResponse>(result.Value.GetRawText());
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

        // Serialize directly to UTF-8 bytes (no intermediate string)
        var content = JsonSerializer.SerializeToUtf8Bytes(message, SendJsonOptions);

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

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _readCts?.Cancel();
        // Don't wait for read task - just kill the process

        if (_process != null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore - process may already be dead
            }
            _process.Dispose();
            _process = null;
        }

        return ValueTask.CompletedTask;
    }

    public static RoslynStartOptions CreateStartOptions(DependencyManager deps, string? logDirectory = null, string? logLevel = null)
    {
        return new RoslynStartOptions
        {
            ServerDllPath = deps.RoslynServerDllPath,
            RazorSourceGeneratorPath = deps.RazorSourceGeneratorPath,
            RazorDesignTimePath = deps.RazorDesignTimePath,
            RazorExtensionPath = deps.RazorExtensionDllPath,
            LogDirectory = logDirectory,
            LogLevel = logLevel ?? "Information"
        };
    }
}
