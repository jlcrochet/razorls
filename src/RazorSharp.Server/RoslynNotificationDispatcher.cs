using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Protocol;

namespace RazorSharp.Server;

internal sealed class RoslynNotificationDispatcher
{
    readonly ILogger _logger;
    readonly Action<JsonElement?> _handleRazorLog;
    readonly Func<bool> _isProjectInitialized;
    readonly Func<Task> _onProjectInitializationComplete;
    readonly Func<string, JsonElement?, Task> _forwardNotificationToClient;
    readonly Func<JsonElement?, string?> _getProgressToken;
    readonly Func<JsonElement?, string?> _getProgressKind;

    public RoslynNotificationDispatcher(
        ILogger logger,
        Action<JsonElement?> handleRazorLog,
        Func<bool> isProjectInitialized,
        Func<Task> onProjectInitializationComplete,
        Func<string, JsonElement?, Task> forwardNotificationToClient,
        Func<JsonElement?, string?> getProgressToken,
        Func<JsonElement?, string?> getProgressKind)
    {
        _logger = logger;
        _handleRazorLog = handleRazorLog;
        _isProjectInitialized = isProjectInitialized;
        _onProjectInitializationComplete = onProjectInitializationComplete;
        _forwardNotificationToClient = forwardNotificationToClient;
        _getProgressToken = getProgressToken;
        _getProgressKind = getProgressKind;
    }

    public async Task HandleAsync(RoslynNotificationWorkItem item, CancellationToken ct)
    {
        _ = ct;
        try
        {
            switch (item.Method)
            {
                case LspMethods.RazorLog:
                    _handleRazorLog(item.Params);
                    break;

                case LspMethods.TextDocumentPublishDiagnostics:
                    if (!_isProjectInitialized())
                    {
                        _logger.LogDebug("Skipping diagnostics publish before project initialization completes");
                        break;
                    }

                    if (item.Params.HasValue)
                    {
                        var uri = item.Params.Value.TryGetProperty("uri", out var uriProp) ? uriProp.GetString() : "unknown";
                        var diagCount = item.Params.Value.TryGetProperty("diagnostics", out var diags) && diags.ValueKind == JsonValueKind.Array
                            ? diags.GetArrayLength()
                            : 0;
                        _logger.LogInformation("Publishing {Count} diagnostics for {Uri}", diagCount, uri);
                    }

                    await _forwardNotificationToClient(item.Method, item.Params);
                    break;

                case LspMethods.ProjectInitializationComplete:
                    await _onProjectInitializationComplete();
                    break;

                case "window/logMessage":
                    await HandleWindowLogMessageAsync(item);
                    break;

                case "window/showMessage":
                    await _forwardNotificationToClient(item.Method, item.Params);
                    break;

                case LspMethods.Progress:
                    var progressToken = _getProgressToken(item.Params);
                    var progressKind = _getProgressKind(item.Params);
                    _logger.LogDebug("Forwarding progress to client: token={Token}, kind={Kind}",
                        progressToken ?? "<none>",
                        progressKind ?? "<none>");
                    await _forwardNotificationToClient(item.Method, item.Params);
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

    private async Task HandleWindowLogMessageAsync(RoslynNotificationWorkItem item)
    {
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
                return;
            }

            if (message != null)
            {
                _logger.LogDebug("[Roslyn] {Message}", message);
            }

            var clampedType = Math.Clamp(type, 1, 4);
            if (clampedType != type)
            {
                var forwardedParams = JsonSerializer.SerializeToElement(new { type = clampedType, message });
                await _forwardNotificationToClient(item.Method, forwardedParams);
                return;
            }

            await _forwardNotificationToClient(item.Method, item.Params);
            return;
        }

        await _forwardNotificationToClient(item.Method, item.Params);
    }
}
