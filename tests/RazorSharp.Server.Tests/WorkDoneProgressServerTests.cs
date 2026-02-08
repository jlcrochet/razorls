using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Protocol;
using RazorSharp.Protocol.Messages;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class WorkDoneProgressServerTests
{
    sealed class FakeProgressRpc : IProgressRpc
    {
        int _sequence;
        readonly object _lock = new();

        public List<(int Seq, string Method, object Params)> Requests { get; } = new();
        public List<(int Seq, string Method, object Params)> Notifications { get; } = new();
        public TaskCompletionSource<bool> CreateCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BeginCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> EndCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<string, object, CancellationToken, Task<JsonElement?>>? OnRequest { get; set; }
        public Action<int, string, object>? OnRequestRecorded { get; set; }
        public Action<int, string, object>? OnNotificationRecorded { get; set; }

        public Task<JsonElement?> InvokeWithParameterObjectAsync(string method, object @params, CancellationToken ct)
        {
            int seq;
            lock (_lock)
            {
                seq = Interlocked.Increment(ref _sequence);
                Requests.Add((seq, method, @params));
            }
            OnRequestRecorded?.Invoke(seq, method, @params);
            if (method == LspMethods.WindowWorkDoneProgressCreate)
            {
                CreateCalled.TrySetResult(true);
            }

            if (OnRequest != null)
            {
                return OnRequest(method, @params, ct);
            }

            return Task.FromResult<JsonElement?>(JsonDocument.Parse("{}").RootElement);
        }

        public Task NotifyWithParameterObjectAsync(string method, object @params)
        {
            int seq;
            lock (_lock)
            {
                seq = Interlocked.Increment(ref _sequence);
                Notifications.Add((seq, method, @params));
            }
            OnNotificationRecorded?.Invoke(seq, method, @params);
            if (method == LspMethods.Progress)
            {
                var kind = GetProgressKind(@params);
                if (kind == "begin")
                {
                    BeginCalled.TrySetResult(true);
                }
                else if (kind == "end")
                {
                    EndCalled.TrySetResult(true);
                }
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CreateRoslynInitParams_UsesClientWorkDoneProgressFlag()
    {
        await WithServer(async server =>
        {
            var method = typeof(RazorLanguageServer).GetMethod(
                "CreateRoslynInitParams",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var enabled = new InitializeParams
            {
                Capabilities = new ClientCapabilities
                {
                    Window = new WindowClientCapabilities { WorkDoneProgress = true }
                }
            };

            var enabledResult = method!.Invoke(server, new object?[] { enabled });
            var enabledJson = JsonSerializer.SerializeToElement(enabledResult);
            Assert.True(enabledJson.GetProperty("capabilities").GetProperty("window").GetProperty("workDoneProgress").GetBoolean());

            var disabled = new InitializeParams
            {
                Capabilities = new ClientCapabilities
                {
                    Window = new WindowClientCapabilities { WorkDoneProgress = false }
                }
            };

            var disabledResult = method.Invoke(server, new object?[] { disabled });
            var disabledJson = JsonSerializer.SerializeToElement(disabledResult);
            Assert.False(disabledJson.GetProperty("capabilities").GetProperty("window").GetProperty("workDoneProgress").GetBoolean());
        });
    }

    [Fact]
    public async Task DiagnosticsProgress_DoesNotStartWhenFast()
    {
        await WithServer(async server =>
        {
            var progressRpc = new FakeProgressRpc();
            server.SetProgressRpcForTests(progressRpc);
            server.SetInitializeParamsForTests(InitParamsWithProgress(true));
            server.SetDiagnosticsProgressDelayForTests(200);
            server.SetForwardToRoslynOverrideForTests(async (_, __, ct) =>
            {
                await Task.Delay(10, ct);
                return JsonSerializer.SerializeToElement(new { kind = "full", resultId = "test", items = Array.Empty<object>() });
            });

            await server.HandleRoslynNotificationForTests(LspMethods.ProjectInitializationComplete, null, CancellationToken.None);

            var diagnosticParams = CreateDiagnosticParams("file:///test.razor");
            await server.HandleDiagnosticAsync(diagnosticParams, CancellationToken.None);

            Assert.Empty(progressRpc.Requests);
            Assert.Empty(progressRpc.Notifications);
        });
    }

    [Fact]
    public async Task DiagnosticsProgress_StartsAndEndsWhenSlow()
    {
        await WithServer(async server =>
        {
            var progressRpc = new FakeProgressRpc();
            server.SetProgressRpcForTests(progressRpc);
            server.SetInitializeParamsForTests(InitParamsWithProgress(true));
            server.MarkClientInitializedForTests();
            server.SetDiagnosticsProgressDelayForTests(50);
            server.SetForwardToRoslynOverrideForTests(async (_, __, ct) =>
            {
                await Task.Delay(120, ct);
                return JsonSerializer.SerializeToElement(new { kind = "full", resultId = "test", items = Array.Empty<object>() });
            });

            await server.HandleRoslynNotificationForTests(LspMethods.ProjectInitializationComplete, null, CancellationToken.None);

            var diagnosticParams = CreateDiagnosticParams("file:///test.razor");
            await server.HandleDiagnosticAsync(diagnosticParams, CancellationToken.None);

            Assert.Single(progressRpc.Requests);
            Assert.Equal(LspMethods.WindowWorkDoneProgressCreate, progressRpc.Requests[0].Method);
            Assert.Equal(2, progressRpc.Notifications.Count);
            Assert.True(progressRpc.Requests[0].Seq < progressRpc.Notifications[0].Seq);

            var kinds = progressRpc.Notifications
                .Select(notification => GetProgressKind(notification.Params))
                .ToArray();
            Assert.Equal(new[] { "begin", "end" }, kinds);
        });
    }

    [Fact]
    public async Task DiagnosticsProgress_ConcurrentRequests_HaveIsolatedLifecycles()
    {
        await WithServer(async server =>
        {
            var progressRpc = new FakeProgressRpc();
            var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            server.SetProgressRpcForTests(progressRpc);
            server.SetInitializeParamsForTests(InitParamsWithProgress(true));
            server.MarkClientInitializedForTests();
            server.SetDiagnosticsProgressDelayForTests(50);
            server.SetForwardToRoslynOverrideForTests(async (_, __, ct) =>
            {
                if (Interlocked.Increment(ref started) == 2)
                {
                    startGate.TrySetResult(true);
                }

                await startGate.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
                await Task.Delay(120, ct);
                return JsonSerializer.SerializeToElement(new { kind = "full", resultId = "test", items = Array.Empty<object>() });
            });

            await server.HandleRoslynNotificationForTests(LspMethods.ProjectInitializationComplete, null, CancellationToken.None);

            var first = server.HandleDiagnosticAsync(CreateDiagnosticParams("file:///a.razor"), CancellationToken.None);
            var second = server.HandleDiagnosticAsync(CreateDiagnosticParams("file:///b.razor"), CancellationToken.None);
            await Task.WhenAll(first, second);

            Assert.All(progressRpc.Requests, request => Assert.Equal(LspMethods.WindowWorkDoneProgressCreate, request.Method));
            var creates = progressRpc.Requests.Where(request => request.Method == LspMethods.WindowWorkDoneProgressCreate).ToArray();
            Assert.Equal(2, creates.Length);

            var tokens = creates
                .Select(request => GetTokenFromParams(request.Params))
                .Where(token => token != null)
                .Cast<string>()
                .ToArray();

            Assert.Equal(2, tokens.Length);
            Assert.Equal(2, tokens.Distinct().Count());

            var notifications = progressRpc.Notifications
                .Where(notification => notification.Method == LspMethods.Progress)
                .ToArray();
            Assert.Equal(4, notifications.Length);

            var beginSeqs = notifications
                .Where(notification => GetProgressKind(notification.Params) == "begin")
                .Select(notification => notification.Seq)
                .ToArray();
            Assert.Equal(2, beginSeqs.Length);

            var endSeqs = notifications
                .Where(notification => GetProgressKind(notification.Params) == "end")
                .Select(notification => notification.Seq)
                .ToArray();
            Assert.Equal(2, endSeqs.Length);

            var minEndSeq = endSeqs.Min();
            var maxBeginSeq = beginSeqs.Max();

            foreach (var token in tokens)
            {
                var tokenNotifications = notifications
                    .Where(notification => GetTokenFromParams(notification.Params) == token)
                    .ToArray();
                Assert.Equal(2, tokenNotifications.Length);

                var beginIndex = Array.FindIndex(tokenNotifications, notification => GetProgressKind(notification.Params) == "begin");
                var endIndex = Array.FindIndex(tokenNotifications, notification => GetProgressKind(notification.Params) == "end");

                Assert.True(beginIndex >= 0, "Missing begin for token.");
                Assert.True(endIndex >= 0, "Missing end for token.");
                Assert.True(tokenNotifications[beginIndex].Seq < tokenNotifications[endIndex].Seq, "End arrived before begin for token.");
                Assert.Equal(new[] { "begin", "end" }, tokenNotifications.Select(notification => GetProgressKind(notification.Params)).ToArray());
                Assert.True(tokenNotifications[beginIndex].Seq > creates.Single(create => GetTokenFromParams(create.Params) == token).Seq);
                minEndSeq = Math.Min(minEndSeq, tokenNotifications[endIndex].Seq);
            }

            Assert.True(minEndSeq > maxBeginSeq, "Expected all begins before any end.");
        });
    }

    [Fact]
    public async Task DiagnosticsMerge_PreservesRelatedDocuments()
    {
        await WithServer(async server =>
        {
            var callCount = 0;
            server.SetForwardToRoslynOverrideForTests((_, @params, _) =>
            {
                callCount++;
                var identifier = @params.TryGetProperty("identifier", out var idProp) ? idProp.GetString() : null;
                if (identifier == "syntax")
                {
                    return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                    {
                        kind = "full",
                        resultId = "syntax",
                        items = new[] { new { message = "a" } },
                        relatedDocuments = new Dictionary<string, object>
                        {
                            ["file:///a.cs"] = new { kind = "full", items = Array.Empty<object>() }
                        }
                    }));
                }

                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                {
                    kind = "full",
                    resultId = "semantic",
                    items = new[] { new { message = "b" } },
                    relatedDocuments = new Dictionary<string, object>
                    {
                        ["file:///b.cs"] = new { kind = "full", items = Array.Empty<object>() }
                    }
                }));
            });

            await server.HandleRoslynNotificationForTests(LspMethods.ProjectInitializationComplete, null, CancellationToken.None);

            var diagnosticParams = CreateDiagnosticParams("file:///test.cs");
            var result = await server.HandleDiagnosticAsync(diagnosticParams, CancellationToken.None);

            Assert.True(result.HasValue);
            var items = result.Value.GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());

            var related = result.Value.GetProperty("relatedDocuments");
            Assert.Equal(JsonValueKind.Object, related.ValueKind);
            Assert.True(related.TryGetProperty("file:///a.cs", out _));
            Assert.True(related.TryGetProperty("file:///b.cs", out _));
            Assert.True(callCount >= 2);
        });
    }

    [Fact]
    public async Task Hover_ShowsProgress_WhenSlow()
    {
        await WithServer(async server =>
        {
            var progressRpc = new FakeProgressRpc();
            server.SetProgressRpcForTests(progressRpc);
            server.SetInitializeParamsForTests(InitParamsWithProgress(true));
            server.MarkClientInitializedForTests();
            server.SetUserRequestProgressDelayForTests(50);
            server.SetForwardToRoslynOverrideForTests(async (_, __, ct) =>
            {
                await Task.Delay(700, ct);
                return JsonSerializer.SerializeToElement(new { contents = "ok" });
            });

            var hoverParams = JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///test.cs" },
                position = new { line = 0, character = 0 }
            });

            await server.HandleHoverAsync(hoverParams, CancellationToken.None);

            Assert.Single(progressRpc.Requests);
            Assert.Equal(LspMethods.WindowWorkDoneProgressCreate, progressRpc.Requests[0].Method);
            var kinds = progressRpc.Notifications
                .Where(notification => notification.Method == LspMethods.Progress)
                .Select(notification => GetProgressKind(notification.Params))
                .ToArray();
            Assert.Equal(new[] { "begin", "end" }, kinds);
        });
    }

    [Fact]
    public async Task DiagnosticsProgress_MixedSpeed_OnlySlowEmitsProgress()
    {
        await WithServer(async server =>
        {
            var progressRpc = new FakeProgressRpc();
            server.SetProgressRpcForTests(progressRpc);
            server.SetInitializeParamsForTests(InitParamsWithProgress(true));
            server.MarkClientInitializedForTests();
            server.SetDiagnosticsProgressDelayForTests(50);
            server.SetForwardToRoslynOverrideForTests(async (_, @params, ct) =>
            {
                var uri = @params.TryGetProperty("textDocument", out var td) && td.TryGetProperty("uri", out var u)
                    ? u.GetString()
                    : null;

                if (string.Equals(uri, "file:///slow.razor", StringComparison.Ordinal))
                {
                    await Task.Delay(120, ct);
                }

                return JsonSerializer.SerializeToElement(new { kind = "full", resultId = "test", items = Array.Empty<object>() });
            });

            await server.HandleRoslynNotificationForTests(LspMethods.ProjectInitializationComplete, null, CancellationToken.None);

            var fast = server.HandleDiagnosticAsync(CreateDiagnosticParams("file:///fast.razor"), CancellationToken.None);
            var slow = server.HandleDiagnosticAsync(CreateDiagnosticParams("file:///slow.razor"), CancellationToken.None);
            await Task.WhenAll(fast, slow);

            var creates = progressRpc.Requests.Where(request => request.Method == LspMethods.WindowWorkDoneProgressCreate).ToArray();
            Assert.Single(creates);

            var notifications = progressRpc.Notifications
                .Where(notification => notification.Method == LspMethods.Progress)
                .ToArray();
            Assert.Equal(2, notifications.Length);

            var kinds = notifications.Select(notification => GetProgressKind(notification.Params)).ToArray();
            Assert.Equal(new[] { "begin", "end" }, kinds);
        });
    }

    [Fact]
    public async Task DiagnosticsProgress_SuppressedWhenClientDoesNotSupport()
    {
        await WithServer(async server =>
        {
            var progressRpc = new FakeProgressRpc();
            server.SetProgressRpcForTests(progressRpc);
            server.SetInitializeParamsForTests(InitParamsWithProgress(false));
            server.SetDiagnosticsProgressDelayForTests(50);
            server.SetForwardToRoslynOverrideForTests(async (_, __, ct) =>
            {
                await Task.Delay(120, ct);
                return JsonSerializer.SerializeToElement(new { kind = "full", resultId = "test", items = Array.Empty<object>() });
            });

            await server.HandleRoslynNotificationForTests(LspMethods.ProjectInitializationComplete, null, CancellationToken.None);

            var diagnosticParams = CreateDiagnosticParams("file:///test.razor");
            await server.HandleDiagnosticAsync(diagnosticParams, CancellationToken.None);

            Assert.Empty(progressRpc.Requests);
            Assert.Empty(progressRpc.Notifications);
        });
    }

    [Fact]
    public async Task WorkspaceInitProgress_BeginsAndEndsOnProjectInitializationComplete()
    {
        await WithServer(async server =>
        {
            var progressRpc = new FakeProgressRpc();
            server.SetProgressRpcForTests(progressRpc);
            server.SetInitializeParamsForTests(InitParamsWithProgress(true));

            server.HandleInitialized();
            await AwaitOrTimeout(progressRpc.CreateCalled.Task, 1000, "Progress create was not sent.");
            await AwaitOrTimeout(progressRpc.BeginCalled.Task, 1000, "Progress begin was not sent.");

            await server.HandleRoslynNotificationForTests(LspMethods.ProjectInitializationComplete, null, CancellationToken.None);
            await AwaitOrTimeout(progressRpc.EndCalled.Task, 1000, "Progress end was not sent.");
        });
    }

    [Fact]
    public async Task WorkspaceInitProgress_EndsOnRoslynExit()
    {
        await WithServer(async server =>
        {
            var progressRpc = new FakeProgressRpc();
            server.SetProgressRpcForTests(progressRpc);
            server.SetInitializeParamsForTests(InitParamsWithProgress(true));

            server.HandleInitialized();
            await AwaitOrTimeout(progressRpc.BeginCalled.Task, 1000, "Progress begin was not sent.");

            server.HandleRoslynProcessExitedForTests(1);
            await AwaitOrTimeout(progressRpc.EndCalled.Task, 1000, "Progress end was not sent.");
        });
    }

    [Fact]
    public async Task WorkspaceInitProgress_EndsOnTimeout()
    {
        await WithServer(async server =>
        {
            var progressRpc = new FakeProgressRpc();
            server.SetProgressRpcForTests(progressRpc);
            server.SetInitializeParamsForTests(InitParamsWithProgress(true));
            server.SetWorkspaceInitProgressTimeoutForTests(50);
            server.SetStartRoslynOverrideForTests(_ => Task.FromResult(true));

            server.HandleInitialized();
            await AwaitOrTimeout(progressRpc.BeginCalled.Task, 1000, "Progress begin was not sent.");
            await AwaitOrTimeout(progressRpc.EndCalled.Task, 1000, "Progress end was not sent.");
        });
    }

    [Fact]
    public async Task DiagnosticsProgress_EndsWhenCreateTimesOut()
    {
        await WithServer(async server =>
        {
            var progressRpc = new FakeProgressRpc
            {
                OnRequest = async (_, __, ct) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return JsonDocument.Parse("{}").RootElement;
                }
            };
            server.SetProgressRpcForTests(progressRpc);
            server.SetInitializeParamsForTests(InitParamsWithProgress(true));
            server.MarkClientInitializedForTests();
            server.SetDiagnosticsProgressDelayForTests(0);
            server.SetForwardToRoslynOverrideForTests((_, __, _) =>
                Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                {
                    kind = "full",
                    resultId = "test",
                    items = Array.Empty<object>()
                })));

            await server.HandleRoslynNotificationForTests(LspMethods.ProjectInitializationComplete, null, CancellationToken.None);

            await server.HandleDiagnosticAsync(CreateDiagnosticParams("file:///test.razor"), CancellationToken.None);

            await AwaitOrTimeout(progressRpc.EndCalled.Task, 3000, "Progress end was not sent.");
        });
    }

    [Fact]
    public async Task WorkDoneProgressCreate_TimesOut_WhenRequestHangs()
    {
        await WithServer(async server =>
        {
            var progressRpc = new FakeProgressRpc
            {
                OnRequest = async (_, __, ct) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return JsonDocument.Parse("{}").RootElement;
                }
            };
            server.SetProgressRpcForTests(progressRpc);
            server.MarkClientInitializedForTests();

            var result = await server.SendWorkDoneProgressBeginForTests("test-token", "RazorSharp", null, CancellationToken.None);

            Assert.True(result);
            Assert.Single(progressRpc.Requests);
            Assert.Single(progressRpc.Notifications);
            Assert.Equal("begin", GetProgressKind(progressRpc.Notifications[0].Params));
        });
    }

    [Fact]
    public async Task WorkDoneProgressCreate_Cancels_WhenRequestIsCancelled()
    {
        await WithServer(async server =>
        {
            var progressRpc = new FakeProgressRpc
            {
                OnRequest = async (_, __, ct) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return JsonDocument.Parse("{}").RootElement;
                }
            };
            server.SetProgressRpcForTests(progressRpc);
            server.MarkClientInitializedForTests();

            using var cts = new CancellationTokenSource(50);
            var result = await server.SendWorkDoneProgressBeginForTests("test-token", "RazorSharp", null, cts.Token);

            Assert.False(result);
            Assert.Single(progressRpc.Requests);
            Assert.Empty(progressRpc.Notifications);
        });
    }

    static InitializeParams InitParamsWithProgress(bool enabled)
        => new()
        {
            Capabilities = new ClientCapabilities
            {
                Window = new WindowClientCapabilities { WorkDoneProgress = enabled }
            }
        };

    static JsonElement CreateDiagnosticParams(string uri)
        => JsonSerializer.SerializeToElement(new
        {
            textDocument = new { uri }
        });

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

    static string? GetTokenFromParams(object @params)
    {
        var element = @params is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(@params);

        return element.TryGetProperty("token", out var token)
            ? token.GetString()
            : null;
    }

    static async Task AwaitOrTimeout(Task task, int timeoutMs, string message)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)) == task;
        Assert.True(completed, message);
    }

    static async Task WithServer(Func<RazorLanguageServer, Task> action)
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        try
        {
            await action(server);
        }
        finally
        {
            await server.DisposeAsync();
            deps.Dispose();
        }
    }
}
