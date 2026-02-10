using Microsoft.Extensions.Logging;
using RazorSharp.Server.Html;
using System.Text.Json;

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

    [Fact]
    public async Task HtmlLanguageClient_RestartedSession_ReopensProjectionBeforeChange()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var client = new HtmlLanguageClient(loggerFactory.CreateLogger<HtmlLanguageClient>());
        var didOpenPayloads = new List<JsonElement>();
        var didChangeCalls = 0;
        const string razorUri = "file:///workspace/test.razor";

        client.SetDidOpenOverrideForTests(payload =>
        {
            didOpenPayloads.Add(JsonSerializer.SerializeToElement(payload));
            return Task.CompletedTask;
        });
        client.SetDidChangeOverrideForTests(_ =>
        {
            didChangeCalls++;
            return Task.CompletedTask;
        });

        client.SetInitializedForTests(true);
        await client.UpdateHtmlProjectionAsync(razorUri, "checksum-1", "<p>first</p>");

        Assert.Single(didOpenPayloads);
        Assert.Equal(0, didChangeCalls);

        client.TriggerHtmlServerExitForTests();
        client.SetInitializedForTests(true);
        await client.UpdateHtmlProjectionAsync(razorUri, "checksum-2", "<p>second</p>");

        Assert.Equal(2, didOpenPayloads.Count);
        Assert.Equal(0, didChangeCalls);
        Assert.Equal(2, didOpenPayloads[1].GetProperty("textDocument").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task HtmlLanguageClient_DidOpenFailure_DoesNotMarkProjectionAsOpened()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var client = new HtmlLanguageClient(loggerFactory.CreateLogger<HtmlLanguageClient>());
        var didOpenAttempts = 0;
        var shouldFail = true;
        var didChangeCalls = 0;
        const string razorUri = "file:///workspace/test.razor";

        client.SetDidOpenOverrideForTests(_ =>
        {
            didOpenAttempts++;
            if (shouldFail)
            {
                shouldFail = false;
                throw new InvalidOperationException("boom");
            }

            return Task.CompletedTask;
        });
        client.SetDidChangeOverrideForTests(_ =>
        {
            didChangeCalls++;
            return Task.CompletedTask;
        });

        client.SetInitializedForTests(true);
        await client.UpdateHtmlProjectionAsync(razorUri, "checksum-1", "<p>first</p>");

        Assert.Equal(1, didOpenAttempts);
        Assert.Equal(0, didChangeCalls);
        Assert.True(client.IsRestartAttemptedForTests());

        client.SetInitializedForTests(true);
        await client.UpdateHtmlProjectionAsync(razorUri, "checksum-2", "<p>second</p>");

        Assert.Equal(2, didOpenAttempts);
        Assert.Equal(0, didChangeCalls);
    }

    [Fact]
    public async Task HtmlLanguageClient_ForcedExit_DoesNotFaultStderrTask()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var client = new HtmlLanguageClient(loggerFactory.CreateLogger<HtmlLanguageClient>());
        var tempDir = Path.Combine(Path.GetTempPath(), "razorsharp-html-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "fake-html-language-server");

        try
        {
            File.WriteAllText(scriptPath, """
                #!/bin/sh
                while true; do
                  echo "stderr tick" 1>&2
                  sleep 0.01
                done
                """);

            try
            {
                File.SetUnixFileMode(
                    scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }
            catch (PlatformNotSupportedException)
            {
                return;
            }
            catch (NotSupportedException)
            {
                return;
            }

            client.SetHtmlServerPathOverrideForTests(scriptPath);
            var started = await client.StartForTestsAsync();
            Assert.True(started);

            await Task.Delay(100);

            client.TriggerHtmlServerExitForTests();

            var stderrTask = client.GetStderrTaskForTests();
            Assert.NotNull(stderrTask);
            await Task.WhenAny(stderrTask!, Task.Delay(1000));

            Assert.False(stderrTask!.IsFaulted);
        }
        finally
        {
            await client.DisposeAsync();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
