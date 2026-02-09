using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Protocol.Messages;

namespace RazorSharp.Server;

internal sealed class WorkspaceWatchedFilesRequestParser
{
    readonly ILogger _logger;
    readonly JsonSerializerOptions _jsonOptions;

    public WorkspaceWatchedFilesRequestParser(ILogger logger, JsonSerializerOptions jsonOptions)
    {
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    public bool TryParse(bool fileWatchingEnabled, JsonElement paramsJson, out DidChangeWatchedFilesParams? @params)
    {
        @params = null;

        if (!fileWatchingEnabled)
        {
            _logger.LogDebug("Ignoring workspace/didChangeWatchedFiles (disabled by initOptions).");
            return false;
        }

        @params = JsonSerializer.Deserialize<DidChangeWatchedFilesParams>(paramsJson, _jsonOptions);
        return @params?.Changes != null && @params.Changes.Length > 0;
    }
}
