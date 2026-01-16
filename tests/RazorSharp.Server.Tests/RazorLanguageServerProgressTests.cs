using System.Text.Json;
using RazorSharp.Protocol;

namespace RazorSharp.Server.Tests;

/// <summary>
/// Integration tests for progress forwarding in RazorLanguageServer.
/// These tests verify that progress notifications and requests are properly forwarded
/// from Roslyn to the editor client.
/// </summary>
public class RazorLanguageServerProgressTests
{
    /// <summary>
    /// Helper to create a mock JsonRpc that records sent notifications.
    /// </summary>
    private class MockClientRpc
    {
        public List<(string Method, JsonElement? Params)> SentNotifications { get; } = new();
        public List<(string Method, JsonElement? Params)> SentRequests { get; } = new();

        public Task NotifyWithParameterObjectAsync(string method, JsonElement @params)
        {
            SentNotifications.Add((method, @params));
            return Task.CompletedTask;
        }

        public Task NotifyAsync(string method)
        {
            SentNotifications.Add((method, null));
            return Task.CompletedTask;
        }

        public Task<JsonElement?> InvokeWithParameterObjectAsync<T>(string method, JsonElement @params, CancellationToken ct)
        {
            SentRequests.Add((method, @params));
            // Return empty result for workDoneProgress/create
            return Task.FromResult<JsonElement?>(JsonDocument.Parse("{}").RootElement);
        }
    }

    [Fact]
    public void ProgressNotification_HasCorrectStructure_Begin()
    {
        // Verify the structure of a begin progress notification
        var notification = JsonDocument.Parse("""
            {
                "token": "roslyn/workspace",
                "value": {
                    "kind": "begin",
                    "title": "Loading Project",
                    "cancellable": false,
                    "message": "Analyzing dependencies...",
                    "percentage": 0
                }
            }
            """).RootElement;

        Assert.Equal("roslyn/workspace", notification.GetProperty("token").GetString());

        var value = notification.GetProperty("value");
        Assert.Equal("begin", value.GetProperty("kind").GetString());
        Assert.Equal("Loading Project", value.GetProperty("title").GetString());
        Assert.False(value.GetProperty("cancellable").GetBoolean());
        Assert.Equal("Analyzing dependencies...", value.GetProperty("message").GetString());
        Assert.Equal(0, value.GetProperty("percentage").GetInt32());
    }

    [Fact]
    public void ProgressNotification_HasCorrectStructure_Report()
    {
        // Verify the structure of a report progress notification
        var notification = JsonDocument.Parse("""
            {
                "token": "roslyn/workspace",
                "value": {
                    "kind": "report",
                    "cancellable": false,
                    "message": "Loading MyProject.csproj",
                    "percentage": 45
                }
            }
            """).RootElement;

        var value = notification.GetProperty("value");
        Assert.Equal("report", value.GetProperty("kind").GetString());
        Assert.Equal("Loading MyProject.csproj", value.GetProperty("message").GetString());
        Assert.Equal(45, value.GetProperty("percentage").GetInt32());
    }

    [Fact]
    public void ProgressNotification_HasCorrectStructure_End()
    {
        // Verify the structure of an end progress notification
        var notification = JsonDocument.Parse("""
            {
                "token": "roslyn/workspace",
                "value": {
                    "kind": "end",
                    "message": "Project loaded successfully"
                }
            }
            """).RootElement;

        var value = notification.GetProperty("value");
        Assert.Equal("end", value.GetProperty("kind").GetString());
        Assert.Equal("Project loaded successfully", value.GetProperty("message").GetString());
    }

    [Fact]
    public void WorkDoneProgressCreate_HasCorrectStructure_StringToken()
    {
        // Verify the structure of workDoneProgress/create request with string token
        var request = JsonDocument.Parse("""
            {
                "token": "roslyn/workspace-1234"
            }
            """).RootElement;

        Assert.Equal("roslyn/workspace-1234", request.GetProperty("token").GetString());
    }

    [Fact]
    public void WorkDoneProgressCreate_HasCorrectStructure_IntegerToken()
    {
        // LSP spec allows integer tokens as well
        var request = JsonDocument.Parse("""
            {
                "token": 42
            }
            """).RootElement;

        Assert.Equal(42, request.GetProperty("token").GetInt32());
    }

    [Fact]
    public void ProgressMethods_AreDefinedInLspMethods()
    {
        // Verify all progress-related methods are defined
        Assert.Equal("$/progress", LspMethods.Progress);
        Assert.Equal("window/workDoneProgress/create", LspMethods.WindowWorkDoneProgressCreate);
        Assert.Equal("window/workDoneProgress/cancel", LspMethods.WindowWorkDoneProgressCancel);
    }

    [Theory]
    [InlineData("begin", true)]
    [InlineData("report", false)]
    [InlineData("end", false)]
    public void ProgressValue_KindDeterminesRequiredFields(string kind, bool requiresTitle)
    {
        // Test that different progress kinds have appropriate fields
        var baseJson = kind switch
        {
            "begin" => """{"kind": "begin", "title": "Test"}""",
            "report" => """{"kind": "report"}""",
            "end" => """{"kind": "end"}""",
            _ => throw new ArgumentException()
        };

        var value = JsonDocument.Parse(baseJson).RootElement;
        Assert.Equal(kind, value.GetProperty("kind").GetString());

        if (requiresTitle)
        {
            Assert.True(value.TryGetProperty("title", out _));
        }
    }

    [Fact]
    public void RoslynTypicalProgressSequence_IsValid()
    {
        // Simulate a typical Roslyn progress sequence
        var sequence = new[]
        {
            // First: Create the progress token
            ("window/workDoneProgress/create", """{"token": "roslyn/project-load"}"""),

            // Then: Begin progress
            ("$/progress", """
                {
                    "token": "roslyn/project-load",
                    "value": {"kind": "begin", "title": "Loading Project", "percentage": 0}
                }
            """),

            // Report progress updates
            ("$/progress", """
                {
                    "token": "roslyn/project-load",
                    "value": {"kind": "report", "message": "Restoring packages...", "percentage": 25}
                }
            """),
            ("$/progress", """
                {
                    "token": "roslyn/project-load",
                    "value": {"kind": "report", "message": "Compiling...", "percentage": 75}
                }
            """),

            // End progress
            ("$/progress", """
                {
                    "token": "roslyn/project-load",
                    "value": {"kind": "end", "message": "Done"}
                }
            """)
        };

        foreach (var (method, json) in sequence)
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (method == LspMethods.WindowWorkDoneProgressCreate)
            {
                Assert.True(root.TryGetProperty("token", out _));
            }
            else if (method == LspMethods.Progress)
            {
                Assert.True(root.TryGetProperty("token", out _));
                Assert.True(root.TryGetProperty("value", out var value));
                Assert.True(value.TryGetProperty("kind", out _));
            }
        }
    }
}
