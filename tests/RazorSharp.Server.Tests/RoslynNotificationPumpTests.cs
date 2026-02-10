using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class RoslynNotificationPumpTests
{
    [Fact]
    public async Task Enqueue_WhenQueueIsFull_DropsOldestRegularNotification()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var tracker = new NotificationBackpressureTracker();
        var dropped = new ConcurrentQueue<string>();
        var processed = new ConcurrentQueue<string>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pump = new RoslynNotificationPump(
            loggerFactory.CreateLogger<RoslynNotificationPump>(),
            tracker,
            async (item, ct) =>
            {
                processed.Enqueue(item.Method);
                if (item.Method == "A")
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(ct);
                }
            },
            _ => false,
            regularQueueCapacity: 2);

        pump.Enqueue(new RoslynNotificationWorkItem("A", null), dropped.Enqueue);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        pump.Enqueue(new RoslynNotificationWorkItem("B", null), dropped.Enqueue);
        pump.Enqueue(new RoslynNotificationWorkItem("C", null), dropped.Enqueue);
        pump.Enqueue(new RoslynNotificationWorkItem("D", null), dropped.Enqueue);

        var queueSnapshot = tracker.GetQueueSnapshot();
        Assert.Equal(2, queueSnapshot.NotificationDepth);
        Assert.Equal(2, queueSnapshot.NotificationPeak);
        Assert.Equal(["B"], dropped.ToArray());

        releaseFirst.TrySetResult();

        await WaitForProcessedCountAsync(processed, expectedCount: 3, timeoutMs: 2000);

        var processedMethods = processed.ToArray();
        Assert.Contains("A", processedMethods);
        Assert.Contains("C", processedMethods);
        Assert.Contains("D", processedMethods);
        Assert.DoesNotContain("B", processedMethods);
    }

    static async Task WaitForProcessedCountAsync(ConcurrentQueue<string> processed, int expectedCount, int timeoutMs)
    {
        var start = DateTime.UtcNow;
        while (processed.Count < expectedCount)
        {
            if ((DateTime.UtcNow - start).TotalMilliseconds >= timeoutMs)
            {
                Assert.Fail($"Timed out waiting for {expectedCount} processed notifications (got {processed.Count}).");
            }

            await Task.Delay(10);
        }
    }
}
