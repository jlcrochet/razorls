using System.Reflection;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class SourceGeneratedPathTests
{
    [Theory]
    [InlineData("/home/user/project/obj/Debug/net10.0/generated/Assembly/Type/File.cs", true)]
    [InlineData(@"C:\Users\project\obj\Release\net10.0\generated\A\T\F.cs", true)]
    [InlineData("/project/OBJ/debug/net10.0/GENERATED/A/T/F.cs", true)]
    [InlineData("/project\\obj/Debug\\net10.0/generated\\A/T\\F.cs", true)]
    public void IsSourceGeneratedPath_PositiveCases(string path, bool expected)
    {
        var result = InvokeIsSourceGeneratedPath(path);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/home/user/project/bin/Debug/file.cs")]
    [InlineData("/home/user/objects/generated/file.cs")]
    [InlineData("/home/user/obj/Debug/file.cs")]
    [InlineData("/home/user/generated/obj-extra/file.cs")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSourceGeneratedPath_NegativeCases(string? path)
    {
        var result = InvokeIsSourceGeneratedPath(path);
        Assert.False(result);
    }

    [Fact]
    public void TryParseSourceGeneratedPath_ForwardSlash_ReturnsCorrectKey()
    {
        var path = "/project/obj/Debug/net10.0/generated/MyAssembly/MyType/MyFile.g.cs";

        var (success, key, isDebug) = InvokeTryParseSourceGeneratedPath(path);

        Assert.True(success);
        Assert.Equal(InvokeMakeSourceGeneratedKey("MyAssembly", "MyType", "MyFile.g.cs"), key);
        Assert.True(isDebug);
    }

    [Fact]
    public void TryParseSourceGeneratedPath_BackslashRelease_ReturnsCorrectKey()
    {
        var path = @"C:\project\obj\Release\net10.0\generated\A\T\F.cs";

        var (success, key, isDebug) = InvokeTryParseSourceGeneratedPath(path);

        Assert.True(success);
        Assert.Equal(InvokeMakeSourceGeneratedKey("A", "T", "F.cs"), key);
        Assert.False(isDebug);
    }

    [Fact]
    public void TryParseSourceGeneratedPath_TooFewSegmentsAfterGenerated_ReturnsFalse()
    {
        var path = "/project/obj/Debug/net10.0/generated/A/T";

        var (success, _, _) = InvokeTryParseSourceGeneratedPath(path);

        Assert.False(success);
    }

    [Fact]
    public void TryParseSourceGeneratedPath_NoObjSegment_ReturnsFalse()
    {
        var path = "/project/bin/Debug/net10.0/generated/A/T/F.cs";

        var (success, _, _) = InvokeTryParseSourceGeneratedPath(path);

        Assert.False(success);
    }

    [Fact]
    public void TryParseSourceGeneratedPath_NoGeneratedSegment_ReturnsFalse()
    {
        var path = "/project/obj/Debug/net10.0/output/A/T/F.cs";

        var (success, _, _) = InvokeTryParseSourceGeneratedPath(path);

        Assert.False(success);
    }

    private static bool InvokeIsSourceGeneratedPath(string? path)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "IsSourceGeneratedPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, [path])!;
    }

    private static (bool Success, string Key, bool IsDebug) InvokeTryParseSourceGeneratedPath(string path)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "TryParseSourceGeneratedPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { path, null, null };
        var result = (bool)method!.Invoke(null, args)!;
        return (result, (string)(args[1] ?? ""), (bool)(args[2] ?? false));
    }

    private static string InvokeMakeSourceGeneratedKey(string assemblyName, string typeName, string hintName)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "MakeSourceGeneratedKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [assemblyName, typeName, hintName])!;
    }
}
