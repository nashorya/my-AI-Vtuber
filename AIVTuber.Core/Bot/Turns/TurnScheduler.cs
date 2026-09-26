namespace AIVTuber.Core.Bot.Turns;

/// <summary>Monotonic millisecond clock. Replaceable in tests with a fake clock so every
/// timer decision is deterministic (plan RT-04: "计时器全部可替换测试").</summary>
internal interface ITurnClock
{
    long NowMs { get; }
}

internal sealed class StopwatchTurnClock : ITurnClock
{
    public long NowMs => Environment.TickCount64;
}

/// <summary>
/// One-shot timer abstraction. Deadlines are expressed on the owning
/// <see cref="ITurnClock"/>'s millisecond timeline, so a fake clock plus a manual
/// scheduler makes every timer decision deterministic in tests.
/// </summary>
internal interface ITurnScheduler : IDisposable
{
    /// <summary>Schedules (replaces) the single pending callback at the absolute deadline.</summary>
    void Schedule(long deadlineMs, Action callback);

    /// <summary>Cancels the pending callback, if any.</summary>
    void Cancel();
}

/// <summary>Production scheduler: wraps <see cref="System.Threading.Timer"/> on the wall monotonic clock.</summary>
internal sealed class TimerTurnScheduler : ITurnScheduler
{
    private readonly Timer _timer;
    private readonly ITurnClock _clock;
    private volatile Action? _callback;

    public TimerTurnScheduler(ITurnClock clock)
    {
        _clock = clock;
        _timer = new Timer(_ => _callback?.Invoke(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Schedule(long deadlineMs, Action callback)
    {
        _callback = callback;
        var delayMs = Math.Max(1, deadlineMs - _clock.NowMs);
        _timer.Change(TimeSpan.FromMilliseconds(delayMs), Timeout.InfiniteTimeSpan);
    }

    public void Cancel()
    {
        _callback = null;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _callback = null;
        _timer.Dispose();
    }
}

/// <summary>Test clock: the test advances time explicitly.</summary>
internal sealed class FakeTurnClock : ITurnClock
{
    public long NowMs { get; private set; }
    public void Advance(long ms) => NowMs += ms;
}

/// <summary>Test scheduler: fires the pending callback only when the fake clock is advanced
/// past its deadline. No real time ever passes.</summary>
internal sealed class ManualTurnScheduler : ITurnScheduler
{
    private long _deadline = long.MaxValue;
    private Action? _callback;

    public bool HasPending => _callback is not null;

    public void Schedule(long deadlineMs, Action callback)
    {
        _deadline = deadlineMs;
        _callback = callback;
    }

    /// <summary>Advances the clock by <paramref name="ms"/> and fires the callback if due.</summary>
    public void Elapse(FakeTurnClock clock, long ms)
    {
        clock.Advance(ms);
        if (_callback is not null && clock.NowMs >= _deadline)
        {
            var cb = _callback;
            Cancel();
            cb();
        }
    }

    public void Cancel()
    {
        _callback = null;
        _deadline = long.MaxValue;
    }

    public void Dispose() => Cancel();
}
