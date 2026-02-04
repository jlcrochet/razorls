using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Protocol.Messages;
using RazorSharp.Utilities;

namespace RazorSharp.Server.Tests;

public class RazorLanguageServerComparerTests
{
    [Fact]
    public async Task Initialize_SetsUriComparerFromWorkspace()
    {
        var root = CreateTempDir();
        var rootUri = new Uri(root).AbsoluteUri;

        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "1.0.0-test", Path.Combine(root, "deps"));
        var server = new RazorLanguageServer(loggerFactory, deps);

        try
        {
            server.SetSkipDependencyCheck(true, true);
            server.SetDependenciesMissingForTests(true);

            var initParams = new InitializeParams
            {
                RootUri = rootUri
            };
            var paramsJson = JsonSerializer.SerializeToElement(initParams);
            await server.HandleInitializeAsync(paramsJson, CancellationToken.None);

            var comparerField = typeof(RazorLanguageServer).GetField("_uriComparer", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(comparerField);
            var comparer = comparerField!.GetValue(server) as StringComparer;
            Assert.NotNull(comparer);

            var isCaseInsensitive = FileSystemCaseSensitivity.IsCaseInsensitiveForPath(root);
            var expected = isCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            Assert.Same(expected, comparer);
        }
        finally
        {
            await server.DisposeAsync();
            DeleteTempDir(root);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "razorsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
