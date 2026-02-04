using System.Reflection;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;

namespace RazorSharp.Server.Tests;

public class RazorLanguageServerTelemetryTests
{
    [Fact]
    public async Task DroppedNotifications_WarnsAtThreshold()
    {
        var provider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "1.0.0-test");
        var server = new RazorLanguageServer(loggerFactory, deps);

        try
        {
            SetPrivateField(server, "_nextDroppedNotificationWarnAt", 1L);
            SetPrivateField(server, "_lastTelemetryWarnUtc", DateTime.MinValue);

            InvokePrivateMethod(server, "RecordDroppedRoslynNotification", "workspace/didChangeWatchedFiles");

            Assert.Contains(provider.Entries, entry =>
                entry.Level == LogLevel.Warning && entry.Message.Contains("Dropped", StringComparison.Ordinal));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task RoslynTimeouts_WarnsAtThreshold()
    {
        var provider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "1.0.0-test");
        var server = new RazorLanguageServer(loggerFactory, deps);

        try
        {
            SetPrivateField(server, "_nextRoslynTimeoutWarnAt", 1L);
            SetPrivateField(server, "_lastTelemetryWarnUtc", DateTime.MinValue);

            InvokePrivateMethod(server, "RecordRoslynRequestTimeout", "textDocument/hover");

            Assert.Contains(provider.Entries, entry =>
                entry.Level == LogLevel.Warning && entry.Message.Contains("timeouts", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static void InvokePrivateMethod(object target, string methodName, string argument)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, new object?[] { argument });
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private sealed class TestLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => GetEntriesSnapshot();

        public ILogger CreateLogger(string categoryName)
            => new TestLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private IReadOnlyList<LogEntry> GetEntriesSnapshot()
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    private sealed class TestLogger : ILogger
    {
        private readonly string _category;
        private readonly List<LogEntry> _entries;

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

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private readonly record struct LogEntry(string Category, LogLevel Level, string Message);
}
