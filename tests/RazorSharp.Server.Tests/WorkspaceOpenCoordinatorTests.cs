using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Protocol;
using RazorSharp.Server;
using RazorSharp.Server.Workspace;

namespace RazorSharp.Server.Tests;

public class WorkspaceOpenCoordinatorTests
{
    [Fact]
    public async Task OpenWorkspaceAsync_SolutionFile_SendsSolutionOpen()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var temp = new TempDir();
        var solutionPath = Path.Combine(temp.Path, "test.slnx");
        File.WriteAllText(solutionPath, "");

        string? method = null;
        object? payload = null;
        var coordinator = CreateCoordinator(
            loggerFactory,
            methodCapture: m => method = m,
            payloadCapture: p => payload = p);

        await coordinator.OpenWorkspaceAsync(solutionPath);

        Assert.Equal(LspMethods.SolutionOpen, method);
        Assert.NotNull(payload);
        var json = JsonSerializer.SerializeToElement(payload);
        Assert.Equal(new Uri(solutionPath).AbsoluteUri, json.GetProperty("solution").GetString());
    }

    [Fact]
    public async Task OpenWorkspaceAsync_ProjectFile_SendsProjectOpen()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var temp = new TempDir();
        var projectPath = Path.Combine(temp.Path, "test.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        string? method = null;
        object? payload = null;
        var coordinator = CreateCoordinator(
            loggerFactory,
            methodCapture: m => method = m,
            payloadCapture: p => payload = p);

        await coordinator.OpenWorkspaceAsync(projectPath);

        Assert.Equal(LspMethods.ProjectOpen, method);
        Assert.NotNull(payload);
        var json = JsonSerializer.SerializeToElement(payload);
        Assert.Single(json.GetProperty("projects").EnumerateArray());
    }

    [Fact]
    public async Task OpenWorkspaceAsync_DirectoryWithoutSolution_SendsProjectOpenForDiscoveredProjects()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var temp = new TempDir();
        var projectA = Path.Combine(temp.Path, "A.csproj");
        var projectB = Path.Combine(temp.Path, "sub", "B.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectB)!);
        File.WriteAllText(projectA, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(projectB, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        string? method = null;
        object? payload = null;
        var coordinator = CreateCoordinator(
            loggerFactory,
            methodCapture: m => method = m,
            payloadCapture: p => payload = p);

        await coordinator.OpenWorkspaceAsync(temp.Path);

        Assert.Equal(LspMethods.ProjectOpen, method);
        Assert.NotNull(payload);
        var json = JsonSerializer.SerializeToElement(payload);
        Assert.Equal(2, json.GetProperty("projects").GetArrayLength());
    }

    static WorkspaceOpenCoordinator CreateCoordinator(
        ILoggerFactory loggerFactory,
        Action<string> methodCapture,
        Action<object?> payloadCapture)
    {
        var manager = new WorkspaceManager(loggerFactory.CreateLogger<WorkspaceManager>());
        return new WorkspaceOpenCoordinator(
            loggerFactory.CreateLogger<WorkspaceOpenCoordinator>(),
            manager,
            static path => path,
            (method, @params) =>
            {
                methodCapture(method);
                payloadCapture(@params);
                return Task.CompletedTask;
            },
            ".slnf",
            ".slnx");
    }

    sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
