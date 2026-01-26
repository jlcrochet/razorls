using System.Collections.Concurrent;

namespace RazorSharp.Server.Utilities;

/// <summary>
/// A simple pool for reusing List instances to reduce allocations.
/// Thread-safe for concurrent rent/return operations.
/// </summary>
public static class ListPool<T>
{
    const int MaxPoolSize = 16;
    static readonly ConcurrentBag<List<T>> Pool = new();

    /// <summary>
    /// Rents a list from the pool, or creates a new one if the pool is empty.
    /// The returned list is cleared and ready for use.
    /// </summary>
    public static List<T> Rent()
    {
        if (Pool.TryTake(out var list))
        {
            return list;
        }
        return new List<T>();
    }

    /// <summary>
    /// Rents a list from the pool with at least the specified capacity.
    /// </summary>
    public static List<T> Rent(int minCapacity)
    {
        if (Pool.TryTake(out var list))
        {
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
        if (Pool.Count >= MaxPoolSize)
        {
            // Pool is full, let GC handle this one
            return;
        }

        list.Clear();
        Pool.Add(list);
    }
}
