using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class RazorLanguageServerSourceGeneratedQueryTests
{
    [Fact]
    public void TryGetQueryValue_UnencodedValue_ReturnsValue()
    {
        var query = "?assemblyName=MyAssembly&typeName=MyType&hintName=MyHint";
        var found = TryGetQueryValue(query, "assemblyName", out var value);

        Assert.True(found);
        Assert.Equal("MyAssembly", value);
    }

    [Fact]
    public void TryGetQueryValue_DecodesEncodedValue()
    {
        var query = "?assemblyName=My%20Assembly&typeName=My+Type&hintName=My%2BHint";

        var assemblyFound = TryGetQueryValue(query, "assemblyName", out var assemblyName);
        var typeFound = TryGetQueryValue(query, "typeName", out var typeName);

        Assert.True(assemblyFound);
        Assert.True(typeFound);
        Assert.Equal("My Assembly", assemblyName);
        Assert.Equal("My Type", typeName);
    }

    [Fact]
    public async Task TransformSourceGeneratedUris_ArrayWithNoChanges_ReturnsSameElement()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        try
        {
            // Set _workspaceRoot so TransformSourceGeneratedUris doesn't bail early
            var field = typeof(RazorLanguageServer).GetField("_workspaceRoot", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field!.SetValue(server, "/tmp/test-workspace");

            var locations = JsonSerializer.SerializeToElement(new[]
            {
                new { uri = "file:///tmp/test-workspace/Program.cs", range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 5 } } },
                new { uri = "file:///tmp/test-workspace/Startup.cs", range = new { start = new { line = 1, character = 0 }, end = new { line = 1, character = 10 } } }
            });

            var result = InvokeTransformSourceGeneratedUris(server, locations);

            // When no URIs need transformation, the original should be returned
            Assert.Equal(locations.GetRawText(), result.GetRawText());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task TransformSourceGeneratedUris_NullResponse_ReturnsSame()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        try
        {
            var field = typeof(RazorLanguageServer).GetField("_workspaceRoot", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field!.SetValue(server, "/tmp/test-workspace");

            var nullElement = JsonSerializer.SerializeToElement<object?>(null);
            var result = InvokeTransformSourceGeneratedUris(server, nullElement);

            Assert.Equal(JsonValueKind.Null, result.ValueKind);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task TransformSourceGeneratedUris_EmptyArray_ReturnsSame()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        try
        {
            var field = typeof(RazorLanguageServer).GetField("_workspaceRoot", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field!.SetValue(server, "/tmp/test-workspace");

            var emptyArray = JsonSerializer.SerializeToElement(Array.Empty<object>());
            var result = InvokeTransformSourceGeneratedUris(server, emptyArray);

            Assert.Equal(JsonValueKind.Array, result.ValueKind);
            Assert.Equal(0, result.GetArrayLength());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task TransformSourceGeneratedUris_ArrayWithNormalUris_ReturnsSameElement()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        try
        {
            var field = typeof(RazorLanguageServer).GetField("_workspaceRoot", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field!.SetValue(server, "/tmp/test-workspace");

            var locations = JsonSerializer.SerializeToElement(new[]
            {
                new { uri = "file:///tmp/test-workspace/Controllers/HomeController.cs", range = new { start = new { line = 5, character = 0 }, end = new { line = 5, character = 10 } } },
                new { uri = "file:///tmp/test-workspace/Models/User.cs", range = new { start = new { line = 10, character = 4 }, end = new { line = 10, character = 20 } } }
            });

            var result = InvokeTransformSourceGeneratedUris(server, locations);

            // Fast path: no roslyn-source-generated:// URIs, so the original element should be returned as-is
            Assert.Equal(locations.GetRawText(), result.GetRawText());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static JsonElement InvokeTransformSourceGeneratedUris(RazorLanguageServer server, JsonElement response)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "TransformSourceGeneratedUris",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (JsonElement)method!.Invoke(server, [response])!;
    }

    private static bool TryGetQueryValue(string query, string key, out string? value)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "TryGetQueryValue",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { query, key, null };
        var result = (bool)method!.Invoke(null, args)!;
        value = (string?)args[2];
        return result;
    }
}
