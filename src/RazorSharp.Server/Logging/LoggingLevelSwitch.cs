using Microsoft.Extensions.Logging;

namespace RazorSharp.Server;

internal sealed class LoggingLevelSwitch
{
    volatile LogLevel _minimumLevel;

    public LoggingLevelSwitch(LogLevel minimumLevel)
    {
        _minimumLevel = minimumLevel;
    }

    public LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set => _minimumLevel = value;
    }

    public bool IsEnabled(LogLevel level) => level >= _minimumLevel;
}
