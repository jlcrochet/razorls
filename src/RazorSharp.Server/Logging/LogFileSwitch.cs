using System.IO;

namespace RazorSharp.Server;

internal sealed class LogFileSwitch : IDisposable
{
    readonly Lock _lock = new();
    StreamWriter? _writer;
    string? _currentPath;

    public LogFileSwitch(string? initialPath = null)
    {
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            SetLogFile(initialPath);
        }
    }

    public bool IsFileEnabled
    {
        get
        {
            lock (_lock)
            {
                return _writer != null;
            }
        }
    }

    public void SetLogFile(string? path)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                _writer?.Dispose();
                _writer = null;
                _currentPath = null;
                return;
            }

            if (string.Equals(_currentPath, path, StringComparison.Ordinal))
            {
                return;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _writer?.Dispose();
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
            _currentPath = path;
        }
    }

    public void WriteLine(string line)
    {
        lock (_lock)
        {
            if (_writer != null)
            {
                _writer.WriteLine(line);
                return;
            }
        }

        Console.Error.WriteLine(line);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
            _currentPath = null;
        }
    }
}
