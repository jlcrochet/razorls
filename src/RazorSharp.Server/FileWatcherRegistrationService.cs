using RazorSharp.Protocol.Messages;

namespace RazorSharp.Server;

internal sealed class FileWatcherRegistrationService
{
    readonly IFileWatcherRegistrationCoordinator _coordinator;
    readonly Func<string?> _getWorkspaceBaseUri;

    public FileWatcherRegistrationService(
        IFileWatcherRegistrationCoordinator coordinator,
        Func<string?> getWorkspaceBaseUri)
    {
        _coordinator = coordinator;
        _getWorkspaceBaseUri = getWorkspaceBaseUri;
    }

    public Task<bool> TryRegisterAsync(
        bool alreadyRegistered,
        InitializeParams? initParams,
        IClientCapabilityRegistrar? registrar,
        CancellationToken ct)
    {
        var dynamicRegistrationSupported =
            initParams?.Capabilities?.Workspace?.DidChangeWatchedFiles?.DynamicRegistration == true;
        var baseUri = _getWorkspaceBaseUri();
        return _coordinator.TryRegisterAsync(
            alreadyRegistered,
            dynamicRegistrationSupported,
            baseUri,
            registrar,
            ct);
    }
}
