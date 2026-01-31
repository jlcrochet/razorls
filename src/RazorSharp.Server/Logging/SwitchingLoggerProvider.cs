using Microsoft.Extensions.Logging;

namespace RazorSharp.Server;

internal sealed class SwitchingLoggerProvider : ILoggerProvider
{
    readonly LoggingLevelSwitch _levelSwitch;
    readonly LogFileSwitch _fileSwitch;

    public SwitchingLoggerProvider(LoggingLevelSwitch levelSwitch, LogFileSwitch fileSwitch)
    {
        _levelSwitch = levelSwitch;
        _fileSwitch = fileSwitch;
    }

    public ILogger CreateLogger(string categoryName) => new SwitchingLogger(categoryName, _levelSwitch, _fileSwitch);

    public void Dispose() => _fileSwitch.Dispose();
}

internal sealed class SwitchingLogger : ILogger
{
    readonly string _category;
    readonly LoggingLevelSwitch _levelSwitch;
    readonly LogFileSwitch _fileSwitch;

    public SwitchingLogger(string category, LoggingLevelSwitch levelSwitch, LogFileSwitch fileSwitch)
    {
        _category = category;
        _levelSwitch = levelSwitch;
        _fileSwitch = fileSwitch;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => _levelSwitch.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId _, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var dotIndex = _category.LastIndexOf('.');
        var shortCategory = dotIndex != -1 ? _category.AsSpan(dotIndex + 1) : _category;
        var message = formatter(state, exception);
        var line = _fileSwitch.IsFileEnabled
            ? $"[{DateTime.Now:HH:mm:ss.fff}] [{logLevel}] {shortCategory}: {message}"
            : $"[{logLevel}] {shortCategory}: {message}";

        _fileSwitch.WriteLine(line);
        if (exception != null)
        {
            _fileSwitch.WriteLine(exception.ToString());
        }
    }
}
