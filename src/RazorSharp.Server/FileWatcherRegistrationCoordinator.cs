using Microsoft.Extensions.Logging;

namespace RazorSharp.Server;

internal interface IFileWatcherRegistrationCoordinator
{
    Task<bool> TryRegisterAsync(
        bool alreadyRegistered,
        bool dynamicRegistrationSupported,
        string? baseUri,
        IClientCapabilityRegistrar? registrar,
        CancellationToken ct);
}

internal sealed class FileWatcherRegistrationCoordinator : IFileWatcherRegistrationCoordinator
{
    readonly ILogger _logger;
    readonly string _registrationId;
    readonly string _watchedFilesMethod;
    readonly Func<string?, object[]> _createFileWatchers;

    public FileWatcherRegistrationCoordinator(
        ILogger logger,
        string registrationId,
        string watchedFilesMethod,
        Func<string?, object[]> createFileWatchers)
    {
        _logger = logger;
        _registrationId = registrationId;
        _watchedFilesMethod = watchedFilesMethod;
        _createFileWatchers = createFileWatchers;
    }

    public async Task<bool> TryRegisterAsync(
        bool alreadyRegistered,
        bool dynamicRegistrationSupported,
        string? baseUri,
        IClientCapabilityRegistrar? registrar,
        CancellationToken ct)
    {
        if (alreadyRegistered)
        {
            return true;
        }

        if (!dynamicRegistrationSupported)
        {
            _logger.LogDebug("Client does not support dynamic didChangeWatchedFiles registration; skipping registration");
            return false;
        }

        if (baseUri == null)
        {
            _logger.LogInformation("Workspace baseUri not available; file watchers will not be scoped to a workspace root.");
        }

        if (registrar == null)
        {
            return false;
        }

        var registrations = new[]
        {
            new
            {
                id = _registrationId,
                method = _watchedFilesMethod,
                registerOptions = new
                {
                    watchers = _createFileWatchers(baseUri)
                }
            }
        };

        try
        {
            await registrar.RegisterAsync(new { registrations }, ct);
            _logger.LogInformation("Registered workspace/didChangeWatchedFiles with client");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register workspace/didChangeWatchedFiles with client");
            return false;
        }
    }
}
