using System.Reflection;
using RazorSharp.Server.Utilities;

namespace RazorSharp.Server.Tests;

public class ListPoolTests
{
    private static int GetMaxPoolSize()
    {
        var field = typeof(ListPool<int>).GetField("MaxPoolSize", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (int)field!.GetRawConstantValue()!;
    }

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

    [Fact]
    public void Return_BoundedPool_DropsExtras()
    {
        var maxPoolSize = GetMaxPoolSize();
        var drained = new List<List<int>>();
        var rented = new List<List<int>>();

        try
        {
            for (var i = 0; i < maxPoolSize * 2; i++)
            {
                drained.Add(ListPool<int>.Rent());
            }

            var returned = new List<List<int>>();
            for (var i = 0; i < maxPoolSize + 5; i++)
            {
                var list = new List<int>(1);
                returned.Add(list);
                ListPool<int>.Return(list);
            }

            var returnedSet = new HashSet<List<int>>(returned);
            var reusedFromReturned = 0;
            for (var i = 0; i < maxPoolSize + 5; i++)
            {
                var list = ListPool<int>.Rent();
                rented.Add(list);
                if (returnedSet.Contains(list))
                {
                    reusedFromReturned++;
                }
            }

            Assert.Equal(maxPoolSize, reusedFromReturned);
        }
        finally
        {
            foreach (var list in rented)
            {
                ListPool<int>.Return(list);
            }

            foreach (var list in drained)
            {
                ListPool<int>.Return(list);
            }
        }
    }
}
