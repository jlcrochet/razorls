using RazorSharp.Server.Utilities;

namespace RazorSharp.Server.Tests;

public class ListPoolTests
{
    [Fact]
    public void Return_ClearsList()
    {
        var list = ListPool<int>.Rent();
        try
        {
            list.Add(1);
            list.Add(2);
            ListPool<int>.Return(list);
            var rentedAgain = ListPool<int>.Rent();
            try
            {
                Assert.Empty(rentedAgain);
            }
            finally
            {
                ListPool<int>.Return(rentedAgain);
            }
        }
        finally
        {
            // list may have been dropped if the pool was full
        }
    }

    [Fact]
    public void Rent_WithMinCapacity_EnsuresCapacity()
    {
        var list = ListPool<int>.Rent(128);
        try
        {
            Assert.True(list.Capacity >= 128);
        }
        finally
        {
            ListPool<int>.Return(list);
        }
    }
}
