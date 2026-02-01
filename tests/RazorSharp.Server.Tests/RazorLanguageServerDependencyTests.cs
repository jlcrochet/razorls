using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Protocol;
using RazorSharp.Protocol.Messages;
using RazorSharp.Server;
using RazorSharp.Server.Configuration;

namespace RazorSharp.Server.Tests;

public class RazorLanguageServerDependencyTests
{
    [Fact]
    public async Task PinnedVersions_DisableAutoUpdateAndCheckUpdates()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        try
        {
            server.SetAutoUpdateEnabledFromCli(true);
            server.SetForceUpdateCheck(true);
            server.ApplyAutoUpdateSettingsForTests(new RoslynOptions { AutoUpdate = true });

            Assert.True(server.AutoUpdateEnabledForTests);
            Assert.True(server.ForceUpdateCheckForTests);

            server.ApplyDependencySettingsForTests(new DependencyOptions
            {
                PinnedRoslynVersion = "1.2.3",
                PinnedExtensionVersion = "4.5.6"
            });

            Assert.True(deps.HasPinnedVersions);
            Assert.False(server.AutoUpdateEnabledForTests);
            Assert.False(server.ForceUpdateCheckForTests);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task PinnedVersions_DisableAutoUpdate_WhenOnlyOnePinned()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        try
        {
            server.SetAutoUpdateEnabledFromCli(true);
            server.SetForceUpdateCheck(true);
            server.ApplyAutoUpdateSettingsForTests(new RoslynOptions { AutoUpdate = true });

            server.ApplyDependencySettingsForTests(new DependencyOptions
            {
                PinnedRoslynVersion = "1.2.3"
            });

            Assert.True(deps.HasPinnedVersions);
            Assert.False(server.AutoUpdateEnabledForTests);
            Assert.False(server.ForceUpdateCheckForTests);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task MissingDependencies_WithAutoUpdate_StartsBackgroundDownloadAndStartsRoslyn()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var loggerFactory = LoggerFactory.Create(builder => { });
            using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test", tempRoot);
            var server = new RazorLanguageServer(loggerFactory, deps);
            try
            {
                var notificationSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var startRoslynCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var ensureCalled = 0;
                var requestCalled = false;

                deps.EnsureDependenciesOverride = (_, __) =>
                {
                    Interlocked.Increment(ref ensureCalled);
                    return Task.FromResult(true);
                };

                server.SetStartRoslynOverrideForTests(_ =>
                {
                    startRoslynCalled.TrySetResult(true);
                    return Task.FromResult(true);
                });

                server.SetClientRequestOverrideForTests((method, @params, _) =>
                {
                    requestCalled = true;
                    return Task.FromResult<JsonElement?>(null);
                });

                server.SetClientNotificationOverrideForTests((method, @params) =>
                {
                    if (method == LspMethods.WindowShowMessage)
                    {
                        notificationSent.TrySetResult(true);
                    }
                    return Task.CompletedTask;
                });

                var initParams = new InitializeParams
                {
                    Capabilities = new ClientCapabilities
                    {
                        Window = new WindowClientCapabilities
                        {
                            ShowMessage = new ShowMessageRequestClientCapabilities()
                        }
                    }
                };

                var initJson = JsonSerializer.SerializeToElement(initParams);
                await server.HandleInitializeAsync(initJson, CancellationToken.None);
                server.HandleInitialized();

                var downloadTask = server.GetDependencyDownloadTaskForTests();
                Assert.NotNull(downloadTask);
                await AwaitOrTimeout(downloadTask!, 2000, "Background dependency download did not complete.");
                await AwaitOrTimeout(startRoslynCalled.Task, 2000, "Roslyn start was not requested.");
                await AwaitOrTimeout(notificationSent.Task, 2000, "User notification was not sent.");

                Assert.Equal(1, ensureCalled);
                Assert.False(requestCalled);
            }
            finally
            {
                await server.DisposeAsync();
            }
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task InitializeAsync_AllowsNonFileRootUri()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var loggerFactory = LoggerFactory.Create(builder => { });
            using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test", tempRoot);

            Directory.CreateDirectory(deps.RoslynPath);
            Directory.CreateDirectory(deps.RazorExtensionPath);
            File.WriteAllText(Path.Combine(deps.RoslynPath, "Microsoft.CodeAnalysis.LanguageServer.dll"), "ok");
            File.WriteAllText(Path.Combine(deps.RazorExtensionPath, "Microsoft.CodeAnalysis.Razor.Compiler.dll"), "ok");
            File.WriteAllText(Path.Combine(deps.RazorExtensionPath, "Microsoft.VisualStudioCode.RazorExtension.dll"), "ok");

            var server = new RazorLanguageServer(loggerFactory, deps);
            try
            {
                server.SetStartRoslynOverrideForTests(_ => Task.FromResult(true));
                var initParams = new InitializeParams { RootUri = "untitled:workspace" };
                var initJson = JsonSerializer.SerializeToElement(initParams);

                await server.HandleInitializeAsync(initJson, CancellationToken.None);

                var workspaceRoot = (string?)GetPrivateField(server, "_workspaceRoot");
                Assert.Null(workspaceRoot);
            }
            finally
            {
                await server.DisposeAsync();
            }
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    static async Task AwaitOrTimeout(Task task, int timeoutMs, string message)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)) == task;
        Assert.True(completed, message);
    }

    static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "razorsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    static void DeleteTempDir(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    static object? GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target);
    }
}
