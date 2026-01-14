using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RazorLS.Dependencies;
using RazorLS.Server.Configuration;

namespace RazorLS.Server.Roslyn;

/// <summary>
/// Client that manages communication with Roslyn using raw LSP protocol (no StreamJsonRpc).
/// This bypasses any potential issues with StreamJsonRpc's message handling.
/// </summary>
public class RoslynRawClient : IAsyncDisposable
{
    private readonly ILogger<RoslynRawClient> _logger;
    private Process? _process;
    private bool _disposed;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private long _nextId = 0;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pendingRequests = new();
    private ConfigurationLoader? _configurationLoader;

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

        var roslynArgs = BuildCommandLineArgs(options);
        var allArgs = $"exec \"{options.ServerDllPath}\" {string.Join(" ", roslynArgs)}";
        _logger.LogDebug("Starting Roslyn: dotnet {Args}", allArgs);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = allArgs,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["DOTNET_CLI_UI_LANGUAGE"] = "en"
            }
        };

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
        var messageBuffer = new MemoryStream();
        int contentLength = -1;
        var headerBuffer = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested && _process != null && !_process.HasExited)
            {
                var bytesRead = await _process.StandardOutput.BaseStream.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                messageBuffer.Write(buffer, 0, bytesRead);

                // Try to parse complete messages from buffer
                while (TryParseMessage(messageBuffer, ref contentLength, headerBuffer, out var message))
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

    private bool TryParseMessage(MemoryStream buffer, ref int contentLength, StringBuilder headerBuffer, out JsonDocument? message)
    {
        message = null;
        var data = buffer.ToArray();

        // If we don't have content length yet, look for headers
        if (contentLength < 0)
        {
            var str = Encoding.UTF8.GetString(data);
            var headerEnd = str.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) return false;

            var headers = str.Substring(0, headerEnd);
            foreach (var line in headers.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(line.Substring(15).Trim());
                    break;
                }
            }

            if (contentLength < 0) return false;

            // Remove headers from buffer
            var remaining = data.Skip(headerEnd + 4).ToArray();
            buffer.SetLength(0);
            buffer.Write(remaining, 0, remaining.Length);
            data = remaining;
        }

        // Check if we have complete content
        if (data.Length < contentLength) return false;

        // Parse JSON content
        var json = Encoding.UTF8.GetString(data, 0, contentLength);
        message = JsonDocument.Parse(json);

        // Remove processed content from buffer
        var rest = data.Skip(contentLength).ToArray();
        buffer.SetLength(0);
        buffer.Write(rest, 0, rest.Length);
        contentLength = -1;

        return true;
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
        if (@params == null) return Array.Empty<object?>();

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
        return Array.Empty<object?>();
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

    private async Task SendMessageAsync(object message)
    {
        if (_process == null) throw new InvalidOperationException("Process not started");

        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var content = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {content.Length}\r\n\r\n";

        var stream = _process.StandardInput.BaseStream;
        await stream.WriteAsync(Encoding.UTF8.GetBytes(header));
        await stream.WriteAsync(content);
        await stream.FlushAsync();
    }

    private static string[] BuildCommandLineArgs(RoslynStartOptions options)
    {
        var args = new List<string>
        {
            "--stdio",
            $"--logLevel={options.LogLevel}",
            $"--razorSourceGenerator={options.RazorSourceGeneratorPath}",
            $"--razorDesignTimePath={options.RazorDesignTimePath}",
            "--extension", options.RazorExtensionPath
        };

        if (!string.IsNullOrEmpty(options.LogDirectory))
        {
            args.Add($"--extensionLogDirectory={options.LogDirectory}");
        }

        return args.ToArray();
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

    public static RoslynStartOptions CreateStartOptions(DependencyManager deps, string? logDirectory = null)
    {
        return new RoslynStartOptions
        {
            ServerDllPath = deps.RoslynServerDllPath,
            RazorSourceGeneratorPath = deps.RazorSourceGeneratorPath,
            RazorDesignTimePath = deps.RazorDesignTimePath,
            RazorExtensionPath = deps.RazorExtensionDllPath,
            LogDirectory = logDirectory
        };
    }
}
