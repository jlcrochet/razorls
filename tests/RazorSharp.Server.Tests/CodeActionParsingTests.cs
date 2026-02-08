using System.Reflection;
using System.Text.Json;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class CodeActionParsingTests
{
    [Fact]
    public void TryGetNestedCodeActions_InvalidArgumentsKind_ReturnsFalse()
    {
        var action = JsonSerializer.SerializeToElement(new
        {
            command = new
            {
                command = "roslyn.client.nestedCodeAction",
                arguments = new
                {
                    invalid = true
                }
            }
        });

        var result = InvokeTryGetNestedCodeActions(action, out var nestedActions);

        Assert.False(result);
        Assert.Equal(JsonValueKind.Undefined, nestedActions.ValueKind);
    }

    [Fact]
    public void TryGetNestedCodeActions_NestedActionsMustBeArray_ReturnsFalse()
    {
        var action = JsonSerializer.SerializeToElement(new
        {
            command = new
            {
                command = "roslyn.client.nestedCodeAction",
                arguments = new object[]
                {
                    new
                    {
                        NestedCodeActions = new
                        {
                            invalid = true
                        }
                    }
                }
            }
        });

        var result = InvokeTryGetNestedCodeActions(action, out var nestedActions);

        Assert.False(result);
        Assert.Equal(JsonValueKind.Object, nestedActions.ValueKind);
    }

    [Fact]
    public void TryGetNestedCodeActions_ValidNestedActions_ReturnsTrue()
    {
        var action = JsonSerializer.SerializeToElement(new
        {
            command = new
            {
                command = "roslyn.client.nestedCodeAction",
                arguments = new object[]
                {
                    new
                    {
                        NestedCodeActions = new object[]
                        {
                            new { title = "Fix issue" }
                        }
                    }
                }
            }
        });

        var result = InvokeTryGetNestedCodeActions(action, out var nestedActions);

        Assert.True(result);
        Assert.Equal(JsonValueKind.Array, nestedActions.ValueKind);
        Assert.Equal(1, nestedActions.GetArrayLength());
    }

    private static bool InvokeTryGetNestedCodeActions(JsonElement action, out JsonElement nestedActions)
    {
        var method = typeof(RazorLanguageServer).GetMethod(
            "TryGetNestedCodeActions",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var args = new object?[] { action, default(JsonElement) };
        var result = (bool)method!.Invoke(null, args)!;
        nestedActions = (JsonElement)args[1]!;
        return result;
    }
}
