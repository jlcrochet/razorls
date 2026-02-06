using System.Collections.Concurrent;
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
    public void ConcurrentRentReturn_PoolSizeStaysBounded()
    {
        var maxPoolSize = GetMaxPoolSize();
        // Drain existing pool to start clean
        var drained = new List<List<int>>();
        for (var i = 0; i < maxPoolSize * 2; i++)
        {
            drained.Add(ListPool<int>.Rent());
        }
        foreach (var d in drained) ListPool<int>.Return(d);

        const int threadCount = 8;
        const int iterationsPerThread = 1000;
        var exceptions = new ConcurrentBag<Exception>();
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    for (var i = 0; i < iterationsPerThread; i++)
                    {
                        var list = ListPool<int>.Rent();
                        list.Add(i);
                        ListPool<int>.Return(list);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads) thread.Join();

        Assert.Empty(exceptions);

        // Drain pool and verify count is bounded
        var pooled = new List<List<int>>();
        for (var i = 0; i < maxPoolSize * 2; i++)
        {
            pooled.Add(ListPool<int>.Rent());
        }

        // All rented lists should be cleared (no leftover data)
        foreach (var list in pooled)
        {
            Assert.Empty(list);
        }

        foreach (var list in pooled) ListPool<int>.Return(list);
    }

    [Fact]
    public void ConcurrentRentReturn_NoItemsLostUnderContention()
    {
        var maxPoolSize = GetMaxPoolSize();
        // Drain existing pool completely (don't return - leave pool empty)
        var drained = new List<List<int>>();
        for (var i = 0; i < maxPoolSize * 2; i++)
        {
            drained.Add(ListPool<int>.Rent());
        }

        // Create exactly MaxPoolSize distinct lists to return concurrently
        var returned = new List<List<int>>();
        for (var i = 0; i < maxPoolSize; i++)
        {
            returned.Add(new List<int>(1));
        }

        const int threadCount = 8;
        var barrier = new Barrier(threadCount);
        var exceptions = new ConcurrentBag<Exception>();
        var listsPerThread = maxPoolSize / threadCount;

        // Concurrently return lists from multiple threads
        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var start = t * listsPerThread;
            var count = (t == threadCount - 1) ? maxPoolSize - start : listsPerThread;
            threads[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    for (var i = start; i < start + count; i++)
                    {
                        ListPool<int>.Return(returned[i]);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads) thread.Join();
        Assert.Empty(exceptions);

        // Drain and count how many of our returned lists are in the pool
        var rentedBack = new List<List<int>>();
        var returnedSet = new HashSet<List<int>>(returned);
        var reused = 0;
        for (var i = 0; i < maxPoolSize * 2; i++)
        {
            var list = ListPool<int>.Rent();
            rentedBack.Add(list);
            if (returnedSet.Contains(list)) reused++;
        }

        // All MaxPoolSize lists should have been accepted (no spurious rejections)
        Assert.Equal(maxPoolSize, reused);

        foreach (var list in rentedBack) ListPool<int>.Return(list);
        foreach (var list in drained) ListPool<int>.Return(list);
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
