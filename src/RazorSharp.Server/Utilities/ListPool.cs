using System.Collections.Concurrent;

namespace RazorSharp.Server.Utilities;

/// <summary>
/// A simple pool for reusing List instances to reduce allocations.
/// Thread-safe for concurrent rent/return operations.
/// </summary>
public static class ListPool<T>
{
    const int MaxPoolSize = 16;
    const int MaxRetainedCapacity = 8192;
    static readonly ConcurrentStack<List<T>> Pool = new();
    static int _poolSize;

    /// <summary>
    /// Rents a list from the pool, or creates a new one if the pool is empty.
    /// The returned list is cleared and ready for use.
    /// </summary>
    public static List<T> Rent()
    {
        if (Pool.TryPop(out var list))
        {
            Interlocked.Decrement(ref _poolSize);
            return list;
        }
        return new List<T>();
    }

    /// <summary>
    /// Rents a list from the pool with at least the specified capacity.
    /// </summary>
    public static List<T> Rent(int minCapacity)
    {
        if (Pool.TryPop(out var list))
        {
            Interlocked.Decrement(ref _poolSize);
            if (list.Capacity < minCapacity)
            {
                list.Capacity = minCapacity;
            }
            return list;
        }
        return new List<T>(minCapacity);
    }

    /// <summary>
    /// Returns a list to the pool for reuse. The list will be cleared.
    /// </summary>
    public static void Return(List<T> list)
    {
        if (list.Capacity > MaxRetainedCapacity)
        {
            // Pool is full, let GC handle this one
            list.Clear();
            return;
        }

        list.Clear();
        if (Interlocked.Increment(ref _poolSize) > MaxPoolSize)
        {
            Interlocked.Decrement(ref _poolSize);
            return;
        }

        Pool.Push(list);
    }
}
