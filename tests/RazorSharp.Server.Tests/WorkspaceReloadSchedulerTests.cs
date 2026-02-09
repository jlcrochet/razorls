using Microsoft.Extensions.Logging;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class WorkspaceReloadSchedulerTests
{
    [Fact]
    public async Task Schedule_DebouncesMultipleRequests()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var calls = 0;
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var scheduler = new WorkspaceReloadScheduler(
            loggerFactory.CreateLogger<WorkspaceReloadScheduler>(),
            static () => "/workspace/test.sln",
            static () => true,
            _ =>
            {
                Interlocked.Increment(ref calls);
                called.TrySetResult();
                return Task.CompletedTask;
            },
            debounceMilliseconds: 100);

        scheduler.Schedule();
        scheduler.Schedule();

        await called.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(200);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Schedule_UsesLatestTargetAfterDebounce()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var calls = 0;
        var lastTarget = string.Empty;
        var target = "/workspace/first.sln";
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var scheduler = new WorkspaceReloadScheduler(
            loggerFactory.CreateLogger<WorkspaceReloadScheduler>(),
            () => target,
            static () => true,
            openTarget =>
            {
                Interlocked.Increment(ref calls);
                lastTarget = openTarget;
                called.TrySetResult();
                return Task.CompletedTask;
            },
            debounceMilliseconds: 100);

        scheduler.Schedule();
        target = "/workspace/second.sln";
        scheduler.Schedule();

        await called.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(200);

        Assert.Equal(1, calls);
        Assert.Equal("/workspace/second.sln", lastTarget);
    }

    [Fact]
    public async Task Schedule_SkipsWhenTargetMissingOrSendingDisabled()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var calls = 0;

        await using var noTargetScheduler = new WorkspaceReloadScheduler(
            loggerFactory.CreateLogger<WorkspaceReloadScheduler>(),
            static () => null,
            static () => true,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            debounceMilliseconds: 50);

        noTargetScheduler.Schedule();
        await Task.Delay(100);

        await using var disabledScheduler = new WorkspaceReloadScheduler(
            loggerFactory.CreateLogger<WorkspaceReloadScheduler>(),
            static () => "/workspace/test.sln",
            static () => false,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            debounceMilliseconds: 50);

        disabledScheduler.Schedule();
        await Task.Delay(100);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DisposeAsync_CancelsPendingReload()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var calls = 0;

        await using var scheduler = new WorkspaceReloadScheduler(
            loggerFactory.CreateLogger<WorkspaceReloadScheduler>(),
            static () => "/workspace/test.sln",
            static () => true,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            debounceMilliseconds: 500);

        scheduler.Schedule();
        await scheduler.DisposeAsync();
        await Task.Delay(600);

        Assert.Equal(0, calls);
    }
}
