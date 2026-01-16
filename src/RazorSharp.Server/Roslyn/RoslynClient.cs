using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using StreamJsonRpc;

namespace RazorSharp.Server.Roslyn;

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

/// <summary>
/// Client that manages communication with the Roslyn language server subprocess.
/// </summary>
public class RoslynClient : IAsyncDisposable
{
    readonly ILogger<RoslynClient> _logger;
    Process? _process;
    JsonRpc? _rpc;
    bool _disposed;

    public event EventHandler<RoslynNotificationEventArgs>? NotificationReceived;
    public event Func<string, JsonElement?, CancellationToken, Task<JsonElement?>>? RequestReceived;

    public RoslynClient(ILogger<RoslynClient> logger)
    {
        _logger = logger;
    }

    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>
    /// Starts the Roslyn language server process.
    /// </summary>
    public async Task StartAsync(RoslynStartOptions options, CancellationToken cancellationToken)
    {
        if (_process != null)
        {
            throw new InvalidOperationException("Roslyn client is already started");
        }

        // Run via dotnet: dotnet exec <dll> <args>

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                "exec", options.ServerDllPath,
                "--stdio",
                $"--loglevel={options.LogLevel}",
                $"--razorDesignTimePath={options.RazorDesignTimePath}",
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

        _logger.LogInformation("Starting Roslyn: {} {}", psi.FileName, string.Join(' ', psi.ArgumentList));

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

        // Set up JSON-RPC connection
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            }
        };

        var handler = new HeaderDelimitedMessageHandler(_process.StandardInput.BaseStream, _process.StandardOutput.BaseStream, formatter);
        _rpc = new JsonRpc(handler);

        // Register handlers for notifications and reverse requests from Roslyn
        _rpc.AddLocalRpcTarget(new RoslynNotificationTarget(this));

        // Add a catch-all handler to see any unhandled requests
        _rpc.AllowModificationWhileListening = true;

        // Add tracing to see what's happening
        _rpc.TraceSource.Switch.Level = System.Diagnostics.SourceLevels.All;
        _rpc.TraceSource.Listeners.Add(new RpcTraceListener(_logger));

        _rpc.StartListening();

        // Log when we start
        _logger.LogInformation("RPC handlers registered, listening for messages");

        _logger.LogInformation("Roslyn language server started (PID: {Pid})", _process.Id);
    }

    /// <summary>
    /// Sends a request to Roslyn and waits for the response.
    /// </summary>
    public async Task<TResponse?> SendRequestAsync<TRequest, TResponse>(
        string method,
        TRequest parameters,
        CancellationToken cancellationToken)
    {
        EnsureConnected();

        _logger.LogDebug("Sending request to Roslyn: {Method}", method);
        return await _rpc!.InvokeWithParameterObjectAsync<TResponse>(method, parameters, cancellationToken);
    }

    /// <summary>
    /// Sends a request with raw JSON parameters.
    /// </summary>
    public async Task<JsonElement?> SendRequestAsync(
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        EnsureConnected();

        _logger.LogDebug("Sending request to Roslyn: {Method}", method);
        if (parameters.HasValue)
        {
            return await _rpc!.InvokeWithParameterObjectAsync<JsonElement?>(method, parameters.Value, cancellationToken);
        }
        return await _rpc!.InvokeAsync<JsonElement?>(method, cancellationToken);
    }

    /// <summary>
    /// Sends a notification to Roslyn (no response expected).
    /// </summary>
    public async Task SendNotificationAsync<TParams>(string method, TParams parameters)
    {
        EnsureConnected();

        _logger.LogDebug("Sending notification to Roslyn: {Method}", method);
        // Roslyn doesn't accept null params - use empty object instead
        if (parameters == null)
        {
            await _rpc!.NotifyWithParameterObjectAsync(method, new { });
        }
        else
        {
            await _rpc!.NotifyWithParameterObjectAsync(method, parameters);
        }
    }

    /// <summary>
    /// Sends a notification with raw JSON parameters.
    /// </summary>
    public async Task SendNotificationAsync(string method, JsonElement? parameters)
    {
        EnsureConnected();

        _logger.LogDebug("Sending notification to Roslyn: {Method}", method);
        if (parameters.HasValue)
        {
            await _rpc!.NotifyWithParameterObjectAsync(method, parameters.Value);
        }
        else
        {
            await _rpc!.NotifyAsync(method);
        }
    }

    internal void OnNotification(string method, JsonElement? @params)
    {
        _logger.LogDebug("Received notification from Roslyn: {Method}", method);
        NotificationReceived?.Invoke(this, new RoslynNotificationEventArgs
        {
            Method = method,
            Params = @params
        });
    }

    internal async Task<JsonElement?> OnRequestAsync(string method, JsonElement? @params, CancellationToken ct)
    {
        _logger.LogDebug("Received request from Roslyn: {Method}", method);
        if (RequestReceived != null)
        {
            return await RequestReceived.Invoke(method, @params, ct);
        }
        return null;
    }

    private void EnsureConnected()
    {
        if (_rpc == null || !IsRunning)
        {
            throw new InvalidOperationException("Roslyn client is not connected");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        _rpc?.Dispose();
        _rpc = null;

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

/// <summary>
/// Target class for receiving notifications and requests from Roslyn.
/// </summary>
internal class RoslynNotificationTarget
{
    readonly RoslynClient _client;

    public RoslynNotificationTarget(RoslynClient client)
    {
        _client = client;
    }

    // Razor notifications
    [JsonRpcMethod("razor/updateHtml", UseSingleObjectParameterDeserialization = true)]
    public void OnRazorUpdateHtml(JsonElement @params)
    {
        _client.OnNotification("razor/updateHtml", @params);
    }

    [JsonRpcMethod("razor/log", UseSingleObjectParameterDeserialization = true)]
    public void OnRazorLog(JsonElement @params)
    {
        _client.OnNotification("razor/log", @params);
    }

    // Workspace notifications
    [JsonRpcMethod("workspace/projectInitializationComplete")]
    public void OnProjectInitializationComplete()
    {
        _client.OnNotification("workspace/projectInitializationComplete", null);
    }

    [JsonRpcMethod("workspace/_roslyn_projectNeedsRestore", UseSingleObjectParameterDeserialization = true)]
    public void OnProjectNeedsRestore(JsonElement @params)
    {
        _client.OnNotification("workspace/_roslyn_projectNeedsRestore", @params);
    }

    [JsonRpcMethod("workspace/refreshSourceGeneratedDocument", UseSingleObjectParameterDeserialization = true)]
    public void OnRefreshSourceGeneratedDocument(JsonElement? @params)
    {
        _client.OnNotification("workspace/refreshSourceGeneratedDocument", @params);
    }

    // Client notifications (logging, messages)
    [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
    public void OnLogMessage(JsonElement @params)
    {
        _client.OnNotification("window/logMessage", @params);
    }

    [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)]
    public void OnShowMessage(JsonElement @params)
    {
        _client.OnNotification("window/showMessage", @params);
    }

    // Diagnostics
    [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
    public void OnPublishDiagnostics(JsonElement @params)
    {
        _client.OnNotification("textDocument/publishDiagnostics", @params);
    }

    // Client capability registration - Roslyn asks us to register capabilities dynamically
    [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
    public object OnRegisterCapability(JsonElement @params)
    {
        // Accept all capability registrations - return empty result to indicate success
        return new { };
    }

    [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
    public object OnUnregisterCapability(JsonElement @params)
    {
        // Accept all capability unregistrations
        return new { };
    }

    // Workspace configuration requests
    [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
    public object?[] OnWorkspaceConfiguration(JsonElement @params)
    {
        // Roslyn requests configuration for specific settings
        // We must return an array with one value per requested item
        Console.Error.WriteLine($"[CONFIGURATION] workspace/configuration request received!");
        Console.Error.WriteLine($"[CONFIGURATION] Params: {JsonSerializer.Serialize(@params)}");
        Console.Error.Flush();
        if (@params.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var results = new List<object?>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("section", out var section))
                {
                    var sectionName = section.GetString() ?? "";

                    // Return appropriate values for Razor settings
                    // These are the settings Roslyn's Razor extension asks for
                    var value = sectionName switch
                    {
                        "razor.format.code_block_brace_on_next_line" => (object?)false,
                        "razor.completion.commit_elements_with_space" => (object?)true,
                        "razor" => new { language_server = new { cohosting_enabled = true } },
                        _ => null // Return null for unknown settings
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

    // Roslyn may send requests that need responses (reverse direction)
    // These are forwarded HTML requests
    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> OnTextDocumentHover(JsonElement @params, CancellationToken ct)
    {
        return await _client.OnRequestAsync("textDocument/hover", @params, ct);
    }

    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> OnTextDocumentCompletion(JsonElement @params, CancellationToken ct)
    {
        return await _client.OnRequestAsync("textDocument/completion", @params, ct);
    }

    [JsonRpcMethod("textDocument/definition", UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> OnTextDocumentDefinition(JsonElement @params, CancellationToken ct)
    {
        return await _client.OnRequestAsync("textDocument/definition", @params, ct);
    }

    [JsonRpcMethod("textDocument/references", UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> OnTextDocumentReferences(JsonElement @params, CancellationToken ct)
    {
        return await _client.OnRequestAsync("textDocument/references", @params, ct);
    }

    [JsonRpcMethod("textDocument/implementation", UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> OnTextDocumentImplementation(JsonElement @params, CancellationToken ct)
    {
        return await _client.OnRequestAsync("textDocument/implementation", @params, ct);
    }

    [JsonRpcMethod("textDocument/documentHighlight", UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> OnTextDocumentDocumentHighlight(JsonElement @params, CancellationToken ct)
    {
        return await _client.OnRequestAsync("textDocument/documentHighlight", @params, ct);
    }

    [JsonRpcMethod("textDocument/signatureHelp", UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> OnTextDocumentSignatureHelp(JsonElement @params, CancellationToken ct)
    {
        return await _client.OnRequestAsync("textDocument/signatureHelp", @params, ct);
    }

    [JsonRpcMethod("textDocument/formatting", UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> OnTextDocumentFormatting(JsonElement @params, CancellationToken ct)
    {
        return await _client.OnRequestAsync("textDocument/formatting", @params, ct);
    }

    [JsonRpcMethod("textDocument/onTypeFormatting", UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> OnTextDocumentOnTypeFormatting(JsonElement @params, CancellationToken ct)
    {
        return await _client.OnRequestAsync("textDocument/onTypeFormatting", @params, ct);
    }

    [JsonRpcMethod("textDocument/foldingRange", UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> OnTextDocumentFoldingRange(JsonElement @params, CancellationToken ct)
    {
        return await _client.OnRequestAsync("textDocument/foldingRange", @params, ct);
    }

    [JsonRpcMethod("textDocument/documentColor", UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> OnTextDocumentDocumentColor(JsonElement @params, CancellationToken ct)
    {
        return await _client.OnRequestAsync("textDocument/documentColor", @params, ct);
    }

    [JsonRpcMethod("textDocument/colorPresentation", UseSingleObjectParameterDeserialization = true)]
    public async Task<JsonElement?> OnTextDocumentColorPresentation(JsonElement @params, CancellationToken ct)
    {
        return await _client.OnRequestAsync("textDocument/colorPresentation", @params, ct);
    }
}

/// <summary>
/// Trace listener to log JSON-RPC activity.
/// </summary>
internal class RpcTraceListener : System.Diagnostics.TraceListener
{
    readonly ILogger _logger;

    public RpcTraceListener(ILogger logger)
    {
        _logger = logger;
    }

    public override void Write(string? message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            _logger.LogWarning("[RPC Trace] {Message}", message);
            Console.Error.WriteLine($"[TRACE-W] {message}");
        }
    }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            Console.Error.WriteLine($"[TRACE-L] {message}");
        }
        if (!string.IsNullOrEmpty(message))
        {
            _logger.LogWarning("[RPC Trace] {Message}", message);
        }
    }
}
