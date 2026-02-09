using Microsoft.Extensions.Logging;

namespace RazorSharp.Server;

internal sealed class WorkspaceReloadScheduler : IAsyncDisposable
{
    readonly ILogger _logger;
    readonly Func<string?> _getWorkspaceOpenTarget;
    readonly Func<bool> _canSendRoslynNotifications;
    readonly Func<string, Task> _openWorkspaceAsync;
    readonly int _debounceMilliseconds;
    readonly Lock _lock = new();
    CancellationTokenSource? _reloadCts;
    Task? _reloadTask;

    public WorkspaceReloadScheduler(
        ILogger logger,
        Func<string?> getWorkspaceOpenTarget,
        Func<bool> canSendRoslynNotifications,
        Func<string, Task> openWorkspaceAsync,
        int debounceMilliseconds)
    {
        _logger = logger;
        _getWorkspaceOpenTarget = getWorkspaceOpenTarget;
        _canSendRoslynNotifications = canSendRoslynNotifications;
        _openWorkspaceAsync = openWorkspaceAsync;
        _debounceMilliseconds = debounceMilliseconds;
    }

    public void Schedule()
    {
        var target = _getWorkspaceOpenTarget();
        if (target == null || !_canSendRoslynNotifications())
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_lock)
        {
            _reloadCts?.Cancel();
            _reloadCts?.Dispose();
            _reloadCts = new CancellationTokenSource();
            cts = _reloadCts;
        }

        _reloadTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceMilliseconds, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogInformation("Workspace files changed; re-opening workspace");
                await _openWorkspaceAsync(target);
            }
            catch (OperationCanceledException)
            {
                // Debounced by a newer change or canceled during shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-open workspace after file changes");
            }
        }, cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _reloadCts?.Cancel();
            _reloadCts?.Dispose();
            _reloadCts = null;
        }

        if (_reloadTask != null)
        {
            try
            {
                await _reloadTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Ignore shutdown errors
            }
        }
    }
}
