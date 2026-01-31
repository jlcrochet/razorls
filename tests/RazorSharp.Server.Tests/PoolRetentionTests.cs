using RazorSharp.Server.Utilities;

namespace RazorSharp.Server.Tests;

public class PoolRetentionTests
{
    [Fact]
    public void ListPool_Return_DropsOversizedLists()
    {
        var list = ListPool<int>.Rent();
        try
        {
            list.Capacity = 20000;
            ListPool<int>.Return(list);

            var rented = ListPool<int>.Rent();
            try
            {
                Assert.True(rented.Capacity < 20000);
            }
            finally
            {
                ListPool<int>.Return(rented);
            }
        }
        finally
        {
            // list may have been dropped; no action needed
        }
    }

    [Fact]
    public void ArrayPoolBufferWriter_Reset_DropsOversizedBuffer()
    {
        using var writer = new ArrayPoolBufferWriter(1_200_000);

        var memory = writer.GetMemory(1_200_000);
        memory.Span.Fill(1);
        writer.Advance(1_200_000);

        var largeBefore = writer.WrittenMemory.Length;
        writer.Reset();

        var after = writer.GetMemory(1).Length;

        Assert.True(largeBefore > after);
    }
}
