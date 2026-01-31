using System;
using System.IO;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class LoggingSwitchTests
{
    [Fact]
    public void LogFileSwitch_WritesToFile_WhenConfigured()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var logPath = Path.Combine(tempRoot, "app.log");
            using var logFileSwitch = new LogFileSwitch(logPath);

            logFileSwitch.WriteLine("hello");

            var content = File.ReadAllText(logPath);
            Assert.Contains("hello", content, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void LogFileSwitch_DisablesFile_WhenPathCleared()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var logPath = Path.Combine(tempRoot, "app.log");
            using var logFileSwitch = new LogFileSwitch(logPath);

            logFileSwitch.SetLogFile(null);

            Assert.False(logFileSwitch.IsFileEnabled);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
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
