using System.Text;

namespace RazorSharp.Server;

internal sealed class NotificationBackpressureTracker
{
    readonly Lock _droppedByMethodLock = new();
    readonly Dictionary<string, long> _droppedByMethod = new(StringComparer.Ordinal);
    long _droppedTotal;
    int _notificationQueueDepth;
    int _priorityNotificationQueueDepth;
    int _notificationQueuePeak;
    int _priorityNotificationQueuePeak;

    public readonly record struct QueueSnapshot(
        int NotificationDepth,
        int NotificationPeak,
        int PriorityDepth,
        int PriorityPeak);

    public readonly record struct DroppedSnapshot(
        long TotalDropped,
        long MethodDropped,
        QueueSnapshot Queue);

    public void Enqueue(bool isPriorityQueue)
    {
        if (isPriorityQueue)
        {
            var depth = Interlocked.Increment(ref _priorityNotificationQueueDepth);
            UpdatePeak(ref _priorityNotificationQueuePeak, depth);
            return;
        }

        var regularDepth = Interlocked.Increment(ref _notificationQueueDepth);
        UpdatePeak(ref _notificationQueuePeak, regularDepth);
    }

    public void Dequeue(bool isPriorityQueue)
    {
        if (isPriorityQueue)
        {
            var depth = Interlocked.Decrement(ref _priorityNotificationQueueDepth);
            if (depth < 0)
            {
                Interlocked.Exchange(ref _priorityNotificationQueueDepth, 0);
            }
            return;
        }

        var regularDepth = Interlocked.Decrement(ref _notificationQueueDepth);
        if (regularDepth < 0)
        {
            Interlocked.Exchange(ref _notificationQueueDepth, 0);
        }
    }

    public DroppedSnapshot RecordDropped(string method)
    {
        var total = Interlocked.Increment(ref _droppedTotal);
        var methodCount = IncrementDroppedMethodCount(method);
        return new DroppedSnapshot(total, methodCount, GetQueueSnapshot());
    }

    public long DroppedTotal => Interlocked.Read(ref _droppedTotal);

    public QueueSnapshot GetQueueSnapshot()
    {
        return new QueueSnapshot(
            NotificationDepth: Volatile.Read(ref _notificationQueueDepth),
            NotificationPeak: Volatile.Read(ref _notificationQueuePeak),
            PriorityDepth: Volatile.Read(ref _priorityNotificationQueueDepth),
            PriorityPeak: Volatile.Read(ref _priorityNotificationQueuePeak));
    }

    public string GetDroppedMethodsSummary(int maxEntries)
    {
        KeyValuePair<string, long>[] snapshot;
        lock (_droppedByMethodLock)
        {
            if (_droppedByMethod.Count == 0)
            {
                return "none";
            }

            snapshot = _droppedByMethod.ToArray();
        }

        Array.Sort(snapshot, static (left, right) =>
        {
            var valueCompare = right.Value.CompareTo(left.Value);
            if (valueCompare != 0)
            {
                return valueCompare;
            }

            return string.Compare(left.Key, right.Key, StringComparison.Ordinal);
        });

        var count = Math.Min(maxEntries, snapshot.Length);
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(snapshot[i].Key);
            builder.Append(':');
            builder.Append(snapshot[i].Value);
        }

        if (snapshot.Length > count)
        {
            builder.Append(", ...");
        }

        return builder.ToString();
    }

    internal Dictionary<string, long> GetDroppedByMethodSnapshotForTests()
    {
        lock (_droppedByMethodLock)
        {
            return new Dictionary<string, long>(_droppedByMethod, StringComparer.Ordinal);
        }
    }

    internal void SetQueueSnapshotForTests(QueueSnapshot snapshot)
    {
        Interlocked.Exchange(ref _notificationQueueDepth, snapshot.NotificationDepth);
        Interlocked.Exchange(ref _notificationQueuePeak, snapshot.NotificationPeak);
        Interlocked.Exchange(ref _priorityNotificationQueueDepth, snapshot.PriorityDepth);
        Interlocked.Exchange(ref _priorityNotificationQueuePeak, snapshot.PriorityPeak);
    }

    private long IncrementDroppedMethodCount(string method)
    {
        lock (_droppedByMethodLock)
        {
            if (_droppedByMethod.TryGetValue(method, out var existing))
            {
                var updated = existing + 1;
                _droppedByMethod[method] = updated;
                return updated;
            }

            _droppedByMethod[method] = 1;
            return 1;
        }
    }

    private static void UpdatePeak(ref int peak, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref peak);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref peak, candidate, current) == current)
            {
                return;
            }
        }
    }
}
