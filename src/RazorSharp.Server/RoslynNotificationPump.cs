using System.Threading.Channels;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RazorSharp.Server;

internal readonly record struct RoslynNotificationWorkItem(string Method, JsonElement? Params);

internal sealed class RoslynNotificationPump : IAsyncDisposable
{
    readonly ILogger _logger;
    readonly NotificationBackpressureTracker _backpressure;
    readonly Func<RoslynNotificationWorkItem, CancellationToken, Task> _handler;
    readonly Func<string, bool> _isHighPriority;
    readonly Channel<RoslynNotificationWorkItem> _priorityChannel;
    readonly Lock _regularQueueLock = new();
    readonly Queue<RoslynNotificationWorkItem> _regularQueue = new();
    readonly SemaphoreSlim _regularQueueSignal = new(0);
    readonly int _regularQueueCapacity;
    readonly CancellationTokenSource _cts = new();
    readonly Task _priorityTask;
    readonly Task _task;

    public RoslynNotificationPump(
        ILogger logger,
        NotificationBackpressureTracker backpressure,
        Func<RoslynNotificationWorkItem, CancellationToken, Task> handler,
        Func<string, bool> isHighPriority,
        int regularQueueCapacity = 1024)
    {
        _logger = logger;
        _backpressure = backpressure;
        _handler = handler;
        _isHighPriority = isHighPriority;
        if (regularQueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(regularQueueCapacity));
        }
        _regularQueueCapacity = regularQueueCapacity;
        _priorityChannel = Channel.CreateUnbounded<RoslynNotificationWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _priorityTask = Task.Run(() => ProcessAsync(_priorityChannel.Reader, isPriorityQueue: true, _cts.Token));
        _task = Task.Run(() => ProcessRegularAsync(_cts.Token));
    }

    public void Enqueue(RoslynNotificationWorkItem item, Action<string> onDropped)
    {
        if (_isHighPriority(item.Method))
        {
            if (!_priorityChannel.Writer.TryWrite(item))
            {
                // Writer should only fail during shutdown. Best-effort handling avoids losing
                // initialization completion/diagnostics if cancellation races channel completion.
                _ = _handler(item, _cts.Token);
                return;
            }

            _backpressure.Enqueue(isPriorityQueue: true);
            return;
        }

        RoslynNotificationWorkItem? dropped = null;
        lock (_regularQueueLock)
        {
            if (_regularQueue.Count >= _regularQueueCapacity)
            {
                dropped = _regularQueue.Dequeue();
                _backpressure.Dequeue(isPriorityQueue: false);
            }

            _regularQueue.Enqueue(item);
            _backpressure.Enqueue(isPriorityQueue: false);
        }

        if (dropped.HasValue)
        {
            onDropped(dropped.Value.Method);
        }

        _regularQueueSignal.Release();
    }

    private async Task ProcessAsync(ChannelReader<RoslynNotificationWorkItem> reader, bool isPriorityQueue, CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var item))
                {
                    _backpressure.Dequeue(isPriorityQueue);
                    await _handler(item, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Roslyn notifications");
        }
    }

    private async Task ProcessRegularAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await _regularQueueSignal.WaitAsync(ct);

                while (TryDequeueRegular(out var item))
                {
                    _backpressure.Dequeue(isPriorityQueue: false);
                    await _handler(item, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Roslyn notifications");
        }
    }

    private bool TryDequeueRegular(out RoslynNotificationWorkItem item)
    {
        lock (_regularQueueLock)
        {
            if (_regularQueue.Count == 0)
            {
                item = default;
                return false;
            }

            item = _regularQueue.Dequeue();
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _priorityChannel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            await _priorityTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore shutdown errors
        }

        try
        {
            await _task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore shutdown errors
        }

        _cts.Dispose();
        _regularQueueSignal.Dispose();
    }
}
