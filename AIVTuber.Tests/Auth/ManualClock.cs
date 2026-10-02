namespace AIVTuber.Tests.Auth;

/// <summary>Test clock whose wall time and monotonic timestamp can move independently,
/// so a rolled-back system clock can be simulated.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _wall = now;
    private long _monoTicks = TimeSpan.FromHours(1).Ticks;

    public override DateTimeOffset GetUtcNow() => _wall;
    public override long GetTimestamp() => _monoTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _wall += by;
        _monoTicks += by.Ticks;
    }

    public void AdvanceMonotonicOnly(TimeSpan by) => _monoTicks += by.Ticks;
    public void SetWall(DateTimeOffset wall) => _wall = wall;
}
