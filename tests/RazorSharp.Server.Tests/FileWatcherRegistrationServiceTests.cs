using RazorSharp.Protocol.Messages;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class FileWatcherRegistrationServiceTests
{
    [Fact]
    public async Task TryRegisterAsync_PassesDynamicRegistrationAndBaseUriToCoordinator()
    {
        var coordinator = new FakeCoordinator { Result = true };
        var service = new FileWatcherRegistrationService(
            coordinator,
            static () => "file:///workspace");

        var initParams = new InitializeParams
        {
            Capabilities = new ClientCapabilities
            {
                Workspace = new WorkspaceClientCapabilities
                {
                    DidChangeWatchedFiles = new DidChangeWatchedFilesClientCapabilities
                    {
                        DynamicRegistration = true
                    }
                }
            }
        };

        var registrar = new NoOpRegistrar();
        var result = await service.TryRegisterAsync(
            alreadyRegistered: false,
            initParams: initParams,
            registrar: registrar,
            ct: CancellationToken.None);

        Assert.True(result);
        Assert.True(coordinator.Called);
        Assert.False(coordinator.LastAlreadyRegistered);
        Assert.True(coordinator.LastDynamicRegistrationSupported);
        Assert.Equal("file:///workspace", coordinator.LastBaseUri);
        Assert.Same(registrar, coordinator.LastRegistrar);
    }

    [Fact]
    public async Task TryRegisterAsync_UsesFalseWhenCapabilityMissing()
    {
        var coordinator = new FakeCoordinator { Result = false };
        var service = new FileWatcherRegistrationService(
            coordinator,
            static () => null);

        var result = await service.TryRegisterAsync(
            alreadyRegistered: true,
            initParams: new InitializeParams(),
            registrar: null,
            ct: CancellationToken.None);

        Assert.False(result);
        Assert.True(coordinator.Called);
        Assert.True(coordinator.LastAlreadyRegistered);
        Assert.False(coordinator.LastDynamicRegistrationSupported);
        Assert.Null(coordinator.LastBaseUri);
        Assert.Null(coordinator.LastRegistrar);
    }

    sealed class FakeCoordinator : IFileWatcherRegistrationCoordinator
    {
        public bool Result { get; set; }
        public bool Called { get; private set; }
        public bool LastAlreadyRegistered { get; private set; }
        public bool LastDynamicRegistrationSupported { get; private set; }
        public string? LastBaseUri { get; private set; }
        public IClientCapabilityRegistrar? LastRegistrar { get; private set; }

        public Task<bool> TryRegisterAsync(
            bool alreadyRegistered,
            bool dynamicRegistrationSupported,
            string? baseUri,
            IClientCapabilityRegistrar? registrar,
            CancellationToken ct)
        {
            _ = ct;
            Called = true;
            LastAlreadyRegistered = alreadyRegistered;
            LastDynamicRegistrationSupported = dynamicRegistrationSupported;
            LastBaseUri = baseUri;
            LastRegistrar = registrar;
            return Task.FromResult(Result);
        }
    }

    sealed class NoOpRegistrar : IClientCapabilityRegistrar
    {
        public Task RegisterAsync(object parameters, CancellationToken ct)
        {
            _ = parameters;
            _ = ct;
            return Task.CompletedTask;
        }
    }
}
