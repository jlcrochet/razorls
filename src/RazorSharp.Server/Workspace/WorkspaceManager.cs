using Microsoft.Extensions.Logging;

namespace RazorSharp.Server.Workspace;

/// <summary>
/// Manages workspace discovery - finding solutions and projects.
/// </summary>
public class WorkspaceManager
{
    readonly ILogger<WorkspaceManager> _logger;

    public WorkspaceManager(ILogger<WorkspaceManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Finds a solution file in the given directory or its parents.
    /// </summary>
    public string? FindSolution(string rootPath)
    {
        var directory = new DirectoryInfo(rootPath);

        while (directory != null)
        {
            FileInfo[] solutions;
            try
            {
                solutions = directory.GetFiles("*.sln");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error searching for solutions in {Path}", directory.FullName);
                directory = directory.Parent;
                continue;
            }

            if (solutions.Length > 0)
            {
                // If multiple solutions, prefer ones that match the directory name
                var preferred = solutions.FirstOrDefault(s =>
                    Path.GetFileNameWithoutExtension(s.Name)
                        .Equals(directory.Name, StringComparison.OrdinalIgnoreCase));

                var solution = (preferred ?? solutions[0]).FullName;
                _logger.LogDebug("Found solution: {Solution}", solution);
                return solution;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Finds all project files in the given directory and subdirectories.
    /// </summary>
    public string[] FindProjects(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return [];
        }

        try
        {
            var projects = new List<string>();
            var stack = new Stack<string>();
            stack.Push(rootPath);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(current, "*.csproj"))
                    {
                        projects.Add(file);
                    }

                    foreach (var dir in Directory.EnumerateDirectories(current))
                    {
                        var name = Path.GetFileName(dir);
                        if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        stack.Push(dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error searching for projects in {Path}", current);
                }
            }

            _logger.LogDebug("Found {Count} projects in {Path}", projects.Count, rootPath);
            return projects.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching for projects in {Path}", rootPath);
            return [];
        }
    }
}
