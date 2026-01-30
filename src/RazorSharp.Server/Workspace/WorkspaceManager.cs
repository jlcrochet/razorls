using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RazorSharp.Server.Workspace;

/// <summary>
/// Manages workspace discovery - finding solutions and projects.
/// </summary>
public class WorkspaceManager
{
    readonly ILogger<WorkspaceManager> _logger;
    static readonly EnumerationOptions EnumerateOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };
    static readonly bool IsCaseInsensitiveFileSystem = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    static readonly StringComparer DirectoryNameComparer = IsCaseInsensitiveFileSystem
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    static readonly string[] DefaultExcludedDirectoryNames =
    [
        "bin",
        "obj",
        ".git",
        ".vs",
        "node_modules"
    ];
    HashSet<string> _excludedDirectoryNames;
    List<GlobPattern> _excludedDirectoryPatterns;

    public WorkspaceManager(ILogger<WorkspaceManager> logger)
    {
        _logger = logger;
        _excludedDirectoryNames = new HashSet<string>(DefaultExcludedDirectoryNames, DirectoryNameComparer);
        _excludedDirectoryPatterns = new List<GlobPattern>();
    }

    /// <summary>
    /// Configures directory names to exclude from workspace searches.
    /// If overrideDirectories is provided (even empty), it replaces the defaults.
    /// additionalDirectories are always added on top.
    /// </summary>
    public void ConfigureExcludedDirectories(string[]? overrideDirectories, string[]? additionalDirectories)
    {
        var newSet = new HashSet<string>(DirectoryNameComparer);
        var newPatterns = new List<GlobPattern>();

        if (overrideDirectories != null)
        {
            AddExclusions(newSet, newPatterns, overrideDirectories);
        }
        else
        {
            AddExclusions(newSet, newPatterns, DefaultExcludedDirectoryNames);
        }

        AddExclusions(newSet, newPatterns, additionalDirectories);
        _excludedDirectoryNames = newSet;
        _excludedDirectoryPatterns = newPatterns;
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
                    foreach (var file in Directory.EnumerateFiles(current, "*.csproj", EnumerateOptions))
                    {
                        projects.Add(file);
                    }

                    foreach (var dir in Directory.EnumerateDirectories(current, "*", EnumerateOptions))
                    {
                        var name = Path.GetFileName(dir);
                        if (ShouldSkipDirectory(rootPath, dir, name))
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

    bool ShouldSkipDirectory(string rootPath, string directoryPath, string? directoryName = null)
    {
        var name = directoryName ?? Path.GetFileName(directoryPath);
        if (_excludedDirectoryNames.Contains(name))
        {
            return true;
        }

        if (_excludedDirectoryPatterns.Count == 0)
        {
            return false;
        }

        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(rootPath, directoryPath);
        }
        catch
        {
            return false;
        }

        var normalizedPath = NormalizePath(relativePath);
        foreach (var pattern in _excludedDirectoryPatterns)
        {
            if (pattern.IsMatch(normalizedPath))
            {
                return true;
            }
        }

        return false;
    }

    static void AddExclusions(HashSet<string> names, List<GlobPattern> patterns, IEnumerable<string>? directories)
    {
        if (directories == null)
        {
            return;
        }

        foreach (var dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var trimmed = dir.Trim();
            if (IsPattern(trimmed))
            {
                patterns.Add(new GlobPattern(trimmed, IsCaseInsensitiveFileSystem));
            }
            else
            {
                names.Add(trimmed);
            }
        }
    }

    static bool IsPattern(string value)
        => value.IndexOfAny(['*', '?', '/', '\\']) >= 0;

    static string NormalizePath(string path)
        => path.Replace('\\', '/');

    sealed class GlobPattern
    {
        readonly Regex _regex;

        public GlobPattern(string pattern, bool ignoreCase)
        {
            var normalized = NormalizePath(pattern);
            _regex = new Regex(
                $"^{GlobToRegex(normalized)}$",
                ignoreCase
                    ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
                    : RegexOptions.CultureInvariant);
        }

        public bool IsMatch(string relativePath)
            => _regex.IsMatch(relativePath);

        static string GlobToRegex(string pattern)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                switch (c)
                {
                    case '*':
                        if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                        {
                            if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                            {
                                builder.Append("(?:.*/)?");
                                i += 2;
                            }
                            else
                            {
                                builder.Append(".*");
                                i++;
                            }
                        }
                        else
                        {
                            builder.Append("[^/]*");
                        }
                        break;
                    case '?':
                        builder.Append("[^/]");
                        break;
                    case '\\':
                    case '/':
                        builder.Append('/');
                        break;
                    case '.':
                    case '$':
                    case '^':
                    case '{':
                    case '[':
                    case '(':
                    case '|':
                    case ')':
                    case '+':
                    case ']':
                    case '}':
                        builder.Append('\\');
                        builder.Append(c);
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }

            return builder.ToString();
        }
    }
}
