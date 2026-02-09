using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class WorkspaceWatchConfigContextFactoryTests
{
    [Fact]
    public void Create_UsesWorkspaceRootForLocalConfigPath()
    {
        var factory = new WorkspaceWatchConfigContextFactory(
            static path => path,
            static () => "/global/omnisharp.json",
            "omnisharp.json");

        var context = factory.Create("/workspace/root");

        Assert.Equal(Path.Combine("/workspace/root", "omnisharp.json"), context.LocalConfigPath);
        Assert.Equal("/global/omnisharp.json", context.GlobalConfigPath);
    }

    [Fact]
    public void Create_LeavesLocalConfigPathNull_WhenWorkspaceRootMissing()
    {
        var localCalls = 0;
        var factory = new WorkspaceWatchConfigContextFactory(
            path =>
            {
                localCalls++;
                return path;
            },
            static () => "/global/omnisharp.json",
            "omnisharp.json");

        var context = factory.Create(null);

        Assert.Null(context.LocalConfigPath);
        Assert.Equal("/global/omnisharp.json", context.GlobalConfigPath);
        Assert.Equal(0, localCalls);
    }
}
