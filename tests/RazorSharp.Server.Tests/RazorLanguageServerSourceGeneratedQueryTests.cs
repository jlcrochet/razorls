using System.Reflection;
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
