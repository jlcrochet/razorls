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

    [Fact]
    public async Task Initialize_ReplaySetUsesConfiguredUriComparer()
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
            var replayingField = typeof(RazorLanguageServer).GetField("_replayingOpenDocuments", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(comparerField);
            Assert.NotNull(replayingField);

            var comparer = (StringComparer)comparerField!.GetValue(server)!;
            var replayingSet = (HashSet<string>)replayingField!.GetValue(server)!;
            Assert.Same(comparer, replayingSet.Comparer);
        }
        finally
        {
            await server.DisposeAsync();
            DeleteTempDir(root);
        }
    }

    [Fact]
    public async Task ConfigureUriComparers_ReinitializesReplaySetWithUpdatedComparer()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "1.0.0-test");
        var server = new RazorLanguageServer(loggerFactory, deps);

        try
        {
            var replayingField = typeof(RazorLanguageServer).GetField("_replayingOpenDocuments", BindingFlags.Instance | BindingFlags.NonPublic);
            var comparerField = typeof(RazorLanguageServer).GetField("_uriComparer", BindingFlags.Instance | BindingFlags.NonPublic);
            var configureMethod = typeof(RazorLanguageServer).GetMethod("ConfigureUriComparers", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(replayingField);
            Assert.NotNull(comparerField);
            Assert.NotNull(configureMethod);

            var previousSet = (HashSet<string>)replayingField!.GetValue(server)!;
            previousSet.Add("file:///test.cs");

            configureMethod!.Invoke(server, [Path.GetTempPath()]);

            var updatedSet = (HashSet<string>)replayingField.GetValue(server)!;
            var configuredComparer = (StringComparer)comparerField!.GetValue(server)!;

            Assert.NotSame(previousSet, updatedSet);
            Assert.Same(configuredComparer, updatedSet.Comparer);
            Assert.Empty(updatedSet);
        }
        finally
        {
            await server.DisposeAsync();
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
