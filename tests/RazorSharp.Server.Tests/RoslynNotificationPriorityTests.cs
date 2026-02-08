using System.Reflection;
using RazorSharp.Protocol;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class RoslynNotificationPriorityTests
{
    [Theory]
    [InlineData(LspMethods.TextDocumentPublishDiagnostics, true)]
    [InlineData(LspMethods.ProjectInitializationComplete, true)]
    [InlineData(LspMethods.Progress, false)]
    [InlineData("window/logMessage", false)]
    public void IsHighPriorityRoslynNotification_ReturnsExpectedValue(string method, bool expected)
    {
        var helper = typeof(RazorLanguageServer).GetMethod(
            "IsHighPriorityRoslynNotification",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(helper);

        var result = (bool)helper!.Invoke(null, [method])!;
        Assert.Equal(expected, result);
    }
}
