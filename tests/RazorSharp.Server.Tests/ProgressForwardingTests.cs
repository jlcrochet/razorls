using System.Text.Json;
using RazorSharp.Protocol;
using RazorSharp.Server.Roslyn;

namespace RazorSharp.Server.Tests;

/// <summary>
/// Tests for LSP progress notification and request forwarding.
/// </summary>
public class ProgressForwardingTests
{
    [Fact]
    public void LspMethods_Progress_HasCorrectValue()
    {
        Assert.Equal("$/progress", LspMethods.Progress);
    }

    [Fact]
    public void LspMethods_WindowWorkDoneProgressCreate_HasCorrectValue()
    {
        Assert.Equal("window/workDoneProgress/create", LspMethods.WindowWorkDoneProgressCreate);
    }

    [Fact]
    public void LspMethods_WindowWorkDoneProgressCancel_HasCorrectValue()
    {
        Assert.Equal("window/workDoneProgress/cancel", LspMethods.WindowWorkDoneProgressCancel);
    }

    [Fact]
    public void RoslynNotificationEventArgs_CanBeCreatedWithProgressMethod()
    {
        var progressParams = JsonDocument.Parse("""
            {
                "token": "test-token",
                "value": {
                    "kind": "begin",
                    "title": "Loading project"
                }
            }
            """).RootElement;

        var eventArgs = new RoslynNotificationEventArgs
        {
            Method = LspMethods.Progress,
            Params = progressParams
        };

        Assert.Equal(LspMethods.Progress, eventArgs.Method);
        Assert.NotNull(eventArgs.Params);
        Assert.Equal("test-token", eventArgs.Params.Value.GetProperty("token").GetString());
    }

    [Fact]
    public void RoslynNotificationEventArgs_CanBeCreatedWithWorkDoneProgressBegin()
    {
        var progressParams = JsonDocument.Parse("""
            {
                "token": "workspace/loading",
                "value": {
                    "kind": "begin",
                    "title": "Loading workspace",
                    "cancellable": false,
                    "percentage": 0
                }
            }
            """).RootElement;

        var eventArgs = new RoslynNotificationEventArgs
        {
            Method = LspMethods.Progress,
            Params = progressParams
        };

        var value = eventArgs.Params!.Value.GetProperty("value");
        Assert.Equal("begin", value.GetProperty("kind").GetString());
        Assert.Equal("Loading workspace", value.GetProperty("title").GetString());
        Assert.Equal(0, value.GetProperty("percentage").GetInt32());
    }

    [Fact]
    public void RoslynNotificationEventArgs_CanBeCreatedWithWorkDoneProgressReport()
    {
        var progressParams = JsonDocument.Parse("""
            {
                "token": "workspace/loading",
                "value": {
                    "kind": "report",
                    "message": "Processing project.csproj",
                    "percentage": 50
                }
            }
            """).RootElement;

        var eventArgs = new RoslynNotificationEventArgs
        {
            Method = LspMethods.Progress,
            Params = progressParams
        };

        var value = eventArgs.Params!.Value.GetProperty("value");
        Assert.Equal("report", value.GetProperty("kind").GetString());
        Assert.Equal("Processing project.csproj", value.GetProperty("message").GetString());
        Assert.Equal(50, value.GetProperty("percentage").GetInt32());
    }

    [Fact]
    public void RoslynNotificationEventArgs_CanBeCreatedWithWorkDoneProgressEnd()
    {
        var progressParams = JsonDocument.Parse("""
            {
                "token": "workspace/loading",
                "value": {
                    "kind": "end",
                    "message": "Workspace loaded"
                }
            }
            """).RootElement;

        var eventArgs = new RoslynNotificationEventArgs
        {
            Method = LspMethods.Progress,
            Params = progressParams
        };

        var value = eventArgs.Params!.Value.GetProperty("value");
        Assert.Equal("end", value.GetProperty("kind").GetString());
        Assert.Equal("Workspace loaded", value.GetProperty("message").GetString());
    }

    [Fact]
    public void WorkDoneProgressCreateParams_CanBeParsed()
    {
        var createParams = JsonDocument.Parse("""
            {
                "token": "unique-progress-token-123"
            }
            """).RootElement;

        Assert.Equal("unique-progress-token-123", createParams.GetProperty("token").GetString());
    }

    [Fact]
    public void WorkDoneProgressCreateParams_TokenCanBeInteger()
    {
        var createParams = JsonDocument.Parse("""
            {
                "token": 12345
            }
            """).RootElement;

        Assert.Equal(12345, createParams.GetProperty("token").GetInt32());
    }
}
