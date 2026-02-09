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
    readonly Channel<RoslynNotificationWorkItem> _channel;
    readonly CancellationTokenSource _cts = new();
    readonly Task _priorityTask;
    readonly Task _task;

    public RoslynNotificationPump(
        ILogger logger,
        NotificationBackpressureTracker backpressure,
        Func<RoslynNotificationWorkItem, CancellationToken, Task> handler,
        Func<string, bool> isHighPriority)
    {
        _logger = logger;
        _backpressure = backpressure;
        _handler = handler;
        _isHighPriority = isHighPriority;
        _priorityChannel = Channel.CreateUnbounded<RoslynNotificationWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _channel = Channel.CreateBounded<RoslynNotificationWorkItem>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _priorityTask = Task.Run(() => ProcessAsync(_priorityChannel.Reader, isPriorityQueue: true, _cts.Token));
        _task = Task.Run(() => ProcessAsync(_channel.Reader, isPriorityQueue: false, _cts.Token));
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

        if (!_channel.Writer.TryWrite(item))
        {
            onDropped(item.Method);
            return;
        }

        _backpressure.Enqueue(isPriorityQueue: false);
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

    public async ValueTask DisposeAsync()
    {
        _priorityChannel.Writer.TryComplete();
        _channel.Writer.TryComplete();
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
    }
}
