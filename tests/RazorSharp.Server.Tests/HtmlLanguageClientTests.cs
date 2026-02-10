using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Server.Html;

namespace RazorSharp.Server.Tests;

public class HtmlLanguageClientTests
{
    [Fact]
    public async Task FlushCachedProjections_SendsDidOpenAndClearsContent()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var client = new HtmlLanguageClient(loggerFactory.CreateLogger<HtmlLanguageClient>());

        client.SetStartAttemptedForTests();

        var razorUri = "file:///workspace/test.razor";
        var checksum = "checksum-1";
        var html = "<p>cached</p>";

        await client.UpdateHtmlProjectionAsync(razorUri, checksum, html);

        var payloads = new List<JsonElement>();
        client.SetDidOpenOverrideForTests(payload =>
        {
            payloads.Add(JsonSerializer.SerializeToElement(payload));
            return Task.CompletedTask;
        });
        client.SetInitializedForTests(true);

        await client.FlushCachedProjectionsForTestsAsync();

        Assert.Single(payloads);

        var textDocument = payloads[0].GetProperty("textDocument");
        Assert.Equal(razorUri + "__virtual.html", textDocument.GetProperty("uri").GetString());
        Assert.Equal("html", textDocument.GetProperty("languageId").GetString());
        Assert.Equal(1, textDocument.GetProperty("version").GetInt32());
        Assert.Equal(html, textDocument.GetProperty("text").GetString());

        var projection = client.GetProjectionByRazorUri(razorUri);
        Assert.NotNull(projection);
        Assert.Null(projection!.Content);
    }

    [Fact]
    public void BuildLaunchCommand_UsesNodeForJavaScriptServer()
    {
        var (fileName, args) = HtmlLanguageClient.BuildLaunchCommandForTests("/tmp/html-ls.js", isWindows: false);

        Assert.Equal("node", fileName);
        Assert.Equal(["/tmp/html-ls.js", "--stdio"], args);
    }

    [Fact]
    public void BuildLaunchCommand_UsesCmdForWindowsCmdScripts()
    {
        var serverPath = @"C:\tools\vscode-html-language-server.cmd";
        var (fileName, args) = HtmlLanguageClient.BuildLaunchCommandForTests(serverPath, isWindows: true);

        Assert.Equal("cmd.exe", fileName);
        Assert.Equal(["/d", "/s", "/c", $"\"{serverPath}\" --stdio"], args);
    }

    [Fact]
    public void BuildLaunchCommand_UsesExecutableDirectlyForNativeBinary()
    {
        var serverPath = "/usr/local/bin/vscode-html-language-server";
        var (fileName, args) = HtmlLanguageClient.BuildLaunchCommandForTests(serverPath, isWindows: false);

        Assert.Equal(serverPath, fileName);
        Assert.Equal(["--stdio"], args);
    }
}
