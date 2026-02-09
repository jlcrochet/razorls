using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Protocol;

namespace RazorSharp.Server;

internal sealed class RoslynReverseRequestDispatcher
{
    readonly ILogger _logger;
    readonly Func<JsonElement?, string?> _getProgressToken;
    readonly Func<string, JsonElement?, CancellationToken, Task<JsonElement?>> _forwardRequestToClient;
    readonly Func<JsonElement, Task> _handleRazorUpdateHtml;
    readonly Func<JsonElement, CancellationToken, Task<JsonElement?>> _handleHtmlFormattingRequest;
    readonly Func<JsonElement, CancellationToken, Task<JsonElement?>> _handleHtmlRangeFormattingRequest;
    readonly Func<JsonElement> _createEmptyObjectResponse;

    public RoslynReverseRequestDispatcher(
        ILogger logger,
        Func<JsonElement?, string?> getProgressToken,
        Func<string, JsonElement?, CancellationToken, Task<JsonElement?>> forwardRequestToClient,
        Func<JsonElement, Task> handleRazorUpdateHtml,
        Func<JsonElement, CancellationToken, Task<JsonElement?>> handleHtmlFormattingRequest,
        Func<JsonElement, CancellationToken, Task<JsonElement?>> handleHtmlRangeFormattingRequest,
        Func<JsonElement> createEmptyObjectResponse)
    {
        _logger = logger;
        _getProgressToken = getProgressToken;
        _forwardRequestToClient = forwardRequestToClient;
        _handleRazorUpdateHtml = handleRazorUpdateHtml;
        _handleHtmlFormattingRequest = handleHtmlFormattingRequest;
        _handleHtmlRangeFormattingRequest = handleHtmlRangeFormattingRequest;
        _createEmptyObjectResponse = createEmptyObjectResponse;
    }

    public async Task<JsonElement?> HandleAsync(string method, JsonElement? @params, CancellationToken ct)
    {
        _logger.LogDebug("Roslyn reverse request: {Method}", method);

        try
        {
            // Handle progress creation requests - forward to editor for status bar spinners
            if (method == LspMethods.WindowWorkDoneProgressCreate)
            {
                var token = _getProgressToken(@params);
                _logger.LogDebug("Forwarding workDoneProgress/create to client: token={Token}", token ?? "<none>");
                return await _forwardRequestToClient(method, @params, ct);
            }

            // Handle razor/updateHtml - sync HTML projection to HTML LS
            if (method == LspMethods.RazorUpdateHtml && @params.HasValue)
            {
                await _handleRazorUpdateHtml(@params.Value);
                return _createEmptyObjectResponse();
            }

            // Handle HTML formatting requests from Roslyn's Razor extension
            if (method == LspMethods.TextDocumentFormatting && @params.HasValue)
            {
                return await _handleHtmlFormattingRequest(@params.Value, ct);
            }

            if (method == LspMethods.TextDocumentRangeFormatting && @params.HasValue)
            {
                return await _handleHtmlRangeFormattingRequest(@params.Value, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Roslyn reverse request: {Method}", method);
        }

        return null;
    }
}
