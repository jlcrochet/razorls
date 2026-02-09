using Microsoft.Extensions.Logging;
using RazorSharp.Protocol;
using RazorSharp.Protocol.Messages;
using RazorSharp.Server.Workspace;

namespace RazorSharp.Server;

internal sealed class WorkspaceOpenCoordinator
{
    readonly ILogger _logger;
    readonly WorkspaceManager _workspaceManager;
    readonly Func<string, string?> _tryGetLocalPath;
    readonly Func<string, object?, Task> _sendRoslynNotificationAsync;
    readonly string _solutionFilterFileName;
    readonly string _solutionXmlFileName;

    public WorkspaceOpenCoordinator(
        ILogger logger,
        WorkspaceManager workspaceManager,
        Func<string, string?> tryGetLocalPath,
        Func<string, object?, Task> sendRoslynNotificationAsync,
        string solutionFilterFileName,
        string solutionXmlFileName)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _tryGetLocalPath = tryGetLocalPath;
        _sendRoslynNotificationAsync = sendRoslynNotificationAsync;
        _solutionFilterFileName = solutionFilterFileName;
        _solutionXmlFileName = solutionXmlFileName;
    }

    public async Task OpenWorkspaceAsync(string rootUriOrPath)
    {
        var rootPath = _tryGetLocalPath(rootUriOrPath);
        if (rootPath == null)
        {
            _logger.LogWarning("Invalid workspace root: {Root}", rootUriOrPath);
            return;
        }

        if (File.Exists(rootPath))
        {
            var extension = Path.GetExtension(rootPath);
            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(_solutionFilterFileName, StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(_solutionXmlFileName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Opening solution: {Solution}", rootPath);
                await _sendRoslynNotificationAsync(LspMethods.SolutionOpen, new SolutionOpenParams
                {
                    Solution = new Uri(rootPath).AbsoluteUri
                });
            }
            else if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Opening project: {Project}", rootPath);
                await _sendRoslynNotificationAsync(LspMethods.ProjectOpen, new ProjectOpenParams
                {
                    Projects = [new Uri(rootPath).AbsoluteUri]
                });
            }
            else
            {
                _logger.LogWarning("Workspace path is a file but not a supported project/solution: {Path}", rootPath);
            }

            return;
        }

        if (!Directory.Exists(rootPath))
        {
            _logger.LogWarning("Workspace root does not exist: {Path}", rootPath);
            return;
        }

        var solution = _workspaceManager.FindSolution(rootPath);
        if (solution != null)
        {
            _logger.LogInformation("Opening solution: {Solution}", solution);
            await _sendRoslynNotificationAsync(LspMethods.SolutionOpen, new SolutionOpenParams
            {
                Solution = new Uri(solution).AbsoluteUri
            });
            return;
        }

        // No solution file found - open projects directly.
        var projects = _workspaceManager.FindProjects(rootPath);
        if (projects.Length > 0)
        {
            _logger.LogInformation("Opening {Count} projects directly", projects.Length);
            await _sendRoslynNotificationAsync(LspMethods.ProjectOpen, new ProjectOpenParams
            {
                Projects = projects.Select(p => new Uri(p).AbsoluteUri).ToArray()
            });
        }
    }
}
