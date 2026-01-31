using Microsoft.Extensions.Logging;
using RazorSharp.Server.Html;

namespace RazorSharp.Server.Tests;

public class HtmlLanguageClientRestartTests
{
    [Fact]
    public void HtmlLanguageClient_AllowsSingleRestartThenDisables()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var client = new HtmlLanguageClient(loggerFactory.CreateLogger<HtmlLanguageClient>());

        Assert.False(client.IsRestartAttemptedForTests());
        Assert.True(client.IsEnabledForTests());

        client.TriggerHtmlServerExitForTests();

        Assert.True(client.IsRestartAttemptedForTests());
        Assert.True(client.IsEnabledForTests());
        Assert.False(client.IsInitializedForTests());
        Assert.False(client.IsStartAttemptedForTests());

        client.TriggerHtmlServerExitForTests();

        Assert.True(client.IsRestartAttemptedForTests());
        Assert.False(client.IsEnabledForTests());
    }
}
