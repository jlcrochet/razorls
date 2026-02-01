using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Protocol;
using RazorSharp.Protocol.Messages;
using RazorSharp.Server;
using RazorSharp.Server.Configuration;

namespace RazorSharp.Server.Tests;

public class FileWatchingTests
{
    [Fact]
    public void CreateFileWatchers_UsesBaseUriWhenProvided()
    {
        var baseUri = "file:///workspace";
        var watchers = InvokeCreateFileWatchers(baseUri);
        var json = JsonSerializer.SerializeToElement(watchers);

        var first = json[0];
        var glob = first.GetProperty("globPattern");
        Assert.Equal(JsonValueKind.Object, glob.ValueKind);
        Assert.Equal(baseUri, glob.GetProperty("baseUri").GetString());
        Assert.Equal("**/*.sln", glob.GetProperty("pattern").GetString());
    }

    [Fact]
    public void CreateFileWatchers_UsesStringGlobWhenBaseUriMissing()
    {
        var watchers = InvokeCreateFileWatchers(null);
        var json = JsonSerializer.SerializeToElement(watchers);

        var first = json[0];
        var glob = first.GetProperty("globPattern");
        Assert.Equal(JsonValueKind.String, glob.ValueKind);
        Assert.Equal("**/*.sln", glob.GetString());
    }

    [Fact]
    public void TryGetWorkspaceBaseUri_UsesDirectoryForFilePath()
    {
        using var serverHolder = CreateServer();
        var server = serverHolder.Server;

        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var solutionPath = Path.Combine(tempRoot, "test.sln");
        File.WriteAllText(solutionPath, "");

        try
        {
            SetPrivateField(server, "_workspaceRoot", solutionPath);
            var baseUri = InvokeTryGetWorkspaceBaseUri(server);
            var expected = new Uri(Path.GetFullPath(tempRoot)).AbsoluteUri;
            Assert.Equal(expected, baseUri);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void IsWorkspaceReloadTriggerPath_RecognizesMsbuildAndSolutionExtensions()
    {
        Assert.True(InvokeIsWorkspaceReloadTriggerPath("/tmp/test.sln"));
        Assert.True(InvokeIsWorkspaceReloadTriggerPath("/tmp/test.slnf"));
        Assert.True(InvokeIsWorkspaceReloadTriggerPath("/tmp/test.slnx"));
        Assert.True(InvokeIsWorkspaceReloadTriggerPath("/tmp/test.csproj"));
        Assert.True(InvokeIsWorkspaceReloadTriggerPath("/tmp/test.props"));
        Assert.True(InvokeIsWorkspaceReloadTriggerPath("/tmp/test.targets"));
        Assert.True(InvokeIsWorkspaceReloadTriggerPath("/tmp/global.json"));
        Assert.False(InvokeIsWorkspaceReloadTriggerPath("/tmp/readme.md"));
    }

    [Fact]
    public async Task HandleDidChangeWatchedFilesAsync_ReloadsOmniSharpConfig()
    {
        using var serverHolder = CreateServer();
        var server = serverHolder.Server;

        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var configPath = Path.Combine(tempRoot, "omnisharp.json");

        try
        {
            File.WriteAllText(configPath, "{\"FormattingOptions\":{\"enableEditorConfigSupport\":false}}");
            server.ConfigurationLoader.SetWorkspaceRoot(tempRoot);
            SetPrivateField(server, "_workspaceRoot", tempRoot);

            Assert.NotNull(server.ConfigurationLoader.Configuration.FormattingOptions);
            Assert.False(server.ConfigurationLoader.Configuration.FormattingOptions!.EnableEditorConfigSupport);

            File.WriteAllText(configPath, "{\"FormattingOptions\":{\"enableEditorConfigSupport\":true}}");

            var @params = new DidChangeWatchedFilesParams
            {
                Changes =
                [
                    new FileEvent
                    {
                        Uri = new Uri(configPath).AbsoluteUri,
                        Type = FileChangeType.Changed
                    }
                ]
            };

            await server.HandleDidChangeWatchedFilesAsync(JsonSerializer.SerializeToElement(@params));

            Assert.NotNull(server.ConfigurationLoader.Configuration.FormattingOptions);
            Assert.True(server.ConfigurationLoader.Configuration.FormattingOptions!.EnableEditorConfigSupport);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleDidChangeWatchedFilesAsync_ForwardsNotificationToRoslyn()
    {
        using var serverHolder = CreateServer();
        var server = serverHolder.Server;
        string? forwarded = null;

        server.SetForwardToRoslynNotificationOverrideForTests((method, _) =>
        {
            forwarded = method;
            return Task.CompletedTask;
        });

        var @params = new DidChangeWatchedFilesParams
        {
            Changes =
            [
                new FileEvent
                {
                    Uri = "file:///tmp/test.cs",
                    Type = FileChangeType.Changed
                }
            ]
        };

        await server.HandleDidChangeWatchedFilesAsync(JsonSerializer.SerializeToElement(@params));

        Assert.Equal(LspMethods.WorkspaceDidChangeWatchedFiles, forwarded);
    }

    [Fact]
    public async Task OpenWorkspaceAsync_UsesSolutionOpenForSlnx()
    {
        using var serverHolder = CreateServer();
        var server = serverHolder.Server;
        string? forwardedMethod = null;
        object? forwardedParams = null;

        server.SetForwardToRoslynNotificationOverrideForTests((method, @params) =>
        {
            forwardedMethod = method;
            forwardedParams = @params;
            return Task.CompletedTask;
        });

        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var solutionPath = Path.Combine(tempRoot, "test.slnx");
        File.WriteAllText(solutionPath, "");

        try
        {
            await InvokeOpenWorkspaceAsync(server, solutionPath);

            Assert.Equal(LspMethods.SolutionOpen, forwardedMethod);
            Assert.NotNull(forwardedParams);
            var json = JsonSerializer.SerializeToElement(forwardedParams);
            Assert.Equal(new Uri(solutionPath).AbsoluteUri, json.GetProperty("solution").GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void HandleInitialized_LogsWhenDynamicRegistrationDisabled()
    {
        var result = CreateServerWithLogger();
        using var serverHolder = result.Holder;
        var provider = result.Provider;
        var server = serverHolder.Server;

        SetPrivateField(server, "_initOptions", new InitializationOptions
        {
            Workspace = new WorkspaceOptions
            {
                EnableFileWatching = true,
                EnableFileWatchingRegistration = false
            }
        });

        server.HandleInitialized();

        Assert.Contains(provider.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("Dynamic file watching registration disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleDidChangeWatchedFilesAsync_DebouncesWorkspaceReload()
    {
        using var serverHolder = CreateServer();
        var server = serverHolder.Server;
        var methods = new List<string>();
        var lockObj = new object();

        server.SetForwardToRoslynNotificationOverrideForTests((method, _) =>
        {
            lock (lockObj)
            {
                methods.Add(method);
            }
            return Task.CompletedTask;
        });

        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var solutionPath = Path.Combine(tempRoot, "test.sln");
        File.WriteAllText(solutionPath, "");

        try
        {
            SetPrivateField(server, "_workspaceOpenTarget", solutionPath);
            SetPrivateField(server, "_workspaceRoot", tempRoot);

            var changeParams = new DidChangeWatchedFilesParams
            {
                Changes =
                [
                    new FileEvent
                    {
                        Uri = new Uri(Path.Combine(tempRoot, "project.csproj")).AbsoluteUri,
                        Type = FileChangeType.Changed
                    }
                ]
            };

            await server.HandleDidChangeWatchedFilesAsync(JsonSerializer.SerializeToElement(changeParams));
            await server.HandleDidChangeWatchedFilesAsync(JsonSerializer.SerializeToElement(changeParams));

            await Task.Delay(200);
            lock (lockObj)
            {
                Assert.DoesNotContain(methods, method => method == LspMethods.SolutionOpen);
            }

            await Task.Delay(1200);
            lock (lockObj)
            {
                Assert.Equal(1, methods.Count(method => method == LspMethods.SolutionOpen));
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleInitialized_LogsWhenBaseUriMissing()
    {
        var result = CreateServerWithLogger();
        using var serverHolder = result.Holder;
        var provider = result.Provider;
        var server = serverHolder.Server;

        server.SetInitializeParamsForTests(new InitializeParams
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
        });

        SetPrivateField(server, "_initOptions", new InitializationOptions
        {
            Workspace = new WorkspaceOptions
            {
                EnableFileWatching = true,
                EnableFileWatchingRegistration = true
            }
        });

        server.HandleInitialized();

        await Task.Delay(100);

        Assert.Contains(provider.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("Workspace baseUri not available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleDidChangeWatchedFilesAsync_DebouncesMultipleTriggerTypes()
    {
        using var serverHolder = CreateServer();
        var server = serverHolder.Server;
        var methods = new List<string>();
        var lockObj = new object();

        server.SetForwardToRoslynNotificationOverrideForTests((method, _) =>
        {
            lock (lockObj)
            {
                methods.Add(method);
            }
            return Task.CompletedTask;
        });

        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var solutionPath = Path.Combine(tempRoot, "test.sln");
        File.WriteAllText(solutionPath, "");

        try
        {
            SetPrivateField(server, "_workspaceOpenTarget", solutionPath);
            SetPrivateField(server, "_workspaceRoot", tempRoot);

            var changeParams = new DidChangeWatchedFilesParams
            {
                Changes =
                [
                    new FileEvent
                    {
                        Uri = new Uri(Path.Combine(tempRoot, "Directory.Build.props")).AbsoluteUri,
                        Type = FileChangeType.Changed
                    },
                    new FileEvent
                    {
                        Uri = new Uri(Path.Combine(tempRoot, "project.csproj")).AbsoluteUri,
                        Type = FileChangeType.Changed
                    }
                ]
            };

            await server.HandleDidChangeWatchedFilesAsync(JsonSerializer.SerializeToElement(changeParams));
            await server.HandleDidChangeWatchedFilesAsync(JsonSerializer.SerializeToElement(changeParams));

            await Task.Delay(1200);
            lock (lockObj)
            {
                Assert.Equal(1, methods.Count(method => method == LspMethods.SolutionOpen));
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    sealed class ServerHolder : IDisposable
    {
        readonly ILoggerFactory _loggerFactory;
        readonly DependencyManager _deps;
        readonly RazorLanguageServer _server;

        public ServerHolder(ILoggerFactory loggerFactory, DependencyManager deps, RazorLanguageServer server)
        {
            _loggerFactory = loggerFactory;
            _deps = deps;
            _server = server;
        }

        public RazorLanguageServer Server => _server;

        public void Dispose()
        {
            _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _deps.Dispose();
            _loggerFactory.Dispose();
        }
    }

    static ServerHolder CreateServer()
    {
        var loggerFactory = LoggerFactory.Create(builder => { });
        var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        return new ServerHolder(loggerFactory, deps, server);
    }

    static (ServerHolder Holder, TestLoggerProvider Provider) CreateServerWithLogger()
    {
        var provider = new TestLoggerProvider();
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
        });
        var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        return (new ServerHolder(loggerFactory, deps, server), provider);
    }

    static object[] InvokeCreateFileWatchers(string? baseUri)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "CreateFileWatchers",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (object[])method!.Invoke(null, new object?[] { baseUri })!;
    }

    static string? InvokeTryGetWorkspaceBaseUri(RazorLanguageServer server)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "TryGetWorkspaceBaseUri",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string?)method!.Invoke(server, null);
    }

    static bool InvokeIsWorkspaceReloadTriggerPath(string path)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "IsWorkspaceReloadTriggerPath",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object?[] { path })!;
    }

    static Task InvokeOpenWorkspaceAsync(RazorLanguageServer server, string path)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "OpenWorkspaceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task)method!.Invoke(server, new object?[] { path })!;
    }

    static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    sealed class TestLoggerProvider : ILoggerProvider
    {
        readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => GetEntriesSnapshot();

        public ILogger CreateLogger(string categoryName)
            => new TestLogger(categoryName, _entries);

        public IReadOnlyList<LogEntry> GetEntriesSnapshot()
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }

        public void Dispose()
        {
        }
    }

    sealed class TestLogger : ILogger
    {
        readonly string _category;
        readonly List<LogEntry> _entries;

        public TestLogger(string category, List<LogEntry> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_entries)
            {
                _entries.Add(new LogEntry(_category, logLevel, message));
            }
        }
    }

    sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    readonly record struct LogEntry(string Category, LogLevel Level, string Message);
}
