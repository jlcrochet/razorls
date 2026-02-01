using System.IO;

namespace RazorSharp.Server;

internal sealed class LogFileSwitch : IDisposable
{
    readonly Lock _lock = new();
    readonly TextWriter _errorWriter;
    StreamWriter? _writer;
    string? _currentPath;

    public LogFileSwitch(string? initialPath = null, TextWriter? errorWriter = null)
    {
        _errorWriter = errorWriter ?? Console.Error;
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
            try
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
            catch (Exception ex)
            {
                _writer?.Dispose();
                _writer = null;
                _currentPath = null;
                _errorWriter.WriteLine($"Failed to open log file '{path}': {ex.Message}");
            }
        }
    }

    public void WriteLine(string line)
    {
        lock (_lock)
        {
            if (_writer != null)
            {
                try
                {
                    _writer.WriteLine(line);
                    return;
                }
                catch (Exception ex)
                {
                    _writer.Dispose();
                    _writer = null;
                    _currentPath = null;
                    _errorWriter.WriteLine($"Failed to write to log file: {ex.Message}");
                }
            }
        }

        _errorWriter.WriteLine(line);
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
