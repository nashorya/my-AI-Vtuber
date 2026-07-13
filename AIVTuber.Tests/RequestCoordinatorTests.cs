using AIVTuber.Core.Bot;

namespace AIVTuber.Tests;

public sealed class RequestCoordinatorTests
{
    [Fact]
    public void Capacity_IsOne()
    {
        Assert.Equal(1, RequestCoordinator.Capacity);
    }

    [Theory]
    [InlineData((int)InputSource.Loopback, 0)]
    [InlineData((int)InputSource.Danmaku, 1)]
    [InlineData((int)InputSource.Microphone, 2)]
    [InlineData((int)InputSource.Manual, 3)]
    public void Priority_IsDeterministic(int source, int expected)
    {
        Assert.Equal(expected, RequestCoordinator.PriorityOf((InputSource)source));
    }

    [Fact]
    public void Priority_RejectsUnknownSource()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestCoordinator.PriorityOf((InputSource)99));
    }

    [Fact]
    public async Task Envelopes_HaveMonotonicGenerationSequenceAndClock()
    {
        var clock = new FakeClock(40, 40);
        await using var coordinator = new RequestCoordinator(clock);
        var envelopes = new List<InputEnvelope>();

        Assert.True(await coordinator.EnqueueAsync(InputSource.Danmaku, (envelope, _) =>
        {
            envelopes.Add(envelope);
            return Task.CompletedTask;
        }));
        Assert.True(await coordinator.EnqueueAsync(InputSource.Microphone, (envelope, _) =>
        {
            envelopes.Add(envelope);
            return Task.CompletedTask;
        }));

        Assert.Collection(envelopes,
            first =>
            {
                Assert.Equal(1, first.Sequence);
                Assert.Equal(1, first.Generation.Value);
                Assert.Equal(40, first.Timestamp);
            },
            second =>
            {
                Assert.Equal(2, second.Sequence);
                Assert.Equal(2, second.Generation.Value);
                Assert.Equal(80, second.Timestamp);
            });
    }

    [Fact]
    public async Task LatestRequest_InvalidatesActiveAndReplacesPending()
    {
        await using var coordinator = new RequestCoordinator();
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var executed = new List<string>();

        var first = coordinator.EnqueueAsync(InputSource.Danmaku, async (_, _) =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task;
            executed.Add("first-late");
        });
        await firstStarted.Task;

        var replaced = coordinator.EnqueueAsync(InputSource.Danmaku, (_, _) =>
        {
            executed.Add("replaced");
            return Task.CompletedTask;
        });
        var latest = coordinator.EnqueueAsync(InputSource.Microphone, (_, _) =>
        {
            executed.Add("latest");
            return Task.CompletedTask;
        });
        releaseFirst.TrySetResult();

        Assert.False(await first);
        Assert.False(await replaced);
        Assert.True(await latest);
        Assert.Equal(["first-late", "latest"], executed);
    }

    [Fact]
    public async Task Loopback_IsDroppedWhileAnotherRequestIsBusy()
    {
        await using var coordinator = new RequestCoordinator();
        var started = NewSignal();
        var release = NewSignal();
        var active = coordinator.EnqueueAsync(InputSource.Danmaku, async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
        });
        await started.Task;

        var loopback = await coordinator.EnqueueAsync(InputSource.Loopback, (_, _) => Task.CompletedTask);
        release.TrySetResult();

        Assert.False(loopback);
        Assert.True(await active);
    }

    [Fact]
    public async Task CancelCurrent_WaitsForCancellationAndLeavesNoBusyRequest()
    {
        await using var coordinator = new RequestCoordinator();
        var started = NewSignal();
        var request = coordinator.EnqueueAsync(InputSource.Microphone, async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await started.Task;

        await coordinator.CancelCurrentAsync();

        Assert.False(await request);
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public async Task ConsumerFault_IsObservedAndNextRequestRecovers()
    {
        await using var coordinator = new RequestCoordinator();
        var failed = coordinator.EnqueueAsync(InputSource.Danmaku,
            (_, _) => throw new InvalidOperationException("boom"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        Assert.Equal("boom", error.Message);

        var recovered = await coordinator.EnqueueAsync(InputSource.Danmaku, (_, _) => Task.CompletedTask);
        Assert.True(recovered);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeClock(long start, long step) : IMonotonicClock
    {
        private long _value = start - step;
        public long GetTimestamp() => Interlocked.Add(ref _value, step);
    }
}
