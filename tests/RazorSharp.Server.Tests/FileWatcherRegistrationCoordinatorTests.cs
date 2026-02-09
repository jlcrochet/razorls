using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class FileWatcherRegistrationCoordinatorTests
{
    [Fact]
    public async Task TryRegisterAsync_SkipsWhenAlreadyRegistered()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var calls = 0;
        var coordinator = CreateCoordinator(loggerFactory);

        var result = await coordinator.TryRegisterAsync(
            alreadyRegistered: true,
            dynamicRegistrationSupported: true,
            baseUri: "file:///workspace",
            registrar: new TestRegistrar(_ =>
            {
                calls++;
                return Task.CompletedTask;
            }),
            ct: CancellationToken.None);

        Assert.True(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TryRegisterAsync_SkipsWhenDynamicRegistrationUnsupported()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var calls = 0;
        var coordinator = CreateCoordinator(loggerFactory);

        var result = await coordinator.TryRegisterAsync(
            alreadyRegistered: false,
            dynamicRegistrationSupported: false,
            baseUri: "file:///workspace",
            registrar: new TestRegistrar(_ =>
            {
                calls++;
                return Task.CompletedTask;
            }),
            ct: CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TryRegisterAsync_RegistersWithExpectedPayload()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        object? captured = null;
        var coordinator = CreateCoordinator(loggerFactory);

        var result = await coordinator.TryRegisterAsync(
            alreadyRegistered: false,
            dynamicRegistrationSupported: true,
            baseUri: "file:///workspace",
            registrar: new TestRegistrar(payload =>
            {
                captured = payload;
                return Task.CompletedTask;
            }),
            ct: CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(captured);

        var json = JsonSerializer.SerializeToElement(captured);
        var registrations = json.GetProperty("registrations");
        Assert.Single(registrations.EnumerateArray());
        var registration = registrations[0];
        Assert.Equal("watcher-registration", registration.GetProperty("id").GetString());
        Assert.Equal("workspace/didChangeWatchedFiles", registration.GetProperty("method").GetString());
        var watcher = registration.GetProperty("registerOptions").GetProperty("watchers")[0];
        Assert.Equal("**/*.sln", watcher.GetProperty("globPattern").GetProperty("pattern").GetString());
    }

    [Fact]
    public async Task TryRegisterAsync_ReturnsFalseWhenRegistrationThrows()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var coordinator = CreateCoordinator(loggerFactory);

        var result = await coordinator.TryRegisterAsync(
            alreadyRegistered: false,
            dynamicRegistrationSupported: true,
            baseUri: "file:///workspace",
            registrar: new TestRegistrar(_ => throw new InvalidOperationException("boom")),
            ct: CancellationToken.None);

        Assert.False(result);
    }

    static FileWatcherRegistrationCoordinator CreateCoordinator(ILoggerFactory loggerFactory)
    {
        return new FileWatcherRegistrationCoordinator(
            loggerFactory.CreateLogger<FileWatcherRegistrationCoordinator>(),
            "watcher-registration",
            "workspace/didChangeWatchedFiles",
            static baseUri => baseUri == null
                ? [new { globPattern = "**/*.sln", kind = 7 }]
                : [new { globPattern = new { baseUri, pattern = "**/*.sln" }, kind = 7 }]);
    }

    sealed class TestRegistrar : IClientCapabilityRegistrar
    {
        readonly Func<object, Task> _register;

        public TestRegistrar(Func<object, Task> register)
        {
            _register = register;
        }

        public Task RegisterAsync(object parameters, CancellationToken ct)
        {
            _ = ct;
            return _register(parameters);
        }
    }
}
