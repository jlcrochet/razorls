using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class LogMessageForwardingTests
{
    [Fact]
    public async Task LogMessage_NoisyType_FilteredToTrace()
    {
        var (holder, provider) = CreateServerWithLogger();
        using (holder)
        {
            var logParams = JsonSerializer.SerializeToElement(new { type = 5, message = "Some noisy trace message" });
            await holder.Server.HandleRoslynNotificationForTests("window/logMessage", logParams, CancellationToken.None);

            Assert.Contains(provider.Entries, entry =>
                entry.Level == LogLevel.Trace &&
                entry.Message.Contains("[Roslyn]", StringComparison.Ordinal) &&
                entry.Message.Contains("Some noisy trace message", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task LogMessage_AssemblyLoadProbing_FilteredToTrace()
    {
        var (holder, provider) = CreateServerWithLogger();
        using (holder)
        {
            var logParams = JsonSerializer.SerializeToElement(new { type = 3, message = "Assembly 'Foo' was not found in this load context" });
            await holder.Server.HandleRoslynNotificationForTests("window/logMessage", logParams, CancellationToken.None);

            Assert.Contains(provider.Entries, entry =>
                entry.Level == LogLevel.Trace &&
                entry.Message.Contains("[Roslyn]", StringComparison.Ordinal) &&
                entry.Message.Contains("not found in this load context", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task LogMessage_NormalType_LoggedAtDebug()
    {
        var (holder, provider) = CreateServerWithLogger();
        using (holder)
        {
            var logParams = JsonSerializer.SerializeToElement(new { type = 3, message = "Normal informational message" });
            await holder.Server.HandleRoslynNotificationForTests("window/logMessage", logParams, CancellationToken.None);

            Assert.Contains(provider.Entries, entry =>
                entry.Level == LogLevel.Debug &&
                entry.Message.Contains("[Roslyn]", StringComparison.Ordinal) &&
                entry.Message.Contains("Normal informational message", StringComparison.Ordinal));
        }
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
