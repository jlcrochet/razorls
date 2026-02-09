using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class OmniSharpConfigPathResolverTests
{
    [Fact]
    public void TryGetGlobalConfigPath_PrefersOmniSharpHome()
    {
        var oldHome = Environment.GetEnvironmentVariable("OMNISHARPHOME");
        var omniSharpHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("OMNISHARPHOME", omniSharpHome);
            var resolver = new OmniSharpConfigPathResolver(Path.GetFullPath, "omnisharp.json");

            var result = resolver.TryGetGlobalConfigPath();

            Assert.Equal(Path.GetFullPath(Path.Combine(omniSharpHome, "omnisharp.json")), result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNISHARPHOME", oldHome);
        }
    }

    [Fact]
    public void TryGetGlobalConfigPath_ReturnsNull_WhenNormalizationFails()
    {
        var oldHome = Environment.GetEnvironmentVariable("OMNISHARPHOME");
        try
        {
            Environment.SetEnvironmentVariable("OMNISHARPHOME", "/tmp/does-not-matter");
            var resolver = new OmniSharpConfigPathResolver(static _ => null, "omnisharp.json");

            var result = resolver.TryGetGlobalConfigPath();

            Assert.Null(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNISHARPHOME", oldHome);
        }
    }

    [Theory]
    [InlineData(StringComparison.OrdinalIgnoreCase, true)]
    [InlineData(StringComparison.Ordinal, false)]
    public void IsConfigPath_UsesProvidedComparison(StringComparison comparison, bool expected)
    {
        var result = OmniSharpConfigPathResolver.IsConfigPath(
            "/TMP/OMNISHARP.JSON",
            localPath: "/tmp/omnisharp.json",
            globalPath: null,
            comparison);

        Assert.Equal(expected, result);
    }
}
