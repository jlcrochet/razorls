using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RazorSharp.Server;

internal sealed class RoslynClientCommandDispatcher
{
    readonly ILogger _logger;
    readonly string _nestedCodeActionCommand;
    readonly string _fixAllCodeActionCommand;
    readonly string _completionComplexEditCommand;
    readonly Func<JsonElement, CancellationToken, Task<JsonElement?>> _handleNestedCodeAction;
    readonly Func<JsonElement, CancellationToken, Task<JsonElement?>> _handleFixAllCodeAction;

    public RoslynClientCommandDispatcher(
        ILogger logger,
        string nestedCodeActionCommand,
        string fixAllCodeActionCommand,
        string completionComplexEditCommand,
        Func<JsonElement, CancellationToken, Task<JsonElement?>> handleNestedCodeAction,
        Func<JsonElement, CancellationToken, Task<JsonElement?>> handleFixAllCodeAction)
    {
        _logger = logger;
        _nestedCodeActionCommand = nestedCodeActionCommand;
        _fixAllCodeActionCommand = fixAllCodeActionCommand;
        _completionComplexEditCommand = completionComplexEditCommand;
        _handleNestedCodeAction = handleNestedCodeAction;
        _handleFixAllCodeAction = handleFixAllCodeAction;
    }

    public async Task<JsonElement?> HandleAsync(string command, JsonElement @params, CancellationToken ct)
    {
        _logger.LogDebug("Handling Roslyn client command: {Command}", command);

        switch (command)
        {
            case var nested when string.Equals(nested, _nestedCodeActionCommand, StringComparison.Ordinal):
                return await _handleNestedCodeAction(@params, ct);

            case var fixAll when string.Equals(fixAll, _fixAllCodeActionCommand, StringComparison.Ordinal):
                // Fix All actions need special handling - forward to Roslyn's fix all resolver
                return await _handleFixAllCodeAction(@params, ct);

            case var completion when string.Equals(completion, _completionComplexEditCommand, StringComparison.Ordinal):
                // Complex completion edits - these should be handled by the editor
                // We can't do much here as they require cursor positioning
                _logger.LogDebug("completionComplexEdit: Ignoring (editor should handle)");
                return null;

            default:
                _logger.LogWarning("Unknown Roslyn client command: {Command}", command);
                return null;
        }
    }
}
