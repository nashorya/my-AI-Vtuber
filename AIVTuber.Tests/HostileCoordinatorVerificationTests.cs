using AIVTuber.Core.Bot;

namespace AIVTuber.Tests;

public class HostileCoordinatorVerificationTests
{
    [Fact]
    public async Task SameClock_ReplayedOneHundredTimes_HasDeterministicMonotonicGenerations()
    {
        await using var coordinator = new RequestCoordinator(new FixedClock(42));
        var observed = new List<InputEnvelope>();

        for (var i = 0; i < 100; i++)
        {
            var accepted = await coordinator.EnqueueAsync(
                InputSource.Manual,
                (envelope, _) =>
                {
                    observed.Add(envelope);
                    return Task.CompletedTask;
                });
            Assert.True(accepted);
        }

        Assert.Equal(Enumerable.Range(1, 100).Select(value => (long)value),
            observed.Select(item => item.Generation.Value));
        Assert.Equal(Enumerable.Range(1, 100).Select(value => (long)value),
            observed.Select(item => item.Sequence));
        Assert.All(observed, item => Assert.Equal(42, item.Timestamp));
    }

    [Fact]
    public async Task NewestRequest_CancelsActiveAndSuppressesItsLateOutput()
    {
        await using var coordinator = new RequestCoordinator(new FixedClock(1));
        var firstEntered = NewSignal();
        var outputs = new List<string>();

        var first = coordinator.EnqueueAsync(InputSource.Danmaku, async (envelope, token) =>
        {
            firstEntered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            if (coordinator.IsCurrent(envelope.Generation)) outputs.Add("stale");
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = coordinator.EnqueueAsync(InputSource.Manual, (envelope, _) =>
        {
            if (coordinator.IsCurrent(envelope.Generation)) outputs.Add("current");
            return Task.CompletedTask;
        });

        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(["current"], outputs);
    }

    [Fact]
    public async Task LoopbackWhileBusy_IsDroppedWithoutAdvancingGeneration()
    {
        await using var coordinator = new RequestCoordinator(new FixedClock(1));
        var entered = NewSignal();
        var release = NewSignal();
        var active = coordinator.EnqueueAsync(InputSource.Manual, async (_, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var generation = coordinator.CurrentGeneration;

        var loopbackAccepted = await coordinator.EnqueueAsync(
            InputSource.Loopback, (_, _) => Task.CompletedTask);

        Assert.False(loopbackAccepted);
        Assert.Equal(generation, coordinator.CurrentGeneration);
        release.SetResult();
        Assert.True(await active.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task PendingBurst_IsCapacityOneAndLatestWins()
    {
        await using var coordinator = new RequestCoordinator(new FixedClock(1));
        var activeEntered = NewSignal();
        var releaseActive = NewSignal();
        var executed = new List<long>();
        var concurrent = 0;
        var maxConcurrent = 0;

        var active = coordinator.EnqueueAsync(InputSource.Manual, async (envelope, _) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            executed.Add(envelope.Generation.Value);
            activeEntered.SetResult();
            await releaseActive.Task;
            Interlocked.Decrement(ref concurrent);
        });
        await activeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replaced = coordinator.EnqueueAsync(InputSource.Danmaku, (envelope, _) =>
        {
            executed.Add(envelope.Generation.Value);
            return Task.CompletedTask;
        });
        var latest = coordinator.EnqueueAsync(InputSource.Microphone, (envelope, _) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            executed.Add(envelope.Generation.Value);
            Interlocked.Decrement(ref concurrent);
            return Task.CompletedTask;
        });

        Assert.False(await replaced.WaitAsync(TimeSpan.FromSeconds(2)));
        releaseActive.SetResult();
        Assert.False(await active.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await latest.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal([1L, 3L], executed);
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task ConsumerFault_IsObservedAndDoesNotPoisonNextRequest()
    {
        await using var coordinator = new RequestCoordinator(new FixedClock(1));

        var failed = coordinator.EnqueueAsync(
            InputSource.Manual,
            (_, _) => throw new InvalidOperationException("boom"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failed.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("boom", error.Message);
        Assert.True(await coordinator.EnqueueAsync(
            InputSource.Manual, (_, _) => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CancelCurrentAsync_WaitsForCancelledCommandCleanup()
    {
        await using var coordinator = new RequestCoordinator(new FixedClock(1));
        var entered = NewSignal();
        var cleanupRelease = NewSignal();
        var active = coordinator.EnqueueAsync(InputSource.Manual, async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await cleanupRelease.Task;
            }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var cancelling = coordinator.CancelCurrentAsync();
        await Task.Yield();
        Assert.False(cancelling.IsCompleted);
        cleanupRelease.SetResult();

        await cancelling.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(await active.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForInflightCleanupAndRejectsFurtherCommands()
    {
        var coordinator = new RequestCoordinator(new FixedClock(1));
        var entered = NewSignal();
        var cleanupRelease = NewSignal();
        var active = coordinator.EnqueueAsync(InputSource.Manual, async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await cleanupRelease.Task;
            }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposing = coordinator.DisposeAsync().AsTask();
        await Task.Yield();
        Assert.False(disposing.IsCompleted);
        cleanupRelease.SetResult();

        await disposing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(await active.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(coordinator.IsBusy);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await coordinator.EnqueueAsync(InputSource.Manual, (_, _) => Task.CompletedTask));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FixedClock(long timestamp) : IMonotonicClock
    {
        public long GetTimestamp() => timestamp;
    }
}
