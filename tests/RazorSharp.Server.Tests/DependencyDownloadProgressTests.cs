using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Protocol;
using RazorSharp.Protocol.Messages;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class DependencyDownloadProgressTests
{
    [Fact]
    public async Task BackgroundDownload_ReportsProgress()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var loggerFactory = LoggerFactory.Create(builder => { });
            using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test", tempRoot);
            var server = new RazorLanguageServer(loggerFactory, deps);
            try
            {
                var progressRpc = new FakeProgressRpc();
                var reportReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                server.SetProgressRpcForTests(progressRpc);
                server.SetInitializeParamsForTests(InitParamsWithProgress(true));

                progressRpc.OnNotificationRecorded = (seq, method, @params) =>
                {
                    if (method != LspMethods.Progress)
                    {
                        return;
                    }

                    var kind = GetProgressKind(@params);
                    if (kind == "report")
                    {
                        reportReceived.TrySetResult(true);
                    }
                };

                server.SetStartRoslynOverrideForTests(_ => Task.FromResult(true));
                deps.EnsureDependenciesOverride = async (ct, onProgress) =>
                {
                    onProgress?.Invoke("downloaded");
                    await Task.Delay(10, ct);
                    return true;
                };

                await server.StartBackgroundDependencyDownloadForTests();

                await AwaitOrTimeout(reportReceived.Task, 1000, "Progress report was not sent.");
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
    public async Task BackgroundDownload_CanRestartAfterCompletion()
    {
        var tempRoot = CreateTempDir();
        try
        {
            using var loggerFactory = LoggerFactory.Create(builder => { });
            using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test", tempRoot);
            var server = new RazorLanguageServer(loggerFactory, deps);
            try
            {
                var ensureCalls = 0;
                deps.EnsureDependenciesOverride = (_, _) =>
                {
                    Interlocked.Increment(ref ensureCalls);
                    return Task.FromResult(false);
                };

                var firstTask = server.StartBackgroundDependencyDownloadForTests();
                await AwaitOrTimeout(firstTask, 2000, "First dependency download did not complete.");

                var secondTask = server.StartBackgroundDependencyDownloadForTests();
                await AwaitOrTimeout(secondTask, 2000, "Second dependency download did not complete.");

                Assert.Equal(2, ensureCalls);
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

    sealed class FakeProgressRpc : IProgressRpc
    {
        public Action<int, string, object>? OnNotificationRecorded { get; set; }

        public Task<JsonElement?> InvokeWithParameterObjectAsync(string method, object @params, CancellationToken ct)
            => Task.FromResult<JsonElement?>(JsonDocument.Parse("{}").RootElement);

        public Task NotifyWithParameterObjectAsync(string method, object @params)
        {
            OnNotificationRecorded?.Invoke(0, method, @params);
            return Task.CompletedTask;
        }
    }

    static InitializeParams InitParamsWithProgress(bool enabled)
        => new()
        {
            Capabilities = new ClientCapabilities
            {
                Window = new WindowClientCapabilities { WorkDoneProgress = enabled }
            }
        };

    static string? GetProgressKind(object @params)
    {
        var element = @params is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(@params);

        if (!element.TryGetProperty("value", out var value))
        {
            return null;
        }

        return value.TryGetProperty("kind", out var kind)
            ? kind.GetString()
            : null;
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
}
