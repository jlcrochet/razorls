using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RazorSharp.Utilities;

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
    static readonly string[] DefaultExcludedDirectoryNames =
    [
        "bin",
        "obj",
        ".git",
        ".vs",
        "node_modules"
    ];
    bool _isCaseInsensitiveFileSystem;
    StringComparer _directoryNameComparer = StringComparer.Ordinal;
    string[]? _excludedOverrideDirectories;
    string[]? _excludedAdditionalDirectories;
    HashSet<string> _excludedDirectoryNames;
    List<GlobPattern> _excludedDirectoryPatterns;

    public WorkspaceManager(ILogger<WorkspaceManager> logger)
    {
        _logger = logger;
        _excludedDirectoryNames = new HashSet<string>(DefaultExcludedDirectoryNames, _directoryNameComparer);
        _excludedDirectoryPatterns = new List<GlobPattern>();
        SetCaseSensitivity(null);
    }

    public void SetCaseSensitivity(string? probePath)
    {
        _isCaseInsensitiveFileSystem = FileSystemCaseSensitivity.IsCaseInsensitiveForPath(probePath);
        _directoryNameComparer = _isCaseInsensitiveFileSystem
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        RebuildExclusions();
    }

    /// <summary>
    /// Configures directory names to exclude from workspace searches.
    /// If overrideDirectories is provided (even empty), it replaces the defaults.
    /// additionalDirectories are always added on top.
    /// </summary>
    public void ConfigureExcludedDirectories(string[]? overrideDirectories, string[]? additionalDirectories)
    {
        _excludedOverrideDirectories = overrideDirectories;
        _excludedAdditionalDirectories = additionalDirectories;
        RebuildExclusions();
    }

    /// <summary>
    /// Finds a solution file in the given directory or its parents.
    /// </summary>
    public string? FindSolution(string rootPath)
    {
        var directory = new DirectoryInfo(rootPath);

        while (directory != null)
        {
            List<FileInfo> solutions;
            try
            {
                solutions = new List<FileInfo>();
                solutions.AddRange(directory.GetFiles("*.sln"));
                solutions.AddRange(directory.GetFiles("*.slnf"));
                solutions.AddRange(directory.GetFiles("*.slnx"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error searching for solutions in {Path}", directory.FullName);
                directory = directory.Parent;
                continue;
            }

            if (solutions.Count > 0)
            {
                // If multiple solutions, prefer ones that match the directory name
                var ordered = solutions
                    .OrderBy(s => GetSolutionExtensionPriority(s.Extension))
                    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var preferred = ordered.FirstOrDefault(s =>
                    Path.GetFileNameWithoutExtension(s.Name)
                        .Equals(directory.Name, StringComparison.OrdinalIgnoreCase));

                var solution = (preferred ?? ordered[0]).FullName;
                _logger.LogDebug("Found solution: {Solution}", solution);
                return solution;
            }

            directory = directory.Parent;
        }

        return null;
    }

    static int GetSolutionExtensionPriority(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".sln" => 0,
            ".slnf" => 1,
            ".slnx" => 2,
            _ => 3
        };

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

    internal bool ShouldSkipDirectory(string rootPath, string directoryPath, string? directoryName = null)
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

    void RebuildExclusions()
    {
        var newSet = new HashSet<string>(_directoryNameComparer);
        var newPatterns = new List<GlobPattern>();

        if (_excludedOverrideDirectories != null)
        {
            AddExclusions(newSet, newPatterns, _excludedOverrideDirectories);
        }
        else
        {
            AddExclusions(newSet, newPatterns, DefaultExcludedDirectoryNames);
        }

        AddExclusions(newSet, newPatterns, _excludedAdditionalDirectories);
        _excludedDirectoryNames = newSet;
        _excludedDirectoryPatterns = newPatterns;
    }

    void AddExclusions(HashSet<string> names, List<GlobPattern> patterns, IEnumerable<string>? directories)
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
                patterns.Add(new GlobPattern(trimmed, _isCaseInsensitiveFileSystem));
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
                    ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled
                    : RegexOptions.CultureInvariant | RegexOptions.Compiled);
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
