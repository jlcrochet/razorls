using System.Reflection;

namespace RazorSharp.Dependencies;

public static class VersionHelper
{
    public static string GetAssemblyVersion(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";
    }
}
